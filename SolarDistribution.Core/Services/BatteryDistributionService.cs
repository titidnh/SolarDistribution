using SolarDistribution.Core.Models;

namespace SolarDistribution.Core.Services;

/// <summary>
/// Distributes the available power (solar surplus + optional grid charge)
/// across batteries using a 3-pass algorithm with priority groups.
///
/// ┌───────────────────────────────────────────────────────────────────────────┐
/// │  ALGORITHM                                                               │
/// │                                                                           │
/// │  Groups: batteries sorted by EffectivePriority ASC                       │
/// │    · SOC < MinPercent → EffectivePriority = 0 (URGENT, always first)      │
/// │                                                                           │
/// │  PASS 1 — Solar surplus → SoftMaxPercent                                  │
/// │    PROPORTIONAL distribution by available space within each group         │
/// │    Batteries capped by MaxChargeRateW → surplus redirected to others      │
/// │                                                                           │
/// │  PASS 2 — Remaining surplus → HardMaxPercent (100%)                       │
/// │    Same logic, same order, target = HardMax                               │
/// │                                                                           │
/// │  PASS 3 — Grid charge → SoftMaxPercent (off-peak only)                   │
/// │    Only if GridChargeAllowedW > 0 (decided by SmartDistribution)           │
/// │    Capped at SoftMax — keeps room for the next solar surplus              │
/// │                                                                           │
/// │  POST-DISTRIBUTION — IdleChargeW                                          │
/// │    Any battery allocated 0 W AND at its target (>= SoftMax) receives      │
/// │    IdleChargeW to keep the BMS active.                                    │
/// │    Conditions: total=0W, SOC >= SoftMax, SOC <= HardMax,                  │
/// │                 surplus >= IdleChargeW (Fix Bug #5).                      │
/// │    FIX Bug #4: IdleChargeW disabled if surplusW = 0.                      │
/// │    FIX Bug #5: IdleChargeW disabled if surplus < IdleChargeW —            │
/// │    avoids silently pulling the difference from the grid.                  │
/// └───────────────────────────────────────────────────────────────────────────┘
/// </summary>
public class BatteryDistributionService : IBatteryDistributionService
{
    private const double DefaultAllocationWindowSeconds = 60;
    private readonly double _allocationWindowHours;

    public BatteryDistributionService()
        : this(TimeSpan.FromSeconds(DefaultAllocationWindowSeconds))
    {
    }

    public BatteryDistributionService(TimeSpan allocationWindow)
    {
        if (allocationWindow <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(allocationWindow), "Allocation window must be > 0.");

        _allocationWindowHours = allocationWindow.TotalHours;
    }

    /// <inheritdoc/>
    public DistributionResult Distribute(double surplusW, IEnumerable<Battery> batteries)
    {
        var batteryList = batteries.ToList();

        var allocated = batteryList.ToDictionary(b => b.Id, _ => 0.0);
        var gridAlloc = batteryList.ToDictionary(b => b.Id, _ => 0.0);
        var currentPct = batteryList.ToDictionary(b => b.Id, b => b.CurrentPercent);

        double remaining = surplusW;

        var groups = batteryList
            .GroupBy(b => Math.Round(b.EffectivePriority, 2))
            .OrderBy(g => g.Key)
            .ToList();

        // ── PASS 1 : surplus solaire → SoftMax ───────────────────────────────
        foreach (var group in groups)
        {
            if (remaining <= 0.01) break;
            remaining = DistributeSurplusToGroup(
                group.ToList(), remaining, allocated, gridAlloc, currentPct, useSoftMax: true, _allocationWindowHours);
        }

        // ── PASS 2 : surplus restant → HardMax ───────────────────────────────
        if (remaining > 0.01)
        {
            foreach (var group in groups)
            {
                if (remaining <= 0.01) break;
                remaining = DistributeSurplusToGroup(
                    group.ToList(), remaining, allocated, gridAlloc, currentPct, useSoftMax: false, _allocationWindowHours);
            }
        }

        // ── PASS 3: grid charge → SoftMax (off-peak hours) ─────────────────
        double gridCharged = 0;

        var gridGroups = batteryList
            .Where(b => b.GridChargeAllowedW > 0)
            .GroupBy(b => Math.Round(b.EffectivePriority, 2))
            .OrderBy(g => g.Key)
            .ToList();

        foreach (var group in gridGroups)
        {
            double consumed = DistributeGridToGroup(
                group.ToList(), allocated, gridAlloc, currentPct, _allocationWindowHours);
            gridCharged += consumed;
        }

        // POST-DISTRIBUTION: IdleChargeW
        // Any battery at 0 W (target reached or no surplus) but still below
        // HardMaxPercent receives IdleChargeW to keep the BMS active.
        //
        // FIX Bug #4: IdleChargeW is set to 0 by SmartDistributionService at peak tariff
        // (via Apply()), which prevents sending 100 W from the grid when
        // surplusW = 0 and the battery is already at its target.
        // We additionally check surplusW > 0 as an extra guard: without solar,
        // IdleChargeW makes no sense (nothing to "absorb") and would become grid charge.
        //
        // FIX Bug #5: IdleChargeW must not be injected if the available surplus
        // is less than IdleChargeW. Example: surplus=50W, IdleChargeW=100W →
        // the battery can only absorb 50W, the algorithm would still send 100W which
        // draws 50W from the grid. In that case we simply do not charge at all.
        // Exception: batteries in emergency grid charge always charge normally
        // via PASS 3 (DistributeGridToGroup), independently of this block.
        foreach (var b in batteryList)
        {
            double total = allocated[b.Id] + gridAlloc[b.Id];
            if (total <= 0.01
                && currentPct[b.Id] >= b.SoftMaxPercent - 0.1  // battery at its target (SoftMax reached)
                && currentPct[b.Id] <= b.HardMaxPercent         // but not beyond hard max
                && b.IdleChargeW > 0
                && surplusW > 0                                  // FIX Bug #4: no IdleCharge without solar surplus
                && (b.HardwareMinChargeW <= 0 || surplusW >= b.HardwareMinChargeW) // hardware threshold
                && !b.IsEmergencyGridCharge)                     // Emergency: charge already handled by PASS 3
            {
                allocated[b.Id] = b.IdleChargeW;
            }
        }

        // ── Final results ────────────────────────────────────────────────────
        var results = batteryList.Select(b =>
        {
            double solar = allocated[b.Id];
            double grid = gridAlloc[b.Id];
            double total = solar + grid;

        // idle = battery is at its target and just receives the hold setpoint
            bool isIdle = grid <= 0.01
                       && solar > 0.01 && solar <= b.IdleChargeW + 0.01
                       && currentPct[b.Id] >= b.SoftMaxPercent - 0.5;

            double energyForSoc = isIdle ? 0 : (solar + grid) * _allocationWindowHours;
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
        bool useSoftMax,
        double allocationWindowHours)
    {
        double remaining = surplusW;

        // ── HardwareMinChargeW guard ──────────────────────────────────────────
        // Upfront exclusion of batteries whose available surplus is less than
        // their hardware minimum threshold, unless in emergency (IsUrgent → EffectivePriority=0,
        // grid charge takes over via PASS 3 / GridChargeAllowedW).
        // Rationale: sending less than HardwareMinChargeW produces no real charge —
        // the setpoint is silently ignored by the hardware (e.g. EcoFlow).
        var active = group
            .Where(b => b.HardwareMinChargeW <= 0              // no hardware constraint
                     || surplusW >= b.HardwareMinChargeW        // sufficient surplus
                     || b.IsEmergencyGridCharge)                // emergency: always included
            .ToList();

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
                double cap = ComputeCyclePowerCap(spaces[b.Id], allocationWindowHours);
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
        Dictionary<int, double> currentPct,
        double allocationWindowHours)
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
                double cycleCap = ComputeCyclePowerCap(spaceToTarget, allocationWindowHours);
                return Math.Min(cycleCap, gridLeft);
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

    private static double ComputeCyclePowerCap(double remainingEnergyWh, double allocationWindowHours)
    {
        if (remainingEnergyWh <= 0.01)
            return 0;

        return remainingEnergyWh / allocationWindowHours;
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
        {
            // Distinguish: SOC already at target vs. allocation is capped to reach target
            if (b.CurrentPercent >= b.SoftMaxPercent - 0.1)
                return $"{prefix}Reached soft max {b.SoftMaxPercent:F0}%";
            else
                return $"{prefix}Capped to reach soft max {b.SoftMaxPercent:F0}% ({total:F0}W)";
        }

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