using FluentAssertions;
using NUnit.Framework;
using SolarDistribution.Core.Models;
using SolarDistribution.Core.Services;

namespace SolarDistribution.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="BatteryDistributionService"/>.
///
/// Common setup:
///   B1: 1024Wh, 500W max, min=20%, soft=80%, Priority=1
///   B2: 1024Wh, 500W max, min=20%, soft=80%, Priority=2
///   B3: 2048Wh, 500W max, min=20%, soft=80%, Priority=2
/// </summary>
[TestFixture]
public class BatteryDistributionServiceTests
{
    private BatteryDistributionService _sut = null!;
    private const double Tolerance = 1.0; // W

    [SetUp]
    public void SetUp()
    {
        _sut = new BatteryDistributionService();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static Battery B(int id, double cap, double rate, double min, double pct, int prio,
        double softMax = 80, double hardMax = 100) => new()
        {
            Id = id,
            CapacityWh = cap,
            MaxChargeRateW = rate,
            MinPercent = min,
            CurrentPercent = pct,
            Priority = prio,
            SoftMaxPercent = softMax,
            HardMaxPercent = hardMax
        };

    private static double Alloc(DistributionResult r, int id) =>
        r.Allocations.First(a => a.BatteryId == id).AllocatedW;

    // ── UC1 ──────────────────────────────────────────────────────────────────

    [Test]
    [Description("UC1 — 500W surplus, all batteries at 50% — priority group absorbs full surplus when one cycle can absorb it")]
    public void UC1_500W_AllAt50Pct_ProportionalSplit()
    {
        // With per-cycle capping (60s worker loop), B1 still has 307.2Wh of room
        // to SoftMax, which is far more than enough to absorb 500W over a single cycle.
        // Since B1 is alone in the highest-priority group, it takes the full surplus.
        var result = _sut.Distribute(500, new[]
        {
            B(1, 1024, 500, 20, 50, 1),
            B(2, 1024, 500, 20, 50, 2),
            B(3, 2048, 500, 20, 50, 2),
        });

        result.UnusedSurplusW.Should().BeApproximately(0, Tolerance);
        result.TotalAllocatedW.Should().BeApproximately(500, Tolerance);
        Alloc(result, 1).Should().BeApproximately(500, Tolerance);
        Alloc(result, 2).Should().BeApproximately(0, Tolerance);
        Alloc(result, 3).Should().BeApproximately(0, Tolerance);
    }

    // ── UC2 ──────────────────────────────────────────────────────────────────

    [Test]
    [Description("UC2 — 1500W surplus, all at 50% — all batteries absorb surplus")]
    public void UC2_1500W_AllAt50Pct_AllBatteriesCharge()
    {
        var result = _sut.Distribute(1500, new[]
        {
            B(1, 1024, 500, 20, 50, 1),
            B(2, 1024, 500, 20, 50, 2),
            B(3, 2048, 500, 20, 50, 2),
        });

        result.UnusedSurplusW.Should().BeApproximately(0, Tolerance, "all surplus should be absorbed");
        result.TotalAllocatedW.Should().BeApproximately(1500, Tolerance);
        Alloc(result, 1).Should().BeGreaterThan(0);
        Alloc(result, 2).Should().BeGreaterThan(0);
        Alloc(result, 3).Should().BeGreaterThan(0);
    }

    // ── UC3 ──────────────────────────────────────────────────────────────────

    [Test]
    [Description("UC3 — 1200W surplus, B1 at 60% — high-priority battery charges at max rate, remaining group shares the rest")]
    public void UC3_1200W_B1At60Pct_ProportionalWithRateCap()
    {
        // Per-cycle capping lets B1 use its full 500W max rate during PASS 1.
        // The remaining 700W then go to the next group proportionally to available space.
        var result = _sut.Distribute(1200, new[]
        {
            B(1, 1024, 500, 20, 60, 1),
            B(2, 1024, 500, 20, 50, 2),
            B(3, 2048, 500, 20, 50, 2),
        });

        result.UnusedSurplusW.Should().BeApproximately(0, Tolerance);
        result.TotalAllocatedW.Should().BeApproximately(1200, Tolerance);
        Alloc(result, 1).Should().BeApproximately(500.0, Tolerance, "B1 can absorb its full rate during the current cycle");
        Alloc(result, 2).Should().BeApproximately(233.3, Tolerance, "remaining surplus is shared proportionally in the second group");
        Alloc(result, 3).Should().BeApproximately(466.7, Tolerance, "remaining surplus is shared proportionally in the second group");
    }

    // ── UC4 ──────────────────────────────────────────────────────────────────

    [Test]
    [Description("UC4 — 400W surplus, B1 at 18% URGENT — B1 absorbs all surplus")]
    public void UC4_400W_B1Urgent_AbsorbsAllSurplus()
    {
        var result = _sut.Distribute(400, new[]
        {
            B(1, 1024, 500, 20, 18, 1), // 18% < 20% → URGENT → EffectivePriority = 0
            B(2, 1024, 500, 20, 50, 2),
            B(3, 2048, 500, 20, 50, 2),
        });

        result.UnusedSurplusW.Should().BeApproximately(0, Tolerance);
        Alloc(result, 1).Should().BeApproximately(400, Tolerance);
        Alloc(result, 2).Should().BeApproximately(0, Tolerance);
        Alloc(result, 3).Should().BeApproximately(0, Tolerance);

        result.Allocations.First(a => a.BatteryId == 1).WasUrgent
            .Should().BeTrue("B1 is below minimum threshold");
        result.Allocations.First(a => a.BatteryId == 2).WasUrgent
            .Should().BeFalse();
    }

    // ── UC5 ──────────────────────────────────────────────────────────────────

    [Test]
    [Description("UC5 — 600W surplus, B1 at 18% URGENT — B1 capped by MaxRate, B2+B3 share remaining")]
    public void UC5_600W_B1Urgent_RateCapped_B2B3ShareRest()
    {
        // B1(prio0 urgent) : min(634.88Wh, 500W, 600W) = 500W, restant=100W
        // B2+B3(prio2) proportionnel :
        //   B2 espace=307.2 (33.3%) → 33.3W,  B3 espace=614.4 (66.7%) → 66.7W
        var result = _sut.Distribute(600, new[]
        {
            B(1, 1024, 500, 20, 18, 1), // URGENT
            B(2, 1024, 500, 20, 50, 2),
            B(3, 2048, 500, 20, 50, 2),
        });

        result.UnusedSurplusW.Should().BeApproximately(0, Tolerance);
        Alloc(result, 1).Should().BeApproximately(500, Tolerance);
        Alloc(result, 2).Should().BeApproximately(33.3, Tolerance);
        Alloc(result, 3).Should().BeApproximately(66.7, Tolerance);
    }

    // ── Edge cases ───────────────────────────────────────────────────────────

    [Test]
    [Description("Edge case — surplus above total capacity — unused surplus returned")]
    public void Edge_SurplusExceedsAllCapacity_ReturnsUnusedSurplus()
    {
        var result = _sut.Distribute(9999, new[]
        {
            B(1, 1024, 500, 20, 90, 1), // Only 10% space = 102.4Wh
        });

        result.TotalAllocatedW.Should().BeLessThan(9999);
        result.UnusedSurplusW.Should().BeGreaterThan(0);
        result.Allocations[0].NewPercent.Should().BeApproximately(90.81, 0.05,
            "NewPercent projects one worker cycle, not one full hour of sustained power");
    }

    [Test]
    [Description("Edge case — zero surplus — no allocation")]
    public void Edge_ZeroSurplus_NothingAllocated()
    {
        var result = _sut.Distribute(0, new[] { B(1, 1024, 500, 20, 50, 1) });

        result.TotalAllocatedW.Should().Be(0);
        result.UnusedSurplusW.Should().Be(0);
        Alloc(result, 1).Should().Be(0);
    }

    [Test]
    [Description("Edge case — two urgent batteries same priority — proportional split")]
    public void Edge_TwoUrgentBatteriesSamePriority_ProportionalSplit()
    {
        var result = _sut.Distribute(300, new[]
        {
            B(1, 1024, 500, 20, 15, 1), // URGENT prio1 → EffectivePriority=0
            B(2, 1024, 500, 20, 10, 1), // URGENT prio1 → EffectivePriority=0
        });

        // Both urgent, same group → proportional
        result.TotalAllocatedW.Should().BeApproximately(300, Tolerance);
        Alloc(result, 1).Should().BeGreaterThan(0);
        Alloc(result, 2).Should().BeGreaterThan(0);

        result.Allocations.Should().AllSatisfy(a =>
            a.WasUrgent.Should().BeTrue("both batteries are urgent"));
    }

    [Test]
    [Description("Edge case — battery already at 100% — skipped, surplus goes to others")]
    public void Edge_BatteryAlreadyFull_SkippedAndSurplusFlowsToOthers()
    {
        var result = _sut.Distribute(200, new[]
        {
            B(1, 1024, 500, 20, 100, 1), // Already full
            B(2, 1024, 500, 20, 50,  2),
        });

        Alloc(result, 1).Should().Be(0, "battery already full");
        Alloc(result, 2).Should().BeApproximately(200, Tolerance);
    }

    // ── Tests on computed Battery properties ────────────────────────────────

    [Test]
    [Description("Battery.EffectivePriority returns 0 when CurrentPercent < MinPercent")]
    public void Battery_EffectivePriority_ReturnsZero_WhenBelowMin()
    {
        var battery = B(1, 1024, 500, 20, 19, 3); // 19% < 20% min

        battery.EffectivePriority.Should().Be(0);
        battery.IsUrgent.Should().BeTrue();
    }

    [Test]
    [Description("Battery.EffectivePriority returns user priority when CurrentPercent >= MinPercent")]
    public void Battery_EffectivePriority_ReturnsUserPriority_WhenAboveMin()
    {
        var battery = B(1, 1024, 500, 20, 20, 3); // exactly at 20% min

        battery.EffectivePriority.Should().Be(3);
        battery.IsUrgent.Should().BeFalse();
    }

    [Test]
    [Description("Battery.SpaceToSoftMaxWh returns 0 if already above soft max")]
    public void Battery_SpaceToSoftMaxWh_ReturnsZero_WhenAboveSoftMax()
    {
        var battery = B(1, 1024, 500, 20, 85, 1, softMax: 80);

        battery.SpaceToSoftMaxWh.Should().Be(0);
    }

    [Test]
    [Description("Battery.SpaceToSoftMaxWh computes correctly below soft max")]
    public void Battery_SpaceToSoftMaxWh_CorrectValue_WhenBelowSoftMax()
    {
        var battery = B(1, 1024, 500, 20, 50, 1, softMax: 80);

        // (80 - 50)% * 1024 = 307.2 Wh
        battery.SpaceToSoftMaxWh.Should().BeApproximately(307.2, 0.01);
    }

    // ── HardwareMinChargeW: physical minimum threshold ──────────────────────
    // Helper battery with HardMaxPercent = CurrentPercent to isolate IdleCharge block
    // (PASS 1 and PASS 2 allocate nothing → only POST-DISTRIBUTION can apply).
    private static Battery BFull(double hardwareMin = 100, double idleW = 100) => new()
    {
        Id = 1,
        CapacityWh = 1024,
        MaxChargeRateW = 1000,
        MinPercent = 20,
        CurrentPercent = 85,
        Priority = 1,
        SoftMaxPercent = 80,
        HardMaxPercent = 85,
        HardwareMinChargeW = hardwareMin,
        IdleChargeW = idleW
    };

    // ── IdleCharge + HardwareMinChargeW ──────────────────────────────────────

    [Test]
    [Description("HardwareMin — surplus < hardware_min_charge_w: IdleCharge not applied (grid-risk prevention)")]
    public void HardwareMin_IdleCharge_NotApplied_WhenSurplusBelowHardwareMin()
    {
        var result = _sut.Distribute(surplusW: 50, new[] { BFull(hardwareMin: 100) });

        Alloc(result, 1).Should().Be(0,
            "surplus (50W) < HardwareMinChargeW (100W) → no charging");
    }

    [Test]
    [Description("HardwareMin — surplus == hardware_min_charge_w: IdleCharge applied (inclusive threshold)")]
    public void HardwareMin_IdleCharge_Applied_WhenSurplusEqualsHardwareMin()
    {
        var result = _sut.Distribute(surplusW: 100, new[] { BFull(hardwareMin: 100) });

        Alloc(result, 1).Should().BeApproximately(100, Tolerance,
            "surplus (100W) == HardwareMinChargeW (100W) → IdleCharge applied");
    }

    [Test]
    [Description("HardwareMin — surplus > hardware_min_charge_w: IdleCharge applied normally")]
    public void HardwareMin_IdleCharge_Applied_WhenSurplusAboveHardwareMin()
    {
        var result = _sut.Distribute(surplusW: 250, new[] { BFull(hardwareMin: 100) });

        Alloc(result, 1).Should().BeApproximately(100, Tolerance,
            "surplus (250W) > HardwareMinChargeW (100W) → IdleCharge applied normally");
    }

    [Test]
    [Description("HardwareMin — hardware_min_charge_w = 0: no guard, IdleCharge applied even with low surplus")]
    public void HardwareMin_IdleCharge_Applied_WhenHardwareMinIsZero()
    {
        var result = _sut.Distribute(surplusW: 10, new[] { BFull(hardwareMin: 0) });

        Alloc(result, 1).Should().BeApproximately(100, Tolerance,
            "HardwareMinChargeW=0 → guard disabled, IdleCharge applies normally");
    }

    // ── HardwareMinChargeW on PASS 1/2 (battery below SoftMax) ─────────────

    [Test]
    [Description("HardwareMin — surplus < hardware_min: battery below SoftMax but skipped in PASS 1/2")]
    public void HardwareMin_Pass12_BatterySkipped_WhenSurplusBelowHardwareMin()
    {
        var battery = new Battery
        {
            Id = 1,
            CapacityWh = 1024,
            MaxChargeRateW = 1000,
            MinPercent = 20,
            CurrentPercent = 50,   // SOC < SoftMax → PASS 1 would charge
            Priority = 1,
            SoftMaxPercent = 80,
            HardMaxPercent = 85,
            HardwareMinChargeW = 100
        };

        var result = _sut.Distribute(surplusW: 60, new[] { battery });

        Alloc(result, 1).Should().Be(0,
            "surplus (60W) < HardwareMinChargeW (100W) → PASS 1/2 skips battery");
    }

    [Test]
    [Description("HardwareMin — surplus >= hardware_min: PASS 1/2 charges normally")]
    public void HardwareMin_Pass12_BatteryCharged_WhenSurplusAboveHardwareMin()
    {
        var battery = new Battery
        {
            Id = 1,
            CapacityWh = 1024,
            MaxChargeRateW = 1000,
            MinPercent = 20,
            CurrentPercent = 50,
            Priority = 1,
            SoftMaxPercent = 80,
            HardMaxPercent = 85,
            HardwareMinChargeW = 100
        };

        var result = _sut.Distribute(surplusW: 200, new[] { battery });

        Alloc(result, 1).Should().BeGreaterThan(0,
            "surplus (200W) >= HardwareMinChargeW (100W) → PASS 1 charges normally");
    }

    // ── Emergency ignores HardwareMinChargeW ────────────────────────────────

    [Test]
    [Description("HardwareMin — emergency grid charge: always charges, even if surplus < hardware_min")]
    public void HardwareMin_EmergencyGridCharge_AlwaysCharges_RegardlessOfHardwareMin()
    {
        var battery = new Battery
        {
            Id = 1,
            CapacityWh = 1024,
            MaxChargeRateW = 1000,
            MinPercent = 20,
            CurrentPercent = 15,
            Priority = 1,
            SoftMaxPercent = 80,
            HardMaxPercent = 85,
            HardwareMinChargeW = 100,
            IdleChargeW = 100,
            GridChargeAllowedW = 1000,
            IsEmergencyGridCharge = true,
            EmergencyGridChargeBelowPercent = 20,
            EmergencyGridChargeTargetPercent = 50
        };

        var result = _sut.Distribute(surplusW: 30, new[] { battery });

        result.Allocations.First(a => a.BatteryId == 1).IsEmergencyGridCharge
            .Should().BeTrue();
        result.GridChargedW.Should().BeGreaterThan(0,
            "emergency grid charge ignore HardwareMinChargeW");
    }

    [Test]
    [Description("Near full battery — still uses max charge rate when remaining energy fits in one worker cycle")]
    public void NearFullBattery_UsesMaxRate_WhenCycleCanAbsorbIt()
    {
        var battery = B(1, 1024, 500, 20, 95, 1);

        var result = _sut.Distribute(500, new[] { battery });

        Alloc(result, 1).Should().BeApproximately(500, Tolerance,
            "5% remaining equals 51.2Wh, which can absorb 500W over a 60s cycle");
        result.Allocations[0].NewPercent.Should().BeApproximately(95.81, 0.05,
            "500W over 60s adds about 8.33Wh, not a full 500Wh");
    }

    [Test]
    [Description("Near full battery — power is capped only to avoid overfilling within the current worker cycle")]
    public void NearFullBattery_CapsOnlyToCurrentCycleAbsorption()
    {
        var battery = B(1, 1024, 500, 20, 99.5, 1);

        var result = _sut.Distribute(500, new[] { battery });

        Alloc(result, 1).Should().BeApproximately(307.2, Tolerance,
            "0.5% remaining equals 5.12Wh, so a 60s cycle should cap power around 307W");
        result.Allocations[0].NewPercent.Should().BeApproximately(100, 0.05);
        result.UnusedSurplusW.Should().BeApproximately(192.8, Tolerance);
    }
}