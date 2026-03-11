using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using SolarDistribution.Worker.Configuration;
using SolarDistribution.Worker.Services;

namespace SolarDistribution.Tests.Unit;

/// <summary>
/// Tests unitaires pour <see cref="IdleChargeHysteresis"/>.
///
/// Paramètres de référence :
///   IdleChargeW    = 100W  (seuil d'activation)
///   IdleStopBufferW = 30W  (hystérésis)
///   → seuil d'arrêt = 100 - 30 = 70W
///   → zone morte    = [70W, 100W[
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
    [Description("surplus < IdleChargeW → idle reste OFF")]
    public void Idle_StaysOff_WhenSurplusBelowStartThreshold()
    {
        _sut.Compute(Bc(), effectiveSurplus: 90).Should().Be(0);
        _sut.IsIdle(1).Should().BeFalse();
    }

    [Test]
    [Description("surplus == IdleChargeW → idle s'active (seuil inclusif)")]
    public void Idle_TurnsOn_WhenSurplusEqualsStartThreshold()
    {
        _sut.Compute(Bc(), effectiveSurplus: 100).Should().Be(100);
        _sut.IsIdle(1).Should().BeTrue();
    }

    [Test]
    [Description("surplus > IdleChargeW → idle s'active")]
    public void Idle_TurnsOn_WhenSurplusAboveStartThreshold()
    {
        _sut.Compute(Bc(), effectiveSurplus: 150).Should().Be(100);
        _sut.IsIdle(1).Should().BeTrue();
    }

    // ── Zone morte : maintien ON ──────────────────────────────────────────────

    [Test]
    [Description("Idle ON, surplus descend dans la zone morte [70,100[ → maintenu ON")]
    public void Idle_MaintainedOn_WhenSurplusInDeadBand()
    {
        _sut.Compute(Bc(), 110); // activation
        _sut.Compute(Bc(), 90).Should().Be(100, "90W dans [70,100[ → maintenu ON");
        _sut.Compute(Bc(), 75).Should().Be(100, "75W dans [70,100[ → maintenu ON");
        _sut.Compute(Bc(), 70).Should().Be(100, "70W == seuil bas inclusif → maintenu ON");
    }

    // ── Arrêt ────────────────────────────────────────────────────────────────

    [Test]
    [Description("Idle ON, surplus passe sous le seuil bas → idle s'arrête")]
    public void Idle_TurnsOff_WhenSurplusBelowStopThreshold()
    {
        _sut.Compute(Bc(), 110); // activation
        _sut.Compute(Bc(), 69).Should().Be(0, "69W < 70W → arrêt");
        _sut.IsIdle(1).Should().BeFalse();
    }

    // ── Zone morte : maintien OFF ─────────────────────────────────────────────

    [Test]
    [Description("Idle OFF, surplus remonte dans la zone morte [70,100[ → maintenu OFF")]
    public void Idle_MaintainedOff_WhenSurplusInDeadBandFromBelow()
    {
        // Pas encore activé → état initial = OFF
        _sut.Compute(Bc(), 80).Should().Be(0, "80W < 100W → pas encore activé");
        _sut.Compute(Bc(), 90).Should().Be(0, "90W dans zone morte, idle jamais activé → OFF");
    }

    [Test]
    [Description("Idle activé puis éteint, surplus remonte dans zone morte → reste OFF")]
    public void Idle_MaintainedOff_AfterStop_WhenSurplusInDeadBand()
    {
        _sut.Compute(Bc(), 110); // ON
        _sut.Compute(Bc(), 60);  // OFF (60 < 70)
        _sut.Compute(Bc(), 85).Should().Be(0, "85W dans zone morte après arrêt → maintenu OFF");
        _sut.Compute(Bc(), 95).Should().Be(0, "95W dans zone morte → maintenu OFF");
    }

    // ── Cycle complet ON/OFF/ON ───────────────────────────────────────────────

    [Test]
    [Description("Scénario complet : oscillation autour de 100W sans hystérésis produirait ON/OFF/ON/OFF")]
    public void Idle_FullCycle_NoOscillation()
    {
        var bc = Bc(idleW: 100, stopBuffer: 30);

        _sut.Compute(bc, 50).Should().Be(0, "surplus=50 → OFF");
        _sut.Compute(bc, 110).Should().Be(100, "surplus=110 → ON");
        _sut.Compute(bc, 90).Should().Be(100, "surplus=90 → zone morte → maintenu ON");
        _sut.Compute(bc, 80).Should().Be(100, "surplus=80 → zone morte → maintenu ON");
        _sut.Compute(bc, 65).Should().Be(0, "surplus=65 → < 70 → OFF");
        _sut.Compute(bc, 80).Should().Be(0, "surplus=80 → zone morte → maintenu OFF");
        _sut.Compute(bc, 95).Should().Be(0, "surplus=95 → zone morte → maintenu OFF");
        _sut.Compute(bc, 105).Should().Be(100, "surplus=105 → >= 100 → ON à nouveau");
    }

    // ── Cas limites ───────────────────────────────────────────────────────────

    [Test]
    [Description("IdleChargeW = 0 → toujours 0, pas d'état")]
    public void Idle_ReturnsZero_WhenIdleChargeWIsZero()
    {
        _sut.Compute(Bc(idleW: 0), 500).Should().Be(0);
        _sut.IsIdle(1).Should().BeFalse();
    }

    [Test]
    [Description("IdleStopBufferW = 0 → seuil unique, zone morte désactivée")]
    public void Idle_SingleThreshold_WhenStopBufferIsZero()
    {
        var bc = Bc(idleW: 100, stopBuffer: 0);

        _sut.Compute(bc, 110).Should().Be(100, "surplus=110 → ON");
        _sut.Compute(bc, 99).Should().Be(0, "surplus=99 < 100 → OFF immédiat (pas de zone morte)");
        _sut.Compute(bc, 100).Should().Be(100, "surplus=100 → ON");
    }

    [Test]
    [Description("Deux batteries indépendantes — états non partagés")]
    public void Idle_IndependentState_PerBattery()
    {
        var bc1 = new BatteryConfig { Id = 1, Name = "B1", IdleChargeW = 100, IdleStopBufferW = 30 };
        var bc2 = new BatteryConfig { Id = 2, Name = "B2", IdleChargeW = 100, IdleStopBufferW = 30 };

        _sut.Compute(bc1, 110); // B1 → ON
        _sut.Compute(bc2, 50);  // B2 → OFF

        _sut.IsIdle(1).Should().BeTrue("B1 est ON");
        _sut.IsIdle(2).Should().BeFalse("B2 est OFF indépendamment de B1");

        _sut.Compute(bc1, 60);  // B1 → OFF
        _sut.Compute(bc2, 120); // B2 → ON

        _sut.IsIdle(1).Should().BeFalse("B1 est passé OFF");
        _sut.IsIdle(2).Should().BeTrue("B2 est passé ON");
    }
}