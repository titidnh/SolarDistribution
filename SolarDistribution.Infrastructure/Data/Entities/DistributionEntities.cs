namespace SolarDistribution.Infrastructure.Data.Entities;

public class DistributionSession
{
    public long     Id              { get; set; }
    public DateTime RequestedAt     { get; set; } = DateTime.UtcNow;
    public double   SurplusW        { get; set; }
    public double   TotalAllocatedW { get; set; }
    public double   UnusedSurplusW  { get; set; }
    public double   GridChargedW    { get; set; }    // charge depuis réseau (Pass 3)
    public string   DecisionEngine  { get; set; } = "Deterministic";
    public double?  MlConfidenceScore { get; set; }

    // ── Tariff context persisted for ML ───────────────────────────────────
    public string? TariffSlotName             { get; set; }
    public double? TariffPricePerKwh          { get; set; }
    public bool    WasGridChargeFavorable      { get; set; }
    public bool    SolarExpectedSoon           { get; set; }
    public double? HoursToNextFavorableTariff  { get; set; }
    public double? AvgSolarForecastWm2         { get; set; }
    public double? TariffMaxSavingsPerKwh      { get; set; }

    public ICollection<BatterySnapshot> BatterySnapshots { get; set; } = new List<BatterySnapshot>();
    public WeatherSnapshot?  Weather      { get; set; }
    public MLPredictionLog?  MlPrediction { get; set; }
    public SessionFeedback?  Feedback     { get; set; }
}

/// <summary>
/// Feedback observed N hours after a session.
/// Contains the REAL labels for ML training.
/// Collected automatically by FeedbackEvaluator.
/// </summary>
public class SessionFeedback
{
    public long     Id        { get; set; }
    public long     SessionId { get; set; }
    public DateTime CollectedAt { get; set; } = DateTime.UtcNow;
    // Delay in hours between session and feedback collection
    public double   FeedbackDelayHours { get; set; }

    /// <summary>Actual SOC of each battery N hours after the session (JSON)</summary>
    public string ObservedSocJson { get; set; } = "{}";

    public double AvgSocAtFeedback { get; set; }
    public double MinSocAtFeedback { get; set; }

    /// <summary>Ratio of stored energy / available energy (0→1)</summary>
    public double EnergyEfficiencyScore { get; set; }

    /// <summary>Batteries that remained above MinPercent (0→1)</summary>
    public double AvailabilityScore { get; set; }

    /// <summary>True label for SoftMax model — SoftMax that would have been optimal</summary>
    public double ObservedOptimalSoftMax      { get; set; }

    /// <summary>True label for preventive model — threshold that would have avoided empty batteries</summary>
    public double ObservedOptimalPreventive   { get; set; }

    /// <summary>Global composite score (0→1) — quality filter before training</summary>
    public double CompositeScore { get; set; }

    public FeedbackStatus Status        { get; set; } = FeedbackStatus.Pending;
    public string?        InvalidReason { get; set; }

    public DistributionSession Session { get; set; } = null!;
}

public enum FeedbackStatus { Pending, Valid, Invalid, Skipped }

public class BatterySnapshot
{
    public long   Id        { get; set; }
    public long   SessionId { get; set; }
    public int    BatteryId { get; set; }
    public double CapacityWh           { get; set; }
    public double MaxChargeRateW       { get; set; }
    public double MinPercent           { get; set; }
    public double SoftMaxPercent       { get; set; }
    public double CurrentPercentBefore { get; set; }
    public double CurrentPercentAfter  { get; set; }
    public int    Priority             { get; set; }
    public bool   WasUrgent            { get; set; }
    public double AllocatedW           { get; set; }
    public bool   IsGridCharge         { get; set; }   // true = chargé depuis réseau
    public string Reason               { get; set; } = string.Empty;

    public DistributionSession Session { get; set; } = null!;
}

public class WeatherSnapshot
{
    public long     Id         { get; set; }
    public long     SessionId  { get; set; }
    public DateTime FetchedAt  { get; set; }
    public double   Latitude   { get; set; }
    public double   Longitude  { get; set; }
    public double   TemperatureC       { get; set; }
    public double   CloudCoverPercent  { get; set; }
    public double   PrecipitationMmH   { get; set; }
    public double   DirectRadiationWm2 { get; set; }
    public double   DiffuseRadiationWm2 { get; set; }
    public double   DaylightHours      { get; set; }
    public double   HoursUntilSunset   { get; set; }
    public string   RadiationForecast12hJson { get; set; } = "[]";
    public string   CloudForecast12hJson     { get; set; } = "[]";

    public DistributionSession Session { get; set; } = null!;
}

public class MLPredictionLog
{
    public long     Id        { get; set; }
    public long     SessionId { get; set; }
    public string   ModelVersion                 { get; set; } = string.Empty;
    public double   ConfidenceScore              { get; set; }
    // Efficiency score computed post-prediction (0→1) — stored for diagnostics
    public double   EfficiencyScore              { get; set; }
    public string   PredictedSoftMaxJson         { get; set; } = string.Empty;
    public double   PredictedPreventiveThreshold { get; set; }
    public bool     WasApplied                   { get; set; }
    public DateTime PredictedAt                  { get; set; }

    public DistributionSession Session { get; set; } = null!;
}
