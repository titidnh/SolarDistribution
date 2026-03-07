using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SolarDistribution.Core.Services.ML;
using SolarDistribution.Worker.Configuration;

namespace SolarDistribution.Worker.Services;

/// <summary>
/// Second BackgroundService autonome gérant deux tâches planifiées :
///
///   1. COLLECTE DE FEEDBACK (fréquent, ex: toutes les heures)
///      Relit le SOC réel des batteries dans HA pour les sessions passées
///      et calcule les vrais labels ML (ObservedOptimalSoftMax, ObservedOptimalPreventive).
///      → Alimente la base avec des données d'entraînement réelles
///
///   2. RÉENTRAÎNEMENT ML (peu fréquent, ex: dimanche 3h du matin)
///      Entraîne les deux modèles FastTree sur les sessions avec feedback valide.
///      → Active le ML dès que MIN_SESSIONS sessions ont un feedback valide
///
/// Configuration dans config.yaml :
///   ml:
///     feedback_delay_hours: 4        # attendre 4h avant de lire le SOC réel
///     feedback_check_interval_hours: 1  # vérifier les feedbacks en attente toutes les heures
///     retrain_cron: "0 3 * * 0"      # dimanche à 3h du matin (syntaxe cron)
///     min_feedback_for_retrain: 50   # minimum de feedbacks valides pour déclencher le retrain
/// </summary>
public class MlRetrainScheduler : BackgroundService
{
    private readonly FeedbackEvaluator           _feedbackEvaluator;
    private readonly IDistributionMLService      _mlService;
    private readonly MlConfig                    _mlConfig;
    private readonly ILogger<MlRetrainScheduler> _logger;

    // Dernier retrain effectué — évite les doublons si le scheduler est redémarré
    private DateTime _lastRetrainAt = DateTime.MinValue;

    public MlRetrainScheduler(
        FeedbackEvaluator           feedbackEvaluator,
        IDistributionMLService      mlService,
        MlConfig                    mlConfig,
        ILogger<MlRetrainScheduler> logger)
    {
        _feedbackEvaluator = feedbackEvaluator;
        _mlService         = mlService;
        _mlConfig          = mlConfig;
        _logger            = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "MlRetrainScheduler started | feedback check every {FeedbackInterval}h | retrain cron: {Cron}",
            _mlConfig.FeedbackCheckIntervalHours,
            _mlConfig.RetrainCron);

        // Petit délai initial pour laisser le SolarWorker se stabiliser d'abord
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;

            // ── 1. Collecte de feedback ───────────────────────────────────────
            try
            {
                int collected = await _feedbackEvaluator.CollectPendingFeedbacksAsync(stoppingToken);

                if (collected > 0)
                {
                    _logger.LogInformation(
                        "Feedback: {Count} new valid feedbacks collected", collected);

                    // Log du statut ML après collecte
                    var status = _mlService.GetStatus();
                    _logger.LogInformation(
                        "ML training readiness: {Valid}/{Min} valid feedbacks " +
                        "(need {Remaining} more before retrain)",
                        status.SessionsInDb, _mlConfig.MinFeedbackForRetrain,
                        Math.Max(0, _mlConfig.MinFeedbackForRetrain - status.SessionsInDb));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Feedback collection failed");
            }

            // ── 2. Vérification du retrain planifié ───────────────────────────
            try
            {
                if (ShouldRetrain(now))
                {
                    await RunRetrainAsync(stoppingToken);
                    _lastRetrainAt = now;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ML retrain failed");
            }

            // ── Attente avant prochain check ──────────────────────────────────
            var interval = TimeSpan.FromHours(_mlConfig.FeedbackCheckIntervalHours);
            _logger.LogDebug("MlRetrainScheduler sleeping {Interval}h", interval.TotalHours);

            await Task.Delay(interval, stoppingToken);
        }
    }

    // ── Logique de décision retrain ───────────────────────────────────────────

    /// <summary>
    /// Détermine si on doit lancer un retrain maintenant.
    /// Combine la vérification cron ET le minimum de feedbacks requis.
    /// </summary>
    private bool ShouldRetrain(DateTime now)
    {
        // Vérifier le minimum de feedbacks valides en base
        var status = _mlService.GetStatus();
        if (status.SessionsInDb < _mlConfig.MinFeedbackForRetrain)
        {
            _logger.LogDebug(
                "Retrain skipped: only {Count}/{Min} valid feedbacks",
                status.SessionsInDb, _mlConfig.MinFeedbackForRetrain);
            return false;
        }

        // Vérifier qu'on n'a pas déjà entraîné aujourd'hui (protection anti-doublon)
        if (_lastRetrainAt.Date == now.Date)
        {
            _logger.LogDebug("Retrain skipped: already trained today at {Time}", _lastRetrainAt);
            return false;
        }

        // Évaluer l'expression cron
        return CronMatches(_mlConfig.RetrainCron, now);
    }

    private async Task RunRetrainAsync(CancellationToken ct)
    {
        _logger.LogInformation("╔══════════════════════════════════════╗");
        _logger.LogInformation("║   ML Retrain starting (scheduled)    ║");
        _logger.LogInformation("╚══════════════════════════════════════╝");

        var result = await _mlService.RetrainAsync(ct);

        if (result.Success)
        {
            _logger.LogInformation(
                "✅ ML Retrain complete | version={Ver} | samples={N} | " +
                "SoftMax R²={R1:F3} | Preventive R²={R2:F3} | active={Active}",
                result.ModelVersion,
                result.TrainingSamples,
                result.SoftMaxRSquared,
                result.PreventiveRSquared,
                result.SoftMaxRSquared >= 0.65 && result.PreventiveRSquared >= 0.65);
        }
        else
        {
            _logger.LogWarning(
                "⚠️  ML Retrain failed: {Error}", result.ErrorMessage);
        }
    }

    // ── Évaluateur Cron minimal ───────────────────────────────────────────────
    // Supporte la syntaxe standard 5 champs : "minute heure jourMois mois jourSemaine"
    // Exemples :
    //   "0 3 * * 0"   → dimanche à 3h00
    //   "0 3 * * *"   → tous les jours à 3h00
    //   "0 2 * * 1"   → lundi à 2h00
    //   "30 4 1 * *"  → le 1er de chaque mois à 4h30

    private static bool CronMatches(string cron, DateTime now)
    {
        try
        {
            var parts = cron.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 5) return false;

            return MatchesCronField(parts[0], now.Minute)
                && MatchesCronField(parts[1], now.Hour)
                && MatchesCronField(parts[2], now.Day)
                && MatchesCronField(parts[3], now.Month)
                && MatchesCronField(parts[4], (int)now.DayOfWeek);
        }
        catch
        {
            return false;
        }
    }

    private static bool MatchesCronField(string field, int value)
    {
        if (field == "*") return true;

        // Listes : "1,3,5"
        if (field.Contains(','))
            return field.Split(',').Any(f => MatchesCronField(f.Trim(), value));

        // Plages : "1-5"
        if (field.Contains('-'))
        {
            var range = field.Split('-');
            if (range.Length == 2
                && int.TryParse(range[0], out int min)
                && int.TryParse(range[1], out int max))
                return value >= min && value <= max;
        }

        // Pas : "*/5" ou "0/15"
        if (field.Contains('/'))
        {
            var step = field.Split('/');
            if (step.Length == 2 && int.TryParse(step[1], out int interval))
            {
                int start = step[0] == "*" ? 0 : int.Parse(step[0]);
                return value >= start && (value - start) % interval == 0;
            }
        }

        // Valeur exacte
        return int.TryParse(field, out int exact) && exact == value;
    }
}
