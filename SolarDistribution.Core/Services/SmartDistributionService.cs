using Microsoft.Extensions.Logging;
using SolarDistribution.Core.Models;
using SolarDistribution.Core.Services.ML;
using SolarDistribution.Core.Repositories;

namespace SolarDistribution.Core.Services;

/// <summary>
/// Façade principale de distribution intelligente.
///
/// Fix #6 : La construction de l'entité DistributionSession est maintenant
/// déléguée à IDistributionSessionFactory (implémentée dans Infrastructure).
/// SmartDistributionService ne dépend plus directement des entités EF ni de JsonSerializer.
///
/// Flux d'exécution par cycle :
///   1. Météo Open-Meteo (avec prévision 12h radiation + nuages)
///   2. Contexte tarifaire (TariffEngine : tarif actuel, favorable ?, soleil prévu ?)
///   3. Features ML construites (météo + état batteries + tarif)
///   4. Prédiction ML si modèle disponible (SoftMax + seuil préventif)
///   5. Batteries effectives : paramètres ML ou déterministes + GridChargeAllowedW
///   6. Distribution 3 passes (surplus→SoftMax, surplus→HardMax, réseau→SoftMax)
///   7. Persistance complète (session + snapshots + météo + ML + tarif)
/// </summary>
public class SmartDistributionService
{
    private readonly IBatteryDistributionService       _algo;
    private readonly IDistributionMLService            _ml;
    private readonly IWeatherService                   _weather;
    private readonly IDistributionRepository           _repo;
    private readonly TariffEngine                      _tariff;
    private readonly IDistributionSessionFactory       _sessionFactory;
    private readonly ILogger<SmartDistributionService> _logger;

    public SmartDistributionService(
        IBatteryDistributionService       algo,
        IDistributionMLService            ml,
        IWeatherService                   weather,
        IDistributionRepository           repo,
        TariffEngine                      tariff,
        IDistributionSessionFactory       sessionFactory,
        ILogger<SmartDistributionService> logger)
    {
        _algo           = algo;
        _ml             = ml;
        _weather        = weather;
        _repo           = repo;
        _tariff         = tariff;
        _sessionFactory = sessionFactory;
        _logger         = logger;
    }

    public async Task<SmartDistributionResult> DistributeAsync(
        double         surplusW,
        IList<Battery> batteries,
        double         latitude,
        double         longitude,
        CancellationToken ct = default)
    {
        // ── 1. Météo ──────────────────────────────────────────────────────────
        var wx = await _weather.GetCurrentWeatherAsync(latitude, longitude, ct);
        if (wx is null)
            _logger.LogWarning("Weather unavailable — proceeding without weather context");

        // ── 2. Contexte tarifaire (heure locale pour les créneaux) ────────────
        var localNow    = DateTime.Now;
        var radForecast = wx?.RadiationForecast12h ?? Array.Empty<double>();
        var tariffCtx   = _tariff.EvaluateContext(localNow, radForecast);

        LogTariffContext(tariffCtx, surplusW);

        // ── 3. Features ML ────────────────────────────────────────────────────
        MLRecommendation? mlReco = null;
        string decisionEngine   = "Deterministic";

        if (wx is not null)
        {
            var features = BuildFeatures(surplusW, batteries, wx, tariffCtx);
            mlReco = await _ml.PredictAsync(features, ct);
        }

        // ── 4. Batteries effectives ───────────────────────────────────────────
        IList<Battery> effective;

        if (mlReco is not null)
        {
            effective      = Apply(batteries, mlReco, tariffCtx);
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

        // ── 5. Log urgences ──────────────────────────────────────────────────
        foreach (var b in effective.Where(b => b.IsEmergencyGridCharge))
        {
            double target = b.EmergencyGridChargeTargetPercent ?? b.SoftMaxPercent;
            _logger.LogWarning(
                "⚡ EMERGENCY grid charge — Battery {Id}: SOC {Soc:F1}% < threshold {Thr:F0}% " +
                "— will charge to {Target:F0}% from grid (solar expected: {Solar})",
                b.Id, b.CurrentPercent, b.EmergencyGridChargeBelowPercent, target,
                tariffCtx.SolarExpectedSoon ? "yes (skipped)" : "no");
        }

        // ── 6. Distribution ───────────────────────────────────────────────────
        var result = _algo.Distribute(surplusW, effective);

        if (result.GridChargedW > 0)
            _logger.LogInformation(
                "Grid charge: {W:F0}W [{Slot}] {Price:F3}€/kWh",
                result.GridChargedW, tariffCtx.ActiveSlotName, tariffCtx.CurrentPricePerKwh);

        // ── 6. Persistance — Fix #6 : délégué à IDistributionSessionFactory ───
        var session = _sessionFactory.Build(result, wx, mlReco, decisionEngine, batteries, tariffCtx);
        await _repo.SaveSessionAsync(session, ct);

        _logger.LogInformation(
            "Cycle done [{Engine}] solar={Solar:F0}W grid={Grid:F0}W unused={Unused:F0}W → session#{Id}",
            decisionEngine, result.TotalAllocatedW, result.GridChargedW, result.UnusedSurplusW, session.Id);

        return new SmartDistributionResult(result, decisionEngine, mlReco, wx, tariffCtx, session.Id);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Calcule GridChargeAllowedW pour chaque batterie selon 2 règles indépendantes :
    ///
    ///   1. URGENCE SOC : si SOC < EmergencyGridChargeBelowPercent
    ///      → recharge forcée réseau, SAUF si soleil attendu (SolarExpectedSoon)
    ///      → GridChargeAllowedW = MaxChargeRateW, cible = EmergencyGridChargeTargetPercent
    ///
    ///   2. TARIF FAVORABLE (logique normale) : tariff.GridChargeAllowed
    ///      → toutes les batteries éligibles chargent jusqu'à SoftMaxPercent
    ///
    /// Les deux peuvent se cumuler — l'urgence prend toujours le dessus sur le tarif.
    /// </summary>
    private static IList<Battery> Apply(
        IList<Battery>    src,
        MLRecommendation? reco,
        TariffContext      tariff)
    {
        return src.Select(b =>
        {
            // ── Urgence SOC : recharge forcée réseau ─────────────────────────
            // Déclenchée si SOC < seuil d'urgence ET pas de soleil attendu.
            // Si le soleil est attendu → l'autoconsommation va résoudre le problème,
            // pas besoin de payer le réseau.
            bool isEmergency = b.EmergencyGridChargeBelowPercent.HasValue
                && b.CurrentPercent < b.EmergencyGridChargeBelowPercent.Value
                && !tariff.SolarExpectedSoon;

            // ── Charge réseau autorisée ───────────────────────────────────────
            // Urgence → toujours autorisée (indépendant du tarif)
            // Sinon → seulement si le tarif est favorable (heures creuses)
            double gridAllowedW = (isEmergency || tariff.GridChargeAllowed)
                ? b.MaxChargeRateW
                : 0;

            return new Battery
            {
                Id             = b.Id,
                CapacityWh     = b.CapacityWh,
                MaxChargeRateW = b.MaxChargeRateW,
                MinPercent     = reco is null
                    ? b.MinPercent
                    : Math.Max(b.MinPercent, reco.RecommendedPreventiveThreshold),
                SoftMaxPercent = reco?.RecommendedSoftMaxPercent ?? b.SoftMaxPercent,
                HardMaxPercent = b.HardMaxPercent,
                CurrentPercent = b.CurrentPercent,
                Priority       = b.Priority,
                GridChargeAllowedW             = gridAllowedW,
                EmergencyGridChargeBelowPercent  = b.EmergencyGridChargeBelowPercent,
                EmergencyGridChargeTargetPercent = isEmergency ? b.EmergencyGridChargeTargetPercent : null,
                IsEmergencyGridCharge            = isEmergency,
            };
        }).ToList();
    }

    private static DistributionFeatures BuildFeatures(
        double surplusW, IList<Battery> batteries,
        WeatherData wx, TariffContext tariff)
    {
        var now = DateTime.UtcNow;

        double[] rad  = wx.RadiationForecast12h.ToArray();
        double avg6h  = rad.Take(6).DefaultIfEmpty(0).Average();

        double hourRad  = 2.0 * Math.PI * now.Hour / 24.0;
        double monthRad = 2.0 * Math.PI * (now.Month - 1) / 12.0;

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
            TotalCapacityWh     = (float)batteries.Sum(b => b.CapacityWh),
            UrgentBatteryCount  = batteries.Count(b => b.IsUrgent),
            TotalMaxChargeRateW = (float)batteries.Sum(b => b.MaxChargeRateW),

            SocStdDev             = (float)StdDev(batteries.Select(b => b.CurrentPercent)),
            CapacityRatio         = batteries.Min(b => b.CapacityWh) > 0
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

        if (ctx.GridChargeAllowed)
            _logger.LogInformation(
                "Tariff [{Slot}] {Price:F3}€/kWh — GRID CHARGE ALLOWED " +
                "(surplus={S:F0}W, solar forecast={F:F0}W/m², savings={Sav:F3}€/kWh)",
                ctx.ActiveSlotName, ctx.CurrentPricePerKwh, surplusW,
                ctx.AvgSolarForecastWm2, ctx.MaxSavingsPerKwh);
        else
            _logger.LogDebug(
                "Tariff [{Slot}] {Price:F3}€/kWh — grid charge blocked ({Reason})",
                ctx.ActiveSlotName, ctx.CurrentPricePerKwh,
                ctx.SolarExpectedSoon ? "solar expected soon" : "price above threshold");
    }
}

public record SmartDistributionResult(
    DistributionResult  Distribution,
    string              DecisionEngine,
    MLRecommendation?   MLRecommendation,
    WeatherData?        Weather,
    TariffContext?      Tariff,
    long                SessionId
);
