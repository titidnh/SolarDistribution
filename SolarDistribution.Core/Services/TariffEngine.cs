using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SolarDistribution.Core.Services;

// ═════════════════════════════════════════════════════════════════════════════
// TariffConfig + TariffSlot — single source of truth located in Core.
// SolarConfig.cs (Worker) references these types via using.
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Configuration for grid electricity tariffs.
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

/// <summary>
/// A tariff slot with time ranges, ISO day-of-week filter (Monday=1..Sunday=7) and price.
///
/// DAY CONVENTION (ISO 8601):
///   1=Monday  2=Tuesday  3=Wednesday  4=Thursday  5=Friday  6=Saturday  7=Sunday
///   Missing or empty list -> active every day.
///
/// YAML EXAMPLES:
///
///   Weekday off-peak spanning midnight:
///     name: "HC Semaine"
///     price_per_kwh: 0.10
///     start_time: "22:00"
///     end_time:   "06:00"
///     days_of_week: [1,2,3,4,5]      # Monday→Friday
///
///   Reduced weekend tariff all day:
///     name: "Week-end"
///     price_per_kwh: 0.12
///     start_time: "00:00"
///     end_time:   "00:00"            # start == end -> ENTIRE day
///     days_of_week: [6,7]            # Saturday + Sunday
///
///   Weekend night even cheaper:
///     name: "Nuit Week-end"
///     price_per_kwh: 0.07
///     start_time: "22:00"
///     end_time:   "06:00"
///     days_of_week: [5,6,7]          # Friday night, Saturday night, Sunday night
/// </summary>
public class TariffSlot
{
    /// <summary>Name for logs (e.g. "HC Semaine", "Week-end").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Price in €/kWh for this slot.</summary>
    public double PricePerKwh { get; set; }

    /// <summary>Inclusive start time in "HH:mm" format.</summary>
    public string StartTime { get; set; } = "00:00";

    /// <summary>
    /// Exclusive end time in "HH:mm" format.
    /// If EndTime &lt; StartTime -> slot spans midnight (e.g. 22:00→06:00).
    /// If StartTime == EndTime == "00:00" -> active for the entire day.
    /// </summary>
    public string EndTime { get; set; } = "00:00";

    /// <summary>
    /// Filter on days of the week — ISO 8601 convention:
    ///   1=Monday  2=Tuesday  3=Wednesday  4=Thursday  5=Friday  6=Saturday  7=Sunday
    /// null or empty list -> active every day.
    ///
    /// IMPORTANT for slots spanning midnight (e.g. 22:00→06:00 with [5]):
    ///   The filter applies to the current day at the evaluated instant.
    ///   Friday 23:30 -> active ✓  (friday=5 is in the list)
    ///   Saturday 02:00 -> active ✓  (saturday=6 is in the list if [5,6])
    ///   Saturday 23:30 -> inactive ✗ if only [5] (saturday=6 absent)
    /// </summary>
    public List<int>? DaysOfWeek { get; set; }

    public TimeSpan ParsedStart => TimeSpan.Parse(StartTime);
    public TimeSpan ParsedEnd   => TimeSpan.Parse(EndTime);

    /// <summary>
    /// Checks if this slot is active at the given instant (local time).
    ///
    /// .NET -> ISO conversion: DayOfWeek.Sunday(0) -> 7, others remain the same.
    /// </summary>
    public bool IsActiveAt(DateTime localTime)
    {
        if (DaysOfWeek is { Count: > 0 })
        {
            int isoDow = localTime.DayOfWeek == DayOfWeek.Sunday
                ? 7
                : (int)localTime.DayOfWeek;   // Lundi=1 … Samedi=6 déjà corrects

            if (!DaysOfWeek.Contains(isoDow))
                return false;
        }

        var tod   = localTime.TimeOfDay;
        var start = ParsedStart;
        var end   = ParsedEnd;

        if (start == end) return true;                          // entire day
        if (start < end)  return tod >= start && tod < end;    // normal slot
        return tod >= start || tod < end;                       // spanning midnight
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// TariffEngine
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Answers tariff-related questions used by the distribution algorithm
/// and by ML to optimize self-consumption.
/// </summary>
public class TariffEngine
{
    private readonly TariffConfig          _config;
    private readonly ILogger<TariffEngine> _logger;

    public TariffEngine(TariffConfig config, ILogger<TariffEngine>? logger = null)
    {
        _config = config;
        _logger = logger ?? NullLogger<TariffEngine>.Instance;
    }

    // ── Tarif instantané ──────────────────────────────────────────────────────

    public TariffSlot? GetActiveSlot(DateTime localTime)
    {
        var matching = _config.Slots.Where(s => s.IsActiveAt(localTime)).ToList();
        if (!matching.Any()) return null;

        if (matching.Count > 1)
        {
            var names    = string.Join(", ", matching.Select(s => $"\"{s.Name}\""));
            double diff  = matching.Max(s => s.PricePerKwh) - matching.Min(s => s.PricePerKwh);

            if (diff > 0.01)
            {
                _logger.LogWarning(
                    "TariffEngine: slot overlap at {Time} — active slots: {Slots} " +
                    "(price diff={Diff:F3}€/kWh). Using cheapest slot as fallback. " +
                    "Check your tariff configuration.",
                    localTime.ToString("HH:mm"), names, diff);

                LastSlotConflict = $"{localTime:HH:mm} — overlapping active slots: {names}";
            }
        }

        return matching.MinBy(s => s.PricePerKwh);
    }

    public string? LastSlotConflict { get; private set; }

    public double? GetCurrentPricePerKwh(DateTime localTime)
        => GetActiveSlot(localTime)?.PricePerKwh;

    public bool IsGridChargeFavorable(DateTime localTime)
    {
        if (_config.GridChargeThresholdPerKwh <= 0) return false;
        var price = GetCurrentPricePerKwh(localTime);
        return price.HasValue && price.Value < _config.GridChargeThresholdPerKwh;
    }

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

    public TariffContext EvaluateContext(DateTime localTime, double[] solarForecastWm2)
    {
        var activeSlot   = GetActiveSlot(localTime);
        double? price    = activeSlot?.PricePerKwh;
        bool isFavorable = IsGridChargeFavorable(localTime);

        int horizon       = _config.SolarForecastHorizonHours;
        double avgSolar   = solarForecastWm2.Take(horizon).DefaultIfEmpty(0).Average();
        bool solarExpected = avgSolar >= _config.MinSolarForecastForGridBlock;

        bool gridChargeAllowed = isFavorable && !solarExpected && _config.Slots.Any();

        double? maxFuture = GetMaxPriceNextHours(localTime, 24);
        double savings    = (maxFuture ?? 0) - (price ?? 0);

        return new TariffContext(
            ActiveSlotName:       activeSlot?.Name,
            CurrentPricePerKwh:   price,
            IsFavorableForGrid:   isFavorable,
            GridChargeAllowed:    gridChargeAllowed,
            AvgSolarForecastWm2:  avgSolar,
            SolarExpectedSoon:    solarExpected,
            HoursToNextFavorable: HoursUntilNextFavorableTariff(localTime),
            MaxSavingsPerKwh:     Math.Max(0, savings),
            ExportPricePerKwh:    _config.ExportPricePerKwh
        );
    }

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

// ═════════════════════════════════════════════════════════════════════════════
// TariffContext
// ═════════════════════════════════════════════════════════════════════════════

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
