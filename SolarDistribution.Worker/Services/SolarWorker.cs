using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SolarDistribution.Core.Models;
using SolarDistribution.Core.Services;
using SolarDistribution.Core.Services.ML;
using SolarDistribution.Worker.Configuration;
using SolarDistribution.Worker.HA;

namespace SolarDistribution.Worker.Services;

/// <summary>
/// Worker autonome principal — tourne indéfiniment dans le container Docker.
///
/// Cycle complet à chaque tick :
///   1. Vérifie la connectivité HA
///   2. Lit le surplus solaire + SOC batteries via HA REST API
///   3. Construit les objets Battery avec les valeurs live
///   4. Calcule la distribution optimale (ML si disponible, sinon déterministe)
///   5. Envoie les commandes de puissance à HA via number.set_value
///   6. Persiste la session en MariaDB (avec météo Open-Meteo)
///   7. Attend l'intervalle configuré avant le prochain cycle
///
/// Résilience :
///   - Erreur de lecture HA → skip cycle, log warning, pas de commande envoyée
///   - Erreur d'écriture HA → log error, continue (pas de crash)
///   - Erreur DB → log error, continue (le cycle suivant retente)
///   - 3 échecs HA consécutifs → backoff exponentiel (max 5 minutes)
/// </summary>
public class SolarWorker : BackgroundService
{
    private readonly SolarConfig                  _config;
    private readonly HomeAssistantDataReader      _reader;
    private readonly HomeAssistantCommandSender   _sender;
    private readonly SmartDistributionService     _smartService;
    private readonly IDistributionMLService       _mlService;
    private readonly ILogger<SolarWorker>         _logger;

    // Compteurs pour backoff sur erreurs consécutives
    private int    _consecutiveHaErrors = 0;
    private const int MaxBackoffSeconds = 300;  // 5 minutes max

    public SolarWorker(
        SolarConfig                  config,
        HomeAssistantDataReader      reader,
        HomeAssistantCommandSender   sender,
        SmartDistributionService     smartService,
        IDistributionMLService       mlService,
        ILogger<SolarWorker>         logger)
    {
        _config       = config;
        _reader       = reader;
        _sender       = sender;
        _smartService = smartService;
        _mlService    = mlService;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "╔══════════════════════════════════════════════╗");
        _logger.LogInformation(
            "║   SolarDistribution Worker starting up       ║");
        _logger.LogInformation(
            "╚══════════════════════════════════════════════╝");
        _logger.LogInformation("  Interval  : {Interval}s", _config.Polling.IntervalSeconds);
        _logger.LogInformation("  HA URL    : {Url}",       _config.HomeAssistant.Url);
        _logger.LogInformation("  Batteries : {Count}",    _config.Batteries.Count);
        _logger.LogInformation("  DryRun    : {DryRun}",   _config.Polling.DryRun);
        _logger.LogInformation("  ML Status : {Status}",   _mlService.GetStatus().IsAvailable ? "available" : "training...");

        if (_config.Polling.DryRun)
            _logger.LogWarning("⚠️  DRY-RUN MODE — no commands will be sent to HA");

        // Attente initiale courte pour laisser HA démarrer si lancé en même temps
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var cycleStart = DateTime.UtcNow;

            try
            {
                await RunCycleAsync(stoppingToken);
                _consecutiveHaErrors = 0;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _consecutiveHaErrors++;
                _logger.LogError(ex,
                    "Unhandled error in cycle #{Count} — will retry",
                    _consecutiveHaErrors);
            }

            // ── Calcul du délai avant prochain cycle ──────────────────────────
            var elapsed = DateTime.UtcNow - cycleStart;
            var baseInterval = TimeSpan.FromSeconds(_config.Polling.IntervalSeconds);
            var delay = _consecutiveHaErrors > 0
                ? ComputeBackoff(_consecutiveHaErrors, baseInterval)
                : baseInterval - elapsed;

            if (delay > TimeSpan.Zero)
            {
                _logger.LogDebug("Next cycle in {Delay:F0}s", delay.TotalSeconds);
                await Task.Delay(delay, stoppingToken);
            }
        }

        _logger.LogInformation("SolarWorker stopped gracefully");
    }

    // ── Cycle principal ───────────────────────────────────────────────────────

    private async Task RunCycleAsync(CancellationToken ct)
    {
        _logger.LogDebug("──── Cycle start ────────────────────────────");

        // ── 1. Lecture HA ─────────────────────────────────────────────────────
        var snapshot = await _reader.ReadAllAsync(ct);

        if (snapshot is null)
        {
            _consecutiveHaErrors++;
            _logger.LogWarning(
                "HA read failed (#{Count}) — skipping cycle", _consecutiveHaErrors);
            return;
        }

        // Ignore les batteries dont la lecture a échoué
        var validReadings = snapshot.Batteries.Where(b => b.ReadSuccess).ToList();
        if (!validReadings.Any())
        {
            _logger.LogWarning("All battery reads failed — skipping cycle");
            return;
        }

        if (validReadings.Count < snapshot.Batteries.Count)
        {
            _logger.LogWarning(
                "{Failed} battery(ies) could not be read — continuing with {Valid} valid batteries",
                snapshot.Batteries.Count - validReadings.Count, validReadings.Count);
        }

        // ── 2. Construction des objets Battery ────────────────────────────────
        var batteries = BuildBatteries(validReadings);

        // ── 3. Distribution intelligente (ML + météo + persistence) ──────────
        var result = await _smartService.DistributeAsync(
            surplusW:   snapshot.SurplusW,
            batteries:  batteries,
            latitude:   _config.Location.Latitude,
            longitude:  _config.Location.Longitude,
            ct:         ct);

        _logger.LogInformation(
            "Distribution: engine={Engine}, allocated={Alloc}W/{Surplus}W, unused={Unused}W | session#{Id}",
            result.DecisionEngine,
            result.Distribution.TotalAllocatedW,
            snapshot.SurplusW,
            result.Distribution.UnusedSurplusW,
            result.SessionId);

        // ── 4. Envoi des commandes HA ─────────────────────────────────────────
        int commandsSent = await _sender.SendCommandsAsync(
            result.Distribution.Allocations, ct);

        _logger.LogInformation(
            "{Sent}/{Total} charge commands sent to HA",
            commandsSent, result.Distribution.Allocations.Count);

        // ── 5. Log de synthèse détaillé ───────────────────────────────────────
        foreach (var alloc in result.Distribution.Allocations)
        {
            var battConfig = _config.Batteries.FirstOrDefault(b => b.Id == alloc.BatteryId);
            _logger.LogInformation(
                "  [{Name}] {Prev:F1}% → {New:F1}% | {W}W | {Reason}",
                battConfig?.Name ?? $"Battery {alloc.BatteryId}",
                alloc.PreviousPercent, alloc.NewPercent,
                alloc.AllocatedW.ToString("F1"),
                alloc.Reason);
        }

        if (result.MLRecommendation is not null)
        {
            _logger.LogInformation(
                "  ML: softMax={SoftMax:F1}%, preventive={Prev:F1}%, confidence={Conf:P0} | {Rationale}",
                result.MLRecommendation.RecommendedSoftMaxPercent,
                result.MLRecommendation.RecommendedPreventiveThreshold,
                result.MLRecommendation.ConfidenceScore,
                result.MLRecommendation.Rationale);
        }

        _logger.LogDebug("──── Cycle end ──────────────────────────────");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private List<Battery> BuildBatteries(List<BatteryReading> readings)
    {
        return readings
            .Select(reading =>
            {
                var bc = _config.Batteries.First(b => b.Id == reading.BatteryId);
                return new Battery
                {
                    Id             = bc.Id,
                    CapacityWh     = bc.CapacityWh,
                    MaxChargeRateW = bc.MaxChargeRateW,
                    MinPercent     = bc.MinPercent,
                    SoftMaxPercent = bc.SoftMaxPercent,
                    HardMaxPercent = bc.HardMaxPercent,
                    CurrentPercent = reading.SocPercent,   // ← valeur live HA
                    Priority       = bc.Priority
                };
            })
            .ToList();
    }

    private static TimeSpan ComputeBackoff(int errorCount, TimeSpan baseInterval)
    {
        double backoffSeconds = Math.Min(
            baseInterval.TotalSeconds * Math.Pow(2, errorCount - 1),
            MaxBackoffSeconds);

        return TimeSpan.FromSeconds(backoffSeconds);
    }
}
