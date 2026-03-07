namespace SolarDistribution.Infrastructure.Data.Entities;

/// <summary>
/// Représente un appel complet à l'API de distribution.
/// Une session = 1 appel POST /api/distribution/calculate.
/// </summary>
public class DistributionSession
{
    public long Id { get; set; }

    /// <summary>Timestamp UTC de la demande</summary>
    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Surplus solaire fourni en W</summary>
    public double SurplusW { get; set; }

    /// <summary>Total effectivement distribué en W</summary>
    public double TotalAllocatedW { get; set; }

    /// <summary>Surplus non absorbé en W (batteries pleines)</summary>
    public double UnusedSurplusW { get; set; }

    /// <summary>
    /// Indique si c'est l'algorithme déterministe ou ML qui a pris la décision.
    /// "Deterministic" | "ML" | "ML-Fallback" (ML tenté mais confiance insuffisante)
    /// </summary>
    public string DecisionEngine { get; set; } = "Deterministic";

    /// <summary>Score de confiance du modèle ML (null si algo déterministe utilisé)</summary>
    public double? MlConfidenceScore { get; set; }

    // Navigation
    public ICollection<BatterySnapshot> BatterySnapshots { get; set; } = [];
    public WeatherSnapshot? Weather { get; set; }
    public MLPredictionLog? MlPrediction { get; set; }
}

/// <summary>
/// État d'une batterie avant et après une session de distribution.
/// Permet de calculer l'efficacité réelle de la charge.
/// </summary>
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

    /// <summary>Watts alloués à cette batterie lors de la session</summary>
    public double AllocatedW { get; set; }

    /// <summary>Raison de l'allocation (texte de l'algorithme)</summary>
    public string Reason { get; set; } = string.Empty;

    // Navigation
    public DistributionSession Session { get; set; } = null!;
}

/// <summary>
/// Données météo Open-Meteo capturées au moment de la session.
/// Features clés pour l'apprentissage ML.
/// </summary>
public class WeatherSnapshot
{
    public long Id { get; set; }
    public long SessionId { get; set; }

    public DateTime FetchedAt { get; set; } = DateTime.UtcNow;

    // Localisation
    public double Latitude { get; set; }
    public double Longitude { get; set; }

    // Conditions actuelles
    /// <summary>Température en °C</summary>
    public double TemperatureC { get; set; }

    /// <summary>Couverture nuageuse en % (0 = ciel clair, 100 = couvert)</summary>
    public double CloudCoverPercent { get; set; }

    /// <summary>Précipitations en mm/h</summary>
    public double PrecipitationMmH { get; set; }

    /// <summary>Rayonnement solaire direct en W/m² (clé pour prédire la production)</summary>
    public double DirectRadiationWm2 { get; set; }

    /// <summary>Rayonnement diffus en W/m²</summary>
    public double DiffuseRadiationWm2 { get; set; }

    /// <summary>Durée du jour en heures (calculée)</summary>
    public double DaylightHours { get; set; }

    /// <summary>Heures restantes avant coucher du soleil au moment de la session</summary>
    public double HoursUntilSunset { get; set; }

    // Prévisions sur les prochaines heures (JSON sérialisé)
    /// <summary>Prévision de rayonnement sur les 12 prochaines heures [W/m²]</summary>
    public string RadiationForecast12hJson { get; set; } = "[]";

    /// <summary>Prévision de couverture nuageuse sur les 12 prochaines heures [%]</summary>
    public string CloudForecast12hJson { get; set; } = "[]";

    // Navigation
    public DistributionSession Session { get; set; } = null!;
}

/// <summary>
/// Log d'une prédiction ML : ce que le modèle a suggéré vs ce qui a été appliqué.
/// Utilisé pour calculer la précision du modèle au fil du temps.
/// </summary>
public class MLPredictionLog
{
    public long Id { get; set; }
    public long SessionId { get; set; }

    /// <summary>Version du modèle ML utilisé</summary>
    public string ModelVersion { get; set; } = string.Empty;

    /// <summary>Score de confiance global de la prédiction (0.0 à 1.0)</summary>
    public double ConfidenceScore { get; set; }

    /// <summary>SoftMaxPercent suggéré par ML pour chaque batterie (JSON)</summary>
    public string PredictedSoftMaxJson { get; set; } = "{}";

    /// <summary>Seuil de recharge préventive suggéré par ML (%)</summary>
    public double PredictedPreventiveThreshold { get; set; }

    /// <summary>La prédiction ML a-t-elle été appliquée (true) ou fallback algo (false) ?</summary>
    public bool WasApplied { get; set; }

    /// <summary>
    /// Score d'efficacité calculé a posteriori (après feedback).
    /// Null tant que le feedback n'a pas été reçu.
    /// </summary>
    public double? EfficiencyScore { get; set; }

    public DateTime PredictedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public DistributionSession Session { get; set; } = null!;
}
