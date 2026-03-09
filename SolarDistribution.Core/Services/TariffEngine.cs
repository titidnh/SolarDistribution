using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SolarDistribution.Core.Services;

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
        if (DaysOfWeek is { Count: > 0 })
        {
            int isoDow = localTime.DayOfWeek == DayOfWeek.Sunday
                ? 7
                : (int)localTime.DayOfWeek;
            if (!DaysOfWeek.Contains(isoDow)) return false;
        }

        var tod   = localTime.TimeOfDay;
        var start = ParsedStart;
        var end   = ParsedEnd;

        if (start == end) return true;
        if (start < end)  return tod >= start && tod < end;
        return tod >= start || tod < end;
    }
}

public class TariffEngine
{
    private readonly TariffConfig          _config;
    private readonly ILogger<TariffEngine> _logger;

    public TariffEngine(TariffConfig config, ILogger<TariffEngine>? logger = null)
    {
        _config = config;
        _logger = logger ?? NullLogger<TariffEngine>.Instance;
    }

    public TariffSlot? GetActiveSlot(DateTime localTime)
    {
        var matching = _config.Slots.Where(s => s.IsActiveAt(localTime)).ToList();
        if (!matching.Any()) return null;

        if (matching.Count > 1)
        {
            var names = string.Join(", ", matching.Select(s => $"\"{s.Name}\""));
            double diff = matching.Max(s => s.PricePerKwh) - matching.Min(s => s.PricePerKwh);
            if (diff > 0.01)
            {
                _logger.LogWarning(
                    "TariffEngine: slot overlap at {Time} — active slots: {Slots} " +
                    "(price diff={Diff:F3}€/kWh). Using cheapest slot.",
                    localTime.ToString("HH:mm"), names, diff);
                LastSlotConflict = $"{localTime:HH:mm} — overlapping: {names}";
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

    public double? HoursUntilNextFavorableTariff(DateTime localTime)
    {
        if (IsGridChargeFavorable(localTime)) return 0;
        for (int m = 1; m <= 24 * 60; m += 15)
        {
            if (IsGridChargeFavorable(localTime.AddMinutes(m)))
                return m / 60.0;
        }
        return null;
    }

    public TariffContext EvaluateContext(
        DateTime localTime,
        double[] solarForecastWm2,
        double? forecastTodayWh = null,
        double? forecastTomorrowWh = null)
    {
        var activeSlot    = GetActiveSlot(localTime);
        double? price     = activeSlot?.PricePerKwh;
        bool isFavorable  = IsGridChargeFavorable(localTime);

        int horizon       = _config.SolarForecastHorizonHours;
        double avgSolar   = solarForecastWm2.Take(horizon).DefaultIfEmpty(0).Average();
        bool solarExpected = avgSolar >= _config.MinSolarForecastForGridBlock;

        bool gridChargeAllowed = isFavorable && !solarExpected && _config.Slots.Any();

        double? maxFuture = GetMaxPriceNextHours(localTime, 24);
        double savings    = (maxFuture ?? 0) - (price ?? 0);

        double? hoursRemainingInSlot = null;
        if (isFavorable && activeSlot is not null)
            hoursRemainingInSlot = ComputeHoursRemainingInSlot(activeSlot, localTime);

        double? hoursUntilSolar = ComputeHoursUntilSolar(localTime, solarForecastWm2);

        return new TariffContext(
            ActiveSlotName:          activeSlot?.Name,
            CurrentPricePerKwh:      price,
            IsFavorableForGrid:      isFavorable,
            GridChargeAllowed:       gridChargeAllowed,
            AvgSolarForecastWm2:     avgSolar,
            SolarExpectedSoon:       solarExpected,
            HoursToNextFavorable:    HoursUntilNextFavorableTariff(localTime),
            MaxSavingsPerKwh:        Math.Max(0, savings),
            ExportPricePerKwh:       _config.ExportPricePerKwh,
            HoursRemainingInSlot:    hoursRemainingInSlot,
            HoursUntilSolar:         hoursUntilSolar,
            SolarForecastWm2:        solarForecastWm2,
            ForecastTodayWh:         forecastTodayWh,
            ForecastTomorrowWh:      forecastTomorrowWh
        );
    }

    private static double ComputeHoursRemainingInSlot(TariffSlot slot, DateTime localTime)
    {
        for (int m = 1; m <= 48 * 60; m++)
        {
            if (!slot.IsActiveAt(localTime.AddMinutes(m)))
                return m / 60.0;
        }
        return 48.0;
    }

    private double ComputeHoursUntilSolar(DateTime localTime, double[] solarForecastWm2)
    {
        int horizon = Math.Max(_config.SolarForecastHorizonHours, solarForecastWm2.Length);
        for (int h = 0; h < solarForecastWm2.Length && h < horizon; h++)
        {
            if (solarForecastWm2[h] >= _config.MinSolarForecastForGridBlock)
                return h;
        }
        return double.MaxValue;
    }

    public double? GetMinPriceNextHours(DateTime localTime, int horizonHours)
    {
        double? min = null;
        for (int h = 0; h < horizonHours; h++)
        {
            var p = GetCurrentPricePerKwh(localTime.AddHours(h));
            if (p.HasValue && (min is null || p.Value < min.Value)) min = p.Value;
        }
        return min;
    }

    private double? GetMaxPriceNextHours(DateTime localTime, int horizonHours)
    {
        double? max = null;
        for (int h = 0; h < horizonHours; h++)
        {
            var p = GetCurrentPricePerKwh(localTime.AddHours(h));
            if (p.HasValue && (max is null || p.Value > max.Value)) max = p.Value;
        }
        return max;
    }
}

public record TariffContext(
    string? ActiveSlotName,
    double? CurrentPricePerKwh,
    bool IsFavorableForGrid,
    bool GridChargeAllowed,
    double AvgSolarForecastWm2,
    bool SolarExpectedSoon,
    double? HoursToNextFavorable,
    double MaxSavingsPerKwh,
    double ExportPricePerKwh,
    /// <summary>Hours remaining in the current favorable slot. Null if not favorable.</summary>
    double? HoursRemainingInSlot,
    /// <summary>Hours until solar is sufficient. double.MaxValue = not forecast in horizon.</summary>
    double? HoursUntilSolar,
    /// <summary>Hourly radiation forecast array (W/m²) for adaptive charge calculation.</summary>
    double[] SolarForecastWm2,
    /// <summary>HA solar forecast today (Wh) — installation-specific. Null if not configured.</summary>
    double? ForecastTodayWh,
    /// <summary>HA solar forecast tomorrow (Wh) — installation-specific. Null if not configured.</summary>
    double? ForecastTomorrowWh
)
{
    public double NormalizedPrice => CurrentPricePerKwh.HasValue
        ? Math.Min(1.0, CurrentPricePerKwh.Value / 0.40)
        : 0.5;

    /// <summary>
    /// True si des prévisions HA de haute qualité (installation-spécifiques) sont disponibles.
    /// Quand true, l'algo utilise ForecastTodayWh/TomorrowWh plutôt que le modèle générique.
    /// </summary>
    public bool HasHaForecast => ForecastTodayWh.HasValue || ForecastTomorrowWh.HasValue;
}
