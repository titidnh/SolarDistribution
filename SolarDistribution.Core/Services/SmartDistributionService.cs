using Microsoft.Extensions.Logging;
using SolarDistribution.Core.Models;
using SolarDistribution.Core.Services.ML;
using SolarDistribution.Core.Repositories;

namespace SolarDistribution.Core.Services;

public class SmartDistributionService
{
    private readonly IBatteryDistributionService _algo;
    private readonly IDistributionMLService _ml;
    private readonly IWeatherService _weather;
    private readonly IDistributionRepository _repo;
    private readonly TariffEngine _tariff;
    private readonly IDistributionSessionFactory _sessionFactory;
    private readonly ILogger<SmartDistributionService> _logger;

    // ── Feature 6 — Yesterday self-sufficiency cache ───────────────────────────
    // Refreshed once per day (UTC date change) to avoid a DB query
    // on every distribution cycle (~1 min). Null = not yet loaded or
    // Solcast not configured.
    private double? _cachedYesterdaySelfSufficiency;
    private int _cachedYesterdayDoy = -1;
    private readonly object _cacheLock = new();

    // ── Emergency charge hysteresis ──────────────────────────────────────────
    // Tracks batteries currently in emergency grid charge mode.
    // Entry:  SOC < EmergencyGridChargeBelowPercent (e.g. 20%)
    // Exit:   SOC >= EmergencyGridChargeTargetPercent (e.g. 50%)
    // Without this, emergency would stop as soon as SOC > 20% (next cycle),
    // never reaching the configured target of 50%.
    private readonly HashSet<int> _emergencyActiveBatteries = new();

    public SmartDistributionService(
        IBatteryDistributionService algo,
        IDistributionMLService ml,
        IWeatherService weather,
        IDistributionRepository repo,
        TariffEngine tariff,
        IDistributionSessionFactory sessionFactory,
        ILogger<SmartDistributionService> logger)
    {
        _algo = algo; _ml = ml; _weather = weather;
        _repo = repo; _tariff = tariff;
        _sessionFactory = sessionFactory; _logger = logger;
    }

    public async Task<SmartDistributionResult> DistributeAsync(
        double surplusW,
        IList<Battery> batteries,
        double latitude,
        double longitude,
        WeatherData? weatherSnapshot = null,
        double? forecastTodayWh = null,
        double? forecastTomorrowWh = null,
        double? estimatedConsumptionNextHoursWh = null,
        double? estimatedConsumptionAverageW = null,
        double? measuredConsumptionW = null,
        double? forecastThisHourWh = null,
        double? forecastNextHourWh = null,
        double? forecastRemainingTodayWh = null,
        double? forecastTodayWhAtStartOfDay = null,
        CancellationToken ct = default)
    {
        // ── 1. Weather ───────────────────────────────────────────────────────
        var wx = weatherSnapshot ?? await _weather.GetCurrentWeatherAsync(latitude, longitude, ct);
        if (wx is null)
            _logger.LogWarning("Weather unavailable — proceeding without weather context");

        // ── 2. Tariff context ────────────────────────────────────────────────
        var localNow = DateTime.Now;
        var radForecast = wx?.RadiationForecast12h ?? Array.Empty<double>();
        var tariffCtx = _tariff.EvaluateContext(
            localNow, radForecast,
            forecastTodayWh, forecastTomorrowWh,
            estimatedConsumptionNextHoursWh,
            forecastThisHourWh, forecastNextHourWh, forecastRemainingTodayWh,
            totalBatteryCapacityWh: batteries.Sum(b => b.CapacityWh),
            avgBatterySocPercent: batteries.Any() ? batteries.Average(b => b.CurrentPercent) : 0,
            avgBatterySoftMaxPercent: batteries.Any() ? batteries.Average(b => b.SoftMaxPercent) : 80);

        LogTariffContext(tariffCtx, surplusW);

        // ── 2b. Feature 6 — Yesterday self-sufficiency (daily cache) ─────────────
        int todayDoy = DateTime.UtcNow.DayOfYear;
        bool needsRefresh;
        lock (_cacheLock) { needsRefresh = todayDoy != _cachedYesterdayDoy; }
        if (needsRefresh)
        {
            var freshValue = await _repo.GetYesterdaySelfSufficiencyAsync(ct);
            lock (_cacheLock)
            {
                _cachedYesterdaySelfSufficiency = freshValue;
                _cachedYesterdayDoy = todayDoy;
            }
            _logger.LogDebug(
                "Feature 6: YesterdaySelfSufficiency refreshed → {Pct}%",
                freshValue?.ToString("F1") ?? "n/a");
        }

        // ── 3. Features ML ────────────────────────────────────────────────────
        MLRecommendation? mlReco = null;
        string decisionEngine = "Deterministic";

        double? yesterdaySS;
        lock (_cacheLock) { yesterdaySS = _cachedYesterdaySelfSufficiency; }

        if (wx is not null)
        {
            var features = BuildFeatures(
                surplusW, batteries, wx, tariffCtx,
                yesterdaySS);
            mlReco = await _ml.PredictAsync(features, ct);
        }

        // ── 4. Effective batteries ───────────────────────────────────────────
        IList<Battery> effective;

        if (mlReco is not null)
        {
            effective = Apply(batteries, mlReco, tariffCtx, surplusW, wx, estimatedConsumptionAverageW: estimatedConsumptionAverageW);
            decisionEngine = mlReco.ConfidenceScore >= 0.75 ? "ML" : "ML-Fallback";
            _logger.LogInformation(
                "ML: softMax={SoftMax:F1}%, preventive={Prev:F1}%, confidence={Conf:P0} [{Engine}]",
                mlReco.RecommendedSoftMaxPercent,
                mlReco.RecommendedPreventiveThreshold,
                mlReco.ConfidenceScore, decisionEngine);
        }
        else
        {
            effective = Apply(batteries, null, tariffCtx, surplusW, wx, estimatedConsumptionAverageW: estimatedConsumptionAverageW);
        }

        // ── 5. Log urgences + charge adaptative ──────────────────────────────
        foreach (var b in effective.Where(b => b.IsEmergencyGridCharge))
        {
            double target = b.EmergencyGridChargeTargetPercent ?? b.SoftMaxPercent;
            bool isNewEmergency = b.EmergencyGridChargeBelowPercent.HasValue
                && b.CurrentPercent < b.EmergencyGridChargeBelowPercent.Value;
            _logger.LogWarning(
                "⚡ EMERGENCY grid charge — Battery {Id}: SOC {Soc:F1}% " +
                "{Phase} target {Target:F0}% from grid (solar expected: {Solar})",
                b.Id, b.CurrentPercent,
                isNewEmergency ? $"< threshold {b.EmergencyGridChargeBelowPercent:F0}% — will charge to" : "— ongoing charge to",
                target,
                tariffCtx.SolarExpectedSoon ? "yes (skipped)" : "no");
        }

        foreach (var b in effective.Where(b => b.GridChargeAllowedW > 0 && !b.IsEmergencyGridCharge))
        {
            _logger.LogInformation(
                "🔋 Smart grid charge — Battery {Id}: SOC {Soc:F1}%→{SoftMax:F0}%, " +
                "{W:F0}W/{Max:F0}W ({Pct:F0}% of max) over {H:F1}h [{Slot}]{FcInfo}",
                b.Id, b.CurrentPercent, b.SoftMaxPercent,
                b.GridChargeAllowedW, b.MaxChargeRateW,
                b.MaxChargeRateW > 0 ? b.GridChargeAllowedW / b.MaxChargeRateW * 100 : 0,
                tariffCtx.HoursRemainingInSlot ?? 0,
                tariffCtx.ActiveSlotName,
                tariffCtx.HasHaForecast ? " [HA forecast]" : " [Open-Meteo]");
        }

        // Log lazy charge: eligible batteries waiting (GridChargeAllowedW == 0 in off-peak)
        if (tariffCtx.GridChargeAllowed)
        {
            foreach (var b in effective.Where(b => b.IsPreventiveGridChargeSkippedUntilSolar && b.GridChargeAllowedW == 0 && !b.IsEmergencyGridCharge))
            {
                _logger.LogInformation(
                    "⏳ Skip preventive HC charge — Battery {Id}: SOC {Soc:F1}% stays above floor {Floor:F1}% " +
                    "at first usable solar ({Projected:F1}% in {SolarH:F1}h) [{Slot}]",
                    b.Id,
                    b.CurrentPercent,
                    b.PreventiveChargeFloorPercent ?? 0,
                    b.ProjectedPercentAtMeaningfulSolar ?? b.CurrentPercent,
                    b.HoursUntilMeaningfulSolar ?? 0,
                    tariffCtx.ActiveSlotName);
            }

            foreach (var b in effective.Where(b => b.IsWaitingForMeaningfulSolar && b.GridChargeAllowedW == 0 && !b.IsEmergencyGridCharge))
            {
                _logger.LogInformation(
                    "⏳ Wait meaningful solar — Battery {Id}: SOC {Soc:F1}% (target {SoftMax:F0}%), " +
                    "first usable solar in {SolarH:F1}h, reserve={Reserve:F0}Wh, expected load={Load:F0}Wh before solar [{Slot}]",
                    b.Id,
                    b.CurrentPercent,
                    b.SoftMaxPercent,
                    b.HoursUntilMeaningfulSolar ?? 0,
                    b.FleetReserveAboveEmergencyWh ?? 0,
                    b.ExpectedLoadBeforeMeaningfulSolarWh ?? 0,
                    tariffCtx.ActiveSlotName);
            }

            foreach (var b in effective.Where(b => b.GridChargeAllowedW == 0
                && !b.IsWaitingForMeaningfulSolar
                && !b.IsPreventiveGridChargeSkippedUntilSolar
                && !b.IsEmergencyGridCharge
                && b.CurrentPercent < b.SoftMaxPercent - b.SocHysteresisPercent))
            {
                _logger.LogInformation(
                    "⏳ Lazy charge — Battery {Id}: SOC {Soc:F1}% (target {SoftMax:F0}%), " +
                    "waiting for end of [{Slot}] slot ({H:F1}h remaining) — will charge later",
                    b.Id, b.CurrentPercent, b.SoftMaxPercent,
                    tariffCtx.ActiveSlotName, tariffCtx.HoursRemainingInSlot ?? 0);
            }
        }

        // ── 6. Distribution ───────────────────────────────────────────────────
        var result = _algo.Distribute(surplusW, effective);

        if (result.GridChargedW > 0)
            _logger.LogInformation(
                "Grid charge: {W:F0}W [{Slot}] {Price:F3}€/kWh",
                result.GridChargedW, tariffCtx.ActiveSlotName, tariffCtx.CurrentPricePerKwh);

        // ── 7. Persistence ───────────────────────────────────────────────────
        var session = _sessionFactory.Build(result, wx, mlReco, decisionEngine, batteries, tariffCtx,
            measuredConsumptionW, forecastTodayWhAtStartOfDay);
        await _repo.SaveSessionAsync(session, ct);

        _logger.LogInformation(
            "Cycle done [{Engine}] solar={Solar:F0}W grid={Grid:F0}W unused={Unused:F0}W → session#{Id}",
            decisionEngine, result.TotalAllocatedW, result.GridChargedW, result.UnusedSurplusW, session.Id);

        return new SmartDistributionResult(result, decisionEngine, mlReco, wx, tariffCtx, session.Id);
    }

    // ── Apply: compute GridChargeAllowedW per battery ───────────────────────────────

    /// <summary>
    /// Decision priority:
    ///   1. SELF-CONSUMPTION: solar arrives before slot end AND battery can wait → 0W grid
    ///   2. SOC EMERGENCY: critical SOC + no solar → MaxChargeRateW (regardless of tariff)
    ///   3. SMART OFF-PEAK CHARGE: adaptive power computed by ComputeAdaptiveGridChargeW
    ///
    /// FIX Bug #4 — IdleChargeW without solar surplus:
    ///   IdleChargeW is set to 0 as soon as surplusW = 0, whether peak or off-peak.
    ///   Its purpose is to absorb residual P1 meter micro-surpluses (noise ±50W)
    ///   when the battery is already at its target — not to pull from the grid.
    ///   In off-peak without surplus, ComputeAdaptiveGridChargeW decides if grid
    ///   charging is warranted (weather, J+1 forecast, hours remaining in slot).
    ///
    /// End-of-day aggressiveness (FIX Bug #7):
    ///   As sunset approaches (< 2h), progressively reduce SoftMax to avoid excessive
    ///   charging when solar production is declining. This prevents overcharging when
    ///   the system can't maintain the planned charge rates.
    /// </summary>
    private IList<Battery> Apply(
        IList<Battery> src,
        MLRecommendation? reco,
        TariffContext tariff,
        double surplusW,
        WeatherData? wx,
        double? estimatedConsumptionAverageW = null,
        double minGridChargeW = 100.0,
        double urgencyThresholdHours = 1.0)
    {
        var emergencyActiveBatteries = _emergencyActiveBatteries;

        return src.Select(b =>
        {
            double baseSoftMax = reco?.RecommendedSoftMaxPercent ?? b.SoftMaxPercent;
            double softMax = baseSoftMax;
            bool stableAroundBaseSoftMax = b.CurrentPercent >= baseSoftMax - b.SocHysteresisPercent;
            double? hoursUntilMeaningfulSolar = GetHoursUntilMeaningfulSolarForBattery(b, tariff);
            bool meaningfulSolarWillArrive = hoursUntilMeaningfulSolar.HasValue
                && hoursUntilMeaningfulSolar.Value < double.MaxValue;
            double? projectedPercentAtMeaningfulSolar = GetProjectedPercentAtMeaningfulSolar(
                b,
                hoursUntilMeaningfulSolar);
            double preventiveChargeFloorPercent = GetPreventiveChargeFloorPercent(b);
            bool shouldUsePreventiveGridCharge = ShouldUsePreventiveGridChargeBeforeMeaningfulSolar(
                b,
                projectedPercentAtMeaningfulSolar,
                meaningfulSolarWillArrive);

            // ── J+1 Boost: bad tomorrow + favourable tariff → charge harder now ──
            // Logic: if ForecastTomorrow < threshold AND we are in a cheap slot (off-peak or
            // any IsFavorableForGrid slot), raise SoftMax to maximise the reserve.
            // Applies at any hour as long as the tariff is advantageous
            // (night off-peak at 2 AM, evening off-peak, weekends, etc.).
            // The boost is capped at HardMaxPercent to never exceed the battery limit.
            if (tariff.HasLowForecastTomorrow && tariff.IsFavorableForGrid)
            {
                double boosted = softMax + tariff.EveningBoostPercent;
                softMax = Math.Min(b.HardMaxPercent, boosted);
            }

            // ── FIX Bug #7 + CALC-03: End-of-day aggressiveness reduction ────────────
            // As sunset approaches, progressively reduce SoftMax target to avoid
            // aggressive charging when solar production is declining.
            //
            // CALC-03 fix: the reduction is applied only to the BOOST DELTA
            // (softMax - baseSoftMax), not to the full softMax. This prevents the
            // end-of-day reduction from cancelling the D+1 boost and dropping
            // below the original base SoftMax.
            //
            // Reduction schedule on the boost delta:
            //   HoursUntilSunset > 3h   : no reduction (normal operation)
            //   3h >= hours > 2h        : reduce boost delta by 30%
            //   2h >= hours > 1h        : reduce boost delta by 60%
            //   1h >= hours             : remove boost delta entirely
            if (wx is not null && wx.HoursUntilSunset <= 3)
            {
                double boostDelta = softMax - baseSoftMax;
                double reductionFactor = wx.HoursUntilSunset switch
                {
                    <= 1 => 1.0,  // Last hour: remove boost entirely
                    <= 2 => 0.6,
                    <= 3 => 0.3,
                    _ => 0
                };

                if (reductionFactor > 0 && boostDelta > 0)
                {
                    softMax -= boostDelta * reductionFactor;
                }
                // softMax never drops below baseSoftMax (the non-boosted value)
                softMax = Math.Max(baseSoftMax, softMax);
            }

            // ── End-of-slot aggressive charging: prevent cycling ────────────────────────────
            // Problem: at the end of a cheap tariff slot (e.g., HC Midi = 0.2h remaining),
            // battery charges to soft_max (80%), then drops slightly from self-discharge,
            // triggering another charge attempt 1 minute later (BMS cycling).
            //
            // Solution: near end of slot, charge to hard_max (100%) instead of soft_max,
            // BUT ONLY if solar won't cover the charge demand in the near future.
            // This fills the battery completely when solar is unlikely, avoiding cycling
            // until the next cheap slot.
            //
            // Conditions:
            //   · In a favorable (cheap) tariff slot → GridChargeAllowed
            //   · Close to end of slot (< 1 hour remaining)
            //   · Battery has headroom (CurrentPercent < HardMaxPercent)
            //   · Solar is NOT arriving soon enough to cover charge demand
            //       - No significant solar in next 3h forecast (ForecastNext3HoursWh ~0)
            //       - OR solar won't arrive before slot ends
            //
            // Effect: battery will have maximum reserve when expensive tariff returns,
            // reducing grid imports during peak hours. But respects solar arrival.
            if (tariff.GridChargeAllowed
                && tariff.HoursRemainingInSlot.HasValue
                && tariff.HoursRemainingInSlot.Value > 0
                && tariff.HoursRemainingInSlot.Value < 1.0  // Last hour of slot
                && !stableAroundBaseSoftMax          // Already stable near soft max → do not force hard max
                && b.CurrentPercent < b.HardMaxPercent)    // Battery not already at max
            {
                double energyNeededToHardMax = (b.HardMaxPercent - b.CurrentPercent) / 100.0 * b.CapacityWh;
                bool batteryCanWaitForMeaningfulSolar = meaningfulSolarWillArrive
                    && !shouldUsePreventiveGridCharge
                    && (!b.EmergencyGridChargeBelowPercent.HasValue
                        || b.CurrentPercent > b.EmergencyGridChargeBelowPercent.Value);
                
                // Check if solar is coming soon and could cover the charge demand
                bool solarCanCoverDemand = false;
                if (tariff.ForecastNext3HoursWh.HasValue && tariff.ForecastNext3HoursWh.Value > energyNeededToHardMax)
                {
                    // Significant solar in next 3h that can cover the demand
                    solarCanCoverDemand = true;
                }
                else if (tariff.HoursUntilSolar.HasValue
                         && tariff.HoursUntilSolar.Value < double.MaxValue
                         && tariff.HoursUntilSolar.Value <= tariff.HoursRemainingInSlot.Value)
                {
                    // Solar arriving before slot ends → wait for it
                    solarCanCoverDemand = true;
                }

                // Charge to hard_max ONLY if solar won't cover the demand soon
                if (!solarCanCoverDemand && !batteryCanWaitForMeaningfulSolar)
                {
                    softMax = b.HardMaxPercent;
                }
            }

            // ── Emergency hysteresis ─────────────────────────────────────────
            // Enter: SOC < EmergencyGridChargeBelowPercent (e.g. 20%)
            // Stay:  ongoing emergency AND SOC < EmergencyGridChargeTargetPercent (e.g. 50%)
            // Exit:  SOC >= target → emergency cleared
            // Without hysteresis the emergency would end at SOC > 20% (next cycle)
            // and the battery would never reach the configured target of 50%.
            //
            // NOTE: solarWillArrive is intentionally NOT used here.
            // Emergency is a safety mechanism that must fire whenever SOC drops
            // below the critical threshold, regardless of solar forecast.
            // Solar forecast doesn't help a battery that is already critically low
            // (forecast ≠ available power NOW, especially under 100% cloud cover).
            double emergencyTarget = b.EmergencyGridChargeTargetPercent ?? b.SoftMaxPercent;
            bool enteringEmergency = b.EmergencyGridChargeBelowPercent.HasValue
                && b.CurrentPercent < b.EmergencyGridChargeBelowPercent.Value;
            bool ongoingEmergency = emergencyActiveBatteries.Contains(b.Id)
                && b.CurrentPercent < emergencyTarget;
            bool isEmergency = enteringEmergency || ongoingEmergency;

            if (isEmergency)
                emergencyActiveBatteries.Add(b.Id);
            else
                emergencyActiveBatteries.Remove(b.Id);

            bool skipPreventiveGridChargeUntilSolar = !isEmergency
                && tariff.IsFavorableForGrid
                && meaningfulSolarWillArrive
                && !shouldUsePreventiveGridCharge
                && (!b.EmergencyGridChargeBelowPercent.HasValue
                    || b.CurrentPercent > b.EmergencyGridChargeBelowPercent.Value);

            double gridAllowedW = 0;

            if (isEmergency)
                gridAllowedW = b.MaxChargeRateW;
            else if (skipPreventiveGridChargeUntilSolar)
                gridAllowedW = 0;
            else if (tariff.GridChargeAllowed)
                gridAllowedW = ComputeAdaptiveGridChargeW(
                    b, softMax, tariff, minGridChargeW, urgencyThresholdHours, lazyBufferHours: tariff.LazyBufferHours);

            // ── FIX Bug #4 — IdleChargeW: solar surplus only ────────────────
            // IdleChargeW has a single purpose: absorb residual P1 meter micro-surpluses
            // when the battery is at its target (noise ±50W, BMS cycling).
            // It must NEVER pull from the grid, whether at peak or off-peak.
            //
            // In off-peak without surplus, ComputeAdaptiveGridChargeW decides whether to
            // charge (based on weather, J+1 forecast, hours remaining in slot).
            // Bypassing that logic with IdleChargeW at off-peak would be wrong:
            // we would charge even when the forecast predicts enough solar tomorrow.
            //
            // Rule: IdleChargeW > 0 only if actual solar surplus > 0.
            double effectiveIdleChargeW = surplusW > 0 ? b.IdleChargeW : 0;

            return new Battery
            {
                Id = b.Id,
                CapacityWh = b.CapacityWh,
                MaxChargeRateW = b.MaxChargeRateW,
                MinPercent = reco is null
                    ? b.MinPercent
                    : Math.Max(b.MinPercent, reco.RecommendedPreventiveThreshold),
                SoftMaxPercent = softMax,
                HardMaxPercent = b.HardMaxPercent,
                CurrentPercent = b.CurrentPercent,
                Priority = b.Priority,
                HardwareMinChargeW = b.HardwareMinChargeW,
                IdleChargeW = effectiveIdleChargeW,
                // Propagation needed so ComputeAdaptiveGridChargeW can access hysteresis
                SocHysteresisPercent = b.SocHysteresisPercent,
                GridChargeAllowedW = gridAllowedW,
                EmergencyGridChargeBelowPercent = b.EmergencyGridChargeBelowPercent,
                EmergencyGridChargeTargetPercent = isEmergency ? b.EmergencyGridChargeTargetPercent : null,
                IsEmergencyGridCharge = isEmergency,
                IsWaitingForMeaningfulSolar = false,
                IsPreventiveGridChargeSkippedUntilSolar = skipPreventiveGridChargeUntilSolar,
                HoursUntilMeaningfulSolar = hoursUntilMeaningfulSolar,
                ProjectedPercentAtMeaningfulSolar = projectedPercentAtMeaningfulSolar,
                PreventiveChargeFloorPercent = skipPreventiveGridChargeUntilSolar ? preventiveChargeFloorPercent : null,
                FleetReserveAboveEmergencyWh = null,
                ExpectedLoadBeforeMeaningfulSolarWh = null,
            };
        }).ToList();
    }

    /// <summary>
    /// Adaptive grid charge power in off-peak — with Lazy Charging.
    ///
    /// Lazy Charging principle:
    ///   Rather than charging immediately at low power when the off-peak slot opens,
    ///   compute the latest start time needed to reach the target before the slot ends,
    ///   then wait until that moment.
    ///
    ///   hoursNeeded = energyNeeded / MaxChargeRateW
    ///   hoursBeforeStart = hoursRemaining - hoursNeeded - lazyBuffer
    ///   → If hoursBeforeStart > 0: too early, return 0 (wait)
    ///   → Otherwise: start charging at full adaptive power
    ///
    /// Benefits:
    ///   - Maximises battery self-powered time before charging starts
    ///   - Charging happens at the end of the night, just before peak tariff returns (6h–7h)
    ///   - Higher power over a shorter duration = fewer partial BMS cycles
    ///   - Higher power over a short duration = fewer partial BMS cycles
    ///
    /// If HA forecasts are available (ForecastTodayWh), they replace
    /// the generic Open-Meteo × 0.15 calculation for estimating expected solar
    /// energy during the remaining slot.
    ///
    /// Logic:
    ///   gross energy = (SoftMax - SOC) × CapacityWh
    ///   expected solar (remaining slot):
    ///     - if HA forecast available → prorate ForecastTodayWh over remaining hours
    ///     - otherwise → Σ forecast[h] × solarEfficiencyFactor
    ///   net energy = max(0, gross - solar)
    ///   power = clamp(net / hoursNeeded, minGridChargeW, MaxChargeRateW)
    /// </summary>
    private static double ComputeAdaptiveGridChargeW(
        Battery b,
        double softMaxPercent,
        TariffContext tariff,
        double minGridChargeW,
        double urgencyThresholdHours,
        double solarEfficiencyFactor = 0.15,
        double lazyBufferHours = 0.5)
    {
        double hoursRemaining = tariff.HoursRemainingInSlot ?? 0;

        // ── FIX Bug #1: SOC hysteresis ────────────────────────────────────────────────────────
        // Original problem: when SOC reaches 90% then drops to 89.9%
        // (EcoFlow self-powered self-discharge), the calculation produced energyNeeded=1Wh
        // → targetW=0.18W → clamped to minGridChargeW=100W, BUT DistributeGridToGroup
        // does Math.Min(spaceToTarget=1Wh, gridLeft=100W) → final command = 1W.
        // Result: 50+ micro-commands ignored by the EcoFlow but counted as BMS cycles.
        //
        // With SocHysteresisPercent = 2%:
        //   · Effective threshold = softMax - hysteresis = 90% - 2% = 88%
        //   · Between 88% and 90% → return 0 (dead zone, self-discharge accepted)
        //   · SOC drops to 87.9% → energyNeeded = ~21Wh → command ≥ 100W (effective)
        //   · SocHysteresisPercent = 0 → behaviour identical to the original
        double rechargeThreshold = softMaxPercent - b.SocHysteresisPercent;
        if (b.CurrentPercent >= rechargeThreshold)
            return 0;
        // ─────────────────────────────────────────────────────────────────────

        if (hoursRemaining <= urgencyThresholdHours)
            return b.MaxChargeRateW;

        bool solarAfterSlot = !tariff.HoursUntilSolar.HasValue
            || tariff.HoursUntilSolar.Value >= double.MaxValue
            || tariff.HoursUntilSolar.Value > hoursRemaining;
        if (solarAfterSlot && hoursRemaining <= urgencyThresholdHours * 2)
            return b.MaxChargeRateW;

        double energyNeededWh = (softMaxPercent - b.CurrentPercent) / 100.0 * b.CapacityWh;

        // ── Expected solar energy during remaining hours ─────────────────────
        double solarExpectedWh;

        if (tariff.HasIntradayForecast && tariff.SolcastHourlyCurveWh is not null)
        {
            // ── Real Solcast hourly curve (Feature 3) ────────────────────────────────────────
            // Replaces the simplified sinusoidal profile: uses real Wh/h Solcast data
            // to compute the expected energy hour by hour.
            //
            // Advantages vs sinusoid:
            //   · Accounts for forecast cloud cover at specific hours
            //   · Integrates the actual orientation/tilt of the installation
            //   · Hourly precision vs daily approximation
            //
            // Integrate the Solcast curve over [now, now + hoursRemaining],
            // weighting the last partial-hour fraction.
            double solarStartH = tariff.HoursUntilSolar.HasValue
                                 && tariff.HoursUntilSolar.Value < double.MaxValue
                ? tariff.HoursUntilSolar.Value : 0.0;

            solarExpectedWh = 0;
            var curve = tariff.SolcastHourlyCurveWh;

            for (int h = 0; h < curve.Length && h < Math.Ceiling(hoursRemaining); h++)
            {
                if (h < solarStartH) continue;
                double hourFraction = Math.Min(1.0, hoursRemaining - h);
                solarExpectedWh += curve[h] * hourFraction;
            }

            // If the curve does not cover the full horizon (e.g. only 3h while 6h remain),
            // extrapolate via the sinusoidal fallback with ForecastTodayWh for the missing hours.
            if (curve.Length < hoursRemaining && tariff.ForecastTodayWh.HasValue)
            {
                double coveredH = curve.Length;
                double remainingUncoveredH = hoursRemaining - coveredH;
                double sunriseH = solarStartH;
                double sunsetH = sunriseH + 12.0;

                double fallbackFraction = SolarFractionBetweenHours(
                    coveredH, coveredH + remainingUncoveredH, sunriseH, sunsetH);
                solarExpectedWh += tariff.ForecastTodayWh.Value * fallbackFraction;
            }
        }
        else if (tariff.HasHaForecast && tariff.ForecastTodayWh.HasValue)
        {
            // Sinusoidal profile with ForecastTodayWh (no intraday entities configured)
            double solarStartH = tariff.HoursUntilSolar.HasValue
                                 && tariff.HoursUntilSolar.Value < double.MaxValue
                ? tariff.HoursUntilSolar.Value : 24.0;

            double solarHoursInSlot = Math.Max(0, hoursRemaining - solarStartH);

            double sunriseH = solarStartH;
            double sunsetH = sunriseH + 12.0;

            double solarFraction = SolarFractionBetweenHours(
                sunriseH, sunriseH + solarHoursInSlot, sunriseH, sunsetH);

            solarExpectedWh = tariff.ForecastTodayWh.Value * solarFraction;

            // If the slot crosses midnight and tomorrow is configured, add the J+1 share
            if (tariff.ForecastTomorrowWh.HasValue && solarStartH >= hoursRemaining && hoursRemaining > 0)
            {
                double tomorrowFraction = SolarFractionBetweenHours(0, solarHoursInSlot, 0, 12.0);
                solarExpectedWh += tariff.ForecastTomorrowWh.Value * tomorrowFraction;
            }
        }
        else
        {
            // Fallback Open-Meteo: W/m² × efficiency factor
            solarExpectedWh = 0;
            double solarStartH = tariff.HoursUntilSolar.HasValue
                                 && tariff.HoursUntilSolar.Value < double.MaxValue
                ? tariff.HoursUntilSolar.Value : double.MaxValue;

            var forecast = tariff.SolarForecastWm2;
            int forecastHours = (int)Math.Min(Math.Ceiling(hoursRemaining), forecast.Length);

            for (int h = 0; h < forecastHours; h++)
            {
                if (h < solarStartH) continue;
                double hourFraction = Math.Min(1.0, hoursRemaining - h);
                solarExpectedWh += forecast[h] * solarEfficiencyFactor * hourFraction;
            }
        }

        double netEnergyNeededWh = Math.Max(0, energyNeededWh - solarExpectedWh);

        // ── Home consumption adjustment ───────────────────────────────────────────────────────────────
        // The estimated solar surplus will first supply home consumption before batteries.
        // If the expected consumption exceeds the expected solar, batteries won't be charged
        // from solar → increase grid charge accordingly.
        //
        // Example: expected solar = 800Wh, estimated consumption = 600Wh, battery deficit = 500Wh
        //   → net solar for batteries = max(0, 800 - 600) = 200Wh
        //   → netEnergyNeededWh = max(0, 500 - 200) = 300Wh (grid needed)
        //
        // Without this correction: netEnergyNeededWh = max(0, 500 - 800) = 0Wh (too optimistic)
        // → batteries arrive empty at peak because solar was absorbed by home consumption.
        if (tariff.EstimatedConsumptionNextHoursWh.HasValue && solarExpectedWh > 0)
        {
            double consumptionLoad = tariff.EstimatedConsumptionNextHoursWh.Value;
            double solarForBatteries = Math.Max(0, solarExpectedWh - consumptionLoad);
            netEnergyNeededWh = Math.Max(0, energyNeededWh - solarForBatteries);
        }

        if (netEnergyNeededWh <= 0)
            return 0;

        // ── Reduce grid charge if solar arrives within < 2h (Feature 3) ───────────────────
        // If Solcast intraday forecasts show significant production in the coming hours,
        // proportionally reduce grid charging.
        // Idea: don't charge 1000W from the grid if 800Wh arrive in 1h30.
        //
        // Proportional reduction: targetW × max(0, 1 - solarCoverage)
        //   where solarCoverage = ForecastNext3HoursWh / netEnergyNeededWh (clamped to 1)
        //
        // Example: netEnergyNeeded = 500Wh, next3h = 400Wh
        //   → solarCoverage = 0.80 → reduction = 80% → grid charge = 20% of targetW
        //
        // Reduction applies ONLY if intraday entities are configured
        // AND if the forecast solar exceeds MinSolarNext3HoursWhForGridReduction threshold.
        // In emergency (hoursRemaining ≤ urgencyThresholdHours), no reduction.
        double intradaySolarReductionFactor = 1.0;

        if (tariff.HasIntradayForecast
            && tariff.ForecastNext3HoursWh.HasValue
            && tariff.ForecastNext3HoursWh.Value > 0
            && hoursRemaining > urgencyThresholdHours)
        {
            double next3hWh = tariff.ForecastNext3HoursWh.Value;

            if (next3hWh > 0 && netEnergyNeededWh > 0)
            {
                double solarCoverage = Math.Min(1.0, next3hWh / netEnergyNeededWh);
                // Soft reduction: keep at least 30% of the charge for emergencies
                intradaySolarReductionFactor = Math.Max(0.30, 1.0 - solarCoverage * 0.7);
            }
        }
        // Compute the minimum duration needed to charge at MaxChargeRateW,
        // then check whether there is still time to wait before starting.
        //
        // hoursNeeded      = net energy / max power
        // hoursBeforeStart = remaining hours - hoursNeeded - lazyBuffer
        //
        // If hoursBeforeStart > 0 → too early → return 0 (standby)
        // If hoursBeforeStart ≤ 0 → time to start → adaptive power
        //
        // Example: off-peak slot 22h→7h (9h), battery needs 0.5h at 1000W.
        //   At 22h00: hoursRemaining=9h, hoursNeeded=0.5h, lazyBuffer=0.5h
        //             → hoursBeforeStart = 9 - 0.5 - 0.5 = 8h → waiting
        //   At 06h00: hoursRemaining=1h → ≤ urgencyThreshold → max charge (case handled above)
        //   At 05h30: hoursRemaining=1.5h, hoursNeeded=0.5h, lazyBuffer=0.5h
        //             → hoursBeforeStart = 1.5 - 0.5 - 0.5 = 0.5h → still positive → waiting
        //   At 06h00: urgencyThreshold → max charge
        //
        // lazyBuffer is a safety margin to absorb uncertainties
        // (drifting SOC, BMS cycle, slight underestimate of needed energy).
        double hoursNeeded = netEnergyNeededWh / b.MaxChargeRateW;
        double hoursBeforeStart = hoursRemaining - hoursNeeded - lazyBufferHours;

        if (hoursBeforeStart > 0)
            return 0; // Too early — waiting, batteries running in self-powered mode

        // Time to start: adaptive power over the remaining time
        double hoursToCharge = Math.Max(hoursNeeded, urgencyThresholdHours);
        double targetW = netEnergyNeededWh / hoursToCharge;

        // Apply intraday reduction if solar is arriving soon
        targetW *= intradaySolarReductionFactor;

        return Math.Clamp(targetW, minGridChargeW, b.MaxChargeRateW);
    }

    private static double GetUsableReserveAboveEmergencyWh(Battery battery)
    {
        double emergencyFloorPercent = battery.EmergencyGridChargeBelowPercent ?? battery.MinPercent;
        return Math.Max(0, (battery.CurrentPercent - emergencyFloorPercent) / 100.0 * battery.CapacityWh);
    }

    private static double? GetProjectedPercentAtMeaningfulSolar(
        Battery battery,
        double? hoursUntilMeaningfulSolar)
    {
        if (!hoursUntilMeaningfulSolar.HasValue || hoursUntilMeaningfulSolar.Value >= double.MaxValue)
            return null;

        double hours = Math.Max(0, hoursUntilMeaningfulSolar.Value);
        double selfDischarge = Math.Max(0, battery.SelfDischargePercentPerHour);

        return Math.Max(0, battery.CurrentPercent - selfDischarge * hours);
    }

    private static double GetPreventiveChargeFloorPercent(Battery battery)
        => battery.PreventiveChargeOnlyIfEmptyBeforeSolar ? 0.0 : battery.MinPercent;

    private static bool ShouldUsePreventiveGridChargeBeforeMeaningfulSolar(
        Battery battery,
        double? projectedPercentAtMeaningfulSolar,
        bool meaningfulSolarWillArrive)
    {
        if (!meaningfulSolarWillArrive || !projectedPercentAtMeaningfulSolar.HasValue)
            return true;

        if (battery.PreventiveChargeOnlyIfEmptyBeforeSolar)
            return projectedPercentAtMeaningfulSolar.Value <= 0.01;

        return projectedPercentAtMeaningfulSolar.Value < battery.MinPercent;
    }

    private static bool CanFleetWaitUntilMeaningfulSolar(
        double totalFleetReserveAboveEmergencyWh,
        double? hoursUntilMeaningfulSolar,
        double? estimatedConsumptionAverageW)
    {
        double? expectedLoadBeforeSolarWh = GetExpectedLoadBeforeMeaningfulSolarWh(
            hoursUntilMeaningfulSolar,
            estimatedConsumptionAverageW);

        return expectedLoadBeforeSolarWh.HasValue
            && totalFleetReserveAboveEmergencyWh >= expectedLoadBeforeSolarWh.Value;
    }

    private static double? GetExpectedLoadBeforeMeaningfulSolarWh(
        double? hoursUntilMeaningfulSolar,
        double? estimatedConsumptionAverageW)
    {
        if (!hoursUntilMeaningfulSolar.HasValue || hoursUntilMeaningfulSolar.Value >= double.MaxValue)
            return null;

        if (!estimatedConsumptionAverageW.HasValue)
            return null;

        return Math.Max(0, estimatedConsumptionAverageW.Value)
            * Math.Max(0, hoursUntilMeaningfulSolar.Value);
    }

    private static double? GetHoursUntilMeaningfulSolarForBattery(Battery b, TariffContext tariff)
    {
        double thresholdW = b.HardwareMinChargeW;
        if (thresholdW <= 0)
            return tariff.HoursUntilSolar;

        if (tariff.ForecastThisHourWh.HasValue && tariff.ForecastThisHourWh.Value >= thresholdW)
            return 0;

        if (tariff.SolcastHourlyCurveWh is { Length: > 0 })
        {
            for (int hour = 0; hour < tariff.SolcastHourlyCurveWh.Length; hour++)
            {
                if (tariff.SolcastHourlyCurveWh[hour] >= thresholdW)
                    return hour;
            }
        }

        if (tariff.ForecastNextHourWh.HasValue && tariff.ForecastNextHourWh.Value >= thresholdW)
            return 1;

        return tariff.HoursUntilSolar;
    }

    private static DistributionFeatures BuildFeatures(
        double surplusW, IList<Battery> batteries,
        WeatherData wx, TariffContext tariff,
        double? yesterdaySelfSufficiencyPct = null)
    {
        var now = DateTime.UtcNow;

        double[] rad = wx.RadiationForecast12h.ToArray();
        double avg6h = rad.Take(6).DefaultIfEmpty(0).Average();

        double hourRad = 2.0 * Math.PI * now.Hour / 24.0;
        double monthRad = 2.0 * Math.PI * (now.Month - 1) / 12.0;

        double totalCap = batteries.Sum(b => b.CapacityWh);

        return new DistributionFeatures
        {
            HourOfDay = now.Hour,
            DayOfWeek = (float)now.DayOfWeek,
            MonthOfYear = now.Month,
            DayOfYear = now.DayOfYear,

            SinHour = (float)Math.Sin(hourRad),
            CosHour = (float)Math.Cos(hourRad),
            SinMonth = (float)Math.Sin(monthRad),
            CosMonth = (float)Math.Cos(monthRad),

            DaylightHours = (float)wx.DaylightHours,
            HoursUntilSunset = (float)wx.HoursUntilSunset,

            CloudCoverPercent = (float)wx.CloudCoverPercent,
            DirectRadiationWm2 = (float)wx.DirectRadiationWm2,
            DiffuseRadiationWm2 = (float)wx.DiffuseRadiationWm2,
            PrecipitationMmH = (float)wx.PrecipitationMmH,
            AvgForecastRadiation6h = (float)avg6h,

            AvgBatteryPercent = (float)batteries.Average(b => b.CurrentPercent),
            MinBatteryPercent = (float)batteries.Min(b => b.CurrentPercent),
            MaxBatteryPercent = (float)batteries.Max(b => b.CurrentPercent),
            TotalCapacityWh = (float)totalCap,
            UrgentBatteryCount = batteries.Count(b => b.IsUrgent),
            TotalMaxChargeRateW = (float)batteries.Sum(b => b.MaxChargeRateW),

            SocStdDev = (float)StdDev(batteries.Select(b => b.CurrentPercent)),
            CapacityRatio = batteries.Min(b => b.CapacityWh) > 0
                ? (float)(batteries.Max(b => b.CapacityWh) / batteries.Min(b => b.CapacityWh))
                : 1.0f,
            NonUrgentBatteryCount = batteries.Count(b => !b.IsUrgent),

            SurplusW = (float)surplusW,

            NormalizedTariff = (float)tariff.NormalizedPrice,
            IsOffPeakHour = tariff.IsFavorableForGrid ? 1f : 0f,
            HoursToNextFavorable = (float)(tariff.HoursToNextFavorable ?? 12.0),
            AvgSolarForecastGrid = (float)tariff.AvgSolarForecastWm2,
            SolarExpectedSoon = tariff.SolarExpectedSoon ? 1f : 0f,
            MaxSavingsPerKwh = (float)tariff.MaxSavingsPerKwh,

            // ML-7
            HoursRemainingInSlot = (float)(tariff.HoursRemainingInSlot ?? 0.0),
            HoursUntilSolarCapped = (float)Math.Min(tariff.HoursUntilSolar ?? 24.0, 24.0),
            WasEmergencySession = batteries.Any(b => b.IsEmergencyGridCharge) ? 1f : 0f,
            NormalizedGridChargeW = batteries.Any(b => b.GridChargeAllowedW > 0)
                ? (float)Math.Clamp(
                    batteries.Where(b => b.GridChargeAllowedW > 0).Average(b => b.GridChargeAllowedW)
                    / Math.Max(1, batteries.Average(b => b.MaxChargeRateW)), 0, 1)
                : 0f,

            // ML-8: HA forecasts — normalized by total capacity to be unitless
            ForecastTodayNormalized = totalCap > 0 && tariff.ForecastTodayWh.HasValue
                ? (float)Math.Clamp(tariff.ForecastTodayWh.Value / totalCap, 0, 5) : 0f,
            ForecastTomorrowNormalized = totalCap > 0 && tariff.ForecastTomorrowWh.HasValue
                ? (float)Math.Clamp(tariff.ForecastTomorrowWh.Value / totalCap, 0, 5) : 0f,
            HasHaForecast = tariff.HasHaForecast ? 1f : 0f,

            // ML-9: solar trend ratio J / J+1 — encodes the solar trend
            // > 1: tomorrow better → less urgent to charge now
            // < 1: tomorrow worse  → preserve / charge harder tonight
            // = 0: data absent (HasHaForecast = 0 in that case)
            ForecastRatioTomorrowVsToday = tariff.ForecastTodayWh.HasValue
                && tariff.ForecastTodayWh.Value > 0
                && tariff.ForecastTomorrowWh.HasValue
                ? (float)Math.Clamp(tariff.ForecastTomorrowWh.Value / tariff.ForecastTodayWh.Value, 0, 3)
                : 1f,

            // ML-9: explicit signal "grid charge was blocked by HA forecast"
            // Allows the ML to differentiate "Open-Meteo sun" vs "precise Solcast sun"
            SolarBlockedByHaForecast = tariff.SolarExpectedFromHa ? 1f : 0f,

            // Feature 6 — Yesterday self-sufficiency, normalised [0–1]
            // 0.0 if no data (Solcast not configured or first day)
            YesterdaySelfSufficiencyPct = yesterdaySelfSufficiencyPct.HasValue
                ? (float)Math.Clamp(yesterdaySelfSufficiencyPct.Value / 100.0, 0, 1)
                : 0f,

            OptimalSoftMaxPercent = 80,
            OptimalPreventiveThreshold = 20,
        };
    }

    private static float StdDev(IEnumerable<double> values)
    {
        var list = values.ToList();
        if (list.Count < 2) return 0f;
        double avg = list.Average();
        return (float)Math.Sqrt(list.Average(v => (v - avg) * (v - avg)));
    }

    /// <summary>
    /// Computes the fraction of solar energy produced between [startH, endH]
    /// normalised over [sunriseH, sunsetH].
    ///
    /// Uses a blend between a sinusoidal profile (clear sky) and a flat profile
    /// (overcast), weighted by cloudCoverPercent:
    ///   - Clear sky (0%): sin(π×t/D) — correctly weights the noon peak
    ///   - Fully overcast (100%): uniform distribution — clouds flatten the profile
    ///   - Partial cloud: linear blend between the two
    ///
    /// This addresses CALC-01: the pure sinusoidal model overestimates the noon
    /// peak contribution under cloudy conditions and at high latitudes.
    /// </summary>
    private static double SolarFractionBetweenHours(
        double startH, double endH, double sunriseH, double sunsetH,
        double cloudCoverPercent = 0)
    {
        double duration = sunsetH - sunriseH;
        if (duration <= 0 || endH <= startH) return 0;

        // Clamp within the solar window
        double a = Math.Max(0, startH - sunriseH);
        double b = Math.Min(duration, endH - sunriseH);
        if (b <= a) return 0;

        // Sinusoidal fraction: integral of sin(π×t/D) between a and b,
        // normalized over [0, D] (total integral = 2D/π)
        double integralTotal = 2.0 * duration / Math.PI;
        double integralSlice = (duration / Math.PI)
            * (Math.Cos(Math.PI * a / duration) - Math.Cos(Math.PI * b / duration));
        double sineFraction = integralTotal > 0 ? Math.Max(0, integralSlice / integralTotal) : 0;

        // Flat fraction: uniform distribution over the daylight window
        double flatFraction = (b - a) / duration;

        // Blend: cloud cover shifts the profile from peaked (sine) towards flat (uniform)
        double cloudFactor = Math.Clamp(cloudCoverPercent / 100.0, 0, 1);
        return (1.0 - cloudFactor) * sineFraction + cloudFactor * flatFraction;
    }

    private void LogTariffContext(TariffContext ctx, double surplusW)
    {
        if (!ctx.CurrentPricePerKwh.HasValue) return;

        string slotInfo = ctx.HoursRemainingInSlot.HasValue
            ? $" | slot ends in {ctx.HoursRemainingInSlot.Value:F1}h" : string.Empty;
        string solarInfo = ctx.HoursUntilSolar.HasValue && ctx.HoursUntilSolar.Value < double.MaxValue
            ? $" | solar in {ctx.HoursUntilSolar.Value:F1}h" : " | no solar forecast";
        string fcInfo = ctx.HasHaForecast
            ? $" | HA fc today={ctx.ForecastTodayWh:F0}Wh tmrw={ctx.ForecastTomorrowWh:F0}Wh"
            : " | Open-Meteo only";
        string haBlockInfo = ctx.SolarExpectedFromHa ? " [blocked by HA forecast]" : string.Empty;
        string eveningBoostInfo = ctx.HasLowForecastTomorrow && ctx.IsFavorableForGrid
            ? $" | ⚡ softmax boost +{ctx.EveningBoostPercent:F0}% (low tmrw forecast + favorable tariff)"
            : string.Empty;
        string intradayInfo = ctx.HasIntradayForecast
            ? $" | Solcast next3h={ctx.ForecastNext3HoursWh:F0}Wh (rem={ctx.ForecastRemainingTodayWh:F0}Wh)"
            : string.Empty;
        string balanceInfo = ctx.EnergyDeficitTodayWh.HasValue
            ? $" | deficit={ctx.EnergyDeficitTodayWh:F0}Wh"
            : string.Empty;
        string balanceBlockInfo = ctx.GridChargeBlockedBySolarSufficiency
            ? " [BLOCKED: solar sufficient today]" : string.Empty;

        // ── Feature 5 — Price and tariff mode used ───────────────────────────
        string tariffModeInfo = ctx.IsDynamicTariff
            ? $" [SPOT {ctx.SpotPricePerKwh:F4}€/kWh | threshold={ctx.DynamicThresholdPerKwh:F4}€/kWh dyn]"
            : $" [slot '{ctx.ActiveSlotName}' YAML]";

        if (ctx.GridChargeAllowed)
            _logger.LogInformation(
                "Tariff {TariffMode} {Price:F4}€/kWh — GRID CHARGE ALLOWED{SlotInfo}{SolarInfo}{FcInfo}{EveningBoost}{Intraday}{Balance} " +
                "(surplus={S:F0}W, savings={Sav:F3}€/kWh)",
                tariffModeInfo, ctx.CurrentPricePerKwh, slotInfo, solarInfo, fcInfo, eveningBoostInfo,
                intradayInfo, balanceInfo, surplusW, ctx.MaxSavingsPerKwh);
        else if (ctx.IsFavorableForGrid)
            _logger.LogInformation(
                "Tariff {TariffMode} {Price:F4}€/kWh — GRID CHARGE BLOCKED{HaBlock}{BalanceBlock}{SlotInfo}{SolarInfo}{FcInfo}{Intraday}{Balance}",
                tariffModeInfo, ctx.CurrentPricePerKwh, haBlockInfo, balanceBlockInfo,
                slotInfo, solarInfo, fcInfo, intradayInfo, balanceInfo);
        else
            _logger.LogInformation(
                "Tariff {TariffMode} {Price:F4}€/kWh — grid charge not favorable{SlotInfo}",
                tariffModeInfo, ctx.CurrentPricePerKwh, slotInfo);
    }
}

public record SmartDistributionResult(
    DistributionResult Distribution,
    string DecisionEngine,
    MLRecommendation? MLRecommendation,
    WeatherData? Weather,
    TariffContext? Tariff,
    long SessionId
);