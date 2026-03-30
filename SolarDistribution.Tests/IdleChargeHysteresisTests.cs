using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using SolarDistribution.Worker.Configuration;
using SolarDistribution.Worker.Services;

namespace SolarDistribution.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="IdleChargeHysteresis"/>.
///
/// Reference parameters:
///   IdleChargeW    = 100W  (activation threshold)
///   IdleStopBufferW = 30W  (hysteresis)
///   -> stop threshold = 100 - 30 = 70W
///   -> dead band      = [70W, 100W[
/// </summary>
[TestFixture]
public class IdleChargeHysteresisTests
{
    private IdleChargeHysteresis _sut = null!;

    private static BatteryConfig Bc(double idleW = 100, double stopBuffer = 30) => new()
    {
        Id = 1,
        Name = "Test Battery",
        IdleChargeW = idleW,
        IdleStopBufferW = stopBuffer,
    };

    [SetUp]
    public void SetUp()
    {
        _sut = new IdleChargeHysteresis(NullLogger.Instance);
    }

    // ── Activation ────────────────────────────────────────────────────────────

    [Test]
    [Description("surplus < IdleChargeW -> idle stays OFF")]
    public void Idle_StaysOff_WhenSurplusBelowStartThreshold()
    {
        _sut.Compute(Bc(), effectiveSurplus: 90).Should().Be(0);
        _sut.IsIdle(1).Should().BeFalse();
    }

    [Test]
    [Description("surplus == IdleChargeW -> idle turns ON (inclusive threshold)")]
    public void Idle_TurnsOn_WhenSurplusEqualsStartThreshold()
    {
        _sut.Compute(Bc(), effectiveSurplus: 100).Should().Be(100);
        _sut.IsIdle(1).Should().BeTrue();
    }

    [Test]
    [Description("surplus > IdleChargeW -> idle turns ON")]
    public void Idle_TurnsOn_WhenSurplusAboveStartThreshold()
    {
        _sut.Compute(Bc(), effectiveSurplus: 150).Should().Be(100);
        _sut.IsIdle(1).Should().BeTrue();
    }

    // ── Dead band: keep ON ──────────────────────────────────────────────

    [Test]
    [Description("Idle ON, surplus drops into dead band [70,100[ -> kept ON")]
    public void Idle_MaintainedOn_WhenSurplusInDeadBand()
    {
        _sut.Compute(Bc(), 110); // activation
        _sut.Compute(Bc(), 90).Should().Be(100, "90W in [70,100[ -> kept ON");
        _sut.Compute(Bc(), 75).Should().Be(100, "75W in [70,100[ -> kept ON");
        _sut.Compute(Bc(), 70).Should().Be(100, "70W == inclusive lower threshold -> kept ON");
    }

    // ── Stop ────────────────────────────────────────────────────────────────

    [Test]
    [Description("Idle ON, surplus falls below lower threshold -> idle turns OFF")]
    public void Idle_TurnsOff_WhenSurplusBelowStopThreshold()
    {
        _sut.Compute(Bc(), 110); // activation
        _sut.Compute(Bc(), 69).Should().Be(0, "69W < 70W -> stop");
        _sut.IsIdle(1).Should().BeFalse();
    }

    // ── Dead band: keep OFF ─────────────────────────────────────────────

    [Test]
    [Description("Idle OFF, surplus rises into dead band [70,100[ -> kept OFF")]
    public void Idle_MaintainedOff_WhenSurplusInDeadBandFromBelow()
    {
        // Not activated yet -> initial state = OFF
        _sut.Compute(Bc(), 80).Should().Be(0, "80W < 100W -> not activated yet");
        _sut.Compute(Bc(), 90).Should().Be(0, "90W in dead band, idle never activated -> OFF");
    }

    [Test]
    [Description("Idle turned ON then OFF, surplus rises into dead band -> stays OFF")]
    public void Idle_MaintainedOff_AfterStop_WhenSurplusInDeadBand()
    {
        _sut.Compute(Bc(), 110); // ON
        _sut.Compute(Bc(), 60);  // OFF (60 < 70)
        _sut.Compute(Bc(), 85).Should().Be(0, "85W in dead band after stop -> kept OFF");
        _sut.Compute(Bc(), 95).Should().Be(0, "95W in dead band -> kept OFF");
    }

    // ── Full ON/OFF/ON cycle ───────────────────────────────────────────────

    [Test]
    [Description("Full scenario: oscillation around 100W without hysteresis would produce ON/OFF/ON/OFF")]
    public void Idle_FullCycle_NoOscillation()
    {
        var bc = Bc(idleW: 100, stopBuffer: 30);

        _sut.Compute(bc, 50).Should().Be(0, "surplus=50 -> OFF");
        _sut.Compute(bc, 110).Should().Be(100, "surplus=110 -> ON");
        _sut.Compute(bc, 90).Should().Be(100, "surplus=90 -> dead band -> kept ON");
        _sut.Compute(bc, 80).Should().Be(100, "surplus=80 -> dead band -> kept ON");
        _sut.Compute(bc, 65).Should().Be(0, "surplus=65 -> < 70 -> OFF");
        _sut.Compute(bc, 80).Should().Be(0, "surplus=80 -> dead band -> kept OFF");
        _sut.Compute(bc, 95).Should().Be(0, "surplus=95 -> dead band -> kept OFF");
        _sut.Compute(bc, 105).Should().Be(100, "surplus=105 -> >= 100 -> ON again");
    }

    // ── Edge cases ───────────────────────────────────────────────────────────

    [Test]
    [Description("IdleChargeW = 0 -> always 0, no state")]
    public void Idle_ReturnsZero_WhenIdleChargeWIsZero()
    {
        _sut.Compute(Bc(idleW: 0), 500).Should().Be(0);
        _sut.IsIdle(1).Should().BeFalse();
    }

    [Test]
    [Description("IdleStopBufferW = 0 -> single threshold, dead band disabled")]
    public void Idle_SingleThreshold_WhenStopBufferIsZero()
    {
        var bc = Bc(idleW: 100, stopBuffer: 0);

        _sut.Compute(bc, 110).Should().Be(100, "surplus=110 -> ON");
        _sut.Compute(bc, 99).Should().Be(0, "surplus=99 < 100 -> immediate OFF (no dead band)");
        _sut.Compute(bc, 100).Should().Be(100, "surplus=100 -> ON");
    }

    [Test]
    [Description("Two independent batteries - non-shared states")]
    public void Idle_IndependentState_PerBattery()
    {
        var bc1 = new BatteryConfig { Id = 1, Name = "B1", IdleChargeW = 100, IdleStopBufferW = 30 };
        var bc2 = new BatteryConfig { Id = 2, Name = "B2", IdleChargeW = 100, IdleStopBufferW = 30 };

        _sut.Compute(bc1, 110); // B1 -> ON
        _sut.Compute(bc2, 50);  // B2 -> OFF

        _sut.IsIdle(1).Should().BeTrue("B1 is ON");
        _sut.IsIdle(2).Should().BeFalse("B2 is OFF independently from B1");

        _sut.Compute(bc1, 60);  // B1 -> OFF
        _sut.Compute(bc2, 120); // B2 -> ON

        _sut.IsIdle(1).Should().BeFalse("B1 switched to OFF");
        _sut.IsIdle(2).Should().BeTrue("B2 switched to ON");
    }
}