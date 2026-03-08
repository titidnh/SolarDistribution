using SolarDistribution.Core.Models;

namespace SolarDistribution.Core.Services;

/// <summary>
/// Distribue la puissance disponible (surplus solaire + éventuelle charge réseau)
/// entre les batteries selon un algorithme 3 passes + groupes de priorité.
///
/// ┌───────────────────────────────────────────────────────────────────────────┐
/// │  ALGORITHME                                                               │
/// │                                                                           │
/// │  Groupes : batteries triées par EffectivePriority ASC                    │
/// │    · SOC < MinPercent → EffectivePriority = 0 (URGENT, toujours premier) │
/// │                                                                           │
/// │  PASS 1 — Surplus solaire → SoftMaxPercent                               │
/// │    Distribution PROPORTIONNELLE par espace disponible dans chaque groupe  │
/// │    Batteries cappées par MaxChargeRateW → surplus redirigé aux autres     │
/// │                                                                           │
/// │  PASS 2 — Surplus restant → HardMaxPercent (100%)                        │
/// │    Même logique, même ordre, cible = HardMax                             │
/// │                                                                           │
/// │  PASS 3 — Charge réseau → SoftMaxPercent (heures creuses uniquement)     │
/// │    Uniquement si GridChargeAllowedW > 0 (décidé par SmartDistribution)   │
/// │    Limité à SoftMax — on garde de la place pour le prochain surplus       │
/// │    Limité par GridChargeAllowedW par batterie                             │
/// └───────────────────────────────────────────────────────────────────────────┘
/// </summary>
public class BatteryDistributionService : IBatteryDistributionService
{
    /// <inheritdoc/>
    public DistributionResult Distribute(double surplusW, IEnumerable<Battery> batteries)
    {
        var batteryList = batteries.ToList();

        // Accumulateurs — indexés par BatteryId
        var allocated  = batteryList.ToDictionary(b => b.Id, _ => 0.0);
        var gridAlloc  = batteryList.ToDictionary(b => b.Id, _ => 0.0);
        var currentPct = batteryList.ToDictionary(b => b.Id, b => b.CurrentPercent);

        double remaining = surplusW;

        var groups = batteryList
            .GroupBy(b => b.EffectivePriority)
            .OrderBy(g => g.Key)
            .ToList();

        // ── PASS 1 : surplus solaire → SoftMax ───────────────────────────────
        foreach (var group in groups)
        {
            if (remaining <= 0.01) break;
            remaining = DistributeSurplusToGroup(
                group.ToList(), remaining, allocated, gridAlloc, currentPct, useSoftMax: true);
        }

        // ── PASS 2 : surplus restant → HardMax ───────────────────────────────
        if (remaining > 0.01)
        {
            foreach (var group in groups)
            {
                if (remaining <= 0.01) break;
                remaining = DistributeSurplusToGroup(
                    group.ToList(), remaining, allocated, gridAlloc, currentPct, useSoftMax: false);
            }
        }

        // ── PASS 3 : charge réseau → SoftMax (heures creuses) ────────────────
        // Uniquement pour les batteries ayant GridChargeAllowedW > 0.
        // On ne dépasse PAS SoftMax — on préserve la capacité pour le prochain surplus solaire.
        double gridCharged = 0;

        var gridGroups = batteryList
            .Where(b => b.GridChargeAllowedW > 0)
            .GroupBy(b => b.EffectivePriority)
            .OrderBy(g => g.Key)
            .ToList();

        foreach (var group in gridGroups)
        {
            double consumed = DistributeGridToGroup(
                group.ToList(), allocated, gridAlloc, currentPct);
            gridCharged += consumed;
        }

        // ── Résultats finaux ──────────────────────────────────────────────────
        var results = batteryList.Select(b =>
        {
            double solar    = allocated[b.Id];
            double grid     = gridAlloc[b.Id];
            double total    = solar + grid;
            // Fix 1 : clamp pour éviter un léger dépassement de HardMaxPercent dû aux erreurs d'arrondi flottant
            double newPct   = Math.Clamp(
                b.CurrentPercent + (total / b.CapacityWh * 100.0),
                0.0, b.HardMaxPercent);

            return new BatteryChargeResult(
                BatteryId:      b.Id,
                AllocatedW:     Math.Round(total, 2),
                PreviousPercent: Math.Round(b.CurrentPercent, 2),
                NewPercent:     Math.Round(newPct, 2),
                WasUrgent:      b.IsUrgent,
                IsGridCharge:   grid > 0.01,
                Reason:         BuildReason(b, solar, grid, newPct)
            );
        }).ToList();

        double totalSolar = Math.Round(surplusW - Math.Max(0, remaining), 2);

        return new DistributionResult(
            SurplusInputW:   surplusW,
            TotalAllocatedW: totalSolar,
            UnusedSurplusW:  Math.Round(Math.Max(0, remaining), 2),
            GridChargedW:    Math.Round(gridCharged, 2),
            Allocations:     results
        );
    }

    // ── Helpers privés ────────────────────────────────────────────────────────

    /// <summary>
    /// Distribue le surplus solaire à un groupe de priorité.
    /// Distribution proportionnelle par espace disponible, avec redistribution
    /// itérative quand une batterie est cappée par son MaxChargeRateW.
    /// Retourne le surplus restant non absorbé.
    /// </summary>
    private static double DistributeSurplusToGroup(
        List<Battery> group,
        double surplusW,
        Dictionary<int, double> allocated,
        Dictionary<int, double> gridAlloc,
        Dictionary<int, double> currentPct,
        bool useSoftMax)
    {
        double remaining = surplusW;
        var active = group.ToList();

        while (remaining > 0.01 && active.Count > 0)
        {
            // Espace disponible vers la cible pour chaque batterie active
            var spaces = active.ToDictionary(b => b.Id, b =>
            {
                double target = useSoftMax ? b.SoftMaxPercent : b.HardMaxPercent;
                return Math.Max(0, (target - currentPct[b.Id]) / 100.0 * b.CapacityWh);
            });

            double totalSpace = spaces.Values.Sum();
            if (totalSpace <= 0.01) break;

            var capped = new List<Battery>();
            double given = 0;

            foreach (var b in active)
            {
                double weight   = spaces[b.Id] / totalSpace;
                double share    = remaining * weight;
                // Fix 2 : rateLeft inclut la charge réseau déjà allouée (gridAlloc)
                // pour garantir que solar + grid ne dépasse jamais MaxChargeRateW
                double rateLeft = b.MaxChargeRateW - allocated[b.Id] - gridAlloc[b.Id];
                double cap      = spaces[b.Id];
                double give     = Math.Min(share, Math.Max(0, rateLeft));
                give            = Math.Min(give, cap);

                allocated[b.Id]  += give;
                currentPct[b.Id] += give / b.CapacityWh * 100.0;
                given            += give;

                if (give >= cap - 0.01 || rateLeft - give <= 0.01)
                    capped.Add(b);
            }

            remaining -= given;
            foreach (var b in capped) active.Remove(b);
            if (capped.Count == 0) break;
        }

        return remaining;
    }

    /// <summary>
    /// Distribue la puissance réseau à un groupe de batteries (Pass 3).
    /// Chaque batterie est limitée par son GridChargeAllowedW ET par l'espace
    /// restant jusqu'à SoftMaxPercent (jamais HardMax depuis le réseau).
    /// Retourne l'énergie totale effectivement allouée depuis le réseau.
    /// </summary>
    private static double DistributeGridToGroup(
        List<Battery> group,
        Dictionary<int, double> solarAllocated,
        Dictionary<int, double> gridAllocated,
        Dictionary<int, double> currentPct)
    {
        double totalConsumed = 0;
        var active = group.ToList();

        while (active.Count > 0)
        {
            // Capacité réseau restante de chaque batterie = min(espace → SoftMax, GridAllowed restant)
            var budgets = active.ToDictionary(b => b.Id, b =>
            {
                double spaceToSoft = Math.Max(0,
                    (b.SoftMaxPercent - currentPct[b.Id]) / 100.0 * b.CapacityWh);
                double rateUsed    = solarAllocated[b.Id] + gridAllocated[b.Id];
                double gridLeft    = Math.Max(0, b.GridChargeAllowedW - rateUsed);
                return Math.Min(spaceToSoft, gridLeft);
            });

            double totalBudget = budgets.Values.Sum();
            if (totalBudget <= 0.01) break;

            var capped = new List<Battery>();

            foreach (var b in active)
            {
                double give = budgets[b.Id];  // chaque batterie prend tout son budget
                if (give <= 0.01) { capped.Add(b); continue; }

                gridAllocated[b.Id]  += give;
                currentPct[b.Id]     += give / b.CapacityWh * 100.0;
                totalConsumed        += give;
                capped.Add(b);  // une seule passe suffit — chaque batterie prend son max réseau
            }

            foreach (var b in capped) active.Remove(b);
            break;  // pas de surplus à redistribuer en charge réseau
        }

        return totalConsumed;
    }

    private static string BuildReason(Battery b, double solar, double grid, double newPct)
    {
        string prefix = b.IsUrgent ? $"[URGENT <{b.MinPercent}%] " : string.Empty;
        double total  = solar + grid;

        if (total <= 0)
            return "No surplus remaining or battery already full";

        if (newPct >= b.HardMaxPercent - 0.1)
            return $"{prefix}Charged to {b.HardMaxPercent:F0}%";

        if (newPct >= b.SoftMaxPercent - 0.1)
            return $"{prefix}Reached soft max {b.SoftMaxPercent:F0}%";

        if (grid > 0.01)
            return $"{prefix}Grid charge off-peak: {grid:F0}W ({b.GridChargeAllowedW:F0}W allowed)";

        if (total >= b.MaxChargeRateW - 0.1)
            return $"{prefix}Capped by MaxChargeRate ({b.MaxChargeRateW:F0}W)";

        return $"{prefix}Proportional share — surplus exhausted";
    }
}
