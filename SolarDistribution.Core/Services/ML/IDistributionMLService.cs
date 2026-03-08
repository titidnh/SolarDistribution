using Microsoft.ML.Data;

namespace SolarDistribution.Core.Services.ML;

// ── ML features ───────────────────────────────────────────────────────────────

/// <summary>
/// Input features for the ML model.
/// Each row represents a historical distribution session with valid feedback.
///
/// CYCLIC ENCODING for hour + month:
///   Raw values (1-12, 0-23) are misleading for trees because
///   December (12) and January (1) appear far apart.
///   Solution: sin/cos — December and January become adjacent on the circle.
///   We also keep raw values for FastTree direct thresholds.
///
/// TARIFF FEATURES:
///   Allow ML to learn WHEN to charge from the grid.
///   E.g.: low tariff + no sun expected -> SoftMax may rise to 90%.
/// </summary>
public class DistributionFeatures
{
    // ── Raw temporal ─────────────────────────────────────────────────────────
    [LoadColumn(0)]  public float HourOfDay   { get; set; }   // 0-23
    [LoadColumn(1)]  public float DayOfWeek   { get; set; }   // 0-6
    [LoadColumn(2)]  public float MonthOfYear { get; set; }   // 1-12
    [LoadColumn(3)]  public float DayOfYear   { get; set; }   // 1-366 : progression annuelle

    // ── Cyclic encoding hour (period = 24h) ─────────────────────────────────
    [LoadColumn(4)]  public float SinHour     { get; set; }   // sin(2π × h / 24)
    [LoadColumn(5)]  public float CosHour     { get; set; }   // cos(2π × h / 24)

    // ── Cyclic encoding month (period = 12) ─────────────────────────────────
    // June (6) = production peak, December (12) = low.
    [LoadColumn(6)]  public float SinMonth    { get; set; }   // sin(2π × (m-1) / 12)
    [LoadColumn(7)]  public float CosMonth    { get; set; }   // cos(2π × (m-1) / 12)

    // ── Direct seasonality ──────────────────────────────────────────────────
    [LoadColumn(8)]  public float DaylightHours    { get; set; }   // h de jour : 8h (déc) → 16h (juin)
    [LoadColumn(9)]  public float HoursUntilSunset { get; set; }

    // ── Weather ─────────────────────────────────────────────────────────────
    [LoadColumn(10)] public float CloudCoverPercent      { get; set; }
    [LoadColumn(11)] public float DirectRadiationWm2     { get; set; }
    [LoadColumn(12)] public float DiffuseRadiationWm2    { get; set; }
    [LoadColumn(13)] public float PrecipitationMmH       { get; set; }
    [LoadColumn(14)] public float AvgForecastRadiation6h { get; set; }  // prévision 6h

    // ── Battery state ───────────────────────────────────────────────────────
    [LoadColumn(15)] public float AvgBatteryPercent   { get; set; }
    [LoadColumn(16)] public float MinBatteryPercent   { get; set; }
    [LoadColumn(17)] public float MaxBatteryPercent   { get; set; }
    [LoadColumn(18)] public float TotalCapacityWh     { get; set; }
    [LoadColumn(19)] public float UrgentBatteryCount  { get; set; }
    [LoadColumn(20)] public float TotalMaxChargeRateW { get; set; }

    // ML-4: dispersion features — allow the model to distinguish
    // heterogeneous installations (batteries with very different capacities)
    // from homogeneous ones, without access to individual features.

    /// <summary>Standard deviation of SOC among batteries (0 if single battery).</summary>
    [LoadColumn(21)] public float SocStdDev           { get; set; }

    /// <summary>Max/min ratio of installed capacities (1.0 if batteries identical).</summary>
    [LoadColumn(22)] public float CapacityRatio        { get; set; }

    /// <summary>Number of batteries with Priority > 0 (non-urgent batteries).</summary>
    [LoadColumn(23)] public float NonUrgentBatteryCount { get; set; }

    // ── Solar surplus ───────────────────────────────────────────────────────
    [LoadColumn(24)] public float SurplusW { get; set; }

    // ── Tariff context ──────────────────────────────────────────────────────
    // These features allow ML to learn how to adapt SoftMax and the preventive
    // threshold according to electricity cost and production forecast.

    /// <summary>Current normalized price 0→1 (0.4 €/kWh = 1.0). 0.5 if unknown.</summary>
    [LoadColumn(25)] public float NormalizedTariff      { get; set; }

    /// <summary>1.0 if currently in a favorable tariff slot, otherwise 0.0</summary>
    [LoadColumn(26)] public float IsOffPeakHour         { get; set; }

    /// <summary>Hours until the next favorable slot (0 = already favorable)</summary>
    [LoadColumn(27)] public float HoursToNextFavorable  { get; set; }

    /// <summary>Average forecasted radiation over the decision horizon (W/m²)</summary>
    [LoadColumn(28)] public float AvgSolarForecastGrid  { get; set; }

    /// <summary>1.0 if significant solar production is expected soon</summary>
    [LoadColumn(29)] public float SolarExpectedSoon     { get; set; }

    /// <summary>Potential savings in €/kWh if charging from grid now vs later</summary>
    [LoadColumn(30)] public float MaxSavingsPerKwh      { get; set; }

    // ── Labels (regression targets — derived from real SessionFeedback) ─────
    [LoadColumn(31)] public float OptimalSoftMaxPercent      { get; set; }
    [LoadColumn(32)] public float OptimalPreventiveThreshold { get; set; }
}

public class SoftMaxPrediction
{
    [ColumnName("Score")] public float PredictedSoftMaxPercent { get; set; }
}

public class PreventivePrediction
{
    [ColumnName("Score")] public float PredictedPreventiveThreshold { get; set; }
}

// ── Résultat de prédiction ────────────────────────────────────────────────────

public record MLRecommendation(
    double RecommendedSoftMaxPercent,
    double RecommendedPreventiveThreshold,
    double ConfidenceScore,
    string ModelVersion,
    string Rationale
);

// ── Interface ─────────────────────────────────────────────────────────────────

public interface IDistributionMLService
{
    Task<MLRecommendation?> PredictAsync(DistributionFeatures features, CancellationToken ct = default);
    Task<MLTrainingResult>  RetrainAsync(CancellationToken ct = default);
    /// <summary>
    /// ML-6 : GetStatus est désormais async pour éviter GetAwaiter().GetResult()
    /// qui peut provoquer un deadlock dans certains contextes de scheduling .NET.
    /// </summary>
    Task<MLModelStatus>     GetStatusAsync(CancellationToken ct = default);
    /// <summary>
    /// ML-5 : Vérifie si le modèle actif a subi une dérive significative
    /// sur les N dernières sessions. Retourne true si un retrain forcé est conseillé.
    /// </summary>
    Task<bool>              CheckForDriftAsync(int windowSize, double threshold, CancellationToken ct = default);
}

public record MLTrainingResult(
    bool   Success,
    int    TrainingSamples,
    double SoftMaxRSquared,
    double PreventiveRSquared,
    string ModelVersion,
    string? ErrorMessage = null
);

public record MLModelStatus(
    bool      IsAvailable,
    string?   ModelVersion,
    int       TrainingSamples,
    double?   SoftMaxRSquared,
    double?   PreventiveRSquared,
    DateTime? TrainedAt,
    int       SessionsInDb,
    int       ValidFeedbacksInDb,
    int       MinSessionsRequired
);
