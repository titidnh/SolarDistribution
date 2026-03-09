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
        CancellationToken ct = default)
    {
        // ── 1. Météo ──────────────────────────────────────────────────────────
        var wx = weatherSnapshot ?? await _weather.GetCurrentWeatherAsync(latitude, longitude, ct);
        if (wx is null)
            _logger.LogWarning("Weather unavailable — proceeding without weather context");

        // ── 2. Contexte tarifaire ─────────────────────────────────────────────
        var localNow     = DateTime.Now;
        var radForecast  = wx?.RadiationForecast12h ?? Array.Empty<double>();
        var tariffCtx    = _tariff.EvaluateContext(
            localNow, radForecast, forecastTodayWh, forecastTomorrowWh);

        LogTariffContext(tariffCtx, surplusW);

        // ── 3. Features ML ────────────────────────────────────────────────────
        MLRecommendation? mlReco = null;
        string decisionEngine = "Deterministic";

        if (wx is not null)
        {
            var features = BuildFeatures(surplusW, batteries, wx, tariffCtx);
            mlReco = await _ml.PredictAsync(features, ct);
        }

        // ── 4. Batteries effectives ───────────────────────────────────────────
        IList<Battery> effective;

        if (mlReco is not null)
        {
            effective = Apply(batteries, mlReco, tariffCtx);
            decisionEngine = mlReco.ConfidenceScore >= 0.75 ? "ML" : "ML-Fallback";
            _logger.LogInformation(
                "ML: softMax={SoftMax:F1}%, preventive={Prev:F1}%, confidence={Conf:P0} [{Engine}]",
                mlReco.RecommendedSoftMaxPercent,
                mlReco.RecommendedPreventiveThreshold,
                mlReco.ConfidenceScore, decisionEngine);
        }
        else
        {
            effective = Apply(batteries, null, tariffCtx);
        }

        // ── 5. Log urgences + charge adaptative ──────────────────────────────
        foreach (var b in effective.Where(b => b.IsEmergencyGridCharge))
        {
            double target = b.EmergencyGridChargeTargetPercent ?? b.SoftMaxPercent;
            _logger.LogWarning(
                "⚡ EMERGENCY grid charge — Battery {Id}: SOC {Soc:F1}% < threshold {Thr:F0}% " +
                "— will charge to {Target:F0}% from grid (solar expected: {Solar})",
                b.Id, b.CurrentPercent, b.EmergencyGridChargeBelowPercent, target,
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

        // ── 6. Distribution ───────────────────────────────────────────────────
        var result = _algo.Distribute(surplusW, effective);

        if (result.GridChargedW > 0)
            _logger.LogInformation(
                "Grid charge: {W:F0}W [{Slot}] {Price:F3}€/kWh",
                result.GridChargedW, tariffCtx.ActiveSlotName, tariffCtx.CurrentPricePerKwh);

        // ── 7. Persistance ────────────────────────────────────────────────────
        var session = _sessionFactory.Build(result, wx, mlReco, decisionEngine, batteries, tariffCtx);
        await _repo.SaveSessionAsync(session, ct);

        _logger.LogInformation(
            "Cycle done [{Engine}] solar={Solar:F0}W grid={Grid:F0}W unused={Unused:F0}W → session#{Id}",
            decisionEngine, result.TotalAllocatedW, result.GridChargedW, result.UnusedSurplusW, session.Id);

        return new SmartDistributionResult(result, decisionEngine, mlReco, wx, tariffCtx, session.Id);
    }

    // ── Apply: calcule GridChargeAllowedW par batterie ────────────────────────

    /// <summary>
    /// Priorité de décision :
    ///   1. AUTOCONSOMMATION : soleil arrive avant fin du slot ET batterie peut tenir → 0W réseau
    ///   2. URGENCE SOC : SOC critique + soleil absent → MaxChargeRateW (indépendant du tarif)
    ///   3. CHARGE INTELLIGENTE HC : puissance adaptative calculée par ComputeAdaptiveGridChargeW
    /// </summary>
    private static IList<Battery> Apply(
        IList<Battery> src,
        MLRecommendation? reco,
        TariffContext tariff,
        double minGridChargeW = 100.0,
        double urgencyThresholdHours = 1.0)
    {
        return src.Select(b =>
        {
            double softMax = reco?.RecommendedSoftMaxPercent ?? b.SoftMaxPercent;

            bool solarWillArrive = tariff.HoursUntilSolar.HasValue
                && tariff.HoursUntilSolar.Value < double.MaxValue;

            bool isEmergency = b.EmergencyGridChargeBelowPercent.HasValue
                && b.CurrentPercent < b.EmergencyGridChargeBelowPercent.Value
                && !solarWillArrive;

            // Autoconsommation possible avant fin du slot ?
            bool solarBeforeSlotEnd = false;
            if (!isEmergency
                && tariff.IsFavorableForGrid
                && tariff.HoursRemainingInSlot.HasValue
                && tariff.HoursUntilSolar.HasValue
                && tariff.HoursUntilSolar.Value < double.MaxValue)
            {
                bool solarArrivesBeforeSlotEnd =
                    tariff.HoursUntilSolar.Value <= tariff.HoursRemainingInSlot.Value;
                bool batteryCanWait = !b.EmergencyGridChargeBelowPercent.HasValue
                    || b.CurrentPercent > b.EmergencyGridChargeBelowPercent.Value;
                solarBeforeSlotEnd = solarArrivesBeforeSlotEnd && batteryCanWait;
            }

            double gridAllowedW = 0;

            if (isEmergency)
                gridAllowedW = b.MaxChargeRateW;
            else if (solarBeforeSlotEnd)
                gridAllowedW = 0;
            else if (tariff.GridChargeAllowed)
                gridAllowedW = ComputeAdaptiveGridChargeW(
                    b, softMax, tariff, minGridChargeW, urgencyThresholdHours);

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
                GridChargeAllowedW = gridAllowedW,
                EmergencyGridChargeBelowPercent = b.EmergencyGridChargeBelowPercent,
                EmergencyGridChargeTargetPercent = isEmergency ? b.EmergencyGridChargeTargetPercent : null,
                IsEmergencyGridCharge = isEmergency,
            };
        }).ToList();
    }

    /// <summary>
    /// Puissance de charge réseau adaptative en HC.
    ///
    /// Si des prévisions HA sont disponibles (ForecastTodayWh), elles remplacent
    /// le calcul générique Open-Meteo × 0.15 pour l'estimation de l'énergie solaire
    /// attendue pendant le créneau restant.
    ///
    /// Logique :
    ///   énergie brute = (SoftMax - SOC) × CapacityWh
    ///   énergie solaire attendue (créneau restant) :
    ///     - si HA forecast dispo → proratisation de ForecastTodayWh sur les heures restantes
    ///     - sinon → Σ forecast[h] × solarEfficiencyFactor
    ///   énergie nette = max(0, brute - solaire)
    ///   puissance = clamp(nette / hoursRemaining, minGridChargeW, MaxChargeRateW)
    /// </summary>
    private static double ComputeAdaptiveGridChargeW(
        Battery b,
        double softMaxPercent,
        TariffContext tariff,
        double minGridChargeW,
        double urgencyThresholdHours,
        double solarEfficiencyFactor = 0.15)
    {
        double hoursRemaining = tariff.HoursRemainingInSlot ?? 0;

        if (hoursRemaining <= urgencyThresholdHours)
            return b.MaxChargeRateW;

        bool solarAfterSlot = !tariff.HoursUntilSolar.HasValue
            || tariff.HoursUntilSolar.Value >= double.MaxValue
            || tariff.HoursUntilSolar.Value > hoursRemaining;
        if (solarAfterSlot && hoursRemaining <= urgencyThresholdHours * 2)
            return b.MaxChargeRateW;

        if (b.CurrentPercent >= softMaxPercent)
            return 0;

        double energyNeededWh = (softMaxPercent - b.CurrentPercent) / 100.0 * b.CapacityWh;

        // ── Énergie solaire attendue pendant les heures restantes ─────────────
        double solarExpectedWh;

        if (tariff.HasHaForecast && tariff.ForecastTodayWh.HasValue)
        {
            // Prévision HA disponible : on suppose une distribution sinusoïdale sur la journée.
            // On prend la fraction de la journée correspondant aux heures restantes dans le slot.
            // Approche conservative : le slot HC est souvent nocturne → peu de soleil dans ce créneau.
            // La valeur ForecastTodayWh couvre les 24h → on prend la fraction correspondant
            // aux heures restantes APRÈS HoursUntilSolar.
            double solarStartH = tariff.HoursUntilSolar.HasValue
                                 && tariff.HoursUntilSolar.Value < double.MaxValue
                ? tariff.HoursUntilSolar.Value : 24.0;

            double solarHoursInSlot = Math.Max(0, hoursRemaining - solarStartH);
            // Fraction de la journée solaire disponible pendant le créneau restant
            double daylightFraction = solarHoursInSlot / 24.0;
            solarExpectedWh = tariff.ForecastTodayWh.Value * daylightFraction;

            // Si le soleil arrive demain (slot traverse minuit), on ajoute la part de demain
            if (tariff.ForecastTomorrowWh.HasValue && solarStartH >= hoursRemaining)
                solarExpectedWh += tariff.ForecastTomorrowWh.Value * (solarHoursInSlot / 24.0);
        }
        else
        {
            // Fallback Open-Meteo : W/m² × facteur d'efficacité
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

        if (netEnergyNeededWh <= 0)
            return 0;

        double targetW = netEnergyNeededWh / hoursRemaining;
        return Math.Clamp(targetW, minGridChargeW, b.MaxChargeRateW);
    }

    private static DistributionFeatures BuildFeatures(
        double surplusW, IList<Battery> batteries,
        WeatherData wx, TariffContext tariff)
    {
        var now = DateTime.UtcNow;

        double[] rad = wx.RadiationForecast12h.ToArray();
        double avg6h = rad.Take(6).DefaultIfEmpty(0).Average();

        double hourRad  = 2.0 * Math.PI * now.Hour / 24.0;
        double monthRad = 2.0 * Math.PI * (now.Month - 1) / 12.0;

        double totalCap = batteries.Sum(b => b.CapacityWh);

        return new DistributionFeatures
        {
            HourOfDay   = now.Hour,
            DayOfWeek   = (float)now.DayOfWeek,
            MonthOfYear = now.Month,
            DayOfYear   = now.DayOfYear,

            SinHour   = (float)Math.Sin(hourRad),
            CosHour   = (float)Math.Cos(hourRad),
            SinMonth  = (float)Math.Sin(monthRad),
            CosMonth  = (float)Math.Cos(monthRad),

            DaylightHours    = (float)wx.DaylightHours,
            HoursUntilSunset = (float)wx.HoursUntilSunset,

            CloudCoverPercent      = (float)wx.CloudCoverPercent,
            DirectRadiationWm2     = (float)wx.DirectRadiationWm2,
            DiffuseRadiationWm2    = (float)wx.DiffuseRadiationWm2,
            PrecipitationMmH       = (float)wx.PrecipitationMmH,
            AvgForecastRadiation6h = (float)avg6h,

            AvgBatteryPercent   = (float)batteries.Average(b => b.CurrentPercent),
            MinBatteryPercent   = (float)batteries.Min(b => b.CurrentPercent),
            MaxBatteryPercent   = (float)batteries.Max(b => b.CurrentPercent),
            TotalCapacityWh     = (float)totalCap,
            UrgentBatteryCount  = batteries.Count(b => b.IsUrgent),
            TotalMaxChargeRateW = (float)batteries.Sum(b => b.MaxChargeRateW),

            SocStdDev = (float)StdDev(batteries.Select(b => b.CurrentPercent)),
            CapacityRatio = batteries.Min(b => b.CapacityWh) > 0
                ? (float)(batteries.Max(b => b.CapacityWh) / batteries.Min(b => b.CapacityWh))
                : 1.0f,
            NonUrgentBatteryCount = batteries.Count(b => !b.IsUrgent),

            SurplusW = (float)surplusW,

            NormalizedTariff     = (float)tariff.NormalizedPrice,
            IsOffPeakHour        = tariff.IsFavorableForGrid ? 1f : 0f,
            HoursToNextFavorable = (float)(tariff.HoursToNextFavorable ?? 12.0),
            AvgSolarForecastGrid = (float)tariff.AvgSolarForecastWm2,
            SolarExpectedSoon    = tariff.SolarExpectedSoon ? 1f : 0f,
            MaxSavingsPerKwh     = (float)tariff.MaxSavingsPerKwh,

            // ML-7
            HoursRemainingInSlot  = (float)(tariff.HoursRemainingInSlot ?? 0.0),
            HoursUntilSolarCapped = (float)Math.Min(tariff.HoursUntilSolar ?? 24.0, 24.0),
            WasEmergencySession   = batteries.Any(b => b.IsEmergencyGridCharge) ? 1f : 0f,
            NormalizedGridChargeW = batteries.Any(b => b.GridChargeAllowedW > 0)
                ? (float)Math.Clamp(
                    batteries.Where(b => b.GridChargeAllowedW > 0).Average(b => b.GridChargeAllowedW)
                    / Math.Max(1, batteries.Average(b => b.MaxChargeRateW)), 0, 1)
                : 0f,

            // ML-8: HA forecasts — normalisés par capacité totale pour être sans dimension
            ForecastTodayNormalized    = totalCap > 0 && tariff.ForecastTodayWh.HasValue
                ? (float)Math.Clamp(tariff.ForecastTodayWh.Value / totalCap, 0, 5) : 0f,
            ForecastTomorrowNormalized = totalCap > 0 && tariff.ForecastTomorrowWh.HasValue
                ? (float)Math.Clamp(tariff.ForecastTomorrowWh.Value / totalCap, 0, 5) : 0f,
            HasHaForecast = tariff.HasHaForecast ? 1f : 0f,

            OptimalSoftMaxPercent      = 80,
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

    private void LogTariffContext(TariffContext ctx, double surplusW)
    {
        if (!ctx.CurrentPricePerKwh.HasValue) return;

        string slotInfo  = ctx.HoursRemainingInSlot.HasValue
            ? $" | slot ends in {ctx.HoursRemainingInSlot.Value:F1}h" : string.Empty;
        string solarInfo = ctx.HoursUntilSolar.HasValue && ctx.HoursUntilSolar.Value < double.MaxValue
            ? $" | solar in {ctx.HoursUntilSolar.Value:F1}h" : " | no solar forecast";
        string fcInfo    = ctx.HasHaForecast
            ? $" | HA fc today={ctx.ForecastTodayWh:F0}Wh tmrw={ctx.ForecastTomorrowWh:F0}Wh"
            : " | Open-Meteo only";

        if (ctx.GridChargeAllowed)
            _logger.LogInformation(
                "Tariff [{Slot}] {Price:F3}€/kWh — GRID CHARGE ALLOWED{SlotInfo}{SolarInfo}{FcInfo} " +
                "(surplus={S:F0}W, savings={Sav:F3}€/kWh)",
                ctx.ActiveSlotName, ctx.CurrentPricePerKwh, slotInfo, solarInfo, fcInfo,
                surplusW, ctx.MaxSavingsPerKwh);
        else if (ctx.IsFavorableForGrid)
            _logger.LogInformation(
                "Tariff [{Slot}] {Price:F3}€/kWh — favorable but skipped (autoconsumption covers){SolarInfo}{SlotInfo}",
                ctx.ActiveSlotName, ctx.CurrentPricePerKwh, solarInfo, slotInfo);
        else
            _logger.LogDebug(
                "Tariff [{Slot}] {Price:F3}€/kWh — grid charge blocked ({Reason}){SlotInfo}",
                ctx.ActiveSlotName, ctx.CurrentPricePerKwh,
                ctx.SolarExpectedSoon ? "solar expected soon" : "price above threshold", slotInfo);
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
