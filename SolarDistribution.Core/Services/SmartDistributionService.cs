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
/// Flux d'exécution :
///   1. Récupère les données météo (Open-Meteo)
///   2. Construit les features ML
///   3. Demande une recommandation au modèle ML
///   4. Si ML disponible et confiant → applique les SoftMax/seuils ML
///      Sinon → utilise l'algorithme déterministe avec paramètres par défaut
///   5. Exécute la distribution
///   6. Persiste session + snapshots + météo + log ML en base
/// </summary>
public class SmartDistributionService
{
    private readonly IBatteryDistributionService _deterministicService;
    private readonly IDistributionMLService      _mlService;
    private readonly IWeatherService             _weatherService;
    private readonly IDistributionRepository     _repository;
    private readonly ILogger<SmartDistributionService> _logger;

    public SmartDistributionService(
        IBatteryDistributionService      deterministicService,
        IDistributionMLService           mlService,
        IWeatherService                  weatherService,
        IDistributionRepository          repository,
        ILogger<SmartDistributionService> logger)
    {
        _deterministicService = deterministicService;
        _mlService            = mlService;
        _weatherService       = weatherService;
        _repository           = repository;
        _logger               = logger;
    }

    /// <summary>
    /// Point d'entrée principal : distribue le surplus avec assistance ML si disponible.
    /// </summary>
    public async Task<SmartDistributionResult> DistributeAsync(
        double surplusW,
        IList<Battery> batteries,
        double latitude,
        double longitude,
        CancellationToken ct = default)
    {
        // ── 1. Météo ──────────────────────────────────────────────────────────
        var weather = await _weatherService.GetCurrentWeatherAsync(latitude, longitude, ct);

        if (weather is null)
            _logger.LogWarning("Weather unavailable — proceeding without weather context");

        // ── 2. Features ML ────────────────────────────────────────────────────
        MLRecommendation? mlReco = null;
        string decisionEngine    = "Deterministic";

        if (weather is not null)
        {
            var features = BuildFeatures(surplusW, batteries, weather);
            mlReco = await _mlService.PredictAsync(features, ct);
        }

        // ── 3. Application ML ou fallback ─────────────────────────────────────
        IList<Battery> effectiveBatteries = batteries;

        if (mlReco is not null)
        {
            // Appliquer le SoftMax recommandé par ML à toutes les batteries
            effectiveBatteries = batteries.Select(b => new Battery
            {
                Id             = b.Id,
                CapacityWh     = b.CapacityWh,
                MaxChargeRateW = b.MaxChargeRateW,
                MinPercent     = Math.Max(b.MinPercent, mlReco.RecommendedPreventiveThreshold),
                SoftMaxPercent = mlReco.RecommendedSoftMaxPercent,
                HardMaxPercent = b.HardMaxPercent,
                CurrentPercent = b.CurrentPercent,
                Priority       = b.Priority
            }).ToList();

            decisionEngine = mlReco.ConfidenceScore >= 0.75 ? "ML" : "ML-Fallback";

            _logger.LogInformation(
                "ML applied: softMax={SoftMax:F1}%, preventive={Prev:F1}%, confidence={Conf:F2}, engine={Engine}",
                mlReco.RecommendedSoftMaxPercent,
                mlReco.RecommendedPreventiveThreshold,
                mlReco.ConfidenceScore,
                decisionEngine);
        }

        // ── 4. Distribution ───────────────────────────────────────────────────
        var result = _deterministicService.Distribute(surplusW, effectiveBatteries);

        // ── 5. Persistance ────────────────────────────────────────────────────
        var session = BuildSession(result, weather, mlReco, decisionEngine, batteries);
        await _repository.SaveSessionAsync(session, ct);

        _logger.LogInformation(
            "Smart distribution complete: engine={Engine}, allocated={Alloc}W, unused={Unused}W, sessionId={Id}",
            decisionEngine, result.TotalAllocatedW, result.UnusedSurplusW, session.Id);

        return new SmartDistributionResult(
            Distribution:   result,
            DecisionEngine: decisionEngine,
            MLRecommendation: mlReco,
            Weather:        weather,
            SessionId:      session.Id
        );
    }

    // ── Builders privés ───────────────────────────────────────────────────────

    private static DistributionFeatures BuildFeatures(
        double surplusW, IList<Battery> batteries, WeatherData weather)
    {
        double avg6hRad = weather.RadiationForecast12h.Take(6).DefaultIfEmpty(0).Average();

        return new DistributionFeatures
        {
            HourOfDay               = DateTime.UtcNow.Hour,
            DayOfWeek               = (float)DateTime.UtcNow.DayOfWeek,
            MonthOfYear             = DateTime.UtcNow.Month,
            HoursUntilSunset        = (float)weather.HoursUntilSunset,
            CloudCoverPercent       = (float)weather.CloudCoverPercent,
            DirectRadiationWm2      = (float)weather.DirectRadiationWm2,
            DiffuseRadiationWm2     = (float)weather.DiffuseRadiationWm2,
            PrecipitationMmH        = (float)weather.PrecipitationMmH,
            AvgForecastRadiation6h  = (float)avg6hRad,
            AvgBatteryPercent       = (float)batteries.Average(b => b.CurrentPercent),
            MinBatteryPercent       = (float)batteries.Min(b => b.CurrentPercent),
            MaxBatteryPercent       = (float)batteries.Max(b => b.CurrentPercent),
            TotalCapacityWh         = (float)batteries.Sum(b => b.CapacityWh),
            UrgentBatteryCount      = batteries.Count(b => b.IsUrgent),
            SurplusW                = (float)surplusW,
            OptimalSoftMaxPercent   = 80,      // valeur par défaut (label calculé lors du retrain)
            OptimalPreventiveThreshold = 20
        };
    }

    private static DistributionSession BuildSession(
        DistributionResult result,
        WeatherData? weather,
        MLRecommendation? mlReco,
        string decisionEngine,
        IList<Battery> originalBatteries)
    {
        var session = new DistributionSession
        {
            RequestedAt      = DateTime.UtcNow,
            SurplusW         = result.SurplusInputW,
            TotalAllocatedW  = result.TotalAllocatedW,
            UnusedSurplusW   = result.UnusedSurplusW,
            DecisionEngine   = decisionEngine,
            MlConfidenceScore = mlReco?.ConfidenceScore
        };

        // Snapshots batteries
        session.BatterySnapshots = result.Allocations.Select(alloc =>
        {
            var original = originalBatteries.FirstOrDefault(b => b.Id == alloc.BatteryId);
            return new BatterySnapshot
            {
                BatteryId            = alloc.BatteryId,
                CapacityWh           = original?.CapacityWh ?? 0,
                MaxChargeRateW       = original?.MaxChargeRateW ?? 0,
                MinPercent           = original?.MinPercent ?? 0,
                SoftMaxPercent       = original?.SoftMaxPercent ?? 80,
                CurrentPercentBefore = alloc.PreviousPercent,
                CurrentPercentAfter  = alloc.NewPercent,
                Priority             = original?.Priority ?? 0,
                WasUrgent            = alloc.WasUrgent,
                AllocatedW           = alloc.AllocatedW,
                Reason               = alloc.Reason
            };
        }).ToList();

        // Snapshot météo
        if (weather is not null)
        {
            session.Weather = new WeatherSnapshot
            {
                FetchedAt               = weather.FetchedAt,
                Latitude                = weather.Latitude,
                Longitude               = weather.Longitude,
                TemperatureC            = weather.TemperatureC,
                CloudCoverPercent       = weather.CloudCoverPercent,
                PrecipitationMmH        = weather.PrecipitationMmH,
                DirectRadiationWm2      = weather.DirectRadiationWm2,
                DiffuseRadiationWm2     = weather.DiffuseRadiationWm2,
                DaylightHours           = weather.DaylightHours,
                HoursUntilSunset        = weather.HoursUntilSunset,
                RadiationForecast12hJson = JsonSerializer.Serialize(weather.RadiationForecast12h),
                CloudForecast12hJson    = JsonSerializer.Serialize(weather.CloudForecast12h)
            };
        }

        // Log ML
        if (mlReco is not null)
        {
            session.MlPrediction = new MLPredictionLog
            {
                ModelVersion                 = mlReco.ModelVersion,
                ConfidenceScore              = mlReco.ConfidenceScore,
                PredictedSoftMaxJson         = JsonSerializer.Serialize(mlReco.RecommendedSoftMaxPercent),
                PredictedPreventiveThreshold = mlReco.RecommendedPreventiveThreshold,
                WasApplied                   = decisionEngine != "Deterministic",
                PredictedAt                  = DateTime.UtcNow
            };
        }

        return session;
    }
}

/// <summary>Résultat enrichi d'une distribution intelligente.</summary>
public record SmartDistributionResult(
    DistributionResult Distribution,
    string             DecisionEngine,
    MLRecommendation?  MLRecommendation,
    WeatherData?       Weather,
    long               SessionId
);
