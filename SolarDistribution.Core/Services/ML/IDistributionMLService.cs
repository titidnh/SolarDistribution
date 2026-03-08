using Microsoft.ML.Data;

namespace SolarDistribution.Core.Services.ML;

// ── ML Features ───────────────────────────────────────────────────────────────

/// <summary>
/// Input features for the ML model.
/// Each row represents a historical distribution session with valid feedback.
///
/// CYCLIC ENCODING for hour + month:
///   Raw values (1-12, 0-23) are misleading for tree models because
///   December (12) and January (1) appear far apart.
///   Solution: use sin/cos — December and January are adjacent on the circle.
///   Raw values are still kept for direct thresholds used by FastTree.
///
/// TARIFF FEATURES:
///   Allow the ML model to learn WHEN to charge from the grid.
///   E.g. if tariff is low and no sun expected → SoftMax can increase to 90%.
/// </summary>
public class DistributionFeatures
{
    // ── Temporel brut ─────────────────────────────────────────────────────────
    [LoadColumn(0)]  public float HourOfDay   { get; set; }   // 0-23
    [LoadColumn(1)]  public float DayOfWeek   { get; set; }   // 0-6
    [LoadColumn(2)]  public float MonthOfYear { get; set; }   // 1-12
    [LoadColumn(3)]  public float DayOfYear   { get; set; }   // 1-366 : progression annuelle

    // ── Encodage cyclique heure (période = 24h) ───────────────────────────────
    [LoadColumn(4)]  public float SinHour     { get; set; }   // sin(2π × h / 24)
    [LoadColumn(5)]  public float CosHour     { get; set; }   // cos(2π × h / 24)

    // ── Encodage cyclique mois (période = 12) ─────────────────────────────────
    // Juin (6) = pic de production, décembre (12) = creux.
    [LoadColumn(6)]  public float SinMonth    { get; set; }   // sin(2π × (m-1) / 12)
    [LoadColumn(7)]  public float CosMonth    { get; set; }   // cos(2π × (m-1) / 12)

    // ── Saisonnalité directe ──────────────────────────────────────────────────
    [LoadColumn(8)]  public float DaylightHours    { get; set; }   // h de jour : 8h (déc) → 16h (juin)
    [LoadColumn(9)]  public float HoursUntilSunset { get; set; }

    // ── Météo ─────────────────────────────────────────────────────────────────
    [LoadColumn(10)] public float CloudCoverPercent      { get; set; }
    [LoadColumn(11)] public float DirectRadiationWm2     { get; set; }
    [LoadColumn(12)] public float DiffuseRadiationWm2    { get; set; }
    [LoadColumn(13)] public float PrecipitationMmH       { get; set; }
    [LoadColumn(14)] public float AvgForecastRadiation6h { get; set; }  // prévision 6h

    // ── État batteries ────────────────────────────────────────────────────────
    [LoadColumn(15)] public float AvgBatteryPercent   { get; set; }
    [LoadColumn(16)] public float MinBatteryPercent   { get; set; }
    [LoadColumn(17)] public float MaxBatteryPercent   { get; set; }
    [LoadColumn(18)] public float TotalCapacityWh     { get; set; }
    [LoadColumn(19)] public float UrgentBatteryCount  { get; set; }
    [LoadColumn(20)] public float TotalMaxChargeRateW { get; set; }

    // ML-4: dispersion features — enable the model to distinguish
    // heterogeneous installations (batteries with very different capacities)
    // from homogeneous ones, without access to individual features.

    /// <summary>Standard deviation of SOC between batteries (0 if single battery).</summary>
    [LoadColumn(21)] public float SocStdDev           { get; set; }

    /// <summary>Max/min ratio of installed capacities (1.0 if batteries are identical).</summary>
    [LoadColumn(22)] public float CapacityRatio        { get; set; }

    /// <summary>Number of batteries with Priority > 0 (non-urgent batteries).</summary>
    [LoadColumn(23)] public float NonUrgentBatteryCount { get; set; }

// ── Solar surplus ───────────────────────────────────────────────────────
    [LoadColumn(24)] public float SurplusW { get; set; }

// ── Tariff context ────────────────────────────────────────────────────
    // These features let the ML model learn to adapt the SoftMax and the
    // preventive threshold according to electricity cost and production forecast.

    /// <summary>Prix actuel normalisé 0→1 (0.4 €/kWh = 1.0). 0.5 si inconnu.</summary>
    [LoadColumn(25)] public float NormalizedTariff      { get; set; }

    /// <summary>1.0 si on est en créneau à tarif favorable, sinon 0.0</summary>
    [LoadColumn(26)] public float IsOffPeakHour         { get; set; }

    /// <summary>Heures avant le prochain créneau favorable (0 = déjà favorable)</summary>
    [LoadColumn(27)] public float HoursToNextFavorable  { get; set; }

    /// <summary>Rayonnement moyen prévu sur l'horizon de décision (W/m²)</summary>
    [LoadColumn(28)] public float AvgSolarForecastGrid  { get; set; }

    /// <summary>1.0 si production solaire significative attendue prochainement</summary>
    [LoadColumn(29)] public float SolarExpectedSoon     { get; set; }

    /// <summary>Économie potentielle en €/kWh si on charge réseau maintenant vs plus tard</summary>
    [LoadColumn(30)] public float MaxSavingsPerKwh      { get; set; }

// ── Labels (regression targets — from real SessionFeedback) ─────────
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

// ── Prediction result ────────────────────────────────────────────────────

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
    /// ML-6: GetStatus is async to avoid GetAwaiter().GetResult()
    /// which can cause a deadlock in certain .NET scheduling contexts.
    /// </summary>
    Task<MLModelStatus>     GetStatusAsync(CancellationToken ct = default);
    /// <summary>
    /// ML-5: Checks whether the active model has undergone significant drift
    /// over the last N sessions. Returns true if a forced retrain is advised.
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
