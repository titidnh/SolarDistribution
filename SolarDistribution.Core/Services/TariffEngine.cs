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
    /// [HA Forecast] Wh threshold below which the day is considered "low solar".
    /// If ForecastTodayWh >= this value → blocks grid charging (solar will cover demand).
    /// Default 500 Wh: below this, we do not rely on solar to fill the batteries.
    /// Example: 2 kWp installation → set ~800 Wh; 4 kWp → ~1500 Wh.
    /// </summary>
    public double MinHaForecastWhForGridBlock { get; set; } = 500.0;

    /// <summary>
    /// [HA Forecast J+1] Below this threshold (Wh), tomorrow is considered "bad".
    /// When in a favourable tariff slot (IsFavorableForGrid), the batteries' SoftMax
    /// is increased by EveningBoostPercent to maximise the reserve.
    /// Default 1000 Wh.
    /// </summary>
    public double LowForecastTomorrowWh { get; set; } = 1000.0;

    /// <summary>
    /// SoftMax bonus (percentage points) added when tomorrow is forecast bad
    /// AND we are in a favourable tariff slot (off-peak, weekend, etc.).
    /// Default 10% → if SoftMax = 80%, rises to 90% during off-peak hours.
    /// </summary>
    public double EveningBoostPercent { get; set; } = 10.0;

    /// <summary>
    /// Lazy Charging — safety buffer (in hours) added to the estimated charge duration
    /// to compute the optimal start time in off-peak.
    ///
    /// Principle: rather than charging immediately when the off-peak slot opens at low power,
    /// the worker waits until the latest possible time, then charges at full power
    /// just before the slot ends (maximises self-powered time, reduces BMS cycles).
    ///
    ///   start time = slot_end - hoursNeeded - lazy_buffer_hours
    ///
    /// Example: off-peak slot 22h→7h (9h), needs 0.5h at 1000W, buffer=0.5h
    ///   → start at 06h00 (9h - 0.5h - 0.5h = 8h waiting from 22h)
    ///
    /// Recommended value: 0.5 (30 min).
    /// Increase if batteries frequently fail to reach target (SOC overestimation or drift).
    /// Set to 0 to disable lazy charging (original behaviour).
    /// </summary>
    public double LazyBufferHours { get; set; } = 0.5;

    /// <summary>
    /// [OPTIONAL] HA entity exposing the current spot price in €/kWh.
    /// Ex : "sensor.tibber_current_price", "sensor.eneco_current_price",
    ///      "sensor.belpower_current_price", "sensor.epex_current_price"
    ///
    /// When this entity is configured AND readable, its price overrides the
    /// active YAML slot in GetCurrentPricePerKwh().
    /// YAML slots are still used as fallback if the read fails.
    ///
    /// Leave null (or comment out in config.yaml) to disable dynamic mode.
    /// </summary>
    public string? CurrentPriceEntity { get; set; }

    /// <summary>
    /// Multiplier for the dynamic grid charge threshold.
    /// grid_charge_threshold = avg_24h × DynamicThresholdFactor
    ///
    /// Example: 24h average price = 0.20 €/kWh, factor = 0.8
    ///   → dynamic threshold = 0.16 €/kWh
    ///   → allows charging when price is 20% below the daily average
    ///
    /// Recommended value: 0.8 (charge when price < 80% of average).
    /// Set 1.0 to charge whenever price is below average.
    /// Ignored if CurrentPriceEntity is null.
    /// </summary>
    public double DynamicThresholdFactor { get; set; } = 0.8;

    public List<TariffSlot> Slots { get; set; } = new List<TariffSlot>();

    /// <summary>
    /// [Intraday] Wh threshold over the next 3 hours above which grid charging
    /// is reduced because solar is arriving soon.
    /// If ForecastNext3HoursWh >= this value → grid charging is proportionally reduced.
    /// Default 200 Wh (= 200W average over 1h ≈ modest solar production).
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

    // ── Dynamic spot price (Feature 5) ───────────────────────────────────────
    // Fed by HomeAssistantDataReader each cycle if CurrentPriceEntity is configured.
    // Null = no spot price available → fallback to YAML slots.
    private double? _liveSpotPrice;

    // Rolling 24h spot price history for computing the dynamic threshold.
    // Each entry = (UTC timestamp, price €/kWh). Maximum 24h of data retained.
    private readonly List<(DateTime Ts, double Price)> _spotPriceHistory = new();
    private const int SpotHistoryMaxHours = 24;

    public TariffEngine(TariffConfig config, ILogger<TariffEngine>? logger = null)
    {
        _config = config;
        _logger = logger ?? NullLogger<TariffEngine>.Instance;
    }

    /// <summary>
    /// Updates the live spot price from HA.
    /// Called by HomeAssistantDataReader after each successful read.
    /// Also feeds the rolling 24h history for dynamic threshold computation.
    /// </summary>
    public void UpdateSpotPrice(double? pricePerKwh)
    {
        _liveSpotPrice = pricePerKwh;

        if (pricePerKwh.HasValue)
        {
            var now = DateTime.UtcNow;
            _spotPriceHistory.Add((now, pricePerKwh.Value));

            // Purge entries older than 24h
            var cutoff = now.AddHours(-SpotHistoryMaxHours);
            _spotPriceHistory.RemoveAll(e => e.Ts < cutoff);
        }
    }

    /// <summary>
    /// Computes the dynamic grid charge threshold = avg_24h × DynamicThresholdFactor.
    /// Returns null if fewer than 3 history points exist (fallback to static YAML threshold).
    /// </summary>
    public double? ComputeDynamicThreshold()
    {
        if (_spotPriceHistory.Count < 3) return null;
        double avg24h = _spotPriceHistory.Average(e => e.Price);
        return avg24h * _config.DynamicThresholdFactor;
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

    /// <summary>
    /// Current price in €/kWh.
    /// Priority: live HA spot price (if CurrentPriceEntity configured AND read OK)
    ///           → fallback: active YAML slot → null if no active slot.
    /// </summary>
    public double? GetCurrentPricePerKwh(DateTime localTime)
    {
        if (_config.CurrentPriceEntity is not null && _liveSpotPrice.HasValue)
            return _liveSpotPrice.Value;

        return GetActiveSlot(localTime)?.PricePerKwh;
    }

    /// <summary>
    /// True if the current tariff is favourable for charging from the grid.
    /// Dynamic mode: spot price &lt; dynamic_threshold (rolling 24h × factor).
    ///   → Logs the threshold used for decision traceability.
    /// Static mode: current price &lt; GridChargeThresholdPerKwh (YAML).
    /// </summary>
    public bool IsGridChargeFavorable(DateTime localTime)
    {
        if (_config.GridChargeThresholdPerKwh <= 0) return false;

        var price = GetCurrentPricePerKwh(localTime);
        if (!price.HasValue) return false;

        bool isDynamic = _config.CurrentPriceEntity is not null && _liveSpotPrice.HasValue;

        if (isDynamic)
        {
            double threshold = ComputeDynamicThreshold() ?? _config.GridChargeThresholdPerKwh;
            bool favorable = price.Value < threshold;

            _logger.LogDebug(
                "Dynamic tariff: spot={Spot:F4}€/kWh threshold={Thr:F4}€/kWh " +
                "(avg24h={Avg:F4} × {Factor}) → {Result}",
                price.Value,
                threshold,
                _spotPriceHistory.Count >= 3 ? _spotPriceHistory.Average(e => e.Price) : 0,
                _config.DynamicThresholdFactor,
                favorable ? "FAVORABLE" : "not favorable");

            return favorable;
        }

        return price.Value < _config.GridChargeThresholdPerKwh;
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

        // Open-Meteo W/m²: generic signal
        bool solarExpectedFromMeteo = avgSolar >= _config.MinSolarForecastForGridBlock;

        // HA Forecast Wh: installation-specific signal, more precise.
        // If HA forecast predicts enough energy today → solar will cover demand.
        // However, without consumption data we cannot determine how much solar
        // actually reaches the batteries — household consumption may absorb most
        // of it. In that case, do not use the HA forecast to block grid charging;
        // the Open-Meteo radiation signal (solarExpectedFromMeteo) still provides
        // a basic guard. This prevents batteries from being denied HC charging
        // when the forecast looks good but consumption is unknown.
        bool solarExpectedFromHa = forecastTodayWh.HasValue
            && forecastTodayWh.Value >= _config.MinHaForecastWhForGridBlock
            && estimatedConsumptionNextHoursWh.HasValue;

        // Logical OR: if either signal predicts solar → block grid charging
        bool solarExpected = solarExpectedFromMeteo || solarExpectedFromHa;

        // ── Daily energy balance (Feature 4) ─────────────────────────────────
        // EnergyDeficitTodayWh = energy needed to fill batteries - remaining solar.
        // If remaining solar covers the deficit → block grid charging even in off-peak.
        double? energyDeficitTodayWh = null;
        bool gridChargeBlockedBySolarSufficiency = false;

        if (forecastRemainingTodayWh.HasValue && totalBatteryCapacityWh > 0)
        {
            double energyNeededWh = (avgBatterySoftMaxPercent - avgBatterySocPercent) / 100.0
                                    * totalBatteryCapacityWh;
            energyNeededWh = Math.Max(0, energyNeededWh);

            // Deduct estimated consumption from available solar when known.
            // Without consumption data, use raw forecast (overestimate) but do NOT
            // block grid charging — we can't reliably determine solar sufficiency.
            double availableSolarWh = forecastRemainingTodayWh.Value;
            if (estimatedConsumptionNextHoursWh.HasValue)
                availableSolarWh = Math.Max(0, availableSolarWh - estimatedConsumptionNextHoursWh.Value);

            energyDeficitTodayWh = energyNeededWh - availableSolarWh;

            // Only block grid charging when consumption is known.
            // Without consumption data the balance overestimates solar availability
            // (all production assumed free for batteries) which can leave batteries
            // uncharged during off-peak, triggering emergency charges later at HP rates.
            if (energyDeficitTodayWh <= 0 && estimatedConsumptionNextHoursWh.HasValue)
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

        // GridChargeAllowed: also blocked if the daily balance is positive (solar sufficient)
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
            // Build a [Wh/h] array from HA Solcast entities:
            //   [0] = this_hour, [1] = next_hour, [2] = linear extrapolation (decay from next_hour)
            // This curve replaces SolarFractionBetweenHours() when available.
        double[]? solcastHourlyCurveWh = null;
        double? forecastNext3HoursWh = null;

        if (forecastNextHourWh.HasValue)
        {
            double thisH = forecastThisHourWh ?? forecastNextHourWh.Value;
            double nextH = forecastNextHourWh.Value;
            // Hour+2 extrapolation: weighted average (decay toward next_hour)
            double h2 = nextH * 0.85; // slight conservative decay

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
            GridChargeBlockedBySolarSufficiency: gridChargeBlockedBySolarSufficiency,
            // Feature 5 — Dynamic tariff
            IsDynamicTariff: _config.CurrentPriceEntity is not null && _liveSpotPrice.HasValue,
            SpotPricePerKwh: _liveSpotPrice,
            DynamicThresholdPerKwh: ComputeDynamicThreshold()
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
    /// <summary>True if the block comes specifically from HA forecast (more precise than Open-Meteo).</summary>
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
    /// <summary>True if tomorrow is forecast below LowForecastTomorrowWh threshold → boost SoftMax in off-peak.</summary>
    bool HasLowForecastTomorrow,
    /// <summary>SoftMax bonus (%) when tomorrow is poor and current slot is favorable.</summary>
    double EveningBoostPercent,
    /// <summary>Safety margin in hours for Lazy Charging (delays start toward end of off-peak slot).</summary>
    double LazyBufferHours,
    /// <summary>
    /// Estimated home consumption over the next hours (Wh).
    /// Computed from rolling average over last N cycles × projection horizon.
    /// Null if no consumption entity is configured or insufficient data.
    /// Used in ComputeAdaptiveGridChargeW to increase grid charging in anticipation
    /// of high expected load (e.g., oven, EV) that would reduce solar self-consumption.
    /// </summary>
    double? EstimatedConsumptionNextHoursWh,

    // ── Intraday Solcast forecast ────────────────────────────────────────────
    /// <summary>
    /// Solcast production THIS HOUR (Wh). Null if not configured.
    /// Helps determine whether solar is ramping up now.
    /// </summary>
    double? ForecastThisHourWh,
    /// <summary>
    /// Solcast production NEXT HOUR (Wh). Null if not configured.
    /// If high → do not charge from grid, solar arrives in &lt; 1h.
    /// </summary>
    double? ForecastNextHourWh,
    /// <summary>
    /// Sum of next 3 Solcast hours (Wh): this_hour + next_hour + hour_after.
    /// Built from ForecastThisHourWh + ForecastNextHourWh + linear extrapolation.
    /// Null si ForecastNextHourWh est absent.
    /// </summary>
    double? ForecastNext3HoursWh,
    /// <summary>
    /// REMAINING Solcast production TODAY (Wh). Null if not configured.
    /// Used for daily energy balance (Feature 4).
    /// </summary>
    double? ForecastRemainingTodayWh,
    /// <summary>
    /// Real hourly Solcast curve [Wh/h] rebuilt from HA entities.
    /// Index 0 = current hour, 1 = next hour, etc.
    /// Null if intraday entities are not configured.
    /// Remplace SolarFractionBetweenHours() dans ComputeAdaptiveGridChargeW.
    /// </summary>
    double[]? SolcastHourlyCurveWh,

    // ── Daily energy balance (Feature 4) ────────────────────────────────────
    /// <summary>
    /// Energy deficit today (Wh):
    ///   capacity × (softMax − avgSoc) − ForecastRemainingTodayWh
    /// Positive → batteries will not be filled by solar alone → grid charge justified.
    /// Negative/zero → remaining solar is sufficient → block grid charging even in off-peak.
    /// Null si ForecastRemainingTodayWh est absent (pas de calcul possible).
    /// </summary>
    double? EnergyDeficitTodayWh,
    /// <summary>
    /// True if grid charging is blocked because remaining solar today
    /// is sufficient to cover battery deficit (EnergyDeficitTodayWh <= 0).
    /// More precise block reason than SolarExpectedSoon (binary).
    /// </summary>
    bool GridChargeBlockedBySolarSufficiency,

    // ── Dynamic SPOT tariff (Feature 5) ─────────────────────────────────────
    /// <summary>
    /// True if price comes from a live HA entity (dynamic SPOT mode).
    /// False = static YAML mode (hard-coded off-peak/peak slots).
    /// </summary>
    bool IsDynamicTariff,
    /// <summary>
    /// Current spot price read from HA (€/kWh). Null if not configured or read failed.
    /// In dynamic mode, this price replaces YAML slot price in all decisions.
    /// </summary>
    double? SpotPricePerKwh,
    /// <summary>
    /// Dynamically computed grid charge threshold = avg_24h × DynamicThresholdFactor.
    /// Null if fewer than 3 history points (fallback to YAML GridChargeThresholdPerKwh).
    /// Logged for traceability of grid-charge decisions.
    /// </summary>
    double? DynamicThresholdPerKwh
)
{
    public double NormalizedPrice => CurrentPricePerKwh.HasValue
        ? Math.Min(1.0, CurrentPricePerKwh.Value / 0.40)
        : 0.5;

    /// <summary>
    /// True if high-quality HA forecasts (installation-specific) are available.
    /// When true, algorithm uses ForecastTodayWh/TomorrowWh instead of generic model.
    /// </summary>
    public bool HasHaForecast => ForecastTodayWh.HasValue || ForecastTomorrowWh.HasValue;

    /// <summary>
    /// True if intraday Solcast entities are available.
    /// When true, SolcastHourlyCurveWh replaces SolarFractionBetweenHours() in adaptive computation.
    /// </summary>
    public bool HasIntradayForecast => SolcastHourlyCurveWh is { Length: > 0 };

    /// <summary>
    /// True if solar is sufficient in the next hours according to intraday Solcast.
    /// Used to block grid charging when solar arrives within &lt; 2h.
    /// Threshold configurable via MinSolarNextHoursWhForGridBlock in TariffConfig.
    /// </summary>
    public bool SolarSufficientSoon =>
        ForecastNext3HoursWh.HasValue && ForecastNext3HoursWh.Value > 0;
}
