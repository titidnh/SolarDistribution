using System;

namespace SolarDistribution.Core.Data.Entities;

public class SessionFeedback
{
    public long Id { get; set; }
    public long SessionId { get; set; }
    public DateTime CollectedAt { get; set; } = DateTime.UtcNow;
    public double FeedbackDelayHours { get; set; }
    public string ObservedSocJson { get; set; } = "{}";
    public double AvgSocAtFeedback { get; set; }
    public double MinSocAtFeedback { get; set; }
    public double EnergyEfficiencyScore { get; set; }
    public double AvailabilityScore { get; set; }
    public double ObservedOptimalSoftMax { get; set; }
    public double ObservedOptimalPreventive { get; set; }
    public double CompositeScore { get; set; }
    public FeedbackStatus Status { get; set; } = FeedbackStatus.Pending;
    public string? InvalidReason { get; set; }

    // ── ML-7 : labels réels mesurés N heures après la session ────────────────

    /// <summary>
    /// Taux d'autosuffisance réel mesuré N heures après la session (0–1).
    /// solar_consumed / (solar_consumed + grid_consumed).
    /// Null si les entités HA de consommation/import ne sont pas configurées.
    /// </summary>
    public double? ActualSelfSufficiencyPct { get; set; }

    /// <summary>
    /// True si du courant a été importé depuis le réseau dans les N heures
    /// suivant la session (lu depuis l'entité grid_import_entity dans HA).
    /// Null si l'entité n'est pas configurée.
    /// </summary>
    public bool? DidImportFromGrid { get; set; }

    /// <summary>
    /// Label de classification : aurait-il fallu charger depuis le réseau
    /// pendant cette session ? Dérivé de DidImportFromGrid et de l'autosuffisance.
    /// Null avant calcul ou si données insuffisantes.
    /// </summary>
    public bool? ShouldChargeFromGrid { get; set; }

    /// <summary>
    /// True si du surplus solaire a été gaspillé (batteries pleines, surplus non absorbé).
    /// Utilisé comme facteur de pondération dans l'entraînement ML :
    /// ces sessions doivent peser plus lourd pour apprendre à ne pas laisser passer le surplus.
    /// </summary>
    public bool SurplusWasted { get; set; } = false;

    /// <summary>
    /// Poids d'entraînement ML calculé pour cette session (1.0 = poids normal).
    /// Augmenté pour les sessions avec surplus gaspillé ou import réseau non voulu.
    /// </summary>
    public double TrainingWeight { get; set; } = 1.0;

    public DistributionSession Session { get; set; } = null!;
}
