using System.Text.Json;
using Microsoft.Extensions.Logging;
using SolarDistribution.Core.Models;
using SolarDistribution.Core.Services.ML;
using SolarDistribution.Core.Data.Entities;
using SolarDistribution.Core.Repositories;

namespace SolarDistribution.Core.Services;

/// <summary>
/// Façade principale de distribution intelligente.
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
    private readonly IBatteryDistributionService      _algo;
    private readonly IDistributionMLService           _ml;
    private readonly IWeatherService                  _weather;
    private readonly IDistributionRepository          _repo;
    private readonly TariffEngine                     _tariff;
    private readonly ILogger<SmartDistributionService> _logger;

    public SmartDistributionService(
        IBatteryDistributionService       algo,
        IDistributionMLService            ml,
        IWeatherService                   weather,
        IDistributionRepository           repo,
        TariffEngine                      tariff,
        ILogger<SmartDistributionService> logger)
    {
        _algo    = algo;
        _ml      = ml;
        _weather = weather;
        _repo    = repo;
        _tariff  = tariff;
        _logger  = logger;
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
        MLRecommendation? mlReco  = null;
        string decisionEngine     = "Deterministic";

        if (wx is not null)
        {
            var features = BuildFeatures(surplusW, batteries, wx, tariffCtx);
            mlReco = await _ml.PredictAsync(features, ct);
        }

        // ── 4. Batteries effectives ───────────────────────────────────────────
        // SoftMax ajusté par ML ou par défaut.
        // GridChargeAllowedW = MaxChargeRateW si charge réseau autorisée, sinon 0.
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

        // ── 5. Distribution ───────────────────────────────────────────────────
        var result = _algo.Distribute(surplusW, effective);

        if (result.GridChargedW > 0)
            _logger.LogInformation(
                "Grid charge: {W:F0}W [{Slot}] {Price:F3}€/kWh",
                result.GridChargedW, tariffCtx.ActiveSlotName, tariffCtx.CurrentPricePerKwh);

        // ── 6. Persistance ────────────────────────────────────────────────────
        var session = BuildSession(result, wx, mlReco, decisionEngine, batteries, tariffCtx);
        await _repo.SaveSessionAsync(session, ct);

        _logger.LogInformation(
            "Cycle done [{Engine}] solar={Solar:F0}W grid={Grid:F0}W unused={Unused:F0}W → session#{Id}",
            decisionEngine, result.TotalAllocatedW, result.GridChargedW, result.UnusedSurplusW, session.Id);

        return new SmartDistributionResult(result, decisionEngine, mlReco, wx, tariffCtx, session.Id);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Applique les recommandations ML (ou les valeurs par défaut) pour produire
    /// les batteries effectives passées à l'algorithme.
    /// </summary>
    private static IList<Battery> Apply(
        IList<Battery>    src,
        MLRecommendation? reco,
        TariffContext      tariff)
    {
        return src.Select(b => new Battery
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
            // Charge réseau : autorisée uniquement si TariffEngine a donné le feu vert
            GridChargeAllowedW = tariff.GridChargeAllowed ? b.MaxChargeRateW : 0
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
            // Temporel brut
            HourOfDay   = now.Hour,
            DayOfWeek   = (float)now.DayOfWeek,
            MonthOfYear = now.Month,
            DayOfYear   = now.DayOfYear,

            // Cyclique
            SinHour   = (float)Math.Sin(hourRad),
            CosHour   = (float)Math.Cos(hourRad),
            SinMonth  = (float)Math.Sin(monthRad),
            CosMonth  = (float)Math.Cos(monthRad),

            // Saisonnalité directe
            DaylightHours    = (float)wx.DaylightHours,
            HoursUntilSunset = (float)wx.HoursUntilSunset,

            // Météo
            CloudCoverPercent      = (float)wx.CloudCoverPercent,
            DirectRadiationWm2     = (float)wx.DirectRadiationWm2,
            DiffuseRadiationWm2    = (float)wx.DiffuseRadiationWm2,
            PrecipitationMmH       = (float)wx.PrecipitationMmH,
            AvgForecastRadiation6h = (float)avg6h,

            // Batteries
            AvgBatteryPercent   = (float)batteries.Average(b => b.CurrentPercent),
            MinBatteryPercent   = (float)batteries.Min(b => b.CurrentPercent),
            MaxBatteryPercent   = (float)batteries.Max(b => b.CurrentPercent),
            TotalCapacityWh     = (float)batteries.Sum(b => b.CapacityWh),
            UrgentBatteryCount  = batteries.Count(b => b.IsUrgent),
            TotalMaxChargeRateW = (float)batteries.Sum(b => b.MaxChargeRateW),

            // ML-4 : features de dispersion des batteries
            SocStdDev             = (float)StdDev(batteries.Select(b => b.CurrentPercent)),
            CapacityRatio         = batteries.Min(b => b.CapacityWh) > 0
                ? (float)(batteries.Max(b => b.CapacityWh) / batteries.Min(b => b.CapacityWh))
                : 1.0f,
            NonUrgentBatteryCount = batteries.Count(b => !b.IsUrgent),

            // Surplus
            SurplusW = (float)surplusW,

            // Tarif
            NormalizedTariff     = (float)tariff.NormalizedPrice,
            IsOffPeakHour        = tariff.IsFavorableForGrid ? 1f : 0f,
            HoursToNextFavorable = (float)(tariff.HoursToNextFavorable ?? 12.0),
            AvgSolarForecastGrid = (float)tariff.AvgSolarForecastWm2,
            SolarExpectedSoon    = tariff.SolarExpectedSoon ? 1f : 0f,
            MaxSavingsPerKwh     = (float)tariff.MaxSavingsPerKwh,

            // Labels par défaut (remplacés par SessionFeedback lors du retrain)
            OptimalSoftMaxPercent      = 80,
            OptimalPreventiveThreshold = 20,
        };
    }

    private static DistributionSession BuildSession(
        DistributionResult result, WeatherData? wx,
        MLRecommendation? mlReco, string engine,
        IList<Battery> orig, TariffContext tariff)
    {
        var session = new DistributionSession
        {
            RequestedAt               = DateTime.UtcNow,
            SurplusW                  = result.SurplusInputW,
            TotalAllocatedW           = result.TotalAllocatedW,
            UnusedSurplusW            = result.UnusedSurplusW,
            GridChargedW              = result.GridChargedW,
            DecisionEngine            = engine,
            MlConfidenceScore         = mlReco?.ConfidenceScore,
            TariffSlotName            = tariff.ActiveSlotName,
            TariffPricePerKwh         = tariff.CurrentPricePerKwh,
            WasGridChargeFavorable    = tariff.IsFavorableForGrid,
            SolarExpectedSoon         = tariff.SolarExpectedSoon,
            HoursToNextFavorableTariff = tariff.HoursToNextFavorable,
            AvgSolarForecastWm2       = tariff.AvgSolarForecastWm2,
            TariffMaxSavingsPerKwh    = tariff.MaxSavingsPerKwh,
        };

        session.BatterySnapshots = result.Allocations.Select(alloc =>
        {
            var o = orig.FirstOrDefault(b => b.Id == alloc.BatteryId);
            return new BatterySnapshot
            {
                BatteryId            = alloc.BatteryId,
                CapacityWh           = o?.CapacityWh       ?? 0,
                MaxChargeRateW       = o?.MaxChargeRateW   ?? 0,
                MinPercent           = o?.MinPercent       ?? 0,
                SoftMaxPercent       = o?.SoftMaxPercent   ?? 80,
                CurrentPercentBefore = alloc.PreviousPercent,
                CurrentPercentAfter  = alloc.NewPercent,
                Priority             = o?.Priority         ?? 0,
                WasUrgent            = alloc.WasUrgent,
                AllocatedW           = alloc.AllocatedW,
                IsGridCharge         = alloc.IsGridCharge,
                Reason               = alloc.Reason
            };
        }).ToList();

        if (wx is not null)
        {
            session.Weather = new WeatherSnapshot
            {
                FetchedAt                = wx.FetchedAt,
                Latitude                 = wx.Latitude,
                Longitude                = wx.Longitude,
                TemperatureC             = wx.TemperatureC,
                CloudCoverPercent        = wx.CloudCoverPercent,
                PrecipitationMmH         = wx.PrecipitationMmH,
                DirectRadiationWm2       = wx.DirectRadiationWm2,
                DiffuseRadiationWm2      = wx.DiffuseRadiationWm2,
                DaylightHours            = wx.DaylightHours,
                HoursUntilSunset         = wx.HoursUntilSunset,
                RadiationForecast12hJson = JsonSerializer.Serialize(wx.RadiationForecast12h),
                CloudForecast12hJson     = JsonSerializer.Serialize(wx.CloudForecast12h)
            };
        }

        if (mlReco is not null)
        {
            session.MlPrediction = new MLPredictionLog
            {
                ModelVersion                 = mlReco.ModelVersion,
                ConfidenceScore              = mlReco.ConfidenceScore,
                PredictedSoftMaxJson         = JsonSerializer.Serialize(mlReco.RecommendedSoftMaxPercent),
                PredictedPreventiveThreshold = mlReco.RecommendedPreventiveThreshold,
                WasApplied                   = engine != "Deterministic",
                PredictedAt                  = DateTime.UtcNow
            };
        }

        return session;
    }

    /// <summary>ML-4 : écart-type population (σ) des SOC batteries.</summary>
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
