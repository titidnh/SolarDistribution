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
    /// Puissance minimale en dessous de laquelle la batterie n'accepte pas la charge (W).
    ///
    /// Contrainte hardware : certaines batteries (ex: EcoFlow Delta) refusent ou ignorent
    /// toute consigne inférieure à ce seuil. Envoyer 50W à une batterie dont le minimum
    /// est 100W ne produit aucune charge réelle — la commande est silencieusement ignorée.
    ///
    /// Impact sur la distribution :
    ///   · PASS 1/2 (surplus solaire) : si surplusW &lt; HardwareMinChargeW, la batterie
    ///     est skippée — le surplus ne suffit pas à franchir le seuil hardware.
    ///   · IdleCharge (POST-DISTRIBUTION) : même garde — remplace l'ancienne condition
    ///     surplusW >= IdleChargeW (Bug #5) qui était un proxy imparfait.
    ///   · Emergency grid charge : ignore HardwareMinChargeW — la batterie doit
    ///     toujours charger quelle que soit la puissance disponible.
    ///   · Grid charge HC (PASS 3) : GridChargeAllowedW est déjà calculé ≥ MinChargeRateW
    ///     par ComputeAdaptiveGridChargeW — pas de garde supplémentaire nécessaire.
    ///
    /// Défaut 0 → désactivé (comportement original, aucun seuil minimum).
    /// Configuré via BatteryConfig.HardwareMinChargeW.
    /// </summary>
    public double HardwareMinChargeW { get; set; } = 0;
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

    // ── ML-8 : cycle de vie ───────────────────────────────────────────────────

    /// <summary>
    /// Nombre de cycles de charge complets lus depuis HA via CycleCountEntity.
    /// 0 si l'entité n'est pas configurée ou si la lecture a échoué.
    /// </summary>
    public int CycleCount { get; set; } = 0;

    /// <summary>
    /// Facteur de réduction de priorité par cycle (depuis BatteryConfig.CycleAgingFactor).
    /// 0 = désactivé (pas de pondération par cycle).
    /// </summary>
    public double CycleAgingFactor { get; set; } = 0.0001;

    // ── Computed ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Priorité effective tenant compte de l'urgence SOC ET du vieillissement par cycles.
    ///
    /// Règle d'urgence (inchangée) :
    ///   SOC &lt; MinPercent → EffectivePriority = 0 (URGENT, toujours premier)
    ///
    /// Pondération par cycles (ML-8) :
    ///   effectivePriority = basePriority × (1 − CycleAgingFactor × CycleCount)
    ///   clampé à [basePriority × 0.5, basePriority] pour rester dans des bornes raisonnables.
    ///
    /// Impact sur la distribution :
    ///   Les batteries moins cyclées (plus neuves) ont une priorité numérique plus basse
    ///   (rappel : tri ASC → priorité 0 = premier). Les batteries âgées, dont le
    ///   EffectivePriority se rapproche de Priority (sans réduction), passent après.
    ///   Effet : le surplus solaire va d'abord aux batteries les plus fraîches.
    /// </summary>
    public double EffectivePriority
    {
        get
        {
            if (CurrentPercent < MinPercent) return 0; // urgence toujours premier

            if (CycleAgingFactor <= 0 || CycleCount <= 0)
                return Priority;

            // Réduction de priorité proportionnelle aux cycles
            double reduction = CycleAgingFactor * CycleCount;
            double aged = Priority * (1.0 - reduction);
            // Clamp : max 50% de réduction pour éviter une inversion trop brutale
            return Math.Clamp(aged, Priority * 0.5, Priority);
        }
    }

    public bool IsUrgent => CurrentPercent < MinPercent;

    public double SpaceToSoftMaxWh =>
        Math.Max(0, (SoftMaxPercent - CurrentPercent) / 100.0 * CapacityWh);

    public double SpaceToHardMaxWh =>
        Math.Max(0, (HardMaxPercent - CurrentPercent) / 100.0 * CapacityWh);
}