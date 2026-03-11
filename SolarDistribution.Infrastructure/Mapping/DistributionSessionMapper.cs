using System.Text.Json;
using SolarDistribution.Core.Data.Entities;
using SolarDistribution.Core.Models;
using SolarDistribution.Core.Services;
using SolarDistribution.Core.Services.ML;

namespace SolarDistribution.Infrastructure.Mapping;

public static class DistributionSessionMapper
{
    public static DistributionSession ToEntity(
        DistributionResult  result,
        WeatherData?        wx,
        MLRecommendation?   mlReco,
        string              decisionEngine,
        IList<Battery>      originalBatteries,
        TariffContext       tariff,
        double?             measuredConsumptionW = null,
        double?             forecastTodayWhAtStartOfDay = null)
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

            // Contexte tarifaire standard
            TariffSlotName             = tariff.ActiveSlotName,
            TariffPricePerKwh          = tariff.CurrentPricePerKwh,
            WasGridChargeFavorable     = tariff.IsFavorableForGrid,
            SolarExpectedSoon          = tariff.SolarExpectedSoon,
            HoursToNextFavorableTariff = tariff.HoursToNextFavorable,
            AvgSolarForecastWm2        = tariff.AvgSolarForecastWm2,
            TariffMaxSavingsPerKwh     = tariff.MaxSavingsPerKwh,

            // Contexte adaptatif étendu (ML-7)
            HoursRemainingInSlot       = tariff.HoursRemainingInSlot,
            HoursUntilSolar            = tariff.HoursUntilSolar.HasValue
                                         && tariff.HoursUntilSolar.Value < double.MaxValue
                ? tariff.HoursUntilSolar.Value : null,

            // Prévisions HA installation-spécifiques (ML-8)
            ForecastTodayWh            = tariff.ForecastTodayWh,
            ForecastTomorrowWh         = tariff.ForecastTomorrowWh,

            // Load forecasting
            EstimatedConsumptionNextHoursWh = tariff.EstimatedConsumptionNextHoursWh,
            MeasuredConsumptionW            = measuredConsumptionW,

            // Intraday + bilan journalier (Feature 3 & 4)
            ForecastRemainingTodayWh        = tariff.ForecastRemainingTodayWh,
            EnergyDeficitTodayWh            = tariff.EnergyDeficitTodayWh,
            // DailySolarConsumedWh = ForecastToday(début journée) − ForecastRemainingToday
            // forecastTodayWhAtStartOfDay est la valeur lue en début de journée (stockée par le worker)
            DailySolarConsumedWh            =
                forecastTodayWhAtStartOfDay.HasValue && tariff.ForecastRemainingTodayWh.HasValue
                    ? Math.Max(0, forecastTodayWhAtStartOfDay.Value - tariff.ForecastRemainingTodayWh.Value)
                    : null,
        };

        session.BatterySnapshots = result.Allocations.Select(alloc =>
        {
            var orig = originalBatteries.FirstOrDefault(b => b.Id == alloc.BatteryId);
            return new BatterySnapshot
            {
                BatteryId             = alloc.BatteryId,
                CapacityWh            = orig?.CapacityWh       ?? 0,
                MaxChargeRateW        = orig?.MaxChargeRateW   ?? 0,
                MinPercent            = orig?.MinPercent       ?? 0,
                SoftMaxPercent        = orig?.SoftMaxPercent   ?? 80,
                CurrentPercentBefore  = alloc.PreviousPercent,
                CurrentPercentAfter   = alloc.NewPercent,
                Priority              = orig?.Priority         ?? 0,
                WasUrgent             = alloc.WasUrgent,
                AllocatedW            = alloc.AllocatedW,
                IsGridCharge          = alloc.IsGridCharge,
                IsEmergencyGridCharge = alloc.IsEmergencyGridCharge,
                GridChargeAllowedW    = orig?.GridChargeAllowedW ?? 0,
                Reason                = alloc.Reason
            };
        }).ToList();

        // Champs de synthèse session
        session.HadEmergencyGridCharge = result.Allocations.Any(a => a.IsEmergencyGridCharge);

        var hcBatteries = originalBatteries
            .Where(b => b.GridChargeAllowedW > 0 && !b.IsEmergencyGridCharge)
            .Select(b => b.GridChargeAllowedW)
            .ToList();
        session.EffectiveGridChargeW = hcBatteries.Any() ? hcBatteries.Average() : null;

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
