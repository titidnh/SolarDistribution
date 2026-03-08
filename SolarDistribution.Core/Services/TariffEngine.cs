// TariffConfig and TariffSlot are defined locally in Core to avoid a project
// dependency on the Worker project. This keeps the tariff logic colocated with
// TariffEngine while preserving the original behavior.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SolarDistribution.Core.Services;

/// <summary>
/// Répond aux questions tarifaires utilisées par l'algorithme de distribution
/// et par le ML pour optimiser l'autoconsommation.
///
/// Fix #8 : ILogger injecté pour tracer les conflits de slots tarifaires
/// (overlap de configuration) qui étaient auparavant silencieux.
/// </summary>
public class TariffEngine
{
    private readonly TariffConfig             _config;
    private readonly ILogger<TariffEngine>    _logger;

    public TariffEngine(TariffConfig config, ILogger<TariffEngine>? logger = null)
    {
        _config = config;
        _logger = logger ?? NullLogger<TariffEngine>.Instance;
    }

/// <summary>
/// Minimal tariff configuration used by TariffEngine. Kept local to Core to
/// avoid referencing the Worker project from Core.
/// </summary>
public class TariffConfig
{
    public string Currency { get; set; } = "EUR";
    public double ExportPricePerKwh { get; set; } = 0.08;
    public double GridChargeThresholdPerKwh { get; set; } = 0.15;
    public double MinSolarForecastForGridBlock { get; set; } = 100.0;
    public int SolarForecastHorizonHours { get; set; } = 4;
    public List<TariffSlot> Slots { get; set; } = new List<TariffSlot>();
}

public class TariffSlot
{
    public string Name { get; set; } = string.Empty;
    public double PricePerKwh { get; set; }
    public string StartTime { get; set; } = "00:00";
    public string EndTime { get; set; } = "00:00";
    public List<int>? DaysOfWeek { get; set; }

    public TimeSpan ParsedStart => TimeSpan.Parse(StartTime);
    public TimeSpan ParsedEnd   => TimeSpan.Parse(EndTime);

    public bool IsActiveAt(DateTime localTime)
    {
        if (DaysOfWeek is { Count: > 0 } && !DaysOfWeek.Contains((int)localTime.DayOfWeek))
            return false;

        var tod   = localTime.TimeOfDay;
        var start = ParsedStart;
        var end   = ParsedEnd;

        if (start == end) return true;
        if (start < end) return tod >= start && tod < end;
        return tod >= start || tod < end;
    }
}

    // ── Tarif instantané ──────────────────────────────────────────────────────

    /// <summary>
    /// Retourne le tarif actif à l'instant donné (heure locale).
    /// Fix #8 : Les conflits de slots (overlap de configuration) sont maintenant
    /// tracés via ILogger plutôt que stockés silencieusement dans LastSlotConflict.
    /// Comportement conservateur : on retourne le slot le moins cher.
    /// </summary>
    public TariffSlot? GetActiveSlot(DateTime localTime)
    {
        var matching = _config.Slots.Where(s => s.IsActiveAt(localTime)).ToList();
        if (!matching.Any()) return null;

        if (matching.Count > 1)
        {
            var names = string.Join(", ", matching.Select(s => $"\"{s.Name}\""));
            double priceDiff = matching.Max(s => s.PricePerKwh) - matching.Min(s => s.PricePerKwh);

            if (priceDiff > 0.01)
            {
                // Fix #8 : log Warning visible en production au lieu d'une propriété silencieuse
                _logger.LogWarning(
                    "TariffEngine: slot overlap at {Time} — active slots: {Slots} " +
                    "(price diff={Diff:F3}€/kWh). Using cheapest slot as fallback. " +
                    "Check your tariff configuration.",
                    localTime.ToString("HH:mm"), names, priceDiff);

                LastSlotConflict = $"{localTime:HH:mm} — slots actifs simultanément : {names}";
            }
        }

        return matching.MinBy(s => s.PricePerKwh);
    }

    /// <summary>
    /// Dernier conflit de slots détecté (null si aucun).
    /// Conservé pour la compatibilité des tests existants.
    /// En production, préférer les logs (Fix #8).
    /// </summary>
    public string? LastSlotConflict { get; private set; }

    /// <summary>Retourne le prix €/kWh à l'instant donné, ou null si inconnu.</summary>
    public double? GetCurrentPricePerKwh(DateTime localTime)
        => GetActiveSlot(localTime)?.PricePerKwh;

    /// <summary>Vrai si on est dans un créneau dont le prix est en-dessous du seuil de charge réseau.</summary>
    public bool IsGridChargeFavorable(DateTime localTime)
    {
        if (_config.GridChargeThresholdPerKwh <= 0) return false;
        var price = GetCurrentPricePerKwh(localTime);
        return price.HasValue && price.Value < _config.GridChargeThresholdPerKwh;
    }

    // ── Prévision tarifaire ───────────────────────────────────────────────────

    /// <summary>
    /// Retourne le tarif minimal prévu sur les N prochaines heures.
    /// </summary>
    public double? GetMinPriceNextHours(DateTime localTime, int horizonHours)
    {
        double? min = null;
        for (int h = 0; h < horizonHours; h++)
        {
            var price = GetCurrentPricePerKwh(localTime.AddHours(h));
            if (price.HasValue && (min is null || price.Value < min.Value))
                min = price.Value;
        }
        return min;
    }

    /// <summary>
    /// Retourne le nombre d'heures jusqu'au prochain créneau favorable (tarif bas).
    /// </summary>
    public double? HoursUntilNextFavorableTariff(DateTime localTime)
    {
        if (IsGridChargeFavorable(localTime)) return 0;

        for (int m = 1; m <= 24 * 60; m += 15)
        {
            var future = localTime.AddMinutes(m);
            if (IsGridChargeFavorable(future))
                return m / 60.0;
        }
        return null;
    }

    // ── Décision de charge réseau ─────────────────────────────────────────────

    /// <summary>
    /// Calcule le contexte tarifaire complet pour un instant donné.
    /// </summary>
    public TariffContext EvaluateContext(DateTime localTime, double[] solarForecastWm2)
    {
        var activeSlot     = GetActiveSlot(localTime);
        double? currentPrice = activeSlot?.PricePerKwh;
        bool isFavorable   = IsGridChargeFavorable(localTime);

        int horizon        = _config.SolarForecastHorizonHours;
        double avgSolarForecast = solarForecastWm2.Take(horizon).DefaultIfEmpty(0).Average();
        bool solarExpected = avgSolarForecast >= _config.MinSolarForecastForGridBlock;

        bool gridChargeAllowed = isFavorable && !solarExpected && _config.Slots.Any();

        double? maxFuturePrice = GetMaxPriceNextHours(localTime, 24);
        double savings = (maxFuturePrice ?? 0) - (currentPrice ?? 0);

        return new TariffContext(
            ActiveSlotName:       activeSlot?.Name,
            CurrentPricePerKwh:   currentPrice,
            IsFavorableForGrid:   isFavorable,
            GridChargeAllowed:    gridChargeAllowed,
            AvgSolarForecastWm2:  avgSolarForecast,
            SolarExpectedSoon:    solarExpected,
            HoursToNextFavorable: HoursUntilNextFavorableTariff(localTime),
            MaxSavingsPerKwh:     Math.Max(0, savings),
            ExportPricePerKwh:    _config.ExportPricePerKwh
        );
    }

    // ── Helpers privés ────────────────────────────────────────────────────────

    private double? GetMaxPriceNextHours(DateTime localTime, int horizonHours)
    {
        double? max = null;
        for (int h = 0; h < horizonHours; h++)
        {
            var price = GetCurrentPricePerKwh(localTime.AddHours(h));
            if (price.HasValue && (max is null || price.Value > max.Value))
                max = price.Value;
        }
        return max;
    }
}

/// <summary>
/// Contexte tarifaire calculé pour un cycle de distribution.
/// </summary>
public record TariffContext(
    string? ActiveSlotName,
    double? CurrentPricePerKwh,
    bool IsFavorableForGrid,
    bool GridChargeAllowed,
    double AvgSolarForecastWm2,
    bool SolarExpectedSoon,
    double? HoursToNextFavorable,
    double MaxSavingsPerKwh,
    double ExportPricePerKwh
)
{
    public double NormalizedPrice => CurrentPricePerKwh.HasValue
        ? Math.Min(1.0, CurrentPricePerKwh.Value / 0.40)
        : 0.5;
}
