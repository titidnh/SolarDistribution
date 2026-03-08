using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SolarDistribution.Core.Services;

// ═════════════════════════════════════════════════════════════════════════════
// TariffConfig + TariffSlot — source de vérité unique dans Core.
// SolarConfig.cs (Worker) référence ces types via using.
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Configuration des tarifs d'électricité réseau.
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
/// Un créneau tarifaire avec plages horaires, filtre jours ISO (Lundi=1..Dimanche=7) et prix.
///
/// CONVENTION JOURS (ISO 8601) :
///   1=Lundi  2=Mardi  3=Mercredi  4=Jeudi  5=Vendredi  6=Samedi  7=Dimanche
///   Absent ou liste vide → actif tous les jours.
///
/// EXEMPLES YAML :
///
///   Heures creuses semaine (chevauchant minuit) :
///     name: "HC Semaine"
///     price_per_kwh: 0.10
///     start_time: "22:00"
///     end_time:   "06:00"
///     days_of_week: [1,2,3,4,5]      # lundi→vendredi
///
///   Week-end tarif réduit toute la journée :
///     name: "Week-end"
///     price_per_kwh: 0.12
///     start_time: "00:00"
///     end_time:   "00:00"            # start == end → TOUTE la journée
///     days_of_week: [6,7]            # samedi + dimanche
///
///   Nuit week-end encore moins chère :
///     name: "Nuit Week-end"
///     price_per_kwh: 0.07
///     start_time: "22:00"
///     end_time:   "06:00"
///     days_of_week: [5,6,7]          # vendredi soir, samedi soir, dimanche soir
/// </summary>
public class TariffSlot
{
    /// <summary>Nom pour les logs (ex: "HC Semaine", "Week-end").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Prix en €/kWh pour ce créneau.</summary>
    public double PricePerKwh { get; set; }

    /// <summary>Heure de début incluse au format "HH:mm".</summary>
    public string StartTime { get; set; } = "00:00";

    /// <summary>
    /// Heure de fin exclue au format "HH:mm".
    /// Si EndTime &lt; StartTime → créneau chevauchant minuit (ex: 22:00→06:00).
    /// Si StartTime == EndTime == "00:00" → actif toute la journée.
    /// </summary>
    public string EndTime { get; set; } = "00:00";

    /// <summary>
    /// Filtre sur les jours de la semaine — convention ISO 8601 :
    ///   1=Lundi  2=Mardi  3=Mercredi  4=Jeudi  5=Vendredi  6=Samedi  7=Dimanche
    /// null ou liste vide → actif tous les jours.
    ///
    /// IMPORTANT pour les créneaux chevauchant minuit (ex: 22:00→06:00 avec [5]) :
    ///   Le filtre porte sur le jour en cours à l'instant évalué.
    ///   Vendredi 23:30 → actif ✓  (vendredi=5 est dans la liste)
    ///   Samedi   02:00 → actif ✓  (samedi=6 est dans la liste si [5,6])
    ///   Samedi   23:30 → inactif ✗ si [5] seulement (samedi=6 absent)
    /// </summary>
    public List<int>? DaysOfWeek { get; set; }

    public TimeSpan ParsedStart => TimeSpan.Parse(StartTime);
    public TimeSpan ParsedEnd   => TimeSpan.Parse(EndTime);

    /// <summary>
    /// Vérifie si ce créneau est actif à l'instant donné (heure locale).
    ///
    /// Conversion .NET → ISO : DayOfWeek.Sunday(0) → 7, les autres restent identiques.
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

        if (start == end) return true;                          // toute la journée
        if (start < end)  return tod >= start && tod < end;    // créneau normal
        return tod >= start || tod < end;                       // chevauchant minuit
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// TariffEngine
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Répond aux questions tarifaires utilisées par l'algorithme de distribution
/// et par le ML pour optimiser l'autoconsommation.
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

                LastSlotConflict = $"{localTime:HH:mm} — slots actifs simultanément : {names}";
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
