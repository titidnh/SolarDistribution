using System.ComponentModel.DataAnnotations;

namespace SolarDistribution.Api.Models;

public class BatteryInputDto
{
    [Required] public int Id { get; set; }
    [Required, Range(1, double.MaxValue)] public double CapacityWh { get; set; }
    [Required, Range(1, double.MaxValue)] public double MaxChargeRateW { get; set; }
    [Required, Range(0, 100)] public double MinPercent { get; set; }
    [Range(0, 100)] public double SoftMaxPercent { get; set; } = 80;
    [Range(0, 100)] public double HardMaxPercent { get; set; } = 100;
    [Required, Range(0, 100)] public double CurrentPercent { get; set; }
    [Required, Range(1, int.MaxValue)] public int Priority { get; set; }
}

public class DistributionRequestDto
{
    /// <summary>Surplus solaire disponible en W</summary>
    /// <example>1200</example>
    [Required, Range(0, double.MaxValue)] public double SurplusW { get; set; }

    [Required, MinLength(1)] public List<BatteryInputDto> Batteries { get; set; } = [];

    /// <summary>Latitude pour la météo Open-Meteo (ex: 50.85 pour Bruxelles)</summary>
    /// <example>50.85</example>
    [Range(-90, 90)] public double? Latitude { get; set; }

    /// <summary>Longitude pour la météo Open-Meteo (ex: 4.35 pour Bruxelles)</summary>
    /// <example>4.35</example>
    [Range(-180, 180)] public double? Longitude { get; set; }
}

public class BatteryChargeResultDto
{
    public int BatteryId { get; set; }
    public double AllocatedW { get; set; }
    public double PreviousPercent { get; set; }
    public double NewPercent { get; set; }
    public bool WasUrgent { get; set; }
    public string Reason { get; set; } = string.Empty;
}

public class WeatherSummaryDto
{
    public double TemperatureC { get; set; }
    public double CloudCoverPercent { get; set; }
    public double DirectRadiationWm2 { get; set; }
    public double HoursUntilSunset { get; set; }
    public double AvgForecastRadiation6hWm2 { get; set; }
}

public class MLRecommendationDto
{
    public double RecommendedSoftMaxPercent { get; set; }
    public double RecommendedPreventiveThreshold { get; set; }
    public double ConfidenceScore { get; set; }
    public string ModelVersion { get; set; } = string.Empty;
    public string Rationale { get; set; } = string.Empty;
}

public class DistributionResponseDto
{
    public long SessionId { get; set; }
    public double SurplusInputW { get; set; }
    public double TotalAllocatedW { get; set; }
    public double UnusedSurplusW { get; set; }
    /// <summary>"Deterministic" | "ML" | "ML-Fallback"</summary>
    public string DecisionEngine { get; set; } = string.Empty;
    public List<BatteryChargeResultDto> Allocations { get; set; } = new List<BatteryChargeResultDto>();
    public WeatherSummaryDto? Weather { get; set; }
    public MLRecommendationDto? MLRecommendation { get; set; }
}
