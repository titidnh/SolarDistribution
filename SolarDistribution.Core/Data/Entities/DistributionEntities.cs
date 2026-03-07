namespace SolarDistribution.Core.Data.Entities;

public class DistributionSession
{
    public long Id { get; set; }
    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;
    public double SurplusW { get; set; }
    public double TotalAllocatedW { get; set; }
    public double UnusedSurplusW { get; set; }
    public string DecisionEngine { get; set; } = "Deterministic";
    public double? MlConfidenceScore { get; set; }
    public ICollection<BatterySnapshot> BatterySnapshots { get; set; } = new List<BatterySnapshot>();
    public WeatherSnapshot? Weather { get; set; }
    public MLPredictionLog? MlPrediction { get; set; }

    /// <summary>
    /// Feedback collecté N heures après la session — contient les labels réels pour l'entraînement ML.
    /// Null tant que le feedback n'a pas encore été collecté.
    /// </summary>
    public SessionFeedback? Feedback { get; set; }
}

/// <summary>
/// Feedback observé réellement après une session de distribution.
///
/// Collecté automatiquement N heures après la session (configurable).
/// Contient les labels RÉELS utilisés pour entraîner le ML —
/// remplace les anciennes heuristiques codées en dur.
///
/// Principe de calcul :
///   - On relit le SOC réel de chaque batterie dans HA N heures plus tard
///   - On compare ce qui s'est passé avec ce qui aurait été optimal
///   - Ces observations deviennent les labels pour le prochain entraînement
/// </summary>
public class SessionFeedback
{
    public long Id { get; set; }
    public long SessionId { get; set; }

    /// <summary>Quand le feedback a été collecté (UTC)</summary>
    public DateTime CollectedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Nombre d'heures après la session où le feedback a été pris</summary>
    public double FeedbackDelayHours { get; set; }

    // ── SOC observés N heures après la session ────────────────────────────────

    /// <summary>
    /// SOC moyen réel de toutes les batteries N heures après la session.
    /// JSON: { "batteryId": socPercent, ... }
    /// ex: {"1": 72.5, "2": 68.0}
    /// </summary>
    public string ObservedSocJson { get; set; } = "{}";

    /// <summary>SOC moyen de toutes les batteries au moment du feedback</summary>
    public double AvgSocAtFeedback { get; set; }

    /// <summary>SOC minimum observé parmi toutes les batteries au moment du feedback</summary>
    public double MinSocAtFeedback { get; set; }

    // ── Labels réels calculés depuis les observations ─────────────────────────

    /// <summary>
    /// Score d'efficacité énergétique réel : énergie effectivement stockée / énergie théoriquement disponible.
    /// Plage : 0.0 (rien stocké) → 1.0 (tout le surplus absorbé).
    /// </summary>
    public double EnergyEfficiencyScore { get; set; }

    /// <summary>
    /// Score de disponibilité : les batteries avaient-elles suffisamment de charge ?
    /// Pénalise les cas où les batteries sont tombées trop bas (< MinPercent).
    /// Plage : 0.0 (batterie urgente) → 1.0 (toujours au-dessus du seuil).
    /// </summary>
    public double AvailabilityScore { get; set; }

    /// <summary>
    /// SoftMax optimal déduit de l'observation réelle.
    /// = SoftMax qui aurait donné le meilleur score global compte tenu de ce qui s'est passé.
    /// C'est le VRAI LABEL pour entraîner le modèle SoftMax.
    /// </summary>
    public double ObservedOptimalSoftMax { get; set; }

    /// <summary>
    /// Seuil préventif optimal déduit de l'observation.
    /// = MinPercent qui aurait évité les situations de batterie vide ou de gaspillage.
    /// C'est le VRAI LABEL pour entraîner le modèle préventif.
    /// </summary>
    public double ObservedOptimalPreventive { get; set; }

    /// <summary>
    /// Score composite final (0.0 → 1.0) : combinaison efficacité + disponibilité.
    /// Utilisé pour filtrer les sessions de mauvaise qualité avant l'entraînement.
    /// </summary>
    public double CompositeScore { get; set; }

    /// <summary>Statut du feedback — permet de filtrer les feedbacks valides</summary>
    public FeedbackStatus Status { get; set; } = FeedbackStatus.Pending;

    /// <summary>Raison si le feedback est invalide (batterie déconnectée, HA injoignable...)</summary>
    public string? InvalidReason { get; set; }

    // Navigation
    public DistributionSession Session { get; set; } = null!;
}

public enum FeedbackStatus
{
    /// <summary>Pas encore collecté (trop tôt)</summary>
    Pending = 0,

    /// <summary>Collecté et valide — utilisable pour l'entraînement</summary>
    Valid = 1,

    /// <summary>Collecté mais invalide (lecture HA échouée, batterie déconnectée...)</summary>
    Invalid = 2,

    /// <summary>Ignoré volontairement (ex: session de test en DryRun)</summary>
    Skipped = 3
}

public class BatterySnapshot
{
    public long Id { get; set; }
    public long SessionId { get; set; }
    public int BatteryId { get; set; }
    public double CapacityWh { get; set; }
    public double MaxChargeRateW { get; set; }
    public double MinPercent { get; set; }
    public double SoftMaxPercent { get; set; }
    public double CurrentPercentBefore { get; set; }
    public double CurrentPercentAfter { get; set; }
    public int Priority { get; set; }
    public bool WasUrgent { get; set; }
    public double AllocatedW { get; set; }
    public string Reason { get; set; } = string.Empty;
    public DistributionSession Session { get; set; } = null!;
}

public class WeatherSnapshot
{
    public long Id { get; set; }
    public long SessionId { get; set; }
    public DateTime FetchedAt { get; set; } = DateTime.UtcNow;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double TemperatureC { get; set; }
    public double CloudCoverPercent { get; set; }
    public double PrecipitationMmH { get; set; }
    public double DirectRadiationWm2 { get; set; }
    public double DiffuseRadiationWm2 { get; set; }
    public double DaylightHours { get; set; }
    public double HoursUntilSunset { get; set; }
    public string RadiationForecast12hJson { get; set; } = "[]";
    public string CloudForecast12hJson { get; set; } = "[]";
    public DistributionSession Session { get; set; } = null!;
}

public class MLPredictionLog
{
    public long Id { get; set; }
    public long SessionId { get; set; }
    public string ModelVersion { get; set; } = string.Empty;
    public double ConfidenceScore { get; set; }
    public string PredictedSoftMaxJson { get; set; } = "{}";
    public double PredictedPreventiveThreshold { get; set; }
    public bool WasApplied { get; set; }
    public double? EfficiencyScore { get; set; }
    public DateTime PredictedAt { get; set; } = DateTime.UtcNow;
    public DistributionSession Session { get; set; } = null!;
}
