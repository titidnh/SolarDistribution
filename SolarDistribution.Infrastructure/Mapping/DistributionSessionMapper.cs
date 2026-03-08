using System.Text.Json;
using SolarDistribution.Core.Data.Entities;
using SolarDistribution.Core.Models;
using SolarDistribution.Core.Services;
using SolarDistribution.Core.Services.ML;

namespace SolarDistribution.Infrastructure.Mapping;

/// <summary>
/// Fix #6 : Responsabilité de la construction des entités de persistance
/// extraite de SmartDistributionService vers ce mapper dédié.
///
/// SmartDistributionService (Core) ne doit pas savoir COMMENT les données
/// sont persistées (EF, JSON, etc.) — c'est le rôle de l'Infrastructure.
/// </summary>
public static class DistributionSessionMapper
{
    public static DistributionSession ToEntity(
        DistributionResult  result,
        WeatherData?        wx,
        MLRecommendation?   mlReco,
        string              decisionEngine,
        IList<Battery>      originalBatteries,
        TariffContext       tariff)
    {
        var session = new DistributionSession
        {
            RequestedAt                = DateTime.UtcNow,
            SurplusW                   = result.SurplusInputW,
            TotalAllocatedW            = result.TotalAllocatedW,
            UnusedSurplusW             = result.UnusedSurplusW,
            GridChargedW               = result.GridChargedW,
            DecisionEngine             = decisionEngine,
            MlConfidenceScore          = mlReco?.ConfidenceScore,
            TariffSlotName             = tariff.ActiveSlotName,
            TariffPricePerKwh          = tariff.CurrentPricePerKwh,
            WasGridChargeFavorable     = tariff.IsFavorableForGrid,
            SolarExpectedSoon          = tariff.SolarExpectedSoon,
            HoursToNextFavorableTariff = tariff.HoursToNextFavorable,
            AvgSolarForecastWm2        = tariff.AvgSolarForecastWm2,
            TariffMaxSavingsPerKwh     = tariff.MaxSavingsPerKwh,
        };

        session.BatterySnapshots = result.Allocations.Select(alloc =>
        {
            var orig = originalBatteries.FirstOrDefault(b => b.Id == alloc.BatteryId);
            return new BatterySnapshot
            {
                BatteryId            = alloc.BatteryId,
                CapacityWh           = orig?.CapacityWh       ?? 0,
                MaxChargeRateW       = orig?.MaxChargeRateW   ?? 0,
                MinPercent           = orig?.MinPercent       ?? 0,
                SoftMaxPercent       = orig?.SoftMaxPercent   ?? 80,
                CurrentPercentBefore = alloc.PreviousPercent,
                CurrentPercentAfter  = alloc.NewPercent,
                Priority             = orig?.Priority         ?? 0,
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
                WasApplied                   = decisionEngine != "Deterministic",
                PredictedAt                  = DateTime.UtcNow
            };
        }

        return session;
    }
}
