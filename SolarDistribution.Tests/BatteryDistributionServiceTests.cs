using FluentAssertions;
using NUnit.Framework;
using SolarDistribution.Core.Models;
using SolarDistribution.Core.Services;

namespace SolarDistribution.Tests.Unit;

/// <summary>
/// Tests unitaires pour <see cref="BatteryDistributionService"/>.
///
/// Setup commun :
///   B1 : 1024Wh, 500W max, min=20%, soft=80%, Priorité=1
///   B2 : 1024Wh, 500W max, min=20%, soft=80%, Priorité=2
///   B3 : 2048Wh, 500W max, min=20%, soft=80%, Priorité=2
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
        Id             = id,
        CapacityWh     = cap,
        MaxChargeRateW = rate,
        MinPercent     = min,
        CurrentPercent = pct,
        Priority       = prio,
        SoftMaxPercent = softMax,
        HardMaxPercent = hardMax
    };

    private static double Alloc(DistributionResult r, int id) =>
        r.Allocations.First(a => a.BatteryId == id).AllocatedW;

    // ── UC1 ──────────────────────────────────────────────────────────────────

    [Test]
    [Description("UC1 — 500W surplus, toutes les batteries à 50% — répartition proportionnelle")]
    public void UC1_500W_AllAt50Pct_ProportionalSplit()
    {
        // B1(prio1, seul dans son groupe) :
        //   SpaceToSoftMax = (80-50)% * 1024 = 307.2Wh → reçoit 307.2W, restant=192.8W
        // B2+B3(prio2, proportionnel) :
        //   B2 espace=307.2Wh (33.3%), B3 espace=614.4Wh (66.7%)
        //   B2 = 192.8 * 0.333 = 64.3W,  B3 = 192.8 * 0.667 = 128.5W
        var result = _sut.Distribute(500, new[]
        {
            B(1, 1024, 500, 20, 50, 1),
            B(2, 1024, 500, 20, 50, 2),
            B(3, 2048, 500, 20, 50, 2),
        });

        result.UnusedSurplusW.Should().BeApproximately(0,     Tolerance);
        result.TotalAllocatedW.Should().BeApproximately(500,  Tolerance);
        Alloc(result, 1).Should().BeApproximately(307.2, Tolerance);
        Alloc(result, 2).Should().BeApproximately(64.3,  Tolerance);
        Alloc(result, 3).Should().BeApproximately(128.5, Tolerance);
    }

    // ── UC2 ──────────────────────────────────────────────────────────────────

    [Test]
    [Description("UC2 — 1500W surplus, toutes à 50% — toutes les batteries absorbent le surplus")]
    public void UC2_1500W_AllAt50Pct_AllBatteriesCharge()
    {
        var result = _sut.Distribute(1500, new[]
        {
            B(1, 1024, 500, 20, 50, 1),
            B(2, 1024, 500, 20, 50, 2),
            B(3, 2048, 500, 20, 50, 2),
        });

        result.UnusedSurplusW.Should().BeApproximately(0,    Tolerance, "tout le surplus doit être absorbé");
        result.TotalAllocatedW.Should().BeApproximately(1500, Tolerance);
        Alloc(result, 1).Should().BeGreaterThan(0);
        Alloc(result, 2).Should().BeGreaterThan(0);
        Alloc(result, 3).Should().BeGreaterThan(0);
    }

    // ── UC3 ──────────────────────────────────────────────────────────────────

    [Test]
    [Description("UC3 — 1200W surplus, B1 à 60% — B1 moins d'espace, B2+B3 proportionnel")]
    public void UC3_1200W_B1At60Pct_ProportionalWithRateCap()
    {
        // B1(prio1) : SpaceToSoftMax=(80-60)%*1024=204.8Wh → 204.8W, restant=995.2W
        // B2+B3(prio2) proportionnel :
        //   B2 space=307.2 (33.3%) → cappée@307.2W (atteint 80%), B3 share=663.5W → cappée@500W (MaxRate)
        //   Redistribution → B3 rate épuisée → pass2 avec 188W
        // Pass2 : B1(prio1) reçoit les 188W restants
        // Total : B1=204.8+188=392.8W, B2=307.2W, B3=500W
        var result = _sut.Distribute(1200, new[]
        {
            B(1, 1024, 500, 20, 60, 1),
            B(2, 1024, 500, 20, 50, 2),
            B(3, 2048, 500, 20, 50, 2),
        });

        result.UnusedSurplusW.Should().BeApproximately(0,    Tolerance);
        result.TotalAllocatedW.Should().BeApproximately(1200, Tolerance);
        Alloc(result, 1).Should().BeApproximately(392.8, Tolerance, "B1 reçoit pass1 + pass2");
        Alloc(result, 2).Should().BeApproximately(307.2, Tolerance, "B2 atteint exactement 80%");
        Alloc(result, 3).Should().BeApproximately(500.0, Tolerance, "B3 cappée par MaxChargeRate");
    }

    // ── UC4 ──────────────────────────────────────────────────────────────────

    [Test]
    [Description("UC4 — 400W surplus, B1 à 18% URGENT — B1 absorbe tout le surplus")]
    public void UC4_400W_B1Urgent_AbsorbsAllSurplus()
    {
        var result = _sut.Distribute(400, new[]
        {
            B(1, 1024, 500, 20, 18, 1), // 18% < 20% → URGENT → EffectivePriority = 0
            B(2, 1024, 500, 20, 50, 2),
            B(3, 2048, 500, 20, 50, 2),
        });

        result.UnusedSurplusW.Should().BeApproximately(0,   Tolerance);
        Alloc(result, 1).Should().BeApproximately(400, Tolerance);
        Alloc(result, 2).Should().BeApproximately(0,   Tolerance);
        Alloc(result, 3).Should().BeApproximately(0,   Tolerance);

        result.Allocations.First(a => a.BatteryId == 1).WasUrgent
            .Should().BeTrue("B1 est sous le seuil minimum");
        result.Allocations.First(a => a.BatteryId == 2).WasUrgent
            .Should().BeFalse();
    }

    // ── UC5 ──────────────────────────────────────────────────────────────────

    [Test]
    [Description("UC5 — 600W surplus, B1 à 18% URGENT — B1 cappée par MaxRate, B2+B3 partagent le reste")]
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

        result.UnusedSurplusW.Should().BeApproximately(0,    Tolerance);
        Alloc(result, 1).Should().BeApproximately(500,  Tolerance);
        Alloc(result, 2).Should().BeApproximately(33.3, Tolerance);
        Alloc(result, 3).Should().BeApproximately(66.7, Tolerance);
    }

    // ── Cas limites ──────────────────────────────────────────────────────────

    [Test]
    [Description("Cas limite — surplus supérieur à la capacité totale — surplus inutilisé retourné")]
    public void Edge_SurplusExceedsAllCapacity_ReturnsUnusedSurplus()
    {
        var result = _sut.Distribute(9999, new[]
        {
            B(1, 1024, 500, 20, 90, 1), // Seulement 10% d'espace = 102.4Wh
        });

        result.TotalAllocatedW.Should().BeLessThan(9999);
        result.UnusedSurplusW.Should().BeGreaterThan(0);
        result.Allocations[0].NewPercent.Should().BeApproximately(100, 0.1);
    }

    [Test]
    [Description("Cas limite — surplus zéro — aucune allocation")]
    public void Edge_ZeroSurplus_NothingAllocated()
    {
        var result = _sut.Distribute(0, new[] { B(1, 1024, 500, 20, 50, 1) });

        result.TotalAllocatedW.Should().Be(0);
        result.UnusedSurplusW.Should().Be(0);
        Alloc(result, 1).Should().Be(0);
    }

    [Test]
    [Description("Cas limite — deux batteries urgentes même priorité — répartition proportionnelle entre elles")]
    public void Edge_TwoUrgentBatteriesSamePriority_ProportionalSplit()
    {
        var result = _sut.Distribute(300, new[]
        {
            B(1, 1024, 500, 20, 15, 1), // URGENT prio1 → EffectivePriority=0
            B(2, 1024, 500, 20, 10, 1), // URGENT prio1 → EffectivePriority=0
        });

        // Les deux urgentes, même groupe → proportionnel
        result.TotalAllocatedW.Should().BeApproximately(300, Tolerance);
        Alloc(result, 1).Should().BeGreaterThan(0);
        Alloc(result, 2).Should().BeGreaterThan(0);

        result.Allocations.Should().AllSatisfy(a =>
            a.WasUrgent.Should().BeTrue("les deux batteries sont urgentes"));
    }

    [Test]
    [Description("Cas limite — batterie déjà à 100% — ignorée, surplus vers les autres")]
    public void Edge_BatteryAlreadyFull_SkippedAndSurplusFlowsToOthers()
    {
        var result = _sut.Distribute(200, new[]
        {
            B(1, 1024, 500, 20, 100, 1), // Déjà pleine
            B(2, 1024, 500, 20, 50,  2),
        });

        Alloc(result, 1).Should().Be(0, "batterie déjà pleine");
        Alloc(result, 2).Should().BeApproximately(200, Tolerance);
    }

    // ── Tests sur les propriétés calculées de Battery ────────────────────────

    [Test]
    [Description("Battery.EffectivePriority retourne 0 quand CurrentPercent < MinPercent")]
    public void Battery_EffectivePriority_ReturnsZero_WhenBelowMin()
    {
        var battery = B(1, 1024, 500, 20, 19, 3); // 19% < 20% min

        battery.EffectivePriority.Should().Be(0);
        battery.IsUrgent.Should().BeTrue();
    }

    [Test]
    [Description("Battery.EffectivePriority retourne la priorité utilisateur quand CurrentPercent >= MinPercent")]
    public void Battery_EffectivePriority_ReturnsUserPriority_WhenAboveMin()
    {
        var battery = B(1, 1024, 500, 20, 20, 3); // exactement à 20% min

        battery.EffectivePriority.Should().Be(3);
        battery.IsUrgent.Should().BeFalse();
    }

    [Test]
    [Description("Battery.SpaceToSoftMaxWh retourne 0 si déjà au-dessus du soft max")]
    public void Battery_SpaceToSoftMaxWh_ReturnsZero_WhenAboveSoftMax()
    {
        var battery = B(1, 1024, 500, 20, 85, 1, softMax: 80);

        battery.SpaceToSoftMaxWh.Should().Be(0);
    }

    [Test]
    [Description("Battery.SpaceToSoftMaxWh calcul correct en-dessous du soft max")]
    public void Battery_SpaceToSoftMaxWh_CorrectValue_WhenBelowSoftMax()
    {
        var battery = B(1, 1024, 500, 20, 50, 1, softMax: 80);

        // (80 - 50)% * 1024 = 307.2 Wh
        battery.SpaceToSoftMaxWh.Should().BeApproximately(307.2, 0.01);
    }
}

