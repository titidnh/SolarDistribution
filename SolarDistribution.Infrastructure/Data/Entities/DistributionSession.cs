using System;
using System.Collections.Generic;

namespace SolarDistribution.Infrastructure.Data.Entities;

public class DistributionSession
{
    public long     Id              { get; set; }
    public DateTime RequestedAt     { get; set; } = DateTime.UtcNow;
    public double   SurplusW        { get; set; }
    public double   TotalAllocatedW { get; set; }
    public double   UnusedSurplusW  { get; set; }
    public double   GridChargedW    { get; set; }
    public string   DecisionEngine  { get; set; } = "Deterministic";
    public double?  MlConfidenceScore { get; set; }

    // Standard tariff context
    public string? TariffSlotName            { get; set; }
    public double? TariffPricePerKwh         { get; set; }
    public bool    WasGridChargeFavorable     { get; set; }
    public bool    SolarExpectedSoon          { get; set; }
    public double? HoursToNextFavorableTariff { get; set; }
    public double? AvgSolarForecastWm2        { get; set; }
    public double? TariffMaxSavingsPerKwh     { get; set; }

    // Extended adaptive context (ML-7)
    /// <summary>Hours remaining in the off-peak slot at session time.</summary>
    public double? HoursRemainingInSlot       { get; set; }
    /// <summary>Hours before next sunlight (null if not forecast or full night).</summary>
    public double? HoursUntilSolar            { get; set; }
    /// <summary>True if at least one battery was in emergency grid charging.</summary>
    public bool    HadEmergencyGridCharge     { get; set; }
    /// <summary>Average effective adaptive grid power (W), excluding emergency.</summary>
    public double? EffectiveGridChargeW       { get; set; }

    // Installation-specific HA forecasts (ML-8)
    /// <summary>Estimated solar production today from HA (Wh). Null if not configured.</summary>
    public double? ForecastTodayWh            { get; set; }
    /// <summary>Estimated solar production tomorrow from HA (Wh). Null if not configured.</summary>
    public double? ForecastTomorrowWh         { get; set; }

    public ICollection<BatterySnapshot> BatterySnapshots { get; set; } = new List<BatterySnapshot>();
    public WeatherSnapshot?  Weather      { get; set; }
    public MLPredictionLog?  MlPrediction { get; set; }
    public SessionFeedback?  Feedback     { get; set; }
}
