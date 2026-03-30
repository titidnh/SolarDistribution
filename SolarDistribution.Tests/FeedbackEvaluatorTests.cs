using FluentAssertions;
using NUnit.Framework;
using NSubstitute;
using SolarDistribution.Core.Data.Entities;
using SolarDistribution.Core.Models;
using SolarDistribution.Core.Repositories;
using SolarDistribution.Worker.Configuration;
using SolarDistribution.Worker.HA;
using SolarDistribution.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace SolarDistribution.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="FeedbackEvaluator"/> calculation formulas.
///
/// STRATEGY: test calculation methods by creating sessions
/// and verifying scores produced by CollectPendingFeedbacksAsync,
/// through a mocked IHomeAssistantClient returning controlled SOC values.
///
/// Tests cover:
///   - ComputeEnergyEfficiency (all energy absorbed / partial / none)
///   - ComputeAvailabilityScore (batteries above / below MinPercent)
///   - ComputeObservedOptimalSoftMax (too low → correction, too high → reduction, balanced)
///   - ComputeObservedOptimalPreventive (battery below min, far above, balanced)
/// </summary>
[TestFixture]
public class FeedbackEvaluatorTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static SolarConfig MakeConfig(int battId = 1, double minPct = 20.0) => new()
    {
        HomeAssistant = new() { Url = "http://ha.local", Token = "tok" },
        Batteries = new()
        {
            new BatteryConfig
            {
                Id         = battId,
                Name       = "TestBatt",
                CapacityWh = 1024,
                MaxChargeRateW = 500,
                MinPercent = minPct,
                SoftMaxPercent = 80,
                HardMaxPercent = 100,
                Priority   = 1,
                Entities   = new() { Soc = "sensor.batt1_soc", ChargePower = "number.batt1_power" }
            }
        },
        Ml = new() { FeedbackDelayHours = 4, FeedbackSoftmaxCorrectionFactor = 10,
                     FeedbackSoftmaxReduction = 5, FeedbackPreventiveFactor = 0.5,
                     FeedbackMaxPreventiveCorrection = 10, FeedbackPreventiveReduction = 3 }
    };

    private static DistributionSession MakeSession(
        double surplusW, double allocatedW, double unusedW,
        double softMaxPct = 80, double minPct = 20,
        long id = 1)
    {
        return new DistributionSession
        {
            Id              = id,
            RequestedAt     = DateTime.UtcNow.AddHours(-5),
            SurplusW        = surplusW,
            TotalAllocatedW = allocatedW,
            UnusedSurplusW  = unusedW,
            DecisionEngine  = "Deterministic",
            BatterySnapshots = new List<BatterySnapshot>
            {
                new()
                {
                    BatteryId            = 1,
                    SoftMaxPercent       = softMaxPct,
                    MinPercent           = minPct,
                    CurrentPercentBefore = 50,
                    CurrentPercentAfter  = 70,
                    AllocatedW           = allocatedW
                }
            }
        };
    }

    private (FeedbackEvaluator evaluator, IDistributionRepository repo, IHomeAssistantClient haClient)
        CreateEvaluator(SolarConfig? config = null)
    {
        var cfg      = config ?? MakeConfig();
        var repo     = Substitute.For<IDistributionRepository>();
        var haClient = Substitute.For<IHomeAssistantClient>();
        var logger   = NullLogger<FeedbackEvaluator>.Instance;
        var evaluator = new FeedbackEvaluator(repo, haClient, cfg, logger);
        return (evaluator, repo, haClient);
    }

    // ── EnergyEfficiency ─────────────────────────────────────────────────────

    [Test]
    public async Task CollectFeedback_FullAbsorption_EfficiencyIsOne()
    {
        var (ev, repo, ha) = CreateEvaluator();
        var session = MakeSession(surplusW: 1000, allocatedW: 1000, unusedW: 0);

        repo.GetSessionsPendingFeedbackAsync(Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(new List<DistributionSession> { session });

        ha.GetNumericStateAsync("sensor.batt1_soc", Arg.Any<CancellationToken>())
            .Returns(75.0);

        SessionFeedback? saved = null;
        await repo.SaveFeedbackAsync(Arg.Do<SessionFeedback>(f => saved = f), Arg.Any<CancellationToken>());

        await ev.CollectPendingFeedbacksAsync();

        saved.Should().NotBeNull();
        saved!.EnergyEfficiencyScore.Should().BeApproximately(1.0, 0.001);
    }

    [Test]
    public async Task CollectFeedback_PartialAbsorption_EfficiencyIsRatio()
    {
        var (ev, repo, ha) = CreateEvaluator();
        var session = MakeSession(surplusW: 1000, allocatedW: 600, unusedW: 400);

        repo.GetSessionsPendingFeedbackAsync(Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(new List<DistributionSession> { session });

        ha.GetNumericStateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(70.0);

        SessionFeedback? saved = null;
        await repo.SaveFeedbackAsync(Arg.Do<SessionFeedback>(f => saved = f), Arg.Any<CancellationToken>());

        await ev.CollectPendingFeedbacksAsync();

        saved!.EnergyEfficiencyScore.Should().BeApproximately(0.6, 0.001);
    }

    [Test]
    public async Task CollectFeedback_ZeroSurplus_EfficiencyIsOne()
    {
        var (ev, repo, ha) = CreateEvaluator();
        var session = MakeSession(surplusW: 0, allocatedW: 0, unusedW: 0);

        repo.GetSessionsPendingFeedbackAsync(Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(new List<DistributionSession> { session });

        ha.GetNumericStateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(60.0);

        SessionFeedback? saved = null;
        await repo.SaveFeedbackAsync(Arg.Do<SessionFeedback>(f => saved = f), Arg.Any<CancellationToken>());

        await ev.CollectPendingFeedbacksAsync();

        saved!.EnergyEfficiencyScore.Should().BeApproximately(1.0, 0.001);
    }

    // ── AvailabilityScore ─────────────────────────────────────────────────────

    [Test]
    public async Task CollectFeedback_BatteryAboveMin_AvailabilityIsOne()
    {
        var (ev, repo, ha) = CreateEvaluator(MakeConfig(minPct: 20));
        var session = MakeSession(1000, 1000, 0);

        repo.GetSessionsPendingFeedbackAsync(Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(new List<DistributionSession> { session });

        ha.GetNumericStateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(45.0); // well above 20%

        SessionFeedback? saved = null;
        await repo.SaveFeedbackAsync(Arg.Do<SessionFeedback>(f => saved = f), Arg.Any<CancellationToken>());

        await ev.CollectPendingFeedbacksAsync();

        saved!.AvailabilityScore.Should().BeApproximately(1.0, 0.001);
    }

    [Test]
    public async Task CollectFeedback_BatteryBelowMin_AvailabilityPenalized()
    {
        var (ev, repo, ha) = CreateEvaluator(MakeConfig(minPct: 20));
        var session = MakeSession(1000, 1000, 0);

        repo.GetSessionsPendingFeedbackAsync(Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(new List<DistributionSession> { session });

        // SOC drops to 10% while MinPercent = 20% → score = 10/20 = 0.5
        ha.GetNumericStateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(10.0);

        SessionFeedback? saved = null;
        await repo.SaveFeedbackAsync(Arg.Do<SessionFeedback>(f => saved = f), Arg.Any<CancellationToken>());

        await ev.CollectPendingFeedbacksAsync();

        saved!.AvailabilityScore.Should().BeApproximately(0.5, 0.01);
    }

    // ── ObservedOptimalSoftMax ────────────────────────────────────────────────

    [Test]
    public async Task CollectFeedback_LowAvailability_SoftMaxIncreasedAboveApplied()
    {
        // AvailabilityScore < 0.7 → SoftMax should be increased
        var (ev, repo, ha) = CreateEvaluator(MakeConfig(minPct: 20));
        var session = MakeSession(1000, 1000, 0, softMaxPct: 80);

        repo.GetSessionsPendingFeedbackAsync(Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(new List<DistributionSession> { session });

        // Observed SOC = 5% << 20% MinPercent → very low availability
        ha.GetNumericStateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(5.0);

        SessionFeedback? saved = null;
        await repo.SaveFeedbackAsync(Arg.Do<SessionFeedback>(f => saved = f), Arg.Any<CancellationToken>());

        await ev.CollectPendingFeedbacksAsync();

        saved!.ObservedOptimalSoftMax.Should().BeGreaterThan(80,
            "applied SoftMax was too low — should recommend higher");
        saved.ObservedOptimalSoftMax.Should().BeLessOrEqualTo(95);
    }

    [Test]
    public async Task CollectFeedback_GoodAvailabilityEquilibrium_SoftMaxUnchanged()
    {
        // Observed SOC > MinPercent → availability OK → SoftMax unchanged
        var (ev, repo, ha) = CreateEvaluator(MakeConfig(minPct: 20));
        var session = MakeSession(1000, 700, 300, softMaxPct: 80);

        repo.GetSessionsPendingFeedbackAsync(Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(new List<DistributionSession> { session });

        // SOC at 60%: well above 20% min and not >> softMax+5 = 85
        ha.GetNumericStateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(60.0);

        SessionFeedback? saved = null;
        await repo.SaveFeedbackAsync(Arg.Do<SessionFeedback>(f => saved = f), Arg.Any<CancellationToken>());

        await ev.CollectPendingFeedbacksAsync();

        saved!.ObservedOptimalSoftMax.Should().BeApproximately(80, 1.0,
            "balanced case → applied SoftMax is good");
    }

    [Test]
    public async Task CollectFeedback_BatteriesUnnecessarilyHigh_SoftMaxReduced()
    {
        // avgSocNow > appliedSoftMax + 5 AND UnusedSurplusW > 0 → reduction
        var (ev, repo, ha) = CreateEvaluator(MakeConfig(minPct: 20));
        var session = MakeSession(1000, 1000, 200, softMaxPct: 80); // unused > 0

        repo.GetSessionsPendingFeedbackAsync(Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(new List<DistributionSession> { session });

        // SOC at 90% >> 80 + 5 = 85 AND unused > 0 → SoftMax too high
        ha.GetNumericStateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(90.0);

        SessionFeedback? saved = null;
        await repo.SaveFeedbackAsync(Arg.Do<SessionFeedback>(f => saved = f), Arg.Any<CancellationToken>());

        await ev.CollectPendingFeedbacksAsync();

        saved!.ObservedOptimalSoftMax.Should().BeLessThan(80,
            "batteries unnecessarily high → SoftMax should be reduced");
    }

    // ── ObservedOptimalPreventive ─────────────────────────────────────────────

    [Test]
    public async Task CollectFeedback_BatteryDroppedBelowMin_PreventiveIncreased()
    {
        var (ev, repo, ha) = CreateEvaluator(MakeConfig(minPct: 20));
        var session = MakeSession(1000, 1000, 0, minPct: 20);

        repo.GetSessionsPendingFeedbackAsync(Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(new List<DistributionSession> { session });

        // Battery dropped to 10% < 20% MinPercent
        ha.GetNumericStateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(10.0);

        SessionFeedback? saved = null;
        await repo.SaveFeedbackAsync(Arg.Do<SessionFeedback>(f => saved = f), Arg.Any<CancellationToken>());

        await ev.CollectPendingFeedbacksAsync();

        saved!.ObservedOptimalPreventive.Should().BeGreaterThan(20,
            "battery dropped below MinPercent → preventive threshold too low");
        saved.ObservedOptimalPreventive.Should().BeLessOrEqualTo(50);
    }

    [Test]
    public async Task CollectFeedback_BatteryWellAboveMin_PreventiveReduced()
    {
        var (ev, repo, ha) = CreateEvaluator(MakeConfig(minPct: 20));
        var session = MakeSession(1000, 1000, 0, minPct: 20);

        repo.GetSessionsPendingFeedbackAsync(Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(new List<DistributionSession> { session });

        // Battery at 50% >> 20 + 20 = 40% → threshold too conservative
        ha.GetNumericStateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(50.0);

        SessionFeedback? saved = null;
        await repo.SaveFeedbackAsync(Arg.Do<SessionFeedback>(f => saved = f), Arg.Any<CancellationToken>());

        await ev.CollectPendingFeedbacksAsync();

        saved!.ObservedOptimalPreventive.Should().BeLessThan(20,
            "battery far above target → preventive threshold can be reduced");
    }

    // ── HA read failure ─────────────────────────────────────────────────────

    [Test]
    public async Task CollectFeedback_AllHaReadsFail_FeedbackIsInvalid()
    {
        var (ev, repo, ha) = CreateEvaluator();
        var session = MakeSession(1000, 1000, 0);

        repo.GetSessionsPendingFeedbackAsync(Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(new List<DistributionSession> { session });

        // All HA reads fail → null
        ha.GetNumericStateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((double?)null);

        SessionFeedback? saved = null;
        await repo.SaveFeedbackAsync(Arg.Do<SessionFeedback>(f => saved = f), Arg.Any<CancellationToken>());

        await ev.CollectPendingFeedbacksAsync();

        saved!.Status.Should().Be(FeedbackStatus.Invalid);
    }

    [Test]
    public async Task CollectFeedback_NoPendingSessions_ReturnsZero()
    {
        var (ev, repo, _) = CreateEvaluator();

        repo.GetSessionsPendingFeedbackAsync(Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(new List<DistributionSession>());

        var count = await ev.CollectPendingFeedbacksAsync();
        count.Should().Be(0);
    }

    // ── CompositeScore ────────────────────────────────────────────────────────

    [Test]
    public async Task CollectFeedback_CompositeScore_IsWeightedAverage()
    {
        // CompositeScore = efficiency * 0.6 + availability * 0.4
        var (ev, repo, ha) = CreateEvaluator(MakeConfig(minPct: 20));
        // efficiency = 0.8 (800/1000), availability = 1.0 (SOC=50>20)
        var session = MakeSession(1000, 800, 200);

        repo.GetSessionsPendingFeedbackAsync(Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(new List<DistributionSession> { session });

        ha.GetNumericStateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(50.0);

        SessionFeedback? saved = null;
        await repo.SaveFeedbackAsync(Arg.Do<SessionFeedback>(f => saved = f), Arg.Any<CancellationToken>());

        await ev.CollectPendingFeedbacksAsync();

        // 0.8 * 0.6 + 1.0 * 0.4 = 0.48 + 0.4 = 0.88
        saved!.CompositeScore.Should().BeApproximately(0.88, 0.01);
    }
}
