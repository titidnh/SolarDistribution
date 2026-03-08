using Microsoft.ML.Data;

namespace SolarDistribution.Core.Services.ML;

// ── Features ML ───────────────────────────────────────────────────────────────

/// <summary>
/// Features d'entrée du modèle ML.
/// Chaque ligne = une session de distribution historique avec feedback valide.
///
/// ENCODAGE CYCLIQUE heure + mois :
///   Les valeurs brutes (1-12, 0-23) sont mal perçues par les arbres car
///   décembre (12) et janvier (1) semblent très distants.
///   Solution : sin/cos — décembre et janvier sont adjacents sur le cercle.
///   On garde aussi les valeurs brutes pour les seuils directs de FastTree.
///
/// FEATURES TARIFAIRES :
///   Permettent au ML d'apprendre QUAND charger depuis le réseau.
///   Ex : si tarif bas + pas de soleil prévu → SoftMax peut monter à 90%.
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

    // ── Surplus solaire ───────────────────────────────────────────────────────
    [LoadColumn(21)] public float SurplusW { get; set; }

    // ── Contexte tarifaire ────────────────────────────────────────────────────
    // Ces features permettent au ML d'apprendre à adapter le SoftMax et le seuil
    // préventif selon le coût de l'électricité et la prévision de production.

    /// <summary>Prix actuel normalisé 0→1 (0.4 €/kWh = 1.0). 0.5 si inconnu.</summary>
    [LoadColumn(22)] public float NormalizedTariff      { get; set; }

    /// <summary>1.0 si on est en créneau à tarif favorable, sinon 0.0</summary>
    [LoadColumn(23)] public float IsOffPeakHour         { get; set; }

    /// <summary>Heures avant le prochain créneau favorable (0 = déjà favorable)</summary>
    [LoadColumn(24)] public float HoursToNextFavorable  { get; set; }

    /// <summary>Rayonnement moyen prévu sur l'horizon de décision (W/m²)</summary>
    [LoadColumn(25)] public float AvgSolarForecastGrid  { get; set; }

    /// <summary>1.0 si production solaire significative attendue prochainement</summary>
    [LoadColumn(26)] public float SolarExpectedSoon     { get; set; }

    /// <summary>Économie potentielle en €/kWh si on charge réseau maintenant vs plus tard</summary>
    [LoadColumn(27)] public float MaxSavingsPerKwh      { get; set; }

    // ── Labels (cibles de régression — issus de SessionFeedback réel) ─────────
    [LoadColumn(28)] public float OptimalSoftMaxPercent      { get; set; }
    [LoadColumn(29)] public float OptimalPreventiveThreshold { get; set; }
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
    MLModelStatus           GetStatus();
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
