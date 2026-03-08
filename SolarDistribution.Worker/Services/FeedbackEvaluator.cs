using System.Text.Json;
using Microsoft.Extensions.Logging;
using SolarDistribution.Core.Data.Entities;
using SolarDistribution.Core.Repositories;
using SolarDistribution.Worker.Configuration;
using SolarDistribution.Worker.HA;

namespace SolarDistribution.Worker.Services;

/// <summary>
/// Collects real feedback for past sessions by re-reading battery SOCs from HA.
///
/// WHY IT MATTERS:
///   Without this, the ML would learn to reproduce a hard-coded rule (heuristic).
///   With this feedback, ML learns from WHAT REALLY HAPPENED.
///
/// WHEN:
///   Triggered by MlRetrainScheduler according to config ml.feedback_delay_hours (default: 4h).
///   We wait N hours after a session to observe the real effect of the decision.
///
/// WHAT WE MEASURE:
///   We re-read the current SOC of each battery in HA.
///   We compute two real labels:
///
///   1. ObservedOptimalSoftMax:
///      - If batteries fell too low after the session → SoftMax was too low,
///        we should have charged more → label = SoftMax + correction
///      - If batteries remained unnecessarily high → label = SoftMax slightly reduced
///
///   2. ObservedOptimalPreventive:
///      - If a battery fell below MinPercent → preventive threshold was too low
///      - Otherwise → preventive threshold was correct or slightly conservative
///
///   3. EnergyEfficiencyScore (0→1):
///      - Ratio of energy stored / energy available
///
///   4. AvailabilityScore (0→1):
///      - Penalizes batteries that are too low at feedback time
/// </summary>
public class FeedbackEvaluator
{
    private readonly IDistributionRepository         _repo;
    private readonly IHomeAssistantClient            _haClient;
    private readonly SolarConfig                     _config;
    private readonly ILogger<FeedbackEvaluator>      _logger;

    public FeedbackEvaluator(
        IDistributionRepository    repo,
        IHomeAssistantClient       haClient,
        SolarConfig                config,
        ILogger<FeedbackEvaluator> logger)
    {
        _repo     = repo;
        _haClient = haClient;
        _config   = config;
        _logger   = logger;
    }

    /// <summary>
    /// Collecte le feedback pour toutes les sessions en attente.
    /// Appelé périodiquement par MlRetrainScheduler.
    /// </summary>
    public async Task<int> CollectPendingFeedbacksAsync(CancellationToken ct = default)
    {
        double delayHours = _config.Ml.FeedbackDelayHours;

        var pendingSessions = await _repo.GetSessionsPendingFeedbackAsync(delayHours, ct);

        if (!pendingSessions.Any())
        {
            _logger.LogDebug("No sessions pending feedback collection");
            return 0;
        }

        _logger.LogInformation(
            "Collecting feedback for {Count} sessions (delay={Delay}h)",
            pendingSessions.Count, delayHours);

        int collected = 0;

        foreach (var session in pendingSessions)
        {
            try
            {
                var feedback = await EvaluateSessionAsync(session, ct);
                await _repo.SaveFeedbackAsync(feedback, ct);

                if (feedback.Status == FeedbackStatus.Valid)
                    collected++;

                _logger.LogInformation(
                    "Feedback session#{Id}: status={Status}, " +
                    "efficiency={Eff:P0}, availability={Avail:P0}, " +
                    "optimalSoftMax={SoftMax:F1}%, optimalPreventive={Prev:F1}%",
                    session.Id, feedback.Status,
                    feedback.EnergyEfficiencyScore, feedback.AvailabilityScore,
                    feedback.ObservedOptimalSoftMax, feedback.ObservedOptimalPreventive);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to collect feedback for session#{Id}", session.Id);
            }
        }

        _logger.LogInformation("Feedback collection complete: {Collected}/{Total} valid",
            collected, pendingSessions.Count);

        return collected;
    }

    // ── Évaluation d'une session ──────────────────────────────────────────────

    private async Task<SessionFeedback> EvaluateSessionAsync(
        DistributionSession session, CancellationToken ct)
    {
        double hoursElapsed = (DateTime.UtcNow - session.RequestedAt).TotalHours;

        // ── Lire le SOC actuel de chaque batterie dans HA ─────────────────────
        var observedSocs = new Dictionary<int, double>();
        bool anyReadFailed = false;

        foreach (var battConfig in _config.Batteries)
        {
            double? soc = await _haClient.GetNumericStateAsync(battConfig.Entities.Soc, ct);

            if (soc is null)
            {
                _logger.LogWarning(
                    "Feedback session#{Id}: cannot read SOC for battery {BattId} ({Name})",
                    session.Id, battConfig.Id, battConfig.Name);
                anyReadFailed = true;
            }
            else
            {
                observedSocs[battConfig.Id] = soc.Value;
            }
        }

        // Si toutes les lectures ont échoué → feedback invalide
        if (!observedSocs.Any())
        {
            return new SessionFeedback
            {
                SessionId          = session.Id,
                CollectedAt        = DateTime.UtcNow,
                FeedbackDelayHours = hoursElapsed,
                Status             = FeedbackStatus.Invalid,
                InvalidReason      = "All battery SOC reads failed (HA unavailable?)"
            };
        }

        // ── Calcul des scores d'efficacité ────────────────────────────────────
        double energyEfficiency = ComputeEnergyEfficiency(session);
        double availability     = ComputeAvailabilityScore(observedSocs, session);
        double composite        = energyEfficiency * 0.6 + availability * 0.4;

        // ── Calcul des labels réels pour l'entraînement ML ────────────────────
        double optimalSoftMax    = ComputeObservedOptimalSoftMax(session, observedSocs, availability);
        double optimalPreventive = ComputeObservedOptimalPreventive(session, observedSocs);

        // Avertissement si des lectures ont partiellement échoué
        string? invalidReason = anyReadFailed
            ? "Some battery reads failed — partial feedback"
            : null;

        return new SessionFeedback
        {
            SessionId                  = session.Id,
            CollectedAt                = DateTime.UtcNow,
            FeedbackDelayHours         = hoursElapsed,
            ObservedSocJson            = JsonSerializer.Serialize(observedSocs),
            AvgSocAtFeedback           = observedSocs.Values.Average(),
            MinSocAtFeedback           = observedSocs.Values.Min(),
            EnergyEfficiencyScore      = energyEfficiency,
            AvailabilityScore          = availability,
            ObservedOptimalSoftMax     = optimalSoftMax,
            ObservedOptimalPreventive  = optimalPreventive,
            CompositeScore             = composite,
            Status                     = anyReadFailed ? FeedbackStatus.Invalid : FeedbackStatus.Valid,
            InvalidReason              = invalidReason
        };
    }

    // ── Calcul EnergyEfficiency ───────────────────────────────────────────────

    /// <summary>
    /// Efficacité énergétique = énergie effectivement stockée / énergie théoriquement disponible.
    ///
    /// Si on avait 1000W de surplus et qu'on n'a pu en stocker que 600W (batteries pleines
    /// ou MaxChargeRate atteint) → score = 0.6
    /// Si tout a été absorbé → score = 1.0
    /// </summary>
    private static double ComputeEnergyEfficiency(DistributionSession session)
    {
        if (session.SurplusW <= 0) return 1.0;

        double ratio = session.TotalAllocatedW / session.SurplusW;
        return Math.Clamp(ratio, 0, 1);
    }

    // ── Calcul AvailabilityScore ──────────────────────────────────────────────

    /// <summary>
    /// Score de disponibilité : les batteries sont-elles à un niveau acceptable N heures après ?
    ///
    /// Pénalise proportionnellement si le SOC est tombé sous MinPercent.
    /// Score = 1.0 si toutes les batteries sont au-dessus de MinPercent
    /// Score = 0.0 si toutes les batteries sont tombées au minimum absolu
    /// </summary>
    private double ComputeAvailabilityScore(
        Dictionary<int, double> observedSocs, DistributionSession session)
    {
        if (!observedSocs.Any()) return 0.5; // valeur neutre si pas de lecture

        var scores = new List<double>();

        foreach (var (battId, soc) in observedSocs)
        {
            var battConfig = _config.Batteries.FirstOrDefault(b => b.Id == battId);
            if (battConfig is null) continue;

            // Score 1.0 si au-dessus de MinPercent
            // Pénalité linéaire jusqu'à 0 si SOC = 0%
            double score = soc >= battConfig.MinPercent
                ? 1.0
                : soc / battConfig.MinPercent;

            scores.Add(score);
        }

        return scores.Any() ? scores.Average() : 0.5;
    }

    // ── Calcul ObservedOptimalSoftMax ─────────────────────────────────────────

    /// <summary>
    /// SoftMax optimal déduit de l'observation réelle.
    ///
    /// Logique :
    ///   - Les batteries sont trop basses (AvailabilityScore faible) après N heures
    ///     → on aurait dû viser plus haut → SoftMax = SoftMax_utilisé + correction
    ///
    ///   - Les batteries sont restées inutilement hautes (>> SoftMax) et il y avait
    ///     encore de la production → on aurait pu charger moins vite → légère réduction
    ///
    ///   - Tout s'est bien passé → on garde le SoftMax utilisé comme label optimal
    /// </summary>
    private double ComputeObservedOptimalSoftMax(
        DistributionSession session,
        Dictionary<int, double> observedSocs,
        double availabilityScore)
    {
        // SoftMax qui a été réellement appliqué lors de la session
        double appliedSoftMax = session.BatterySnapshots.Any()
            ? session.BatterySnapshots.Average(b => b.SoftMaxPercent)
            : 80.0;

        double avgSocNow = observedSocs.Values.DefaultIfEmpty(50).Average();

        // Cas 1 : batteries trop basses → on aurait dû viser plus haut
        if (availabilityScore < 0.7)
        {
            // ML-3 : correction proportionnelle à la sévérité, facteur configurable
            double penalty = (0.7 - availabilityScore) / 0.7; // 0→1
            double correction = penalty * _config.Ml.FeedbackSoftmaxCorrectionFactor;
            return Math.Clamp(appliedSoftMax + correction, 60, 95);
        }

        // Cas 2 : batteries inutilement hautes et pas de pénurie
        // (le surplus n'a pas pu être absorbé → batteries déjà trop pleines)
        if (avgSocNow > appliedSoftMax + 5 && session.UnusedSurplusW > 0)
        {
            // ML-3 : réduction configurable
            return Math.Clamp(appliedSoftMax - _config.Ml.FeedbackSoftmaxReduction, 60, 95);
        }

        // Cas 3 : équilibre → le SoftMax appliqué était bon
        return Math.Clamp(appliedSoftMax, 60, 95);
    }

    // ── Calcul ObservedOptimalPreventive ──────────────────────────────────────

    /// <summary>
    /// Seuil préventif optimal déduit de l'observation.
    ///
    /// Logique :
    ///   - Une batterie est tombée sous MinPercent → le seuil était trop bas,
    ///     on aurait dû garder davantage en réserve → PreventiveThreshold + correction
    ///
    ///   - Les batteries n'ont jamais approché MinPercent → le seuil était adapté
    ///     ou légèrement trop conservateur → maintenu ou réduit légèrement
    /// </summary>
    private double ComputeObservedOptimalPreventive(
        DistributionSession session,
        Dictionary<int, double> observedSocs)
    {
        double appliedMinPercent = session.BatterySnapshots.Any()
            ? session.BatterySnapshots.Average(b => b.MinPercent)
            : 20.0;

        double minObservedSoc = observedSocs.Values.DefaultIfEmpty(50).Min();

        // Batterie tombée trop bas → augmenter le seuil préventif
        if (minObservedSoc < appliedMinPercent)
        {
            double shortfall = appliedMinPercent - minObservedSoc;
            // ML-3 : facteur et plafond de correction configurables
            double correction = Math.Min(
                shortfall * _config.Ml.FeedbackPreventiveFactor,
                _config.Ml.FeedbackMaxPreventiveCorrection);
            return Math.Clamp(appliedMinPercent + correction, 15, 50);
        }

        // Batterie restée très au-dessus → on était peut-être trop conservateur
        if (minObservedSoc > appliedMinPercent + 20)
        {
            // ML-3 : réduction configurable
            return Math.Clamp(appliedMinPercent - _config.Ml.FeedbackPreventiveReduction, 15, 50);
        }

        // Équilibre → seuil correct
        return Math.Clamp(appliedMinPercent, 15, 50);
    }
}
