using System;
using System.Collections.Generic;

namespace SolarDistribution.Core.Data.Entities;

public class DistributionSession
{
    public long Id { get; set; }
    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;
    public double SurplusW { get; set; }
    public double TotalAllocatedW { get; set; }
    public double UnusedSurplusW { get; set; }
    public double GridChargedW { get; set; }
    public string DecisionEngine { get; set; } = "Deterministic";
    public double? MlConfidenceScore { get; set; }

    // Standard tariff context
    public string? TariffSlotName { get; set; }
    public double? TariffPricePerKwh { get; set; }
    public bool WasGridChargeFavorable { get; set; }
    public bool SolarExpectedSoon { get; set; }
    public double? HoursToNextFavorableTariff { get; set; }
    public double? AvgSolarForecastWm2 { get; set; }
    public double? TariffMaxSavingsPerKwh { get; set; }

    // Extended adaptive context (ML-7)
    /// <summary>Hours remaining in the off-peak slot at the time of the session.</summary>
    public double? HoursRemainingInSlot { get; set; }
    /// <summary>Hours until next sunlight (null if none expected or total night).</summary>
    public double? HoursUntilSolar { get; set; }
    /// <summary>True if at least one battery was in emergency grid charge.</summary>
    public bool HadEmergencyGridCharge { get; set; }
    /// <summary>Average effective adaptive grid power (W), excluding emergency.</summary>
    public double? EffectiveGridChargeW { get; set; }

    // Installation-specific HA forecasts (ML-8)
    /// <summary>Solar production estimated for today from HA (Wh). Null if not configured.</summary>
    public double? ForecastTodayWh { get; set; }
    /// <summary>Solar production estimated for tomorrow from HA (Wh). Null if not configured.</summary>
    public double? ForecastTomorrowWh { get; set; }

    // Load forecasting (estimated consumption)
    public double? MeasuredConsumptionW { get; set; }
    public double? EstimatedConsumptionNextHoursWh { get; set; }

    // Intraday + daily balance (Feature 3 & 4)
    public double? ForecastRemainingTodayWh { get; set; }
    public double? EnergyDeficitTodayWh { get; set; }
    public double? DailySolarConsumedWh { get; set; }

    public ICollection<BatterySnapshot> BatterySnapshots { get; set; } = new List<BatterySnapshot>();
    public WeatherSnapshot? Weather { get; set; }
    public MLPredictionLog? MlPrediction { get; set; }
    public SessionFeedback? Feedback { get; set; }
}
