namespace SolarDistribution.Core.Models;

/// <summary>
/// Represents a physical battery with its configuration and current state.
/// </summary>
public class Battery
{
    public int Id { get; set; }
    public double CapacityWh { get; set; }
    public double MaxChargeRateW { get; set; }
    public double MinPercent { get; set; }
    public double SoftMaxPercent { get; set; } = 80;
    public double HardMaxPercent { get; set; } = 100;
    public double CurrentPercent { get; set; }
    public int Priority { get; set; }

    /// <summary>
    /// Puissance max autorisée depuis le réseau pour cette batterie (W).
    ///   0  → charge réseau interdite (surplus solaire uniquement).
    ///   >0 → charge réseau permise (heures creuses ou urgence SOC).
    /// Calculé par SmartDistributionService selon le contexte tarifaire.
    /// </summary>
    public double GridChargeAllowedW { get; set; } = 0;

    /// <summary>
    /// Puissance de maintien envoyée à la batterie une fois sa cible atteinte (W).
    ///
    /// Quand la batterie a absorbé son surplus (SoftMax ou HardMax atteint),
    /// au lieu d'envoyer 0 W, on envoie IdleChargeW pour :
    ///   • Éviter le cycling on/off de certains BMS
    ///   • Indiquer à l'onduleur que la charge est toujours autorisée
    ///   • Absorber les micro-surplus résiduels (bruit du compteur P1)
    ///
    /// Défaut 0 W (comportement standard : coupe à la cible).
    /// Configuré via BatteryConfig.IdleChargeW (défaut config = 100 W).
    /// </summary>
    public double IdleChargeW { get; set; } = 0;

    /// <summary>
    /// Zone morte SOC (%) autour de la cible SoftMax pour la charge réseau HC (Fix Bug #1).
    ///
    /// Évite les micro-commandes de 1-3W générées quand la batterie oscille juste
    /// sous sa cible par auto-décharge EcoFlow self-powered (~1-2%/h).
    ///
    /// Avec SocHysteresisPercent = 2 :
    ///   · Cible 90% → recharge réseau autorisée seulement si SOC &lt; 88%
    ///   · Entre 88% et 90% : pas de charge réseau (auto-décharge acceptée)
    ///   · SOC descend à 87.9% → recharge réseau normale (≥ 100W)
    ///
    /// Défaut 0 → désactivé, comportement original.
    /// Propagé depuis BatteryConfig.SocHysteresisPercent.
    /// </summary>
    public double SocHysteresisPercent { get; set; } = 0.0;

    public double? EmergencyGridChargeTargetPercent { get; set; }
    public double? EmergencyGridChargeBelowPercent { get; set; }
    public bool IsEmergencyGridCharge { get; set; } = false;

    // ── Computed ──────────────────────────────────────────────────────────────
    public int EffectivePriority => CurrentPercent < MinPercent ? 0 : Priority;
    public bool IsUrgent => CurrentPercent < MinPercent;

    public double SpaceToSoftMaxWh =>
        Math.Max(0, (SoftMaxPercent - CurrentPercent) / 100.0 * CapacityWh);

    public double SpaceToHardMaxWh =>
        Math.Max(0, (HardMaxPercent - CurrentPercent) / 100.0 * CapacityWh);
}