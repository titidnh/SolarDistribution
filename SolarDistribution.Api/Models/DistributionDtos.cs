using System.ComponentModel.DataAnnotations;

namespace SolarDistribution.Api.Models;

/// <summary>Input data for a battery.</summary>
public class BatteryInputDto
{
    /// <summary>Unique battery identifier.</summary>
    /// <example>1</example>
    [Required] public int Id { get; set; }

    /// <summary>Total battery capacity in Wh.</summary>
    /// <example>10000</example>
    [Required, Range(1, double.MaxValue)] public double CapacityWh { get; set; }

    /// <summary>Maximum charge rate in W.</summary>
    /// <example>3000</example>
    [Required, Range(1, double.MaxValue)] public double MaxChargeRateW { get; set; }

    /// <summary>Minimum charge level (%) — battery won't discharge below this.</summary>
    /// <example>10</example>
    [Required, Range(0, 100)] public double MinPercent { get; set; }

    /// <summary>Soft maximum level (%) — normal charge target. Must be &lt; HardMaxPercent.</summary>
    /// <example>80</example>
    [Range(0, 100)] public double SoftMaxPercent { get; set; } = 80;

    /// <summary>Hard maximum level (%) — absolute ceiling, never exceeded.</summary>
    /// <example>100</example>
    [Range(0, 100)] public double HardMaxPercent { get; set; } = 100;

    /// <summary>Current charge level (%).</summary>
    /// <example>50</example>
    [Required, Range(0, 100)] public double CurrentPercent { get; set; }

    /// <summary>Charging priority (1 = highest). Batteries with lowest value are filled first.</summary>
    /// <example>1</example>
    [Required, Range(1, int.MaxValue)] public int Priority { get; set; }
}

/// <summary>Distribution calculation request.</summary>
public class DistributionRequestDto
{
    /// <summary>Available solar surplus in W.</summary>
    /// <example>1200</example>
    [Required, Range(0, double.MaxValue)] public double SurplusW { get; set; }

    /// <summary>List of batteries to charge (at least one required).</summary>
    [Required, MinLength(1)] public List<BatteryInputDto> Batteries { get; set; } = [];

    /// <summary>Latitude for Open-Meteo weather data (e.g. 50.85 for Brussels). Optional — Brussels used if omitted.</summary>
    /// <example>50.85</example>
    [Range(-90, 90)] public double? Latitude { get; set; }

    /// <summary>Longitude for Open-Meteo weather data (e.g. 4.35 for Brussels). Optional — Brussels used if omitted.</summary>
    /// <example>4.35</example>
    [Range(-180, 180)] public double? Longitude { get; set; }
}

/// <summary>Charge allocation result for a single battery.</summary>
public class BatteryChargeResultDto
{
    /// <summary>Battery identifier.</summary>
    public int BatteryId { get; set; }

    /// <summary>Power allocated to this battery in W.</summary>
    public double AllocatedW { get; set; }

    /// <summary>Charge level before distribution (%).</summary>
    public double PreviousPercent { get; set; }

    /// <summary>Estimated charge level after distribution (%).</summary>
    public double NewPercent { get; set; }

    /// <summary>True if the battery was below the urgent threshold and received priority charge.</summary>
    public bool WasUrgent { get; set; }

    /// <summary>Human-readable explanation of the allocation decision.</summary>
    public string Reason { get; set; } = string.Empty;
}

/// <summary>Open-Meteo weather conditions at the time of distribution.</summary>
public class WeatherSummaryDto
{
    /// <summary>Outside temperature in °C.</summary>
    public double TemperatureC { get; set; }

    /// <summary>Cloud cover percentage (0–100).</summary>
    public double CloudCoverPercent { get; set; }

    /// <summary>Direct solar radiation in W/m².</summary>
    public double DirectRadiationWm2 { get; set; }

    /// <summary>Hours until sunset.</summary>
    public double HoursUntilSunset { get; set; }

    /// <summary>Average forecast solar radiation over the next 6 hours in W/m².</summary>
    public double AvgForecastRadiation6hWm2 { get; set; }
}

/// <summary>ML model recommendation used to adjust distribution parameters.</summary>
public class MLRecommendationDto
{
    /// <summary>ML-recommended SoftMax charge level (%).</summary>
    public double RecommendedSoftMaxPercent { get; set; }

    /// <summary>ML-recommended preventive threshold (%).</summary>
    public double RecommendedPreventiveThreshold { get; set; }

    /// <summary>Model confidence score (0.0–1.0).</summary>
    public double ConfidenceScore { get; set; }

    /// <summary>Model version used.</summary>
    public string ModelVersion { get; set; } = string.Empty;

    /// <summary>Explanation of the recommendation.</summary>
    public string Rationale { get; set; } = string.Empty;
}

/// <summary>Full distribution response including allocations, weather and ML data.</summary>
public class DistributionResponseDto
{
    /// <summary>Persisted session ID (0 for simulations).</summary>
    public long SessionId { get; set; }

    /// <summary>Input solar surplus in W.</summary>
    public double SurplusInputW { get; set; }

    /// <summary>Total power distributed across all batteries in W.</summary>
    public double TotalAllocatedW { get; set; }

    /// <summary>Unused surplus in W (batteries full or rate-limited).</summary>
    public double UnusedSurplusW { get; set; }

    /// <summary>Engine used: "Deterministic" | "ML" | "ML-Fallback"</summary>
    public string DecisionEngine { get; set; } = string.Empty;

    /// <summary>Per-battery allocation results.</summary>
    public List<BatteryChargeResultDto> Allocations { get; set; } = new List<BatteryChargeResultDto>();

    /// <summary>Weather data at distribution time (null if unavailable).</summary>
    public WeatherSummaryDto? Weather { get; set; }

    /// <summary>ML recommendation applied (null if Deterministic engine used).</summary>
    public MLRecommendationDto? MLRecommendation { get; set; }
}


/// <summary>
/// Bilan énergétique journalier — une ligne par date calendaire.
/// Retourné par GET /api/distribution/summary/daily
/// </summary>
public class DailySummaryDto
{
    /// <summary>Date du bilan (UTC, sans heure).</summary>
    public DateTime Date { get; set; }

    /// <summary>Énergie solaire autoconsommée (Wh). Null si Solcast non configuré.</summary>
    public double? SolarConsumedWh { get; set; }

    /// <summary>Énergie totale soutirée depuis le réseau sur la journée (Wh).</summary>
    public double GridConsumedWh { get; set; }

    /// <summary>Énergie chargée dans les batteries depuis le réseau (Wh).</summary>
    public double GridChargedWh { get; set; }

    /// <summary>Énergie distribuée aux batteries depuis le surplus solaire (Wh).</summary>
    public double SolarAllocatedWh { get; set; }

    /// <summary>Surplus solaire non utilisé (batteries pleines) (Wh).</summary>
    public double UnusedSurplusWh { get; set; }

    /// <summary>Économies estimées en € grâce à la charge en heures creuses. Null si pas de contexte tarifaire.</summary>
    public double? EstimatedSavingsEur { get; set; }

    /// <summary>
    /// Taux d'autosuffisance (%) = solaire / (solaire + réseau) × 100.
    /// Null si Solcast non configuré.
    /// </summary>
    public double? SelfSufficiencyPct { get; set; }

    /// <summary>Nombre de sessions de distribution sur cette journée.</summary>
    public int SessionCount { get; set; }

    /// <summary>Timestamp UTC du dernier calcul de ce bilan.</summary>
    public DateTime ComputedAt { get; set; }
}