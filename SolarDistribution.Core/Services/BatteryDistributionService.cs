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
/// │                                                                           │
/// │  POST-DISTRIBUTION — IdleChargeW                                          │
/// │    Toute batterie allouée à 0 W reçoit IdleChargeW (défaut 100 W)        │
/// │    à condition d'être encore sous HardMaxPercent                          │
/// │    ET qu'il y ait un surplus solaire réel (surplusW > 0).                │
/// │    FIX Bug #4 : IdleChargeW est désactivé quand surplusW = 0 et que la   │
/// │    batterie a IdleChargeW = 0 (forcé par SmartDistributionService en HP). │
/// │    Cela évite d'envoyer 100 W depuis le réseau en tarif HP               │
/// │    uniquement pour "maintenir" une batterie déjà à sa cible.             │
/// └───────────────────────────────────────────────────────────────────────────┘
/// </summary>
public class BatteryDistributionService : IBatteryDistributionService
{
    /// <inheritdoc/>
    public DistributionResult Distribute(double surplusW, IEnumerable<Battery> batteries)
    {
        var batteryList = batteries.ToList();

        var allocated = batteryList.ToDictionary(b => b.Id, _ => 0.0);
        var gridAlloc = batteryList.ToDictionary(b => b.Id, _ => 0.0);
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

        // ── POST-DISTRIBUTION : IdleChargeW ──────────────────────────────────
        // Toute batterie à 0 W (cible atteinte ou pas de surplus) mais encore sous
        // HardMaxPercent reçoit IdleChargeW pour maintenir le BMS actif.
        //
        // FIX Bug #4 : IdleChargeW est mis à 0 par SmartDistributionService en tarif HP
        // (via Apply()), ce qui empêche d'envoyer 100 W depuis le réseau quand
        // surplusW = 0 et que la batterie est déjà à sa cible.
        // Ici on vérifie en plus surplusW > 0 comme garde supplémentaire : sans solaire,
        // IdleChargeW n'a aucun sens (rien à "absorber") et deviendrait de la charge réseau.
        foreach (var b in batteryList)
        {
            double total = allocated[b.Id] + gridAlloc[b.Id];
            if (total <= 0.01
                && currentPct[b.Id] < b.HardMaxPercent - 0.1
                && b.IdleChargeW > 0
                && surplusW > 0)   // FIX Bug #4 : pas d'IdleCharge sans surplus solaire
            {
                // On ajoute IdleChargeW dans allocated (surplus) pour que le bilan soit cohérent
                // On NE modifie PAS currentPct car c'est une consigne symbolique, pas de l'énergie
                allocated[b.Id] = b.IdleChargeW;
            }
        }

        // ── Résultats finaux ──────────────────────────────────────────────────
        var results = batteryList.Select(b =>
        {
            double solar = allocated[b.Id];
            double grid = gridAlloc[b.Id];
            double total = solar + grid;

            // idle = la batterie est à sa cible et reçoit juste la consigne de maintien
            bool isIdle = grid <= 0.01
                       && solar > 0.01 && solar <= b.IdleChargeW + 0.01
                       && currentPct[b.Id] >= b.SoftMaxPercent - 0.5;

            double newPct = isIdle
                ? b.CurrentPercent   // idle : SOC projeté inchangé
                : Math.Clamp(
                    b.CurrentPercent + ((solar - (isIdle ? solar : 0) + grid) / b.CapacityWh * 100.0),
                    0.0, b.HardMaxPercent);

            // Recalcul propre sans ambiguïté
            double energyForSoc = isIdle ? 0 : (solar + grid);
            double projectedPct = Math.Clamp(
                b.CurrentPercent + (energyForSoc / b.CapacityWh * 100.0),
                0.0, b.HardMaxPercent);

            return new BatteryChargeResult(
                BatteryId: b.Id,
                AllocatedW: Math.Round(total, 2),
                PreviousPercent: Math.Round(b.CurrentPercent, 2),
                NewPercent: Math.Round(projectedPct, 2),
                WasUrgent: b.IsUrgent,
                IsGridCharge: grid > 0.01,
                IsEmergencyGridCharge: b.IsEmergencyGridCharge && grid > 0.01,
                Reason: BuildReason(b, solar, grid, projectedPct, isIdle)
            );
        }).ToList();

        double totalSolar = Math.Round(surplusW - Math.Max(0, remaining), 2);

        return new DistributionResult(
            SurplusInputW: surplusW,
            TotalAllocatedW: totalSolar,
            UnusedSurplusW: Math.Round(Math.Max(0, remaining), 2),
            GridChargedW: Math.Round(gridCharged, 2),
            Allocations: results
        );
    }

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
                double weight = spaces[b.Id] / totalSpace;
                double share = remaining * weight;
                double rateLeft = b.MaxChargeRateW - allocated[b.Id] - gridAlloc[b.Id];
                double cap = spaces[b.Id];
                double give = Math.Min(share, Math.Max(0, rateLeft));
                give = Math.Min(give, cap);

                allocated[b.Id] += give;
                currentPct[b.Id] += give / b.CapacityWh * 100.0;
                given += give;

                if (give >= cap - 0.01 || rateLeft - give <= 0.01)
                    capped.Add(b);
            }

            remaining -= given;
            foreach (var b in capped) active.Remove(b);
            if (capped.Count == 0) break;
        }

        return remaining;
    }

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
            var budgets = active.ToDictionary(b => b.Id, b =>
            {
                double gridTarget = b.IsEmergencyGridCharge && b.EmergencyGridChargeTargetPercent.HasValue
                    ? b.EmergencyGridChargeTargetPercent.Value
                    : b.SoftMaxPercent;
                double spaceToTarget = Math.Max(0,
                    (gridTarget - currentPct[b.Id]) / 100.0 * b.CapacityWh);
                double rateUsed = solarAllocated[b.Id] + gridAllocated[b.Id];
                double gridLeft = Math.Max(0, b.GridChargeAllowedW - rateUsed);
                return Math.Min(spaceToTarget, gridLeft);
            });

            double totalBudget = budgets.Values.Sum();
            if (totalBudget <= 0.01) break;

            var capped = new List<Battery>();

            foreach (var b in active)
            {
                double give = budgets[b.Id];
                if (give <= 0.01) { capped.Add(b); continue; }

                gridAllocated[b.Id] += give;
                currentPct[b.Id] += give / b.CapacityWh * 100.0;
                totalConsumed += give;
                capped.Add(b);
            }

            foreach (var b in capped) active.Remove(b);
            break;
        }

        return totalConsumed;
    }

    private static string BuildReason(Battery b, double solar, double grid, double newPct, bool isIdle)
    {
        string prefix = b.IsUrgent ? $"[URGENT <{b.MinPercent}%] " : string.Empty;
        double total = solar + grid;

        if (isIdle)
            return $"{prefix}Idle hold {b.IdleChargeW:F0}W (target reached)";

        if (total <= 0)
            return "No surplus remaining or battery already full";

        if (newPct >= b.HardMaxPercent - 0.1)
            return $"{prefix}Charged to {b.HardMaxPercent:F0}%";

        if (newPct >= b.SoftMaxPercent - 0.1)
            return $"{prefix}Reached soft max {b.SoftMaxPercent:F0}%";

        if (grid > 0.01)
        {
            if (b.IsEmergencyGridCharge)
            {
                double target = b.EmergencyGridChargeTargetPercent ?? b.SoftMaxPercent;
                return $"{prefix}[EMERGENCY] Grid charge: SOC < {b.EmergencyGridChargeBelowPercent:F0}% — charging to {target:F0}% ({grid:F0}W)";
            }
            return $"{prefix}Grid charge off-peak: {grid:F0}W ({b.GridChargeAllowedW:F0}W allowed)";
        }

        if (total >= b.MaxChargeRateW - 0.1)
            return $"{prefix}Capped by MaxChargeRate ({b.MaxChargeRateW:F0}W)";

        return $"{prefix}Proportional share — surplus exhausted";
    }
}