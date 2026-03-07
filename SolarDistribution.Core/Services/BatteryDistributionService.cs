using SolarDistribution.Core.Models;

namespace SolarDistribution.Core.Services;

/// <summary>
/// Distributes solar surplus power across batteries using priority groups + proportional fill.
///
/// ┌─────────────────────────────────────────────────────────────────────────────┐
/// │  ALGORITHM                                                                  │
/// │                                                                             │
/// │  Step 1 — Group batteries by EffectivePriority (ASC)                       │
/// │    · Battery below MinPercent → EffectivePriority = 0 (URGENT)             │
/// │    · Urgent batteries are always served first, regardless of user priority  │
/// │                                                                             │
/// │  Step 2 — PASS 1: fill each group toward SoftMaxPercent (default 80%)      │
/// │    · Within a group: distribute PROPORTIONALLY by SpaceToSoftMaxWh         │
/// │    · If a battery hits MaxChargeRateW before its share → redistribute       │
/// │      remaining surplus to other batteries in the same group (iterative)     │
/// │    · Move to next priority group with leftover surplus                      │
/// │                                                                             │
/// │  Step 3 — PASS 2: if surplus remains after all groups reached SoftMax      │
/// │    · Same groups, same order, same proportional logic                       │
/// │    · Now filling toward HardMaxPercent (100%)                               │
/// └─────────────────────────────────────────────────────────────────────────────┘
/// </summary>
public class BatteryDistributionService : IBatteryDistributionService
{
    /// <inheritdoc/>
    public DistributionResult Distribute(double surplusW, IEnumerable<Battery> batteries)
    {
        var batteryList = batteries.ToList();

        // Running totals — keyed by BatteryId
        var allocated   = batteryList.ToDictionary(b => b.Id, _ => 0.0);
        var currentPct  = batteryList.ToDictionary(b => b.Id, b => b.CurrentPercent);

        double remaining = surplusW;

        var groups = batteryList
            .GroupBy(b => b.EffectivePriority)
            .OrderBy(g => g.Key)
            .ToList();

        // ── PASS 1: toward SoftMax ───────────────────────────────────────────
        foreach (var group in groups)
        {
            if (remaining <= 0.01) break;
            remaining = DistributeToGroup(group.ToList(), remaining, allocated, currentPct, useSoftMax: true);
        }

        // ── PASS 2: toward HardMax (100%) if surplus remains ─────────────────
        if (remaining > 0.01)
        {
            foreach (var group in groups)
            {
                if (remaining <= 0.01) break;
                remaining = DistributeToGroup(group.ToList(), remaining, allocated, currentPct, useSoftMax: false);
            }
        }

        // ── Build final results ───────────────────────────────────────────────
        var results = batteryList
            .Select(b =>
            {
                double alloc  = allocated[b.Id];
                double newPct = b.CurrentPercent + (alloc / b.CapacityWh * 100.0);

                return new BatteryChargeResult(
                    BatteryId:       b.Id,
                    AllocatedW:      Math.Round(alloc,  2),
                    PreviousPercent: Math.Round(b.CurrentPercent, 2),
                    NewPercent:      Math.Round(newPct, 2),
                    WasUrgent:       b.IsUrgent,
                    Reason:          BuildReason(b, alloc, newPct)
                );
            })
            .ToList();

        return new DistributionResult(
            SurplusInputW:    surplusW,
            TotalAllocatedW:  Math.Round(surplusW - Math.Max(0, remaining), 2),
            UnusedSurplusW:   Math.Round(Math.Max(0, remaining), 2),
            Allocations:      results
        );
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Private
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Distributes surplus to a single priority group, proportionally by available space.
    /// Iterates until no more surplus can be absorbed (rate-capped batteries are removed
    /// each round and the remaining surplus is redistributed).
    /// </summary>
    private static double DistributeToGroup(
        List<Battery> group,
        double surplusW,
        Dictionary<int, double> allocated,
        Dictionary<int, double> currentPct,
        bool useSoftMax)
    {
        double remaining = surplusW;
        var active = group.ToList();

        while (remaining > 0.01 && active.Count > 0)
        {
            // Available capacity for each active battery this iteration (space only)
            var spaces = active.ToDictionary(
                b => b.Id,
                b =>
                {
                    double pct    = currentPct[b.Id];
                    double target = useSoftMax ? b.SoftMaxPercent : b.HardMaxPercent;
                    double space  = Math.Max(0, (target - pct) / 100.0 * b.CapacityWh);
                    return space;
                });

            double totalSpace = spaces.Values.Sum();
            if (totalSpace <= 0.01) break;
            var capped = new List<Battery>();
            double given = 0;

            foreach (var b in active)
            {
                double weight = spaces[b.Id] / totalSpace;
                double share = remaining * weight;

                double rateLeft = b.MaxChargeRateW - allocated[b.Id];
                double cap = spaces[b.Id];
                double give = Math.Min(share, Math.Max(0, rateLeft));
                give = Math.Min(give, cap);

                allocated[b.Id] += give;
                currentPct[b.Id] += give / b.CapacityWh * 100.0;
                given += give;

                // Battery hit its cap (space or rate) → will be removed so surplus flows to others
                if (give >= cap - 0.01 || rateLeft <= 0.01)
                    capped.Add(b);
            }

            remaining -= given;

            foreach (var b in capped)
                active.Remove(b);

            // No battery was capped → fully distributed or stuck
            if (capped.Count == 0) break;
        }

        return remaining;
    }

    private static string BuildReason(Battery b, double alloc, double newPct)
    {
        if (alloc <= 0)
            return "No surplus remaining or battery already full";

        string prefix = b.IsUrgent ? $"[URGENT <{b.MinPercent}%] " : string.Empty;

        if (newPct >= b.HardMaxPercent - 0.1)
            return $"{prefix}Charged to 100%";
        if (newPct >= b.SoftMaxPercent - 0.1)
            return $"{prefix}Reached soft max {b.SoftMaxPercent}%";
        if (alloc >= b.MaxChargeRateW - 0.1)
            return $"{prefix}Capped by MaxChargeRate ({b.MaxChargeRateW}W)";

        return $"{prefix}Proportional share — surplus exhausted";
    }
}
