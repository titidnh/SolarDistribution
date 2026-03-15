using System;

namespace SolarDistribution.Core.Data.Entities;

/// <summary>
/// Bilan énergétique journalier agrégé — une ligne par date calendaire (UTC).
///
/// Calculé en fin de journée solaire (coucher du soleil ou minuit) par
/// DailySummaryService, déclenché depuis MlRetrainScheduler.
///
/// Permet de répondre à :
///   "Combien d'énergie solaire ai-je autoconsommée ce mois-ci ?"
///   "Quel était mon taux d'autosuffisance hier ?"
///   "Le ML doit-il être plus ou moins agressif vu le ratio J-1 ?"
/// </summary>
public class DailySummary
{
    public long Id { get; set; }

    /// <summary>Date calendaire UTC (sans heure). Clé métier unique.</summary>
    public DateTime Date { get; set; }

    /// <summary>
    /// Énergie solaire effectivement autoconsommée (Wh).
    /// Calculée : ForecastTodayWh(début_journée) − ForecastRemainingTodayWh(fin_journée).
    /// Null si les entités Solcast ne sont pas configurées.
    /// </summary>
    public double? SolarConsumedWh { get; set; }

    /// <summary>
    /// Énergie soutirée depuis le réseau sur toute la journée (Wh).
    /// Somme de GridChargedW × durée_cycle sur toutes les sessions du jour.
    /// </summary>
    public double GridConsumedWh { get; set; }

    /// <summary>
    /// Énergie chargée dans les batteries depuis le réseau (Wh).
    /// Somme de GridChargedW × durée_cycle — sous-ensemble de GridConsumedWh.
    /// </summary>
    public double GridChargedWh { get; set; }

    /// <summary>
    /// Énergie totale distribuée aux batteries depuis le surplus solaire (Wh).
    /// Somme de TotalAllocatedW × durée_cycle sur toutes les sessions du jour.
    /// </summary>
    public double SolarAllocatedWh { get; set; }

    /// <summary>
    /// Surplus solaire non utilisé (batteries pleines ou aucune batterie éligible) (Wh).
    /// Somme de UnusedSurplusW × durée_cycle.
    /// </summary>
    public double UnusedSurplusWh { get; set; }

    /// <summary>
    /// Économies estimées en € = GridChargedWh × (tarif_HP − tarif_HC).
    /// Calcul simplifié : GridChargedWh / 1000 × MaxSavingsPerKwh moyen du jour.
    /// Null si aucun contexte tarifaire n'est disponible pour la journée.
    /// </summary>
    public double? EstimatedSavingsEur { get; set; }

    /// <summary>
    /// Taux d'autosuffisance (%) = SolarConsumedWh / (SolarConsumedWh + GridConsumedWh) × 100.
    /// Null si SolarConsumedWh est absent (Solcast non configuré).
    /// Feature ML YesterdaySelfSufficiencyPct : permet au modèle d'apprendre depuis
    /// la performance réelle de la journée précédente.
    /// </summary>
    public double? SelfSufficiencyPct { get; set; }

    /// <summary>Nombre de sessions de distribution sur cette journée.</summary>
    public int SessionCount { get; set; }

    /// <summary>Timestamp UTC de la dernière mise à jour de cet enregistrement.</summary>
    public DateTime ComputedAt { get; set; } = DateTime.UtcNow;
}
