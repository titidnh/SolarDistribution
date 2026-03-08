namespace SolarDistribution.Core.Models;

/// <summary>
/// Represents a physical battery with its configuration and current state.
/// </summary>
public class Battery
{
    public int    Id             { get; set; }
    public double CapacityWh     { get; set; }
    public double MaxChargeRateW { get; set; }
    public double MinPercent     { get; set; }
    public double SoftMaxPercent { get; set; } = 80;
    public double HardMaxPercent { get; set; } = 100;
    public double CurrentPercent { get; set; }
    public int    Priority       { get; set; }

    /// <summary>
    /// Puissance max autorisée depuis le réseau pour cette batterie (W).
    ///   0  → charge réseau interdite (mode normal, surplus solaire uniquement).
    ///   >0 → charge réseau permise (heures creuses à tarif favorable, ou urgence).
    /// Calculé par SmartDistributionService en fonction du contexte tarifaire et de l'urgence.
    /// </summary>
    public double GridChargeAllowedW { get; set; } = 0;

    /// <summary>
    /// Cible de SOC pour la recharge réseau en urgence (%).
    /// Quand GridChargeAllowedW > 0 pour raison d'urgence, la Pass 3 charge
    /// jusqu'à cette cible au lieu de SoftMaxPercent.
    /// null = utilise SoftMaxPercent.
    /// </summary>
    public double? EmergencyGridChargeTargetPercent { get; set; }

    /// <summary>
    /// Seuil d'urgence (%) provenant de la config — propagé pour BuildReason et logs.
    /// null = pas de recharge réseau d'urgence configurée.
    /// </summary>
    public double? EmergencyGridChargeBelowPercent { get; set; }

    /// <summary>True si la recharge réseau de cette batterie est déclenchée par urgence SOC.</summary>
    public bool IsEmergencyGridCharge { get; set; } = false;

    // ── Computed ──────────────────────────────────────────────────────────────

    /// <summary>Si SOC < MinPercent → URGENT → priorité 0, passe avant tout.</summary>
    public int  EffectivePriority => CurrentPercent < MinPercent ? 0 : Priority;
    public bool IsUrgent          => CurrentPercent < MinPercent;

    public double SpaceToSoftMaxWh =>
        Math.Max(0, (SoftMaxPercent - CurrentPercent) / 100.0 * CapacityWh);

    public double SpaceToHardMaxWh =>
        Math.Max(0, (HardMaxPercent - CurrentPercent) / 100.0 * CapacityWh);
}
