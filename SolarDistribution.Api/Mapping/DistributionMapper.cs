using SolarDistribution.Api.Models;
using SolarDistribution.Core.Models;
using SolarDistribution.Core.Services;

namespace SolarDistribution.Api.Mapping;

public static class DistributionMapper
{
    public static Battery ToDomain(this BatteryInputDto dto) => new()
    {
        Id             = dto.Id,
        CapacityWh     = dto.CapacityWh,
        MaxChargeRateW = dto.MaxChargeRateW,
        MinPercent     = dto.MinPercent,
        SoftMaxPercent = dto.SoftMaxPercent,
        HardMaxPercent = dto.HardMaxPercent,
        CurrentPercent = dto.CurrentPercent,
        Priority       = dto.Priority
    };

    public static DistributionResponseDto ToDto(this SmartDistributionResult smart) => new()
    {
        SessionId       = smart.SessionId,
        SurplusInputW   = smart.Distribution.SurplusInputW,
        TotalAllocatedW = smart.Distribution.TotalAllocatedW,
        UnusedSurplusW  = smart.Distribution.UnusedSurplusW,
        DecisionEngine  = smart.DecisionEngine,
        Allocations     = smart.Distribution.Allocations.Select(a => a.ToDto()).ToList(),
        Weather         = smart.Weather?.ToDto(),
        MLRecommendation = smart.MLRecommendation?.ToDto()
    };

    public static BatteryChargeResultDto ToDto(this Core.Models.BatteryChargeResult r) => new()
    {
        BatteryId       = r.BatteryId,
        AllocatedW      = r.AllocatedW,
        PreviousPercent = r.PreviousPercent,
        NewPercent      = r.NewPercent,
        WasUrgent       = r.WasUrgent,
        Reason          = r.Reason
    };

    private static WeatherSummaryDto ToDto(this WeatherData w) => new()
    {
        TemperatureC              = w.TemperatureC,
        CloudCoverPercent         = w.CloudCoverPercent,
        DirectRadiationWm2        = w.DirectRadiationWm2,
        HoursUntilSunset          = w.HoursUntilSunset,
        AvgForecastRadiation6hWm2 = w.RadiationForecast12h.Take(6).DefaultIfEmpty(0).Average()
    };

    private static MLRecommendationDto ToDto(this Core.Services.ML.MLRecommendation ml) => new()
    {
        RecommendedSoftMaxPercent      = ml.RecommendedSoftMaxPercent,
        RecommendedPreventiveThreshold = ml.RecommendedPreventiveThreshold,
        ConfidenceScore                = ml.ConfidenceScore,
        ModelVersion                   = ml.ModelVersion,
        Rationale                      = ml.Rationale
    };
}
