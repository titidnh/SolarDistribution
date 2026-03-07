using Microsoft.ML.Data;

namespace SolarDistribution.Core.Services.ML;

// ── Données d'entraînement ────────────────────────────────────────────────────

/// <summary>
/// Features utilisées pour entraîner et prédire.
/// Chaque ligne = une session de distribution historique.
/// </summary>
public class DistributionFeatures
{
    // Temporel
    [LoadColumn(0)] public float HourOfDay { get; set; }         // 0-23
    [LoadColumn(1)] public float DayOfWeek { get; set; }         // 0-6
    [LoadColumn(2)] public float MonthOfYear { get; set; }       // 1-12
    [LoadColumn(3)] public float HoursUntilSunset { get; set; }  // 0-12+

    // Météo
    [LoadColumn(4)] public float CloudCoverPercent { get; set; }
    [LoadColumn(5)] public float DirectRadiationWm2 { get; set; }
    [LoadColumn(6)] public float DiffuseRadiationWm2 { get; set; }
    [LoadColumn(7)] public float PrecipitationMmH { get; set; }
    [LoadColumn(8)] public float AvgForecastRadiation6h { get; set; } // moyenne sur 6h à venir

    // État batteries (moyennes pondérées)
    [LoadColumn(9)]  public float AvgBatteryPercent { get; set; }
    [LoadColumn(10)] public float MinBatteryPercent { get; set; }
    [LoadColumn(11)] public float MaxBatteryPercent { get; set; }
    [LoadColumn(12)] public float TotalCapacityWh { get; set; }
    [LoadColumn(13)] public float UrgentBatteryCount { get; set; }

    // Surplus
    [LoadColumn(14)] public float SurplusW { get; set; }

    // Cible : SoftMax optimal observé a posteriori (régression)
    [LoadColumn(15)] public float OptimalSoftMaxPercent { get; set; }

    // Cible : seuil préventif optimal (régression)
    [LoadColumn(16)] public float OptimalPreventiveThreshold { get; set; }
}

/// <summary>Prédiction du SoftMaxPercent optimal.</summary>
public class SoftMaxPrediction
{
    [ColumnName("Score")]
    public float PredictedSoftMaxPercent { get; set; }
}

/// <summary>Prédiction du seuil préventif optimal.</summary>
public class PreventivePrediction
{
    [ColumnName("Score")]
    public float PredictedPreventiveThreshold { get; set; }
}

// ── Résultat de prédiction ────────────────────────────────────────────────────

/// <summary>
/// Recommandations du modèle ML pour une session de distribution.
/// </summary>
public record MLRecommendation(
    /// <summary>SoftMaxPercent recommandé (ex: 70% si nuit longue prévue)</summary>
    double RecommendedSoftMaxPercent,

    /// <summary>Seuil de recharge préventive recommandé (ex: 40% si peu de production demain)</summary>
    double RecommendedPreventiveThreshold,

    /// <summary>Score de confiance global du modèle (0.0 à 1.0)</summary>
    double ConfidenceScore,

    /// <summary>Version du modèle ayant produit cette recommandation</summary>
    string ModelVersion,

    /// <summary>Explication textuelle de la décision</summary>
    string Rationale
);

// ── Interface ─────────────────────────────────────────────────────────────────

public interface IDistributionMLService
{
    /// <summary>
    /// Prédit les paramètres optimaux pour la session en cours.
    /// Retourne null si le modèle n'est pas encore disponible (pas assez de données).
    /// </summary>
    Task<MLRecommendation?> PredictAsync(DistributionFeatures features, CancellationToken ct = default);

    /// <summary>
    /// Ré-entraîne le modèle sur l'ensemble des sessions historiques disponibles.
    /// </summary>
    Task<MLTrainingResult> RetrainAsync(CancellationToken ct = default);

    /// <summary>Statut actuel du modèle ML.</summary>
    MLModelStatus GetStatus();
}

public record MLTrainingResult(
    bool Success,
    int TrainingSamples,
    double SoftMaxRSquared,
    double PreventiveRSquared,
    string ModelVersion,
    string? ErrorMessage = null
);

public record MLModelStatus(
    bool IsAvailable,
    string? ModelVersion,
    int TrainingSamples,
    double? SoftMaxRSquared,
    double? PreventiveRSquared,
    DateTime? TrainedAt,
    int SessionsInDb,
    int ValidFeedbacksInDb,
    int MinSessionsRequired
);
