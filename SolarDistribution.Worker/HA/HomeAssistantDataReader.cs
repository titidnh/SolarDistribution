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
    List<BatteryReading> Batteries,
    DateTime ReadAt
);

public record BatteryReading(
    int     BatteryId,
    string  Name,
    double  SocPercent,
    /// <summary>
    /// Puissance max de recharge lue depuis HA (en W).
    /// Null si MaxChargeRateEntity n'est pas configurée ou si la lecture a échoué
    /// → l'appelant doit utiliser la valeur statique du config.yaml comme fallback.
    /// </summary>
    double? MaxChargeRateW,
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
        double? surplus = await _client.GetNumericStateAsync(_config.Solar.SurplusEntity, ct);

        if (surplus is null)
        {
            _logger.LogError(
                "Cannot read surplus entity '{Entity}' — skipping this cycle",
                _config.Solar.SurplusEntity);
            return null;
        }

        double surplusW = Math.Max(0, surplus.Value);

        // ── Optionnels (production + conso) ──────────────────────────────────
        double? productionW  = null;
        double? consumptionW = null;

        if (_config.Solar.ProductionEntity is not null)
            productionW = await _client.GetNumericStateAsync(_config.Solar.ProductionEntity, ct);

        if (_config.Solar.ConsumptionEntity is not null)
            consumptionW = await _client.GetNumericStateAsync(_config.Solar.ConsumptionEntity, ct);

        // ── SOC + MaxChargeRate de chaque batterie ────────────────────────────
        var readings = new List<BatteryReading>();

        foreach (var b in _config.Batteries)
        {
            // SOC — obligatoire
            double? soc = await _client.GetNumericStateAsync(b.Entities.Soc, ct);

            if (soc is null)
            {
                _logger.LogWarning(
                    "Cannot read SOC for battery {Id} ({Name}) entity '{Entity}'",
                    b.Id, b.Name, b.Entities.Soc);

                readings.Add(new BatteryReading(b.Id, b.Name, 0, null, ReadSuccess: false));
                continue;
            }

            // MaxChargeRate — optionnel, depuis HA si l'entité est configurée
            double? maxChargeRateW = null;

            if (b.Entities.MaxChargeRateEntity is not null)
            {
                double? rawRate = await _client.GetNumericStateAsync(
                    b.Entities.MaxChargeRateEntity, ct);

                if (rawRate is not null)
                {
                    // Convertir en W si l'entité expose des Ampères (MaxRateReadMultiplier)
                    maxChargeRateW = rawRate.Value * b.Entities.MaxRateReadMultiplier;

                    _logger.LogDebug(
                        "Battery {Id} ({Name}): live MaxChargeRate = {Rate:F0}W " +
                        "(raw={Raw:F2}, multiplier={Mult})",
                        b.Id, b.Name, maxChargeRateW, rawRate, b.Entities.MaxRateReadMultiplier);
                }
                else
                {
                    _logger.LogWarning(
                        "Battery {Id} ({Name}): cannot read MaxChargeRateEntity '{Entity}' " +
                        "— falling back to static max_charge_rate_w={Static}W",
                        b.Id, b.Name, b.Entities.MaxChargeRateEntity, b.MaxChargeRateW);
                }
            }

            readings.Add(new BatteryReading(b.Id, b.Name, soc.Value, maxChargeRateW, ReadSuccess: true));
        }

        _logger.LogInformation(
            "HA snapshot: surplus={Surplus}W, production={Prod}W, consumption={Cons}W | batteries=[{Batteries}]",
            surplusW,
            productionW?.ToString("F0") ?? "n/a",
            consumptionW?.ToString("F0") ?? "n/a",
            string.Join(", ", readings.Select(r =>
                r.ReadSuccess
                    ? $"{r.Name}:{r.SocPercent:F1}%{(r.MaxChargeRateW.HasValue ? $"/{r.MaxChargeRateW:F0}W" : "")}"
                    : $"{r.Name}:ERR")));

        return new HaSnapshot(surplusW, productionW, consumptionW, readings, DateTime.UtcNow);
    }
}
