using Microsoft.ML.Data;

namespace SolarDistribution.Core.Services.ML;

/// <summary>
/// Input features for the ML model.
/// Each row represents a historical distribution session with valid feedback.
/// </summary>
public class DistributionFeatures
{
    // ── Raw temporal ─────────────────────────────────────────────────────────
    [LoadColumn(0)] public float HourOfDay { get; set; }
    [LoadColumn(1)] public float DayOfWeek { get; set; }
    [LoadColumn(2)] public float MonthOfYear { get; set; }
    [LoadColumn(3)] public float DayOfYear { get; set; }

    // ── Cyclic encoding ──────────────────────────────────────────────────────
    [LoadColumn(4)] public float SinHour { get; set; }
    [LoadColumn(5)] public float CosHour { get; set; }
    [LoadColumn(6)] public float SinMonth { get; set; }
    [LoadColumn(7)] public float CosMonth { get; set; }

    // ── Seasonality ─────────────────────────────────────────────────────────
    [LoadColumn(8)] public float DaylightHours { get; set; }
    [LoadColumn(9)] public float HoursUntilSunset { get; set; }

    // ── Weather ─────────────────────────────────────────────────────────────
    [LoadColumn(10)] public float CloudCoverPercent { get; set; }
    [LoadColumn(11)] public float DirectRadiationWm2 { get; set; }
    [LoadColumn(12)] public float DiffuseRadiationWm2 { get; set; }
    [LoadColumn(13)] public float PrecipitationMmH { get; set; }
    [LoadColumn(14)] public float AvgForecastRadiation6h { get; set; }

    // ── Battery state ────────────────────────────────────────────────────────
    [LoadColumn(15)] public float AvgBatteryPercent { get; set; }
    [LoadColumn(16)] public float MinBatteryPercent { get; set; }
    [LoadColumn(17)] public float MaxBatteryPercent { get; set; }
    [LoadColumn(18)] public float TotalCapacityWh { get; set; }
    [LoadColumn(19)] public float UrgentBatteryCount { get; set; }
    [LoadColumn(20)] public float TotalMaxChargeRateW { get; set; }

    // ── ML-4: battery dispersion ─────────────────────────────────────────────
    [LoadColumn(21)] public float SocStdDev { get; set; }
    [LoadColumn(22)] public float CapacityRatio { get; set; }
    [LoadColumn(23)] public float NonUrgentBatteryCount { get; set; }

    // ── Solar surplus ────────────────────────────────────────────────────────
    [LoadColumn(24)] public float SurplusW { get; set; }

    // ── Tariff context ───────────────────────────────────────────────────────
    [LoadColumn(25)] public float NormalizedTariff { get; set; }
    [LoadColumn(26)] public float IsOffPeakHour { get; set; }
    [LoadColumn(27)] public float HoursToNextFavorable { get; set; }
    [LoadColumn(28)] public float AvgSolarForecastGrid { get; set; }
    [LoadColumn(29)] public float SolarExpectedSoon { get; set; }
    [LoadColumn(30)] public float MaxSavingsPerKwh { get; set; }

    // ── ML-7: extended adaptive context ─────────────────────────────────────
    /// <summary>Hours remaining in the current favorable tariff slot (0 if not favorable).</summary>
    [LoadColumn(31)] public float HoursRemainingInSlot { get; set; }

    /// <summary>Hours until solar is sufficient, clamped to 24h (0 = already available).</summary>
    [LoadColumn(32)] public float HoursUntilSolarCapped { get; set; }

    /// <summary>1.0 if any battery was in emergency grid charge during this session.</summary>
    [LoadColumn(33)] public float WasEmergencySession { get; set; }

    /// <summary>Adaptive grid charge power normalized by total MaxChargeRateW (0–1).</summary>
    [LoadColumn(34)] public float NormalizedGridChargeW { get; set; }

    // ── ML-8: HA solar forecasts (installation-specific) ─────────────────────
    /// <summary>
    /// Production solaire estimée AUJOURD'HUI depuis HA (Wh), normalisée par capacité totale.
    /// Valeur source : ForecastTodayEntity (ex: Solcast). 0 si non configuré.
    /// Normalisée : ForecastTodayWh / TotalCapacityWh → sans unité, comparable entre installations.
    /// SIGNAL DIRECT pour le ML : "combien d'énergie vient du solaire aujourd'hui par rapport
    /// à ce que mes batteries peuvent absorber" → calibre SoftMax et seuil préventif.
    /// </summary>
    [LoadColumn(35)] public float ForecastTodayNormalized { get; set; }

    /// <summary>
    /// Production solaire estimée DEMAIN depuis HA (Wh), normalisée par capacité totale.
    /// Valeur source : ForecastTomorrowEntity (ex: Solcast). 0 si non configuré.
    /// SIGNAL PROSPECTIF pour le ML : permet d'apprendre "si demain est bien ensoleillé,
    /// ne pas trop charger la nuit → garder de la place pour l'autoconsommation du lendemain".
    /// </summary>
    [LoadColumn(36)] public float ForecastTomorrowNormalized { get; set; }

    /// <summary>
    /// 1.0 si les prévisions HA sont disponibles (ForecastToday ou Tomorrow non nulles).
    /// Permet au ML de pondérer différemment les sessions avec données précises
    /// vs sessions basées uniquement sur Open-Meteo.
    /// </summary>
    [LoadColumn(37)] public float HasHaForecast { get; set; }

    // ── ML-9: tendance solaire J vs J+1 ──────────────────────────────────────
    /// <summary>
    /// Ratio ForecastTomorrow / ForecastToday.
    /// &gt; 1 : demain meilleur → moins urgent de charger ce soir.
    /// &lt; 1 : demain pire    → charger plus maintenant pour compenser.
    /// = 1 : valeur neutre si données absentes (HasHaForecast = 0).
    /// Clampé à [0, 3] pour éviter les valeurs extrêmes (ex: today ≈ 0Wh).
    /// </summary>
    [LoadColumn(38)] public float ForecastRatioTomorrowVsToday { get; set; }

    /// <summary>
    /// 1.0 si la charge réseau a été bloquée spécifiquement par le forecast HA (Solcast)
    /// plutôt que par Open-Meteo générique. Signal de qualité de donnée pour le ML.
    /// </summary>
    [LoadColumn(39)] public float SolarBlockedByHaForecast { get; set; }

    // ── Feature 6 — Bilan J-1 ────────────────────────────────────────────────
    /// <summary>
    /// Taux d'autosuffisance de la journée précédente (0–100), normalisé /100.
    /// 0 si aucune donnée disponible (Solcast non configuré ou première journée).
    ///
    /// SIGNAL RÉTROSPECTIF pour le ML :
    ///   - J-1 à 90% → batteries bien gérées hier → contexte favorable pour aujourd'hui
    ///   - J-1 à 30% → algo trop conservateur → pousser SoftMax à la hausse
    /// Complète ForecastRatioTomorrowVsToday (prospectif) avec la réalité observée.
    /// </summary>
    [LoadColumn(42)] public float YesterdaySelfSufficiencyPct { get; set; }

    // ── ML-7 : labels enrichis de feedback réel ──────────────────────────────

    /// <summary>
    /// Taux d'autosuffisance réel mesuré N heures après la session (0–1), normalisé.
    /// Calculé depuis GridImportEntity et ConsumptionEntity dans HA.
    /// 0 si données indisponibles.
    /// SIGNAL DIRECT pour la régression : mesure l'efficacité réelle de la décision.
    /// </summary>
    [LoadColumn(43)] public float ActualSelfSufficiencyNormalized { get; set; }

    /// <summary>
    /// 1.0 si du courant a été importé depuis le réseau dans les N heures suivant la session.
    /// Signal binaire pour le modèle de classification ShouldChargeFromGrid.
    /// 0 si pas d'import ou entité non configurée.
    /// </summary>
    [LoadColumn(44)] public float DidImportFromGrid { get; set; }

    /// <summary>
    /// Poids d'entraînement ML-7d. Utilisé par FastTree comme colonne de poids d'exemple.
    /// Valeur > 1 pour les sessions avec surplus gaspillé ou import non voulu.
    /// </summary>
    [LoadColumn(45)] public float SampleWeight { get; set; }

    // ── Labels ───────────────────────────────────────────────────────────────
    [LoadColumn(40)] public float OptimalSoftMaxPercent { get; set; }
    [LoadColumn(41)] public float OptimalPreventiveThreshold { get; set; }

    /// <summary>
    /// ML-7c : label de classification binaire.
    /// True si la session aurait dû déclencher une charge réseau.
    /// </summary>
    [LoadColumn(46)] public bool ShouldChargeFromGrid { get; set; }
}

public class SoftMaxPrediction
{
    [ColumnName("Score")] public float PredictedSoftMaxPercent { get; set; }
}

public class PreventivePrediction
{
    [ColumnName("Score")] public float PredictedPreventiveThreshold { get; set; }
}

/// <summary>
/// ML-7c : sortie du modèle de classification binaire ShouldChargeFromGrid.
/// Probabilité que la session aurait dû déclencher une charge réseau.
/// </summary>
public class GridChargePrediction
{
    [ColumnName("PredictedLabel")] public bool PredictedShouldCharge { get; set; }
    [ColumnName("Probability")] public float Probability { get; set; }
    [ColumnName("Score")] public float Score { get; set; }
}

public record MLRecommendation(
    double RecommendedSoftMaxPercent,
    double RecommendedPreventiveThreshold,
    double ConfidenceScore,
    string ModelVersion,
    string Rationale,
    /// <summary>ML-7c : true si le modèle de classification prédit qu'une charge réseau est nécessaire.</summary>
    bool? ShouldChargeFromGridPrediction = null,
    /// <summary>ML-7c : probabilité associée à la prédiction de classification (0–1).</summary>
    double? GridChargeClassificationConfidence = null
);

public interface IDistributionMLService
{
    Task<MLRecommendation?> PredictAsync(DistributionFeatures features, CancellationToken ct = default);
    Task<MLTrainingResult> RetrainAsync(CancellationToken ct = default);
    Task<MLModelStatus> GetStatusAsync(CancellationToken ct = default);
    Task<bool> CheckForDriftAsync(int windowSize, double threshold, CancellationToken ct = default);
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