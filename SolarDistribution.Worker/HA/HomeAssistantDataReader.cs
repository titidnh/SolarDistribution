using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Text.Json;
using SolarDistribution.Core.Data.Entities;
using SolarDistribution.Core.Repositories;
using SolarDistribution.Core.Services;
using SolarDistribution.Worker.Configuration;

namespace SolarDistribution.Worker.HA;

/// <summary>
/// Reads all required values from HA in a single cycle.
/// </summary>
public record HaSnapshot(
    double SurplusW,
    double? ProductionW,
    double? ConsumptionW,
    /// <summary>Estimated solar production today (Wh) — from HA, optional.</summary>
    double? ForecastTodayWh,
    /// <summary>Estimated solar production tomorrow (Wh) — from HA, optional.</summary>
    double? ForecastTomorrowWh,
    /// <summary>Estimated solar production THIS HOUR (Wh) — Solcast intraday, optional.</summary>
    double? ForecastThisHourWh,
    /// <summary>Estimated solar production NEXT HOUR (Wh) — Solcast intraday, optional.</summary>
    double? ForecastNextHourWh,
    /// <summary>Estimated REMAINING solar production TODAY (Wh) — for daily energy balance.</summary>
    double? ForecastRemainingTodayWh,
    /// <summary>
    /// Zone/device consumption read from HA (W).
    /// Key = HA entity_id, value = read power (W).
    /// Empty if ZoneConsumptionEntities is not configured or ConsumptionEntity is already present.
    /// </summary>
    Dictionary<string, double> ZoneConsumptionW,
    /// <summary>
    /// Estimated consumption over the next hours (Wh) — rolling average × horizon.
    /// Computed from average of last N cycles (MariaDB) × ConsumptionProjectionHours.
    /// Null if insufficient data or entities not configured.
    /// </summary>
    double? EstimatedConsumptionNextHoursWh,
    List<BatteryReading> Batteries,
    DateTime ReadAt
);

public record BatteryReading(
    int BatteryId,
    string Name,
    double SocPercent,
    double? MaxChargeRateW,
    /// <summary>
    /// Current real charge power read from HA (W).
    /// Null if CurrentChargePowerEntity is not configured or read failed.
    /// Used to correct raw surplus: real_surplus = HA_surplus + Σ CurrentChargeW.
    /// </summary>
    double? CurrentChargeW,
    bool ReadSuccess,
    /// <summary>
    /// ML-8: Number of charge cycles read from CycleCountEntity in HA.
    /// 0 if entity is not configured or read failed.
    /// </summary>
    int CycleCount = 0
);

public class HomeAssistantDataReader
{
    private readonly IHomeAssistantClient _client;
    private readonly SolarConfig _config;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TariffEngine _tariffEngine;
    private readonly IHeatingSourceSelectorService _heatingSourceSelector;
    private readonly IHeatingPreheatMlService _heatingPreheatMl;
    private readonly ILogger<HomeAssistantDataReader> _logger;
    private DateTime _lastHeatingSamplePersistedAtUtc = DateTime.MinValue;
    private DateTime _lastLearningRefreshUtc = DateTime.MinValue;
    // Volatile reference swap: writer replaces the entire dictionary atomically.
    // Readers always see a consistent snapshot (no torn reads).
    private volatile Dictionary<string, double> _learnedSpeedDegPerHour = new(StringComparer.OrdinalIgnoreCase);
    private volatile Dictionary<string, int> _learnedSpeedSampleCount = new(StringComparer.OrdinalIgnoreCase);

    public HomeAssistantDataReader(
        IHomeAssistantClient client,
        SolarConfig config,
        IServiceScopeFactory scopeFactory,
        TariffEngine tariffEngine,
        IHeatingSourceSelectorService heatingSourceSelector,
        IHeatingPreheatMlService heatingPreheatMl,
        ILogger<HomeAssistantDataReader> logger)
    {
        _client = client;
        _config = config;
        _scopeFactory = scopeFactory;
        _tariffEngine = tariffEngine;
        _heatingSourceSelector = heatingSourceSelector;
        _heatingPreheatMl = heatingPreheatMl;
        _logger = logger;
    }

    public async Task<HaSnapshot?> ReadAllAsync(CancellationToken ct = default)
    {
        // ── Solar surplus ────────────────────────────────────────────────────
        double? rawSurplus = await _client.GetNumericStateAsync(_config.Solar.SurplusEntity, ct);

        if (rawSurplus is null)
        {
            _logger.LogError(
                "Cannot read surplus entity '{Entity}' — skipping this cycle",
                _config.Solar.SurplusEntity);
            return null;
        }

        double surplusW = ComputeSurplus(rawSurplus.Value, _config.Solar.SurplusMode);

        _logger.LogDebug(
            "Surplus: raw={Raw:F0}W, mode={Mode}, effective={Surplus:F0}W",
            rawSurplus.Value, _config.Solar.SurplusMode, surplusW);

        // ── Optional production + consumption ────────────────────────────────
        double? productionW = null;
        double? consumptionW = null;

        if (_config.Solar.ProductionEntity is not null)
            productionW = await _client.GetNumericStateAsync(_config.Solar.ProductionEntity, ct);

        if (_config.Solar.ConsumptionEntity is not null)
            consumptionW = await _client.GetNumericStateAsync(_config.Solar.ConsumptionEntity, ct);

        // ── Zone consumption (optional — complement or alternative to ConsumptionEntity) ──
        var zoneConsumptionW = new Dictionary<string, double>();

        if (consumptionW is null && _config.Solar.ZoneConsumptionEntities.Count > 0)
        {
            double zoneTotal = 0;
            foreach (var entity in _config.Solar.ZoneConsumptionEntities)
            {
                double? zoneW = await _client.GetNumericStateAsync(entity, ct);
                if (zoneW is not null)
                {
                    zoneConsumptionW[entity] = zoneW.Value;
                    zoneTotal += zoneW.Value;
                    _logger.LogDebug("Zone consumption [{Entity}]: {W:F0}W", entity, zoneW.Value);
                }
                else
                {
                    _logger.LogDebug("Zone consumption entity '{Entity}' unreadable — skipped", entity);
                }
            }

            if (zoneConsumptionW.Count > 0)
            {
                consumptionW = zoneTotal;
                _logger.LogDebug(
                    "Zone consumption total: {Total:F0}W from {Count}/{Total2} configured entities",
                    zoneTotal, zoneConsumptionW.Count, _config.Solar.ZoneConsumptionEntities.Count);
            }
        }

        // ── Consumption rolling average + projection ─────────────────────────
        double? estimatedConsumptionNextHoursWh = null;

        int rollingWindow = _config.Solar.ConsumptionRollingWindowCycles;
        double projectionHours = _config.Solar.ConsumptionProjectionHours;

        if (rollingWindow > 0 && projectionHours > 0)
        {
            double? rollingAvgW;
            await using (var scope = _scopeFactory.CreateAsyncScope())
            {
                var repo = scope.ServiceProvider.GetRequiredService<IDistributionRepository>();
                rollingAvgW = await repo.GetRecentConsumptionAvgWAsync(rollingWindow, ct);
            }

            if (rollingAvgW is not null)
            {
                // CALC-02 fix: apply time-of-day weighting to the rolling average.
                // Standard residential consumption peaks in the morning (7-9) and
                // evening (17-21). The rolling average ignores these patterns,
                // leading to underestimation during peaks and overestimation during
                // off-peak hours. The multiplier adjusts the flat average to better
                // reflect expected consumption in the upcoming projection window.
                double todMultiplier = GetTimeOfDayConsumptionMultiplier(DateTime.Now.Hour);
                estimatedConsumptionNextHoursWh = rollingAvgW.Value * todMultiplier * projectionHours;
                _logger.LogDebug(
                    "Load forecast: rolling avg={Avg:F0}W (last {N} cycles) × ToD={Tod:F2} × {H:F1}h = {Wh:F0}Wh estimated consumption",
                    rollingAvgW.Value, rollingWindow, todMultiplier, projectionHours, estimatedConsumptionNextHoursWh);
            }
            else if (consumptionW is not null)
            {
                estimatedConsumptionNextHoursWh = consumptionW.Value * projectionHours;
                _logger.LogDebug(
                    "Load forecast: no DB history yet — using live consumption={W:F0}W × {H:F1}h = {Wh:F0}Wh",
                    consumptionW.Value, projectionHours, estimatedConsumptionNextHoursWh);
            }
        }

        // ── HA solar forecasts (optional — strongly recommended) ─────────────
        double? forecastTodayWh = null;
        double? forecastTomorrowWh = null;
        double? forecastThisHourWh = null;
        double? forecastNextHourWh = null;
        double? forecastRemainingTodayWh = null;

        if (_config.Solar.ForecastTodayEntity is not null)
        {
            var rawToday = await _client.GetNumericStateAsync(_config.Solar.ForecastTodayEntity, ct);
            if (rawToday is not null)
            {
                double mult = IsKwh(_config.Solar.ForecastTodayUnit) ? 1000.0 : 1.0;
                forecastTodayWh = rawToday.Value * mult;
                _logger.LogDebug(
                    "Solar forecast today: {V:F0} Wh (raw={Raw:F3} {Unit})",
                    forecastTodayWh, rawToday, _config.Solar.ForecastTodayUnit);
            }
            else
                _logger.LogDebug(
                    "Solar forecast today entity '{Entity}' unreadable — will use Open-Meteo fallback",
                    _config.Solar.ForecastTodayEntity);
        }

        if (_config.Solar.ForecastTomorrowEntity is not null)
        {
            var rawTomorrow = await _client.GetNumericStateAsync(_config.Solar.ForecastTomorrowEntity, ct);
            if (rawTomorrow is not null)
            {
                double mult = IsKwh(_config.Solar.ForecastTomorrowUnit) ? 1000.0 : 1.0;
                forecastTomorrowWh = rawTomorrow.Value * mult;
                _logger.LogDebug(
                    "Solar forecast tomorrow: {V:F0} Wh (raw={Raw:F3} {Unit})",
                    forecastTomorrowWh, rawTomorrow, _config.Solar.ForecastTomorrowUnit);
            }
            else
                _logger.LogDebug(
                    "Solar forecast tomorrow entity '{Entity}' unreadable — will use Open-Meteo fallback",
                    _config.Solar.ForecastTomorrowEntity);
        }

        // ── Intraday Solcast forecasts ───────────────────────────────────────
        if (_config.Solar.ForecastThisHourEntity is not null)
        {
            var rawThisHour = await _client.GetNumericStateAsync(_config.Solar.ForecastThisHourEntity, ct);
            if (rawThisHour is not null)
            {
                double mult = IsKwh(_config.Solar.ForecastThisHourUnit) ? 1000.0 : 1.0;
                forecastThisHourWh = rawThisHour.Value * mult;
            }
            _logger.LogDebug(
                "Solcast this_hour: {V} Wh (unit={Unit})",
                forecastThisHourWh?.ToString("F0") ?? "n/a", _config.Solar.ForecastThisHourUnit);
        }

        if (_config.Solar.ForecastNextHourEntity is not null)
        {
            var rawNextHour = await _client.GetNumericStateAsync(_config.Solar.ForecastNextHourEntity, ct);
            if (rawNextHour is not null)
            {
                double mult = IsKwh(_config.Solar.ForecastNextHourUnit) ? 1000.0 : 1.0;
                forecastNextHourWh = rawNextHour.Value * mult;
            }
            _logger.LogDebug(
                "Solcast next_hour: {V} Wh (unit={Unit})",
                forecastNextHourWh?.ToString("F0") ?? "n/a", _config.Solar.ForecastNextHourUnit);
        }

        if (_config.Solar.ForecastRemainingTodayEntity is not null)
        {
            var rawRemaining = await _client.GetNumericStateAsync(_config.Solar.ForecastRemainingTodayEntity, ct);
            if (rawRemaining is not null)
            {
                double mult = IsKwh(_config.Solar.ForecastRemainingTodayUnit) ? 1000.0 : 1.0;
                forecastRemainingTodayWh = rawRemaining.Value * mult;
                _logger.LogDebug(
                    "Solcast remaining_today: {V:F0} Wh (raw={Raw:F3} {Unit})",
                    forecastRemainingTodayWh, rawRemaining, _config.Solar.ForecastRemainingTodayUnit);
            }
            else
                _logger.LogDebug("Solcast remaining_today: n/a");
        }

        // ── SOC + MaxChargeRate for each battery ─────────────────────────────
        var readings = new List<BatteryReading>();

        foreach (var b in _config.Batteries)
        {
            double? soc = await _client.GetNumericStateAsync(b.Entities.Soc, ct);

            if (soc is null)
            {
                _logger.LogWarning(
                    "Cannot read SOC for battery {Id} ({Name}) entity '{Entity}'",
                    b.Id, b.Name, b.Entities.Soc);

                readings.Add(new BatteryReading(b.Id, b.Name, 0, null, null, ReadSuccess: false));
                continue;
            }

            double? maxChargeRateW = null;

            if (b.Entities.MaxChargeRateEntity is not null)
            {
                double? rawRate = await _client.GetNumericStateAsync(
                    b.Entities.MaxChargeRateEntity, ct);

                if (rawRate is not null)
                {
                    maxChargeRateW = rawRate.Value * b.Entities.MaxRateReadMultiplier;
                    _logger.LogDebug(
                        "Battery {Id} ({Name}): live MaxChargeRate = {Rate:F0}W (raw={Raw:F2}, ×{Mult})",
                        b.Id, b.Name, maxChargeRateW, rawRate, b.Entities.MaxRateReadMultiplier);
                }
                else
                {
                    _logger.LogWarning(
                        "Battery {Id} ({Name}): cannot read MaxChargeRateEntity '{Entity}' — fallback to {Static}W",
                        b.Id, b.Name, b.Entities.MaxChargeRateEntity, b.MaxChargeRateW);
                }
            }

            double? currentChargeW = null;

            if (b.Entities.CurrentChargePowerEntity is not null)
            {
                double? rawCharge = await _client.GetNumericStateAsync(
                    b.Entities.CurrentChargePowerEntity, ct);

                if (rawCharge is not null)
                {
                    // Allow negative values (discharge) — needed for surplus correction.
                    // Positive = charging, negative = discharging.
                    currentChargeW = rawCharge.Value * b.Entities.CurrentChargePowerMultiplier;
                    _logger.LogDebug(
                        "Battery {Id} ({Name}): current charge = {W:F0}W (raw={Raw:F2})",
                        b.Id, b.Name, currentChargeW, rawCharge);
                }
                else
                {
                    _logger.LogDebug(
                        "Battery {Id} ({Name}): cannot read CurrentChargePowerEntity '{Entity}' — no surplus correction",
                        b.Id, b.Name, b.Entities.CurrentChargePowerEntity);
                }
            }

            int cycleCount = 0;

            if (b.Entities.CycleCountEntity is not null)
            {
                double? rawCycles = await _client.GetNumericStateAsync(
                    b.Entities.CycleCountEntity, ct);

                if (rawCycles is not null)
                {
                    cycleCount = (int)Math.Max(0, Math.Round(rawCycles.Value));
                    _logger.LogDebug(
                        "Battery {Id} ({Name}): cycle count = {Cycles} (raw={Raw:F1})",
                        b.Id, b.Name, cycleCount, rawCycles);
                }
                else
                {
                    _logger.LogDebug(
                        "Battery {Id} ({Name}): cannot read CycleCountEntity '{Entity}' — no lifecycle weighting",
                        b.Id, b.Name, b.Entities.CycleCountEntity);
                }
            }

            readings.Add(new BatteryReading(b.Id, b.Name, soc.Value, maxChargeRateW, currentChargeW,
                ReadSuccess: true, CycleCount: cycleCount));
        }

        _logger.LogInformation(
            "HA snapshot: surplus={Surplus}W, prod={Prod}W, cons={Cons}W, " +
            "fcToday={FcToday}Wh, fcTomorrow={FcTomorrow}Wh, " +
            "fcThisH={FcThis}Wh, fcNextH={FcNext}Wh, fcRemaining={FcRem}Wh, " +
            "estConsNext={EstCons}Wh | batteries=[{Batteries}]",
            surplusW,
            productionW?.ToString("F0") ?? "n/a",
            consumptionW?.ToString("F0") ?? "n/a",
            forecastTodayWh?.ToString("F0") ?? "n/a",
            forecastTomorrowWh?.ToString("F0") ?? "n/a",
            forecastThisHourWh?.ToString("F0") ?? "n/a",
            forecastNextHourWh?.ToString("F0") ?? "n/a",
            forecastRemainingTodayWh?.ToString("F0") ?? "n/a",
            estimatedConsumptionNextHoursWh?.ToString("F0") ?? "n/a",
            string.Join(", ", readings.Select(r =>
                r.ReadSuccess
                    ? $"{r.Name}:{r.SocPercent:F1}%{(r.MaxChargeRateW.HasValue ? $"/{r.MaxChargeRateW:F0}W" : "")}{(r.CurrentChargeW.HasValue ? $" now={r.CurrentChargeW:F0}W" : "")}"
                    : $"{r.Name}:ERR")));

        // ── Dynamic spot price (Feature 5) ───────────────────────────────────
        if (_config.Tariff.CurrentPriceEntity is not null)
        {
            double? spotPrice = await _client.GetNumericStateAsync(
                _config.Tariff.CurrentPriceEntity, ct);

            if (spotPrice is not null)
            {
                _tariffEngine.UpdateSpotPrice(spotPrice.Value);
                _logger.LogDebug(
                    "Spot price read: {Price:F4} €/kWh (from HA entity '{Entity}')",
                    spotPrice.Value, _config.Tariff.CurrentPriceEntity);
            }
            else
            {
                _tariffEngine.UpdateSpotPrice(null);
                _logger.LogWarning(
                    "⚠️  Spot price entity '{Entity}' unreadable — falling back to YAML tariff slots for this cycle.",
                    _config.Tariff.CurrentPriceEntity);
            }
        }

        await ReadAndPersistHeatingSampleAsync(ct);

        return new HaSnapshot(surplusW, productionW, consumptionW,
            forecastTodayWh, forecastTomorrowWh,
            forecastThisHourWh, forecastNextHourWh, forecastRemainingTodayWh,
            zoneConsumptionW, estimatedConsumptionNextHoursWh,
            readings, DateTime.UtcNow);
    }

    private async Task ReadAndPersistHeatingSampleAsync(CancellationToken ct)
    {
        if (!_config.Heating.Enabled)
            return;

        var nowUtc = DateTime.UtcNow;
        int samplingSeconds = Math.Max(60, _config.Heating.SamplingIntervalSeconds);
        if (_lastHeatingSamplePersistedAtUtc != DateTime.MinValue
            && (nowUtc - _lastHeatingSamplePersistedAtUtc).TotalSeconds < samplingSeconds)
        {
            return;
        }

        var thermostatStates = await GetThermostatStatesAsync(ct);

        var aggregationMode = (_config.Heating.ZoneAggregationMode ?? "average").Trim().ToLowerInvariant();

        double? indoorTempC = await ReadAggregatedNumericAsync(
            _config.Heating.IndoorTemperatureEntity,
            _config.Heating.IndoorTemperatureEntities,
            thermostatStates,
            attributeName: "current_temperature",
            aggregationMode,
            ct);

        double? targetTempC = await ReadAggregatedNumericAsync(
            _config.Heating.TargetTemperatureEntity,
            _config.Heating.TargetTemperatureEntities,
            thermostatStates,
            attributeName: "temperature",
            aggregationMode,
            ct);

        double? outdoorTempC = await ReadOptionalNumericAsync(_config.Heating.OutdoorTemperatureEntity, ct);
        double? outdoorHumidityPct = await ReadOptionalNumericAsync(_config.Heating.OutdoorHumidityEntity, ct);
        double? windSpeedMs = await ReadOptionalNumericAsync(_config.Heating.WindSpeedEntity, ct);
        double? solarIrradianceWm2 = await ReadOptionalNumericAsync(_config.Heating.SolarIrradianceEntity, ct);

        double? forecastH1 = await ReadOptionalNumericAsync(_config.Heating.ForecastOutdoorTempNextHourEntity, ct);
        double? forecastH2 = await ReadOptionalNumericAsync(_config.Heating.ForecastOutdoorTempNext2HoursEntity, ct);
        double? forecastH3 = await ReadOptionalNumericAsync(_config.Heating.ForecastOutdoorTempNext3HoursEntity, ct);

        string? thermostatMode = await ReadTextFromEitherAsync(
            _config.Heating.HvacModeEntity,
            thermostatStates,
            attributeName: "hvac_mode",
            ct);

        string? hvacAction = await ReadTextFromEitherAsync(
            _config.Heating.HvacActionEntity,
            thermostatStates,
            attributeName: "hvac_action",
            ct);

        if (string.IsNullOrWhiteSpace(hvacAction) && _config.Heating.HvacActionEntities.Count > 0)
        {
            hvacAction = await ReadTextFromEntitiesAsync(_config.Heating.HvacActionEntities, ct);
        }

        bool? isHeatingOn = InterpretHeatingOn(hvacAction);
        string? presenceMode = await ReadOptionalTextAsync(_config.Heating.PresenceModeEntity, ct);
        bool? isNearHome = await ReadOptionalBoolAsync(_config.Heating.NearHomeEntity, ct);
        bool? isOffPeak = await ReadOptionalBoolAsync(_config.Heating.IsOffPeakEntity, ct);
        double? currentPricePerKwh = await ReadOptionalNumericAsync(_config.Heating.CurrentPriceEntity, ct);

        // Energy prices: electricity comes from current_price_entity (existing),
        // gas can come from a dedicated HA sensor or fixed config value.
        double gasPricePerKwh = _config.Heating.Gas.GasPricePerKwh;
        if (!string.IsNullOrWhiteSpace(_config.Heating.Gas.GasPriceEntity))
        {
            var gasPriceFromHa = await ReadOptionalNumericAsync(_config.Heating.Gas.GasPriceEntity, ct);
            if (gasPriceFromHa.HasValue && gasPriceFromHa.Value > 0)
                gasPricePerKwh = gasPriceFromHa.Value;
        }

        var heatingSources = _config.Heating.Sources
            .Where(s => !string.IsNullOrWhiteSpace(s.Name))
            .Select(s => new HeatingSourceDefinition(
                Name: s.Name,
                Type: s.Type,
                Enabled: s.Enabled,
                Priority: s.Priority,
                CopRefTempC: s.CopRefTempC,
                CopAtRefTemp: s.CopAtRefTemp,
                CopSlopePerDegC: s.CopSlopePerDegC,
                CopMinValue: s.CopMinValue,
                BoilerEfficiency: s.BoilerEfficiency))
            .ToList();

        var sourceSelection = _heatingSourceSelector.SelectOptimalSource(
            heatingSources,
            outdoorTempC ?? 7.0,
            currentPricePerKwh ?? 0.25,
            gasPricePerKwh);

        sourceSelection ??= new HeatingSourceCostResult(
            BestSourceName: null,
            BestSourceType: null,
            BestCop: 0,
            BestCostPerKwhThermal: 0,
            AllSources: Array.Empty<HeatingSourceBreakdown>(),
            Reason: "No source selection");

        await using var scope = _scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDistributionRepository>();

        await RefreshLearnedSourceSpeedAsync(repo, nowUtc, ct);

        var mlContext = new HeatingOrchestratorContext(
            CurrentTempC: indoorTempC ?? targetTempC ?? _config.Heating.ComfortSetpointC,
            TargetTempC: targetTempC ?? _config.Heating.ComfortSetpointC,
            OutsideTempC: outdoorTempC ?? 7.0,
            NowLocal: DateTime.Now,
            DesiredReadyAtLocal: null,
            PresenceMode: ParsePresenceMode(presenceMode),
            IsOffPeak: isOffPeak == true,
            CurrentPricePerKwh: currentPricePerKwh ?? 0.25,
            IsWeatherWarmingSoon: (forecastH3 ?? outdoorTempC ?? 7.0) > (forecastH1 ?? outdoorTempC ?? 7.0));

        var mlEstimate = await _heatingPreheatMl.EstimateAsync(mlContext, ct);
        var selected = ApplyAdvancedSourceSwitching(
            sourceSelection,
            nowLocal: DateTime.Now,
            currentTempC: indoorTempC,
            targetTempC: targetTempC,
            mlEstimate: mlEstimate,
            minLearningSamples: _config.Heating.ComfortConstraints.MinSamplesForLearning);

        var selectedBreakdown = sourceSelection.AllSources
            .FirstOrDefault(x => string.Equals(x.Name, selected.BestSourceName, StringComparison.OrdinalIgnoreCase));
        selectedBreakdown ??= sourceSelection.AllSources.FirstOrDefault();

        _logger.LogDebug(
            "Heating source selected: {Name}/{Type} at {Cost:F4} €/kWh_th (reason: {Reason}; ML p90={P90:F1}m)",
            selected.BestSourceName ?? "n/a",
            selected.BestSourceType ?? "n/a",
            selected.BestCostPerKwhThermal,
            selected.Reason,
            mlEstimate.P90Minutes);

        double? gasConsumptionM3h = null;
        if (string.Equals(selected.BestSourceType, "gas", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(selected.BestSourceName))
        {
            var selectedGasSource = _config.Heating.Sources.FirstOrDefault(s =>
                string.Equals(s.Name, selected.BestSourceName, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(selectedGasSource?.GasConsumptionEntity))
                gasConsumptionM3h = await ReadOptionalNumericAsync(selectedGasSource.GasConsumptionEntity, ct);
        }

        var forecastJson = JsonSerializer.Serialize(new
        {
            next1h = forecastH1,
            next2h = forecastH2,
            next3h = forecastH3
        });

        var sample = new HeatingSample
        {
            SampledAtUtc = nowUtc,
            IndoorTempC = indoorTempC,
            TargetTempC = targetTempC,
            OutdoorTempC = outdoorTempC,
            OutdoorHumidityPct = outdoorHumidityPct,
            WindSpeedMs = windSpeedMs,
            SolarIrradianceWm2 = solarIrradianceWm2,
            ForecastOutdoorTempNextHoursJson = forecastJson,
            ThermostatMode = NormalizeText(thermostatMode),
            HvacAction = NormalizeText(hvacAction),
            IsHeatingOn = isHeatingOn,
            PresenceMode = NormalizeText(presenceMode),
            IsNearHome = isNearHome,
            IsOffPeak = isOffPeak,
            CurrentPricePerKwh = currentPricePerKwh,

            ActiveSourceName = NormalizeText(selected.BestSourceName),
            ActiveSourceType = NormalizeText(selected.BestSourceType),
            GasConsumptionM3h = gasConsumptionM3h,
            HeatPumpCop = string.Equals(selected.BestSourceType, "heat_pump", StringComparison.OrdinalIgnoreCase)
                ? selected.BestCop
                : null,
            EstimatedCostPerKwhThermal = (selectedBreakdown?.CostPerKwhThermal ?? selected.BestCostPerKwhThermal) > 0
                ? (selectedBreakdown?.CostPerKwhThermal ?? selected.BestCostPerKwhThermal)
                : null
        };

        await repo.SaveHeatingSampleAsync(sample, ct);

        // Optional gas meter persistence from HA cumulative meter.
        if (_config.Heating.Gas.Enabled
            && string.Equals(_config.Heating.Gas.MeterMode, "ha_entity", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(_config.Heating.Gas.MeterEntity))
        {
            var meterReadingM3 = await ReadOptionalNumericAsync(_config.Heating.Gas.MeterEntity, ct);
            if (meterReadingM3.HasValue)
            {
                var lastMeter = await repo.GetLastGasMeterReadingBeforeAsync(nowUtc, ct);
                bool changed = lastMeter is null || Math.Abs(lastMeter.ReadingM3 - meterReadingM3.Value) > 0.0001;
                if (changed)
                {
                    await repo.SaveGasMeterReadingAsync(new GasMeterReading
                    {
                        ReadAtUtc = nowUtc,
                        ReadingM3 = meterReadingM3.Value,
                        Source = "ha_auto",
                        Note = null
                    }, ct);
                }
            }
        }

        _lastHeatingSamplePersistedAtUtc = nowUtc;

        _logger.LogDebug(
            "Heating sample persisted: indoor={Indoor}C target={Target}C outside={Outside}C mode={Mode} action={Action} presence={Presence} offpeak={OffPeak} price={Price}",
            sample.IndoorTempC?.ToString("F1") ?? "n/a",
            sample.TargetTempC?.ToString("F1") ?? "n/a",
            sample.OutdoorTempC?.ToString("F1") ?? "n/a",
            sample.ThermostatMode ?? "n/a",
            sample.HvacAction ?? "n/a",
            sample.PresenceMode ?? "n/a",
            sample.IsOffPeak?.ToString() ?? "n/a",
            sample.CurrentPricePerKwh?.ToString("F4") ?? "n/a");
    }

    private async Task<List<HaState>> GetThermostatStatesAsync(CancellationToken ct)
    {
        var states = new List<HaState>();

        if (!string.IsNullOrWhiteSpace(_config.Heating.ThermostatEntity))
        {
            var single = await _client.GetStateAsync(_config.Heating.ThermostatEntity, ct);
            if (single is not null)
                states.Add(single);
        }

        foreach (var entity in _config.Heating.ThermostatEntities)
        {
            if (string.IsNullOrWhiteSpace(entity))
                continue;

            var state = await _client.GetStateAsync(entity, ct);
            if (state is not null && states.All(s => !string.Equals(s.EntityId, state.EntityId, StringComparison.OrdinalIgnoreCase)))
            {
                states.Add(state);
            }
        }

        return states;
    }

    private async Task<double?> ReadOptionalNumericAsync(string? entityId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(entityId)) return null;
        return await _client.GetNumericStateAsync(entityId, ct);
    }

    private async Task<string?> ReadOptionalTextAsync(string? entityId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(entityId)) return null;
        var state = await _client.GetStateAsync(entityId, ct);
        return NormalizeText(state?.State);
    }

    private async Task<bool?> ReadOptionalBoolAsync(string? entityId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(entityId)) return null;

        var state = await _client.GetStateAsync(entityId, ct);
        if (state is null) return null;

        var txt = state.State.Trim();
        if (bool.TryParse(txt, out var asBool)) return asBool;
        if (double.TryParse(txt, NumberStyles.Float, CultureInfo.InvariantCulture, out var asNum)) return asNum > 0;

        return txt.Equals("on", StringComparison.OrdinalIgnoreCase)
            || txt.Equals("home", StringComparison.OrdinalIgnoreCase)
            || txt.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<double?> ReadAggregatedNumericAsync(
        string? primaryEntity,
        IReadOnlyList<string> multiEntities,
        IReadOnlyList<HaState> fallbackStates,
        string attributeName,
        string aggregationMode,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(primaryEntity))
        {
            var direct = await _client.GetNumericStateAsync(primaryEntity, ct);
            if (direct is not null)
                return direct;
        }

        if (multiEntities.Count > 0)
        {
            var values = new List<double>();
            foreach (var entity in multiEntities)
            {
                if (string.IsNullOrWhiteSpace(entity)) continue;
                var value = await _client.GetNumericStateAsync(entity, ct);
                if (value is not null) values.Add(value.Value);
            }

            var aggregated = Aggregate(values, aggregationMode);
            if (aggregated is not null)
                return aggregated;
        }

        var fallbackValues = fallbackStates
            .Select(state => TryReadNumericAttribute(state, attributeName))
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .ToList();

        return Aggregate(fallbackValues, aggregationMode);
    }

    private async Task<string?> ReadTextFromEitherAsync(
        string? primaryEntity,
        IReadOnlyList<HaState> fallbackStates,
        string attributeName,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(primaryEntity))
        {
            var txt = await ReadOptionalTextAsync(primaryEntity, ct);
            if (!string.IsNullOrWhiteSpace(txt))
                return txt;
        }

        foreach (var state in fallbackStates)
        {
            if (state.Attributes.TryGetProperty(attributeName, out var attr))
            {
                var txt = NormalizeText(attr.ToString());
                if (!string.IsNullOrWhiteSpace(txt)) return txt;
            }

            var stateValue = NormalizeText(state.State);
            if (!string.IsNullOrWhiteSpace(stateValue))
                return stateValue;
        }

        return null;
    }

    private async Task<string?> ReadTextFromEntitiesAsync(IReadOnlyList<string> entities, CancellationToken ct)
    {
        var values = new List<string>();
        foreach (var entity in entities)
        {
            if (string.IsNullOrWhiteSpace(entity)) continue;
            var txt = await ReadOptionalTextAsync(entity, ct);
            if (!string.IsNullOrWhiteSpace(txt)) values.Add(txt);
        }

        if (values.Count == 0)
            return null;

        if (values.Any(v => v.Contains("heat", StringComparison.OrdinalIgnoreCase)
                         || v.Equals("on", StringComparison.OrdinalIgnoreCase)
                         || v.Equals("heating", StringComparison.OrdinalIgnoreCase)))
        {
            return "heating";
        }

        return values[0];
    }

    private static bool IsKwh(string unit) =>
        string.Equals(unit, "kWh", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// CALC-02 fix: time-of-day multiplier for consumption estimation.
    /// Adjusts the flat rolling average to reflect typical residential consumption
    /// patterns (morning/evening peaks, overnight trough).
    /// Multiplier is centred around 1.0 so the daily average is preserved.
    /// </summary>
    private static double GetTimeOfDayConsumptionMultiplier(int hour) => hour switch
    {
        >= 0 and < 6  => 0.6,   // Night: low baseline (standby, fridge)
        >= 6 and < 9  => 1.3,   // Morning peak: heating, hot water, breakfast
        >= 9 and < 12 => 1.0,   // Late morning: moderate
        >= 12 and < 14 => 1.1,  // Lunch: cooking
        >= 14 and < 17 => 0.9,  // Afternoon: below average
        >= 17 and < 21 => 1.4,  // Evening peak: cooking, lighting, appliances
        >= 21 and < 23 => 1.0,  // Late evening: moderate
        _ => 0.7                // 23h: winding down
    };

    private static double? TryReadNumericAttribute(HaState? state, string attributeName)
    {
        if (state is null) return null;
        if (!state.Attributes.TryGetProperty(attributeName, out var attr)) return null;

        if (attr.ValueKind == JsonValueKind.Number && attr.TryGetDouble(out var n))
            return n;

        if (double.TryParse(attr.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            return parsed;

        return null;
    }

    private static bool? InterpretHeatingOn(string? hvacAction)
    {
        if (string.IsNullOrWhiteSpace(hvacAction)) return null;
        return hvacAction.Contains("heat", StringComparison.OrdinalIgnoreCase)
            || hvacAction.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    private static double? Aggregate(IReadOnlyCollection<double> values, string aggregationMode)
    {
        if (values.Count == 0) return null;

        return aggregationMode switch
        {
            "min" => values.Min(),
            "max" => values.Max(),
            _ => values.Average()
        };
    }

    private static string? NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value.Trim();
    }

    private async Task RefreshLearnedSourceSpeedAsync(
        IDistributionRepository repo,
        DateTime nowUtc,
        CancellationToken ct)
    {
        int refreshMinutes = Math.Max(1, _config.Heating.ComfortConstraints.LearningRefreshMinutes);
        if (_lastLearningRefreshUtc != DateTime.MinValue
            && (nowUtc - _lastLearningRefreshUtc).TotalMinutes < refreshMinutes)
        {
            return;
        }

        var samples = await repo.GetHeatingSamplesForTrainingAsync(maxRecords: 5000, windowDays: 30, ct);
        var ordered = samples
            .Where(x => x.IndoorTempC.HasValue && !string.IsNullOrWhiteSpace(x.ActiveSourceName))
            .OrderBy(x => x.SampledAtUtc)
            .ToList();

        var speedBySource = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < ordered.Count; i++)
        {
            var prev = ordered[i - 1];
            var curr = ordered[i];

            if (!string.Equals(prev.ActiveSourceName, curr.ActiveSourceName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (prev.IsHeatingOn != true || curr.IsHeatingOn != true)
                continue;

            var dtHours = (curr.SampledAtUtc - prev.SampledAtUtc).TotalHours;
            if (dtHours <= 0 || dtHours > 0.5)
                continue;

            var dTemp = curr.IndoorTempC!.Value - prev.IndoorTempC!.Value;
            if (dTemp <= 0)
                continue;

            var speed = dTemp / dtHours; // deg C per hour
            if (speed <= 0 || speed > 8.0)
                continue;

            var key = curr.ActiveSourceName!;
            if (!speedBySource.TryGetValue(key, out var bucket))
            {
                bucket = new List<double>();
                speedBySource[key] = bucket;
            }
            bucket.Add(speed);
        }

        // Build new dictionaries then swap references atomically (volatile write).
        var newSpeed = speedBySource.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.OrderBy(v => v).ElementAt(kv.Value.Count / 2),
            StringComparer.OrdinalIgnoreCase);

        var newCount = speedBySource.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Count,
            StringComparer.OrdinalIgnoreCase);

        _learnedSpeedDegPerHour = newSpeed;
        _learnedSpeedSampleCount = newCount;

        _lastLearningRefreshUtc = nowUtc;
    }

    private HeatingSourceCostResult ApplyAdvancedSourceSwitching(
        HeatingSourceCostResult baseline,
        DateTime nowLocal,
        double? currentTempC,
        double? targetTempC,
        HeatingPreheatEstimate mlEstimate,
        int minLearningSamples)
    {
        if (baseline.AllSources.Count == 0)
            return baseline;

        var selected = baseline;

        // 1) Time-window rule: prefer configured source when additional cost stays acceptable.
        foreach (var rule in _config.Heating.SourceTimeRules)
        {
            if (!rule.Enabled || string.IsNullOrWhiteSpace(rule.PreferredSourceName))
                continue;
            if (!IsHourInRange(nowLocal.Hour, rule.StartHourLocal, rule.EndHourLocal))
                continue;

            var candidate = baseline.AllSources.FirstOrDefault(x =>
                string.Equals(x.Name, rule.PreferredSourceName, StringComparison.OrdinalIgnoreCase));
            if (candidate is null)
                continue;

            double bestCost = Math.Max(0.00001, baseline.BestCostPerKwhThermal);
            double overPct = ((candidate.CostPerKwhThermal - bestCost) / bestCost) * 100.0;
            if (overPct <= rule.MaxOverBestCostPct)
            {
                selected = new HeatingSourceCostResult(
                    BestSourceName: candidate.Name,
                    BestSourceType: candidate.Type,
                    BestCop: candidate.Cop,
                    BestCostPerKwhThermal: candidate.CostPerKwhThermal,
                    AllSources: baseline.AllSources,
                    Reason: $"Time window rule {rule.StartHourLocal:00}:00-{rule.EndHourLocal:00}:00");
                break;
            }
        }

        // 2) Comfort override: if ML says ETA risk is too high and comfort is critical,
        // switch to the fastest source learned from historical transitions.
        var cc = _config.Heating.ComfortConstraints;
        if (!cc.Enabled || !currentTempC.HasValue || !targetTempC.HasValue)
            return selected;

        double deltaTemp = targetTempC.Value - currentTempC.Value;
        bool criticalComfort = currentTempC.Value < cc.MinimumComfortTempC || deltaTemp >= cc.CriticalDeltaTempC;
        bool etaRisk = mlEstimate.P90Minutes > cc.MaxMlEtaP90Minutes;
        if (!criticalComfort || !etaRisk)
            return selected;

        var fastest = baseline.AllSources
            .Select(s => new
            {
                Source = s,
                Speed = _learnedSpeedDegPerHour.TryGetValue(s.Name, out var v) ? v : 0,
                Count = _learnedSpeedSampleCount.TryGetValue(s.Name, out var c) ? c : 0
            })
            .Where(x => x.Count >= Math.Max(1, minLearningSamples) && x.Speed > 0)
            .OrderByDescending(x => x.Speed)
            .FirstOrDefault();

        if (fastest is null)
            return selected;

        double bestCostForGuard = Math.Max(0.00001, baseline.BestCostPerKwhThermal);
        double overBestPct = ((fastest.Source.CostPerKwhThermal - bestCostForGuard) / bestCostForGuard) * 100.0;
        if (overBestPct > cc.MaxComfortOverrideOverBestCostPct)
            return selected;

        return new HeatingSourceCostResult(
            BestSourceName: fastest.Source.Name,
            BestSourceType: fastest.Source.Type,
            BestCop: fastest.Source.Cop,
            BestCostPerKwhThermal: fastest.Source.CostPerKwhThermal,
            AllSources: baseline.AllSources,
            Reason: $"Comfort override via learned speed + ML ETA risk (p90={mlEstimate.P90Minutes:F0}m)");
    }

    private static bool IsHourInRange(int hour, int startHour, int endHour)
    {
        if (startHour == endHour)
            return true;
        if (startHour < endHour)
            return hour >= startHour && hour < endHour;
        return hour >= startHour || hour < endHour; // overnight range, e.g. 22 -> 06
    }

    private static HeatingPresenceMode ParsePresenceMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
            return HeatingPresenceMode.Home;

        return mode.Trim().ToLowerInvariant() switch
        {
            "away" => HeatingPresenceMode.Away,
            "sleep" => HeatingPresenceMode.Sleep,
            "near_home" => HeatingPresenceMode.NearHome,
            "nearhome" => HeatingPresenceMode.NearHome,
            _ => HeatingPresenceMode.Home
        };
    }

    private static double ComputeSurplus(double rawValue, string mode) =>
        mode.ToLowerInvariant() switch
        {
            "p1_invert" => Math.Max(0, -rawValue),
            _ => Math.Max(0, rawValue),
        };
}