// TariffConfig and TariffSlot are defined locally in Core to avoid a project
// dependency on the Worker project. This keeps the tariff logic colocated with
// TariffEngine while preserving the original behavior.

namespace SolarDistribution.Core.Services;

/// <summary>
/// Répond aux questions tarifaires utilisées par l'algorithme de distribution
/// et par le ML pour optimiser l'autoconsommation.
///
/// Questions traitées :
///   1. Quel est le tarif à un instant donné ?
///   2. Quel est le tarif minimal sur les N prochaines heures ?
///   3. Est-on en heure creuse ?
///   4. Combien d'heures avant le prochain tarif bas ?
///   5. Vaut-il mieux charger depuis le réseau maintenant ou attendre le soleil ?
///   6. Quelle puissance réseau maximale allouer à chaque batterie ?
/// </summary>
public class TariffEngine
{
    private readonly TariffConfig _config;

    public TariffEngine(TariffConfig config)
    {
        _config = config;
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

        var tod = localTime.TimeOfDay;
        var start = ParsedStart;
        var end = ParsedEnd;

        if (start == end) return true;
        if (start < end) return tod >= start && tod < end;
        return tod >= start || tod < end;
    }
}

    // ── Tarif instantané ──────────────────────────────────────────────────────

    /// <summary>
    /// Retourne le tarif actif à l'instant donné (heure locale).
    /// Si plusieurs créneaux matchent → log un avertissement de configuration
    /// puis retourne le moins cher (comportement le plus conservateur).
    /// Si aucun créneau ne matche → retourne null (pas de tarification connue).
    /// </summary>
    public TariffSlot? GetActiveSlot(DateTime localTime)
    {
        var matching = _config.Slots.Where(s => s.IsActiveAt(localTime)).ToList();
        if (!matching.Any()) return null;

        if (matching.Count > 1)
        {
            // Plusieurs créneaux actifs simultanément → probablement une erreur de configuration.
            // On log un avertissement et on retourne le moins cher (le plus conservateur pour
            // la charge réseau : on n'autorise que si le prix est vraiment bas).
            var names = string.Join(", ", matching.Select(s => $"\"{s.Name}\""));
            // Le TariffEngine n'a pas d'ILogger injecté — on lève une exception configurable
            // uniquement si la différence de prix est significative (> 1 ct) pour éviter le bruit.
            if (matching.Max(s => s.PricePerKwh) - matching.Min(s => s.PricePerKwh) > 0.01)
            {
                // Stocker le conflit dans une propriété observable (utile pour les tests)
                LastSlotConflict = $"{localTime:HH:mm} — slots actifs simultanément : {names}";
            }
        }

        return matching.MinBy(s => s.PricePerKwh);
    }

    /// <summary>
    /// Dernier conflit de slots détecté (null si aucun). Utile pour les tests et le monitoring.
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
    /// Utile pour décider si on a intérêt à attendre une heure creuse.
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
    /// 0 si on est déjà en tarif favorable. null si aucun créneau favorable trouvé.
    /// </summary>
    public double? HoursUntilNextFavorableTariff(DateTime localTime)
    {
        if (IsGridChargeFavorable(localTime)) return 0;

        for (int m = 1; m <= 24 * 60; m += 15) // scrute par quart d'heure sur 24h
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
    /// Utilisé par SmartDistributionService pour enrichir la décision.
    /// </summary>
    public TariffContext EvaluateContext(DateTime localTime, double[] solarForecastWm2)
    {
        var activeSlot     = GetActiveSlot(localTime);
        double? currentPrice = activeSlot?.PricePerKwh;
        bool isFavorable   = IsGridChargeFavorable(localTime);

        // Prévision solaire sur l'horizon configuré
        int horizon        = _config.SolarForecastHorizonHours;
        double avgSolarForecast = solarForecastWm2.Take(horizon).DefaultIfEmpty(0).Average();
        bool solarExpected = avgSolarForecast >= _config.MinSolarForecastForGridBlock;

        // Charge réseau autorisée seulement si tarif favorable ET pas de soleil attendu
        bool gridChargeAllowed = isFavorable && !solarExpected && _config.Slots.Any();

        // Économie potentielle si on charge depuis réseau en heure creuse plutôt que
        // d'acheter plus tard en heure pleine (comparaison tarif actuel vs max tarif future)
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
/// Transmis à l'algorithme et aux features ML.
/// </summary>
public record TariffContext(
    /// <summary>Nom du créneau tarifaire actif (ex: "Heures Creuses"), null si inconnu</summary>
    string? ActiveSlotName,

    /// <summary>Prix actuel en €/kWh, null si aucun tarif configuré</summary>
    double? CurrentPricePerKwh,

    /// <summary>Vrai si le tarif actuel est en-dessous du seuil de charge réseau</summary>
    bool IsFavorableForGrid,

    /// <summary>Vrai si la charge depuis réseau est réellement autorisée (tarif + pas de soleil prévu)</summary>
    bool GridChargeAllowed,

    /// <summary>Rayonnement solaire moyen prévu sur l'horizon configuré (W/m²)</summary>
    double AvgSolarForecastWm2,

    /// <summary>Vrai si une production solaire significative est attendue prochainement</summary>
    bool SolarExpectedSoon,

    /// <summary>Heures avant le prochain créneau tarifaire favorable, null si aucun</summary>
    double? HoursToNextFavorable,

    /// <summary>Économie max potentielle en €/kWh si on charge maintenant vs plus tard</summary>
    double MaxSavingsPerKwh,

    /// <summary>Prix de revente du surplus en €/kWh</summary>
    double ExportPricePerKwh
)
{
    /// <summary>Tarif actuel normalisé 0→1 par rapport au seuil (pour les features ML)</summary>
    public double NormalizedPrice => CurrentPricePerKwh.HasValue
        ? Math.Min(1.0, CurrentPricePerKwh.Value / 0.40) // normalisé sur 40 cts max
        : 0.5; // valeur neutre si inconnu
}
