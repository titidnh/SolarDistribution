using Microsoft.Extensions.Logging;
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
    private readonly ILogger<HomeAssistantDataReader> _logger;

    public HomeAssistantDataReader(
        IHomeAssistantClient client,
        SolarConfig config,
        ILogger<HomeAssistantDataReader> logger)
    {
        _client = client;
        _config = config;
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
        double? productionW  = null;
        double? consumptionW = null;

        if (_config.Solar.ProductionEntity is not null)
            productionW = await _client.GetNumericStateAsync(_config.Solar.ProductionEntity, ct);

        if (_config.Solar.ConsumptionEntity is not null)
            consumptionW = await _client.GetNumericStateAsync(_config.Solar.ConsumptionEntity, ct);

        // ── Prévisions solaires HA (optionnelles — chaudement recommandées) ───
        double? forecastTodayWh    = null;
        double? forecastTomorrowWh = null;

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
            "fcToday={FcToday}Wh, fcTomorrow={FcTomorrow}Wh | batteries=[{Batteries}]",
            surplusW,
            productionW?.ToString("F0") ?? "n/a",
            consumptionW?.ToString("F0") ?? "n/a",
            forecastTodayWh?.ToString("F0") ?? "n/a",
            forecastTomorrowWh?.ToString("F0") ?? "n/a",
            string.Join(", ", readings.Select(r =>
                r.ReadSuccess
                    ? $"{r.Name}:{r.SocPercent:F1}%{(r.MaxChargeRateW.HasValue ? $"/{r.MaxChargeRateW:F0}W" : "")}{(r.CurrentChargeW.HasValue ? $" now={r.CurrentChargeW:F0}W" : "")}"
                    : $"{r.Name}:ERR")));

        return new HaSnapshot(surplusW, productionW, consumptionW,
            forecastTodayWh, forecastTomorrowWh, readings, DateTime.UtcNow);
    }

    private static double ComputeSurplus(double rawValue, string mode) =>
        mode.ToLowerInvariant() switch
        {
            "p1_invert" => Math.Max(0, -rawValue),
            _           => Math.Max(0,  rawValue),
        };
}
