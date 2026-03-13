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

    // ── État anti-oscillation surplus (Fix Bug #3) ────────────────────────────
    private bool _isChargingFromSurplus = false;
    private int _consecutiveChargeCycles = 0;

    // ── Rolling-window surplus smoother (Item 2 — P1 correction) ─────────────
    // Moyenne mobile sur les 3 derniers cycles pour filtrer les pics P1 bruités.
    // Un pic soudain (ex: chauffe-eau qui démarre) est lissé avant d'être distribué.
    private readonly Queue<double> _surplusWindow = new();
    private const int SurplusSmootherWindowSize = 3;

    // ── Bilan énergétique journalier (Feature 4) ──────────────────────────────
    // On retient la valeur ForecastTodayWh lue en début de journée pour calculer
    // DailySolarConsumedWh = ForecastToday(début) − ForecastRemainingToday(maintenant).
    private double? _forecastTodayWhAtStartOfDay = null;
    private int _lastDayOfYear = -1;

    // ── Hystérésis IdleCharge par batterie ────────────────────────────────────
    // Délégué à IdleChargeHysteresis pour être testable indépendamment.
    private readonly IdleChargeHysteresis _idleHysteresis;
    private readonly SolarDistribution.Core.Services.IStatusService _statusService;
    // ── Surplus anomaly detection (Item 9) ─────────────────────────────────
    private int _consecutiveSurplusAnomalies = 0;

    public SolarWorker(
        SolarConfig config,
        HomeAssistantDataReader reader,
        HomeAssistantCommandSender sender,
        SmartDistributionService smartService,
        WeatherCacheService weatherCache,
        IDistributionMLService mlService,
        ILogger<SolarWorker> logger,
        SolarDistribution.Core.Services.IStatusService statusService)
    {
        _config = config; _reader = reader; _sender = sender;
        _smartService = smartService; _weatherCache = weatherCache;
        _mlService = mlService; _logger = logger;
        _idleHysteresis = new IdleChargeHysteresis(logger);
        _statusService = statusService;
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
        _logger.LogInformation("  SurplusBuffer  : {Start}W start / {Stop}W stop / {Min} cycles min",
            _config.Polling.SurplusBufferW,
            _config.Polling.SurplusStopBufferW,
            _config.Polling.MinChargeDurationCycles);

        foreach (var b in _config.Batteries)
            _logger.LogInformation(
                "  [{Name}] maxRate={Rate}W idle={Idle}W idleStop={IdleStop}W softMax={SoftMax}% hysteresis={Hyst}%",
                b.Name, b.MaxChargeRateW, b.IdleChargeW, b.IdleStopBufferW, b.SoftMaxPercent, b.SocHysteresisPercent);

        // ── Item 2 — Warning si current_charge_power_entity absent ───────────
        // Sans cette entité le surplus brut P1 n'est pas corrigé : le Worker
        // sous-estime le disponible et envoie moins de puissance aux batteries.
        foreach (var b in _config.Batteries.Where(b => b.Entities.CurrentChargePowerEntity is null))
            _logger.LogWarning(
                "⚠️  Battery [{Name}]: 'current_charge_power_entity' is not configured. " +
                "Surplus correction will be skipped for this battery — the worker may under-charge " +
                "when batteries are already charging. Configure the entity in config.yaml for accurate P1 correction.",
                b.Name);

        _logger.LogInformation("  ML Status      : {Status}",
            (await _mlService.GetStatusAsync(stoppingToken)).IsAvailable ? "available" : "training...");

        bool haFcToday = _config.Solar.ForecastTodayEntity is not null;
        bool haFcTomorrow = _config.Solar.ForecastTomorrowEntity is not null;
        _logger.LogInformation(
            "  HA Forecasts   : today={Today}, tomorrow={Tomorrow}",
            haFcToday ? _config.Solar.ForecastTodayEntity : "not configured",
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

            var elapsed = DateTime.UtcNow - cycleStart;
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

        double rawSurplus = snapshot.SurplusW;
        double correctedSurplus = rawSurplus + currentBatteriesChargeW;

        // ── Item 2c — Warning si surplus négatif après correction ─────────────
        // Un surplus négatif après ajout de la charge batterie signale une
        // misconfiguration : soit le signe de current_charge_power_multiplier est
        // inversé, soit l'entité surplus_entity retourne de l'import au lieu de
        // l'export. On log et on clamp à 0 pour ne pas envoyer de commandes aberrantes.
        if (correctedSurplus < 0)
        {
            _logger.LogWarning(
                "⚠️  Surplus négatif après correction : P1={Raw:F0}W + batteries_now={Bat:F0}W = {Corrected:F0}W. " +
                "Vérifier le signe de 'current_charge_power_multiplier' ou le mode 'surplus_mode'. " +
                "Surplus forcé à 0 pour ce cycle.",
                rawSurplus, currentBatteriesChargeW, correctedSurplus);
            correctedSurplus = 0;
        }

        // ── Item 2b — Rolling-window surplus smoother (moyenne mobile 3 cycles) ─
        // Filtre les pics P1 soudains (latence ~10s, pic de consommation brusque)
        // avant de distribuer. La décision est prise sur la moyenne des N derniers
        // cycles plutôt que sur la valeur instantanée, ce qui évite d'envoyer
        // 3000W aux batteries lors d'un spike d'une seule lecture.
        _surplusWindow.Enqueue(correctedSurplus);
        if (_surplusWindow.Count > SurplusSmootherWindowSize)
            _surplusWindow.Dequeue();
        double smoothedSurplus = _surplusWindow.Average();

        if (_surplusWindow.Count == SurplusSmootherWindowSize
            && Math.Abs(smoothedSurplus - correctedSurplus) > 50)
        {
            _logger.LogDebug(
                "Surplus smoother: raw={Raw:F0}W corrected={Corr:F0}W smoothed={Smooth:F0}W " +
                "(window={Win} cycles) — delta={Delta:F0}W filtered",
                rawSurplus, correctedSurplus, smoothedSurplus,
                SurplusSmootherWindowSize, correctedSurplus - smoothedSurplus);
        }

        // ── Item 9 — Surplus anomaly detection (plausibility checks)
        //  · If a configured absolute plausibility threshold is exceeded
        //    OR if surplus > production when production is available,
        //    mark the cycle anomalous, skip sending any commands and log.
        //  · After N consecutive anomalies, create a persistent HA notification.
        if (_config.Solar.MaxPlausibleSurplusW.HasValue && smoothedSurplus > _config.Solar.MaxPlausibleSurplusW.Value)
        {
            _consecutiveSurplusAnomalies++;
            _logger.LogWarning(
                "⚠️  Surplus anomaly: smoothed={Smooth:F0}W > MaxPlausible={Max:F0}W — skipping cycle ({Count}/{Limit})",
                smoothedSurplus, _config.Solar.MaxPlausibleSurplusW.Value, _consecutiveSurplusAnomalies, _config.Polling.MaxConsecutiveAnomaliesBeforeAlert);

            if (_consecutiveSurplusAnomalies >= Math.Max(1, _config.Polling.MaxConsecutiveAnomaliesBeforeAlert))
            {
                string msg = $"Observed surplus {smoothedSurplus:F0}W exceeds configured MaxPlausibleSurplusW {_config.Solar.MaxPlausibleSurplusW.Value:F0}W for {_consecutiveSurplusAnomalies} consecutive cycles. Raw P1={rawSurplus:F0}W, production={(snapshot.ProductionW.HasValue ? snapshot.ProductionW.Value.ToString("F0") + "W" : "n/a")}. Please check the P1 meter and inverter sensors.";
                try { await _sender.CreatePersistentNotificationAsync("SolarWorker — surplus anomaly detected", msg, ct); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to send persistent notification for surplus anomaly"); }
                _consecutiveSurplusAnomalies = 0;
            }

            return;
        }

        if (snapshot.ProductionW.HasValue && smoothedSurplus > snapshot.ProductionW.Value + 1.0)
        {
            _consecutiveSurplusAnomalies++;
            _logger.LogWarning(
                "⚠️  Surplus anomaly: smoothed={Smooth:F0}W > production={Prod:F0}W — skipping cycle ({Count}/{Limit})",
                smoothedSurplus, snapshot.ProductionW.Value, _consecutiveSurplusAnomalies, _config.Polling.MaxConsecutiveAnomaliesBeforeAlert);

            if (_consecutiveSurplusAnomalies >= Math.Max(1, _config.Polling.MaxConsecutiveAnomaliesBeforeAlert))
            {
                string msg = $"Observed surplus {smoothedSurplus:F0}W greater than reported production {snapshot.ProductionW.Value:F0}W for {_consecutiveSurplusAnomalies} consecutive cycles. Raw P1={rawSurplus:F0}W. Please verify sensors and config.";
                try { await _sender.CreatePersistentNotificationAsync("SolarWorker — production/surplus mismatch", msg, ct); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to send persistent notification for production mismatch"); }
                _consecutiveSurplusAnomalies = 0;
            }

            return;
        }

        // clear anomaly counter on normal cycle
        _consecutiveSurplusAnomalies = 0;

        // ── Fix Bug #3 : double seuil anti-oscillation ─────────────────────────
        // Remplace l'ancien : effectiveSurplus = Max(0, corrected - bufferW)
        // qui causait des ON/OFF toutes les 5 min quand le soleil fluctue autour
        // du seuil (ex: nuages passagers). Voir ComputeEffectiveSurplus().
        // Le smoothedSurplus (moyenne mobile) est passé à la place du correctedSurplus
        // brut pour absorber les pics P1 avant l'hystérésis de démarrage/arrêt.
        double effectiveSurplus = ComputeEffectiveSurplus(smoothedSurplus);

        if (currentBatteriesChargeW > 0)
            _logger.LogInformation(
                "Surplus correction: P1={Raw:F0}W + batteries_now={BatNow:F0}W = real={Corrected:F0}W " +
                "smoothed={Smooth:F0}W − buffer={Buf:F0}W = effective={Eff:F0}W",
                rawSurplus, currentBatteriesChargeW, correctedSurplus,
                smoothedSurplus, _config.Polling.SurplusBufferW, effectiveSurplus);
        else if (_config.Polling.SurplusBufferW > 0 && rawSurplus > 0)
            _logger.LogDebug(
                "Surplus: raw={Raw:F0}W smoothed={Smooth:F0}W − buffer={Buf:F0}W = effective={Eff:F0}W " +
                "(no current_charge_power_entity configured — correction skipped)",
                rawSurplus, smoothedSurplus, _config.Polling.SurplusBufferW, effectiveSurplus);

        // BuildBatteries doit être appelé APRÈS effectiveSurplus pour que l'hystérésis
        // IdleCharge (IdleChargeHysteresis) puisse évaluer le seuil correct.
        var batteries = BuildBatteries(validReadings, effectiveSurplus);

        var wxSnapshot = _weatherCache.GetCurrent();

        // ── Bilan énergétique journalier : tracker ForecastTodayWh au lever du jour ──
        // On mémorise la première valeur de ForecastTodayWh de la journée pour calculer
        // DailySolarConsumedWh = forecastAtStartOfDay − forecastRemainingNow.
        int todayDoy = DateTime.Now.DayOfYear;
        if (todayDoy != _lastDayOfYear)
        {
            _forecastTodayWhAtStartOfDay = snapshot.ForecastTodayWh;
            _lastDayOfYear = todayDoy;
            if (_forecastTodayWhAtStartOfDay.HasValue)
                _logger.LogInformation(
                    "New day — ForecastTodayWh reference set to {V:F0}Wh for daily solar consumption tracking",
                    _forecastTodayWhAtStartOfDay.Value);
        }

        var result = await _smartService.DistributeAsync(
            surplusW: effectiveSurplus,
            batteries: batteries,
            latitude: _config.Location.Latitude,
            longitude: _config.Location.Longitude,
            weatherSnapshot: wxSnapshot,
            forecastTodayWh: snapshot.ForecastTodayWh,
            forecastTomorrowWh: snapshot.ForecastTomorrowWh,
            estimatedConsumptionNextHoursWh: snapshot.EstimatedConsumptionNextHoursWh,
            measuredConsumptionW: snapshot.ConsumptionW,
            forecastThisHourWh: snapshot.ForecastThisHourWh,
            forecastNextHourWh: snapshot.ForecastNextHourWh,
            forecastRemainingTodayWh: snapshot.ForecastRemainingTodayWh,
            forecastTodayWhAtStartOfDay: _forecastTodayWhAtStartOfDay,
            ct: ct);

        // ── Feature 10 — update live status for HA templates / API
        try
        {
            bool gridAllowed = result.Tariff?.GridChargeAllowed ?? false;
            DateTime? nextGridStart = null;
            if (result.Tariff is not null)
            {
                if (gridAllowed && result.Tariff.HoursRemainingInSlot.HasValue)
                    nextGridStart = DateTime.UtcNow.AddHours(result.Tariff.HoursRemainingInSlot.Value);
                else if (result.Tariff.HoursToNextFavorable.HasValue)
                    nextGridStart = DateTime.UtcNow.AddHours(result.Tariff.HoursToNextFavorable.Value);
            }

            string decision;
            if (result.Distribution.TotalAllocatedW > 0)
                decision = $"Charging {result.Distribution.TotalAllocatedW:F0}W solar" +
                    (result.Distribution.GridChargedW > 0 ? $", grid {result.Distribution.GridChargedW:F0}W" : "");
            else
                decision = $"No charge — unused {result.Distribution.UnusedSurplusW:F0}W";

            _statusService.Update(decision, effectiveSurplus, gridAllowed, nextGridStart);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to update live status");
        }

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

    private List<Battery> BuildBatteries(List<BatteryReading> readings, double effectiveSurplus)
    {
        return readings.Select(reading =>
        {
            var bc = _config.Batteries.First(b => b.Id == reading.BatteryId);
            double effectiveMaxRate = reading.MaxChargeRateW ?? bc.MaxChargeRateW;

            if (reading.MaxChargeRateW.HasValue && reading.MaxChargeRateW != bc.MaxChargeRateW)
                _logger.LogDebug(
                    "Battery {Id} ({Name}): MaxChargeRate live={Live:F0}W vs static={Static:F0}W — using live",
                    bc.Id, bc.Name, reading.MaxChargeRateW, bc.MaxChargeRateW);

            // ── Hystérésis IdleChargeW (Anti-oscillation IdleCharge) ─────────
            double effectiveIdleChargeW = _idleHysteresis.Compute(bc, effectiveSurplus);

            return new Battery
            {
                Id = bc.Id,
                CapacityWh = bc.CapacityWh,
                MaxChargeRateW = effectiveMaxRate,
                MinPercent = bc.MinPercent,
                SoftMaxPercent = bc.SoftMaxPercent,
                HardMaxPercent = bc.HardMaxPercent,
                CurrentPercent = reading.SocPercent,
                Priority = bc.Priority,
                HardwareMinChargeW = bc.HardwareMinChargeW,
                IdleChargeW = effectiveIdleChargeW,
                SocHysteresisPercent = bc.SocHysteresisPercent,
                EmergencyGridChargeBelowPercent = bc.EmergencyGridChargeBelowPercent,
                EmergencyGridChargeTargetPercent = bc.EmergencyGridChargeTargetPercent,
            };
        }).ToList();
    }

    /// <summary>
    /// Calcule le surplus effectif à distribuer avec hystérésis double-seuil (Fix Bug #3).
    ///
    /// Transitions :
    ///   Idle → Charging   : correctedSurplus &gt; SurplusBufferW (200W)
    ///   Charging → Idle   : correctedSurplus &lt; SurplusStopBufferW (80W)
    ///                       ET _consecutiveChargeCycles ≥ MinChargeDurationCycles
    ///   Zone [80W–200W]   : état maintenu (pas de transition)
    ///
    /// Résultat : plus d'ON/OFF toutes les 5 min lors des passages nuageux.
    /// </summary>
    private double ComputeEffectiveSurplus(double correctedSurplus)
    {
        double startThreshold = _config.Polling.SurplusBufferW;
        double stopThreshold = _config.Polling.SurplusStopBufferW;

        // Config invalide → fallback comportement original
        if (stopThreshold >= startThreshold)
            return Math.Max(0, correctedSurplus - startThreshold);

        if (!_isChargingFromSurplus)
        {
            if (correctedSurplus > startThreshold)
            {
                _isChargingFromSurplus = true;
                _consecutiveChargeCycles = 1;
                _logger.LogInformation(
                    "⚡ Solar charge STARTED — surplus {S:F0}W > start threshold {T:F0}W",
                    correctedSurplus, startThreshold);
            }
            return 0;
        }
        else
        {
            _consecutiveChargeCycles++;

            bool belowStop = correctedSurplus < stopThreshold;
            bool minDuration = _consecutiveChargeCycles >= _config.Polling.MinChargeDurationCycles;
            bool durationDisabled = _config.Polling.MinChargeDurationCycles <= 0;

            if (belowStop && (minDuration || durationDisabled))
            {
                _logger.LogInformation(
                    "🔌 Solar charge STOPPED — surplus {S:F0}W < stop threshold {T:F0}W (after {C} cycles)",
                    correctedSurplus, stopThreshold, _consecutiveChargeCycles);
                _isChargingFromSurplus = false;
                _consecutiveChargeCycles = 0;
                return 0;
            }

            if (belowStop)
                _logger.LogDebug(
                    "Solar charge maintained — surplus {S:F0}W below stop threshold but min duration " +
                    "not reached ({C}/{Min} cycles)",
                    correctedSurplus, _consecutiveChargeCycles, _config.Polling.MinChargeDurationCycles);

            return Math.Max(0, correctedSurplus - startThreshold);
        }
    }

    private static TimeSpan ComputeBackoff(int errorCount, TimeSpan baseInterval)
    {
        double backoffSeconds = Math.Min(
            baseInterval.TotalSeconds * Math.Pow(2, errorCount - 1),
            MaxBackoffSeconds);
        return TimeSpan.FromSeconds(backoffSeconds);
    }
}