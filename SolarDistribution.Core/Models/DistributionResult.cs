namespace SolarDistribution.Core.Models;

/// <summary>Résultat d'allocation pour une batterie individuelle.</summary>
public record BatteryChargeResult(
    int    BatteryId,
    double AllocatedW,
    double PreviousPercent,
    double NewPercent,
    bool   WasUrgent,
    bool   IsGridCharge = false,   // true = puissance venant du réseau (Pass 3)
    string Reason = ""
);

/// <summary>Résultat complet d'une distribution pour un cycle donné.</summary>
public record DistributionResult(
    double SurplusInputW,
    double TotalAllocatedW,    // total alloué depuis surplus solaire
    double UnusedSurplusW,     // surplus non absorbé
    double GridChargedW,       // total chargé depuis le réseau (Pass 3)
    List<BatteryChargeResult> Allocations
);
