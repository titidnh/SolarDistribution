using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SolarDistribution.Core.Repositories;
using SolarDistribution.Core.Services;
using SolarDistribution.Worker.Configuration;

namespace SolarDistribution.Worker.HA;

/// <summary>
/// Lit toutes les valeurs nécessaires depuis HA en un seul cycle.
/// </summary>
public record HaSnapshot(
    double SurplusW,
    double? ProductionW,
    double? ConsumptionW,
    /// <summary>Production solaire estimée aujourd'hui (Wh) — depuis HA, optionnel.</summary>
    double? ForecastTodayWh,
    /// <summary>Production solaire estimée demain (Wh) — depuis HA, optionnel.</summary>
    double? ForecastTomorrowWh,
    /// <summary>Production solaire estimée CETTE HEURE (Wh) — Solcast intraday, optionnel.</summary>
    double? ForecastThisHourWh,
    /// <summary>Production solaire estimée L'HEURE SUIVANTE (Wh) — Solcast intraday, optionnel.</summary>
    double? ForecastNextHourWh,
    /// <summary>Production solaire RESTANTE AUJOURD'HUI (Wh) — pour le bilan énergétique journalier.</summary>
    double? ForecastRemainingTodayWh,
    /// <summary>
    /// Consommation par zone/appareil lue depuis HA (W).
    /// Clé = entity_id HA, valeur = puissance lue (W).
    /// Vide si ZoneConsumptionEntities non configuré ou si ConsumptionEntity est déjà présent.
    /// </summary>
    Dictionary<string, double> ZoneConsumptionW,
    /// <summary>
    /// Consommation estimée sur les prochaines heures (Wh) — moyenne roulante × horizon.
    /// Calculée à partir de la moyenne des N derniers cycles (MariaDB) × ConsumptionProjectionHours.
    /// Null si données insuffisantes ou entités non configurées.
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
    /// Puissance de charge réelle actuelle lue depuis HA (W).
    /// Null si CurrentChargePowerEntity non configurée ou lecture échouée.
    /// Utilisée pour corriger le surplus brut : surplus_réel = surplus_HA + Σ CurrentChargeW.
    /// </summary>
    double? CurrentChargeW,
    bool ReadSuccess,
    /// <summary>
    /// ML-8 : Nombre de cycles de charge lus depuis CycleCountEntity dans HA.
    /// 0 si l'entité n'est pas configurée ou si la lecture a échoué.
    /// </summary>
    int CycleCount = 0
);

public class HomeAssistantDataReader
{
    private readonly IHomeAssistantClient _client;
    private readonly SolarConfig _config;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TariffEngine _tariffEngine;
    private readonly ILogger<HomeAssistantDataReader> _logger;

    public HomeAssistantDataReader(
        IHomeAssistantClient client,
        SolarConfig config,
        IServiceScopeFactory scopeFactory,
        TariffEngine tariffEngine,
        ILogger<HomeAssistantDataReader> logger)
    {
        _client = client;
        _config = config;
        _scopeFactory = scopeFactory;
        _tariffEngine = tariffEngine;
        _logger = logger;
    }

    public async Task<HaSnapshot?> ReadAllAsync(CancellationToken ct = default)
    {
        // ── Surplus solaire ───────────────────────────────────────────────────
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

        // ── Optionnels production + conso ─────────────────────────────────────
        double? productionW = null;
        double? consumptionW = null;

        if (_config.Solar.ProductionEntity is not null)
            productionW = await _client.GetNumericStateAsync(_config.Solar.ProductionEntity, ct);

        if (_config.Solar.ConsumptionEntity is not null)
            consumptionW = await _client.GetNumericStateAsync(_config.Solar.ConsumptionEntity, ct);

        // ── Consommation par zone (optionnel — complément ou alternative à ConsumptionEntity) ──
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

        // ── Rolling average de consommation + projection ──────────────────────
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
                estimatedConsumptionNextHoursWh = rollingAvgW.Value * projectionHours;
                _logger.LogDebug(
                    "Load forecast: rolling avg={Avg:F0}W (last {N} cycles) × {H:F1}h = {Wh:F0}Wh estimated consumption",
                    rollingAvgW.Value, rollingWindow, projectionHours, estimatedConsumptionNextHoursWh);
            }
            else if (consumptionW is not null)
            {
                estimatedConsumptionNextHoursWh = consumptionW.Value * projectionHours;
                _logger.LogDebug(
                    "Load forecast: no DB history yet — using live consumption={W:F0}W × {H:F1}h = {Wh:F0}Wh",
                    consumptionW.Value, projectionHours, estimatedConsumptionNextHoursWh);
            }
        }

        // ── Prévisions solaires HA (optionnelles — chaudement recommandées) ───
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
                // Solcast retourne des kWh → convertir en Wh
                forecastTodayWh = rawToday.Value * 1000.0;
                _logger.LogDebug(
                    "Solar forecast today: {V:F0} Wh (from HA, raw={Raw:F3} kWh)",
                    forecastTodayWh, rawToday);
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
                // Solcast retourne des kWh → convertir en Wh
                forecastTomorrowWh = rawTomorrow.Value * 1000.0;
                _logger.LogDebug(
                    "Solar forecast tomorrow: {V:F0} Wh (from HA, raw={Raw:F3} kWh)",
                    forecastTomorrowWh, rawTomorrow);
            }
            else
                _logger.LogDebug(
                    "Solar forecast tomorrow entity '{Entity}' unreadable — will use Open-Meteo fallback",
                    _config.Solar.ForecastTomorrowEntity);
        }

        // ── Prévisions Solcast intra-journalières ─────────────────────────────
        // forecast_this_hour et forecast_next_hour sont déjà en Wh — pas de conversion.
        // forecast_remaining_today est en kWh → convertir en Wh.
        if (_config.Solar.ForecastThisHourEntity is not null)
        {
            forecastThisHourWh = await _client.GetNumericStateAsync(_config.Solar.ForecastThisHourEntity, ct);
            _logger.LogDebug(
                "Solcast this_hour: {V} Wh",
                forecastThisHourWh?.ToString("F0") ?? "n/a");
        }

        if (_config.Solar.ForecastNextHourEntity is not null)
        {
            forecastNextHourWh = await _client.GetNumericStateAsync(_config.Solar.ForecastNextHourEntity, ct);
            _logger.LogDebug(
                "Solcast next_hour: {V} Wh",
                forecastNextHourWh?.ToString("F0") ?? "n/a");
        }

        if (_config.Solar.ForecastRemainingTodayEntity is not null)
        {
            var rawRemaining = await _client.GetNumericStateAsync(_config.Solar.ForecastRemainingTodayEntity, ct);
            if (rawRemaining is not null)
            {
                // Solcast retourne des kWh → convertir en Wh
                forecastRemainingTodayWh = rawRemaining.Value * 1000.0;
                _logger.LogDebug(
                    "Solcast remaining_today: {V:F0} Wh (raw={Raw:F3} kWh)",
                    forecastRemainingTodayWh, rawRemaining);
            }
            else
                _logger.LogDebug("Solcast remaining_today: n/a");
        }

        // ── SOC + MaxChargeRate de chaque batterie ────────────────────────────
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
                    currentChargeW = Math.Max(0, rawCharge.Value * b.Entities.CurrentChargePowerMultiplier);
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

        // ── Prix spot dynamique (Feature 5) ──────────────────────────────────
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

        return new HaSnapshot(surplusW, productionW, consumptionW,
            forecastTodayWh, forecastTomorrowWh,
            forecastThisHourWh, forecastNextHourWh, forecastRemainingTodayWh,
            zoneConsumptionW, estimatedConsumptionNextHoursWh,
            readings, DateTime.UtcNow);
    }

    private static double ComputeSurplus(double rawValue, string mode) =>
        mode.ToLowerInvariant() switch
        {
            "p1_invert" => Math.Max(0, -rawValue),
            _ => Math.Max(0, rawValue),
        };
}