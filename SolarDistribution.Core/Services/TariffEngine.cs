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

    /// <summary>
    /// [HA Forecast] Seuil en Wh en dessous duquel la journée est considérée « peu solaire ».
    /// Si ForecastTodayWh >= cette valeur → bloque la charge réseau (le soleil couvrira la demande).
    /// Défaut 500 Wh : en dessous, on ne compte pas sur le solaire pour remplir les batteries.
    /// Exemple : installation 2 kWc → mettre ~800 Wh ; 4 kWc → ~1500 Wh.
    /// </summary>
    public double MinHaForecastWhForGridBlock { get; set; } = 500.0;

    /// <summary>
    /// [HA Forecast J+1] En dessous de ce seuil (Wh), demain est considéré « mauvais ».
    /// Quand on est dans un créneau tarifaire favorable (IsFavorableForGrid), le SoftMax
    /// des batteries est augmenté de EveningBoostPercent pour maximiser la réserve.
    /// Défaut 1000 Wh.
    /// </summary>
    public double LowForecastTomorrowWh { get; set; } = 1000.0;

    /// <summary>
    /// Bonus SoftMax (points de %) ajouté quand demain est prévu mauvais
    /// ET qu'on est dans un créneau tarifaire favorable (HC, week-end, etc.).
    /// Défaut 10% → si SoftMax = 80%, passe à 90% pendant les heures creuses.
    /// </summary>
    public double EveningBoostPercent { get; set; } = 10.0;

    /// <summary>
    /// Lazy Charging — marge de sécurité (en heures) ajoutée à la durée de charge estimée
    /// pour calculer l'heure de démarrage optimale en HC.
    ///
    /// Principe : plutôt que de charger dès l'ouverture du slot HC à faible puissance,
    /// le worker attend l'heure la plus tardive possible, puis charge à pleine puissance
    /// juste avant la fin du slot (maximise le temps en self-powered, réduit les cycles BMS).
    ///
    ///   heure de démarrage = fin_du_slot - hoursNeeded - lazy_buffer_hours
    ///
    /// Exemple : slot HC 22h→7h (9h), besoin de 0.5h à 1000W, buffer=0.5h
    ///   → démarrage à 06h00 (9h - 0.5h - 0.5h = 8h d'attente depuis 22h)
    ///
    /// Valeur recommandée : 0.5 (30 min).
    /// Augmenter si les batteries décrochent souvent (SOC surestime ou dérive).
    /// Mettre à 0 pour désactiver le lazy charging (comportement original).
    /// </summary>
    public double LazyBufferHours { get; set; } = 0.5;

    public List<TariffSlot> Slots { get; set; } = new List<TariffSlot>();

    /// <summary>
    /// [Intraday] Seuil Wh sur les 3 prochaines heures au-dessus duquel la charge réseau
    /// est réduite car le solaire arrive bientôt.
    /// Si ForecastNext3HoursWh >= cette valeur → on réduit la charge réseau proportionnellement.
    /// Défaut 200 Wh (= 200W moyen sur 1h ≈ une production solaire modeste).
    /// </summary>
    public double MinSolarNext3HoursWhForGridReduction { get; set; } = 200.0;
}

public class TariffSlot
{
    public string Name { get; set; } = string.Empty;
    public double PricePerKwh { get; set; }
    public string StartTime { get; set; } = "00:00";
    public string EndTime { get; set; } = "00:00";
    public List<int>? DaysOfWeek { get; set; }

    public TimeSpan ParsedStart => TimeSpan.Parse(StartTime);
    public TimeSpan ParsedEnd => TimeSpan.Parse(EndTime);

    public bool IsActiveAt(DateTime localTime)
    {
        if (DaysOfWeek is { Count: > 0 })
        {
            int isoDow = localTime.DayOfWeek == DayOfWeek.Sunday
                ? 7
                : (int)localTime.DayOfWeek;
            if (!DaysOfWeek.Contains(isoDow)) return false;
        }

        var tod = localTime.TimeOfDay;
        var start = ParsedStart;
        var end = ParsedEnd;

        if (start == end) return true;
        if (start < end) return tod >= start && tod < end;
        return tod >= start || tod < end;
    }
}

public class TariffEngine
{
    private readonly TariffConfig _config;
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
        double? forecastTomorrowWh = null,
        double? estimatedConsumptionNextHoursWh = null,
        double? forecastThisHourWh = null,
        double? forecastNextHourWh = null,
        double? forecastRemainingTodayWh = null,
        double totalBatteryCapacityWh = 0,
        double avgBatterySocPercent = 0,
        double avgBatterySoftMaxPercent = 80)
    {
        var activeSlot = GetActiveSlot(localTime);
        double? price = activeSlot?.PricePerKwh;
        bool isFavorable = IsGridChargeFavorable(localTime);

        int horizon = _config.SolarForecastHorizonHours;
        double avgSolar = solarForecastWm2.Take(horizon).DefaultIfEmpty(0).Average();

        // Open-Meteo W/m² : signal générique
        bool solarExpectedFromMeteo = avgSolar >= _config.MinSolarForecastForGridBlock;

        // HA Forecast Wh : signal installation-spécifique, plus précis
        // Si le forecast HA prédit assez d'énergie aujourd'hui → le solaire couvrira la demande
        bool solarExpectedFromHa = forecastTodayWh.HasValue
            && forecastTodayWh.Value >= _config.MinHaForecastWhForGridBlock;

        // OR logique : si l'un ou l'autre signal prédit du solaire → bloquer la charge réseau
        bool solarExpected = solarExpectedFromMeteo || solarExpectedFromHa;

        // ── Bilan énergétique journalier (Feature 4) ─────────────────────────
        // EnergyDeficitTodayWh = énergie nécessaire pour remplir les batteries - solaire restant.
        // Si le solaire restant couvre le déficit → bloquer la charge réseau même en HC.
        double? energyDeficitTodayWh = null;
        bool gridChargeBlockedBySolarSufficiency = false;

        if (forecastRemainingTodayWh.HasValue && totalBatteryCapacityWh > 0)
        {
            double energyNeededWh = (avgBatterySoftMaxPercent - avgBatterySocPercent) / 100.0
                                    * totalBatteryCapacityWh;
            energyNeededWh = Math.Max(0, energyNeededWh);

            energyDeficitTodayWh = energyNeededWh - forecastRemainingTodayWh.Value;

            // Si le solaire restant couvre le besoin batterie → pas besoin de charger du réseau
            if (energyDeficitTodayWh <= 0)
            {
                gridChargeBlockedBySolarSufficiency = true;
                _logger.LogDebug(
                    "Energy balance: need={Need:F0}Wh, solar_remaining={Solar:F0}Wh → deficit={Deficit:F0}Wh " +
                    "(solar sufficient — grid charge blocked)",
                    energyNeededWh, forecastRemainingTodayWh.Value, energyDeficitTodayWh);
            }
            else
            {
                _logger.LogDebug(
                    "Energy balance: need={Need:F0}Wh, solar_remaining={Solar:F0}Wh → deficit={Deficit:F0}Wh " +
                    "(grid charge needed)",
                    energyNeededWh, forecastRemainingTodayWh.Value, energyDeficitTodayWh);
            }
        }

        // GridChargeAllowed : bloqué aussi si le bilan journalier est positif (solaire suffisant)
        bool gridChargeAllowed = isFavorable
            && !solarExpected
            && !gridChargeBlockedBySolarSufficiency
            && _config.Slots.Any();

        double? maxFuture = GetMaxPriceNextHours(localTime, 24);
        double savings = (maxFuture ?? 0) - (price ?? 0);

        double? hoursRemainingInSlot = null;
        if (isFavorable && activeSlot is not null)
            hoursRemainingInSlot = ComputeHoursRemainingInSlot(activeSlot, localTime);

        double? hoursUntilSolar = ComputeHoursUntilSolar(localTime, solarForecastWm2);

        // ── Intraday Solcast curve (Feature 3) ───────────────────────────────
        // On construit un tableau [Wh/h] à partir des entités Solcast HA :
        //   [0] = this_hour, [1] = next_hour, [2] = extrapolation linéaire (decay de next_hour)
        // Cette courbe remplace SolarFractionBetweenHours() quand elle est disponible.
        double[]? solcastHourlyCurveWh = null;
        double? forecastNext3HoursWh   = null;

        if (forecastNextHourWh.HasValue)
        {
            double thisH = forecastThisHourWh ?? forecastNextHourWh.Value;
            double nextH = forecastNextHourWh.Value;
            // Extrapolation heure+2 : moyenne pondérée (decay vers next_hour)
            double h2 = nextH * 0.85; // légère décroissance conservative

            solcastHourlyCurveWh = [thisH, nextH, h2];
            forecastNext3HoursWh = thisH + nextH + h2;

            _logger.LogDebug(
                "Solcast intraday curve: [{H0:F0}, {H1:F0}, {H2:F0}] Wh → next3h={N3:F0}Wh",
                thisH, nextH, h2, forecastNext3HoursWh);
        }

        return new TariffContext(
            ActiveSlotName: activeSlot?.Name,
            CurrentPricePerKwh: price,
            IsFavorableForGrid: isFavorable,
            GridChargeAllowed: gridChargeAllowed,
            AvgSolarForecastWm2: avgSolar,
            SolarExpectedSoon: solarExpected,
            SolarExpectedFromHa: solarExpectedFromHa,
            HoursToNextFavorable: HoursUntilNextFavorableTariff(localTime),
            MaxSavingsPerKwh: Math.Max(0, savings),
            ExportPricePerKwh: _config.ExportPricePerKwh,
            HoursRemainingInSlot: hoursRemainingInSlot,
            HoursUntilSolar: hoursUntilSolar,
            SolarForecastWm2: solarForecastWm2,
            ForecastTodayWh: forecastTodayWh,
            ForecastTomorrowWh: forecastTomorrowWh,
            HasLowForecastTomorrow: forecastTomorrowWh.HasValue
                                     && forecastTomorrowWh.Value < _config.LowForecastTomorrowWh,
            EveningBoostPercent: _config.EveningBoostPercent,
            LazyBufferHours: _config.LazyBufferHours,
            EstimatedConsumptionNextHoursWh: estimatedConsumptionNextHoursWh,
            ForecastThisHourWh: forecastThisHourWh,
            ForecastNextHourWh: forecastNextHourWh,
            ForecastNext3HoursWh: forecastNext3HoursWh,
            ForecastRemainingTodayWh: forecastRemainingTodayWh,
            SolcastHourlyCurveWh: solcastHourlyCurveWh,
            EnergyDeficitTodayWh: energyDeficitTodayWh,
            GridChargeBlockedBySolarSufficiency: gridChargeBlockedBySolarSufficiency
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
    /// <summary>True si le blocage vient spécifiquement du forecast HA (plus précis qu'Open-Meteo).</summary>
    bool SolarExpectedFromHa,
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
    double? ForecastTomorrowWh,
    /// <summary>True si demain est prévu sous le seuil LowForecastTomorrowWh → boost SoftMax en HC.</summary>
    bool HasLowForecastTomorrow,
    /// <summary>Bonus SoftMax (%) quand demain est mauvais et qu'on est dans un créneau favorable.</summary>
    double EveningBoostPercent,
    /// <summary>Marge de sécurité en heures pour le Lazy Charging (décalage du démarrage vers la fin du slot HC).</summary>
    double LazyBufferHours,
    /// <summary>
    /// Consommation maison estimée sur les prochaines heures (Wh).
    /// Calculée par rolling average des N derniers cycles × horizon de projection.
    /// Null si aucune entité de consommation n'est configurée ou si insuffisamment de données.
    /// Utilisée dans ComputeAdaptiveGridChargeW pour augmenter la charge réseau en anticipation
    /// d'une forte conso prévue (ex: four, EV) qui réduirait l'autoconsommation solaire.
    /// </summary>
    double? EstimatedConsumptionNextHoursWh,

    // ── Intraday Solcast forecast ────────────────────────────────────────────
    /// <summary>
    /// Production Solcast CETTE HEURE (Wh). Null si non configuré.
    /// Permet de savoir si le solaire monte en ce moment.
    /// </summary>
    double? ForecastThisHourWh,
    /// <summary>
    /// Production Solcast L'HEURE SUIVANTE (Wh). Null si non configuré.
    /// Si élevé → ne pas charger depuis le réseau, le solaire arrive dans &lt; 1h.
    /// </summary>
    double? ForecastNextHourWh,
    /// <summary>
    /// Somme des 3 prochaines heures Solcast (Wh) : this_hour + next_hour + heure_après.
    /// Construit depuis ForecastThisHourWh + ForecastNextHourWh + extrapolation linéaire.
    /// Null si ForecastNextHourWh est absent.
    /// </summary>
    double? ForecastNext3HoursWh,
    /// <summary>
    /// Production Solcast RESTANTE AUJOURD'HUI (Wh). Null si non configuré.
    /// Utilisé pour le bilan énergétique journalier (Feature 4).
    /// </summary>
    double? ForecastRemainingTodayWh,
    /// <summary>
    /// Courbe Solcast horaire réelle [Wh/h] reconstituée depuis les entités HA.
    /// Index 0 = heure courante, 1 = heure suivante, etc.
    /// Null si les entités intraday ne sont pas configurées.
    /// Remplace SolarFractionBetweenHours() dans ComputeAdaptiveGridChargeW.
    /// </summary>
    double[]? SolcastHourlyCurveWh,

    // ── Bilan énergétique journalier (Feature 4) ─────────────────────────────
    /// <summary>
    /// Déficit énergétique aujourd'hui (Wh) :
    ///   capacity × (softMax − avgSoc) − ForecastRemainingTodayWh
    /// Positif → les batteries ne seront pas remplies par le solaire seul → charge réseau justifiée.
    /// Négatif/nul → le solaire restant suffit → bloquer la charge réseau même en HC.
    /// Null si ForecastRemainingTodayWh est absent (pas de calcul possible).
    /// </summary>
    double? EnergyDeficitTodayWh,
    /// <summary>
    /// True si la charge réseau est bloquée car le solaire restant aujourd'hui
    /// est suffisant pour couvrir le déficit batterie (EnergyDeficitTodayWh ≤ 0).
    /// Motif de blocage plus précis que SolarExpectedSoon (qui est binaire).
    /// </summary>
    bool GridChargeBlockedBySolarSufficiency
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

    /// <summary>
    /// True si les entités Solcast intraday sont disponibles.
    /// Quand true, SolcastHourlyCurveWh remplace SolarFractionBetweenHours() dans le calcul adaptatif.
    /// </summary>
    public bool HasIntradayForecast => SolcastHourlyCurveWh is { Length: > 0 };

    /// <summary>
    /// True si le solaire est suffisant dans les prochaines heures selon Solcast intraday.
    /// Utilisé pour bloquer la charge réseau quand le solaire arrive dans &lt; 2h.
    /// Seuil configurable via MinSolarNextHoursWhForGridBlock dans TariffConfig.
    /// </summary>
    public bool SolarSufficientSoon =>
        ForecastNext3HoursWh.HasValue && ForecastNext3HoursWh.Value > 0;
}