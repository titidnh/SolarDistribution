using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SolarDistribution.Core.Models;
using SolarDistribution.Core.Services;
using SolarDistribution.Core.Services.ML;
using SolarDistribution.Worker.Configuration;
using SolarDistribution.Worker.HA;

namespace SolarDistribution.Worker.Services;

public class SolarWorker : BackgroundService
{
    private readonly SolarConfig _config;
    private readonly HomeAssistantDataReader _reader;
    private readonly HomeAssistantCommandSender _sender;
    private readonly SmartDistributionService _smartService;
    private readonly WeatherCacheService _weatherCache;
    private readonly IDistributionMLService _mlService;
    private readonly ILogger<SolarWorker> _logger;

    private int _consecutiveHaErrors = 0;
    private const int MaxBackoffSeconds = 300;

    public SolarWorker(
        SolarConfig config,
        HomeAssistantDataReader reader,
        HomeAssistantCommandSender sender,
        SmartDistributionService smartService,
        WeatherCacheService weatherCache,
        IDistributionMLService mlService,
        ILogger<SolarWorker> logger)
    {
        _config = config; _reader = reader; _sender = sender;
        _smartService = smartService; _weatherCache = weatherCache;
        _mlService = mlService; _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("╔══════════════════════════════════════════════╗");
        _logger.LogInformation("║   SolarDistribution Worker starting up       ║");
        _logger.LogInformation("╚══════════════════════════════════════════════╝");
        _logger.LogInformation("  Interval       : {Interval}s", _config.Polling.IntervalSeconds);
        _logger.LogInformation("  HA URL         : {Url}", _config.HomeAssistant.Url);
        _logger.LogInformation("  Batteries      : {Count}", _config.Batteries.Count);
        _logger.LogInformation("  DryRun         : {DryRun}", _config.Polling.DryRun);
        _logger.LogInformation("  SurplusBuffer  : {Buffer}W", _config.Polling.SurplusBufferW);

        foreach (var b in _config.Batteries)
            _logger.LogInformation(
                "  [{Name}] maxRate={Rate}W idle={Idle}W softMax={SoftMax}%",
                b.Name, b.MaxChargeRateW, b.IdleChargeW, b.SoftMaxPercent);

        _logger.LogInformation("  ML Status      : {Status}",
            (await _mlService.GetStatusAsync(stoppingToken)).IsAvailable ? "available" : "training...");

        bool haFcToday    = _config.Solar.ForecastTodayEntity    is not null;
        bool haFcTomorrow = _config.Solar.ForecastTomorrowEntity is not null;
        _logger.LogInformation(
            "  HA Forecasts   : today={Today}, tomorrow={Tomorrow}",
            haFcToday    ? _config.Solar.ForecastTodayEntity    : "not configured",
            haFcTomorrow ? _config.Solar.ForecastTomorrowEntity : "not configured");

        if (!haFcToday && !haFcTomorrow)
            _logger.LogWarning(
                "⚠️  HA solar forecasts not configured. Configure Solcast or Forecast.solar " +
                "entities in config.yaml for improved grid charge accuracy.");

        if (_config.Polling.DryRun)
            _logger.LogWarning("⚠️  DRY-RUN MODE — no commands will be sent to HA");

        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var cycleStart = DateTime.UtcNow;
            try
            {
                await RunCycleAsync(stoppingToken);
                _consecutiveHaErrors = 0;
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _consecutiveHaErrors++;
                _logger.LogError(ex, "Unhandled error in cycle #{Count} — will retry", _consecutiveHaErrors);
            }

            var elapsed     = DateTime.UtcNow - cycleStart;
            var baseInterval = TimeSpan.FromSeconds(_config.Polling.IntervalSeconds);
            var delay = _consecutiveHaErrors > 0
                ? ComputeBackoff(_consecutiveHaErrors, baseInterval)
                : baseInterval - elapsed;

            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, stoppingToken);
        }

        _logger.LogInformation("SolarWorker stopped gracefully");
    }

    private async Task RunCycleAsync(CancellationToken ct)
    {
        var snapshot = await _reader.ReadAllAsync(ct);

        if (snapshot is null)
        {
            _consecutiveHaErrors++;
            _logger.LogWarning("HA read failed (#{Count}) — skipping cycle", _consecutiveHaErrors);
            return;
        }

        var validReadings = snapshot.Batteries.Where(b => b.ReadSuccess).ToList();
        if (!validReadings.Any())
        {
            _logger.LogWarning("All battery reads failed — skipping cycle");
            return;
        }

        if (validReadings.Count < snapshot.Batteries.Count)
            _logger.LogWarning(
                "{Failed} battery(ies) could not be read — continuing with {Valid} valid batteries",
                snapshot.Batteries.Count - validReadings.Count, validReadings.Count);

        var batteries = BuildBatteries(validReadings);

        // ── Correction du surplus brut ─────────────────────────────────────────
        // Le surplus HA (P1 ou sensor) est déjà NET de la charge batterie actuelle.
        // Exemple : batteries chargent à 200W + 200W = 400W, P1 = -912W
        //   → surplus brut = 912W, mais 400W de ça sont déjà des batteries
        //   → surplus réel disponible pour redistribuer = 912 + 400 = 1312W
        //   → le Worker peut alors ordonner 1312W aux batteries
        //   → gain réel pour les batteries = 1312W (au lieu de 912W sans correction)
        //
        // Sans correction : order 912W → les batteries reçoivent 912W au lieu de 1312W
        // Avec correction : order 1312W → les batteries reçoivent 1312W (correct)
        //
        // La correction n'est appliquée que si current_charge_power_entity est configurée.
        double currentBatteriesChargeW = validReadings
            .Where(r => r.CurrentChargeW.HasValue)
            .Sum(r => r.CurrentChargeW!.Value);

        double rawSurplus       = snapshot.SurplusW;
        double correctedSurplus = rawSurplus + currentBatteriesChargeW;
        double effectiveSurplus = Math.Max(0, correctedSurplus - _config.Polling.SurplusBufferW);

        if (currentBatteriesChargeW > 0)
            _logger.LogInformation(
                "Surplus correction: P1={Raw:F0}W + batteries_now={BatNow:F0}W = real={Corrected:F0}W " +
                "− buffer={Buf:F0}W = effective={Eff:F0}W",
                rawSurplus, currentBatteriesChargeW, correctedSurplus,
                _config.Polling.SurplusBufferW, effectiveSurplus);
        else if (_config.Polling.SurplusBufferW > 0 && rawSurplus > 0)
            _logger.LogDebug(
                "Surplus: raw={Raw:F0}W − buffer={Buf:F0}W = effective={Eff:F0}W " +
                "(no current_charge_power_entity configured — correction skipped)",
                rawSurplus, _config.Polling.SurplusBufferW, effectiveSurplus);

        var wxSnapshot = _weatherCache.GetCurrent();

        var result = await _smartService.DistributeAsync(
            surplusW:           effectiveSurplus,
            batteries:          batteries,
            latitude:           _config.Location.Latitude,
            longitude:          _config.Location.Longitude,
            weatherSnapshot:    wxSnapshot,
            forecastTodayWh:    snapshot.ForecastTodayWh,
            forecastTomorrowWh: snapshot.ForecastTomorrowWh,
            ct: ct);

        _logger.LogInformation(
            "Distribution [{Engine}]: P1={Raw:F0}W batNow={BatNow:F0}W buf={Buf:F0}W eff={Eff:F0}W " +
            "→ alloc={Alloc:F0}W grid={Grid:F0}W unused={Unused:F0}W | session#{Id}",
            result.DecisionEngine,
            rawSurplus, currentBatteriesChargeW, _config.Polling.SurplusBufferW, effectiveSurplus,
            result.Distribution.TotalAllocatedW,
            result.Distribution.GridChargedW,
            result.Distribution.UnusedSurplusW,
            result.SessionId);

        int commandsSent = await _sender.SendCommandsAsync(result.Distribution.Allocations, ct);
        _logger.LogInformation("{Sent}/{Total} charge commands sent to HA",
            commandsSent, result.Distribution.Allocations.Count);

        foreach (var alloc in result.Distribution.Allocations)
        {
            var bc = _config.Batteries.FirstOrDefault(b => b.Id == alloc.BatteryId);
            _logger.LogInformation(
                "  [{Name}] {Prev:F1}% → {New:F1}% | {W:F1}W | {Reason}",
                bc?.Name ?? $"Battery {alloc.BatteryId}",
                alloc.PreviousPercent, alloc.NewPercent,
                alloc.AllocatedW, alloc.Reason);
        }

        if (result.MLRecommendation is not null)
            _logger.LogInformation(
                "  ML: softMax={SoftMax:F1}%, preventive={Prev:F1}%, conf={Conf:P0} | {Rationale}",
                result.MLRecommendation.RecommendedSoftMaxPercent,
                result.MLRecommendation.RecommendedPreventiveThreshold,
                result.MLRecommendation.ConfidenceScore,
                result.MLRecommendation.Rationale);
    }

    private List<Battery> BuildBatteries(List<BatteryReading> readings)
    {
        return readings.Select(reading =>
        {
            var bc = _config.Batteries.First(b => b.Id == reading.BatteryId);
            double effectiveMaxRate = reading.MaxChargeRateW ?? bc.MaxChargeRateW;

            if (reading.MaxChargeRateW.HasValue && reading.MaxChargeRateW != bc.MaxChargeRateW)
                _logger.LogDebug(
                    "Battery {Id} ({Name}): MaxChargeRate live={Live:F0}W vs static={Static:F0}W — using live",
                    bc.Id, bc.Name, reading.MaxChargeRateW, bc.MaxChargeRateW);

            return new Battery
            {
                Id             = bc.Id,
                CapacityWh     = bc.CapacityWh,
                MaxChargeRateW = effectiveMaxRate,
                MinPercent     = bc.MinPercent,
                SoftMaxPercent = bc.SoftMaxPercent,
                HardMaxPercent = bc.HardMaxPercent,
                CurrentPercent = reading.SocPercent,
                Priority       = bc.Priority,
                IdleChargeW    = bc.IdleChargeW,             // ← nouveau
                EmergencyGridChargeBelowPercent  = bc.EmergencyGridChargeBelowPercent,
                EmergencyGridChargeTargetPercent = bc.EmergencyGridChargeTargetPercent,
            };
        }).ToList();
    }

    private static TimeSpan ComputeBackoff(int errorCount, TimeSpan baseInterval)
    {
        double backoffSeconds = Math.Min(
            baseInterval.TotalSeconds * Math.Pow(2, errorCount - 1),
            MaxBackoffSeconds);
        return TimeSpan.FromSeconds(backoffSeconds);
    }
}
