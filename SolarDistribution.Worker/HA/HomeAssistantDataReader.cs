using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SolarDistribution.Core.Repositories;
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
    int     BatteryId,
    string  Name,
    double  SocPercent,
    double? MaxChargeRateW,
    /// <summary>
    /// Puissance de charge réelle actuelle lue depuis HA (W).
    /// Null si CurrentChargePowerEntity non configurée ou lecture échouée.
    /// Utilisée pour corriger le surplus brut : surplus_réel = surplus_HA + Σ CurrentChargeW.
    /// </summary>
    double? CurrentChargeW,
    bool    ReadSuccess
);

public class HomeAssistantDataReader
{
    private readonly IHomeAssistantClient _client;
    private readonly SolarConfig          _config;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<HomeAssistantDataReader> _logger;

    public HomeAssistantDataReader(
        IHomeAssistantClient client,
        SolarConfig config,
        IServiceScopeFactory scopeFactory,
        ILogger<HomeAssistantDataReader> logger)
    {
        _client       = client;
        _config       = config;
        _scopeFactory = scopeFactory;
        _logger       = logger;
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
        double? productionW  = null;
        double? consumptionW = null;

        if (_config.Solar.ProductionEntity is not null)
            productionW = await _client.GetNumericStateAsync(_config.Solar.ProductionEntity, ct);

        if (_config.Solar.ConsumptionEntity is not null)
            consumptionW = await _client.GetNumericStateAsync(_config.Solar.ConsumptionEntity, ct);

        // ── Consommation par zone (optionnel — complément ou alternative à ConsumptionEntity) ──
        // Si ConsumptionEntity est déjà configuré, les zones sont ignorées pour éviter la redondance.
        // Sinon, on lit chaque entité de zone et on les somme pour obtenir la consommation totale estimée.
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
        // On calcule la moyenne des N derniers cycles depuis MariaDB, puis on projette
        // la consommation estimée sur les prochaines heures (horizon configurable).
        // Cette valeur alimentera EstimatedConsumptionNextHoursWh dans TariffContext
        // pour affiner ComputeAdaptiveGridChargeW (la charge réseau doit couvrir
        // non seulement le déficit batterie, mais aussi la conso maison prévue).
        double? estimatedConsumptionNextHoursWh = null;

        int rollingWindow = _config.Solar.ConsumptionRollingWindowCycles;
        double projectionHours = _config.Solar.ConsumptionProjectionHours;

        if (rollingWindow > 0 && projectionHours > 0)
        {
            // Priorité : rolling average depuis DB (plus stable que la lecture live)
            // Utilise un scope dédié pour éviter le conflit Singleton/Scoped lifetime.
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
                // Fallback : lecture live si pas encore de données historiques
                estimatedConsumptionNextHoursWh = consumptionW.Value * projectionHours;
                _logger.LogDebug(
                    "Load forecast: no DB history yet — using live consumption={W:F0}W × {H:F1}h = {Wh:F0}Wh",
                    consumptionW.Value, projectionHours, estimatedConsumptionNextHoursWh);
            }
        }

        // ── Prévisions solaires HA (optionnelles — chaudement recommandées) ───
        double? forecastTodayWh    = null;
        double? forecastTomorrowWh = null;
        double? forecastThisHourWh    = null;
        double? forecastNextHourWh    = null;
        double? forecastRemainingTodayWh = null;

        if (_config.Solar.ForecastTodayEntity is not null)
        {
            forecastTodayWh = await _client.GetNumericStateAsync(_config.Solar.ForecastTodayEntity, ct);
            if (forecastTodayWh is not null)
                _logger.LogDebug("Solar forecast today: {V:F0} Wh (from HA)", forecastTodayWh);
            else
                _logger.LogDebug(
                    "Solar forecast today entity '{Entity}' unreadable — will use Open-Meteo fallback",
                    _config.Solar.ForecastTodayEntity);
        }

        if (_config.Solar.ForecastTomorrowEntity is not null)
        {
            forecastTomorrowWh = await _client.GetNumericStateAsync(_config.Solar.ForecastTomorrowEntity, ct);
            if (forecastTomorrowWh is not null)
                _logger.LogDebug("Solar forecast tomorrow: {V:F0} Wh (from HA)", forecastTomorrowWh);
            else
                _logger.LogDebug(
                    "Solar forecast tomorrow entity '{Entity}' unreadable — will use Open-Meteo fallback",
                    _config.Solar.ForecastTomorrowEntity);
        }

        // ── Prévisions Solcast intra-journalières ─────────────────────────────
        // Ces entités donnent la courbe horaire réelle : this_hour, next_hour, remaining_today.
        // Elles remplacent le profil sinusoïdal générique dans ComputeAdaptiveGridChargeW
        // et permettent de prendre des décisions précises à l'échelle horaire.
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
            forecastRemainingTodayWh = await _client.GetNumericStateAsync(_config.Solar.ForecastRemainingTodayEntity, ct);
            _logger.LogDebug(
                "Solcast remaining_today: {V} Wh",
                forecastRemainingTodayWh?.ToString("F0") ?? "n/a");
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

            // CurrentChargePower — pour corriger le surplus brut HA
            // Le surplus P1/sensor inclut déjà la charge actuelle des batteries.
            // En ajoutant la charge actuelle au surplus, on obtient le vrai disponible.
            double? currentChargeW = null;

            if (b.Entities.CurrentChargePowerEntity is not null)
            {
                double? rawCharge = await _client.GetNumericStateAsync(
                    b.Entities.CurrentChargePowerEntity, ct);

                if (rawCharge is not null)
                {
                    // Clamp à 0 : on ne veut que la charge positive (pas la décharge)
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

            readings.Add(new BatteryReading(b.Id, b.Name, soc.Value, maxChargeRateW, currentChargeW, ReadSuccess: true));
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
            _           => Math.Max(0,  rawValue),
        };
}
