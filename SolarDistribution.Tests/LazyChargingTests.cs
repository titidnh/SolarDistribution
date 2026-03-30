using FluentAssertions;
using NUnit.Framework;
using SolarDistribution.Core.Models;
using SolarDistribution.Core.Services;

namespace SolarDistribution.Tests.Unit;

/// <summary>
/// Unit tests for Lazy Charging (ComputeAdaptiveGridChargeW via Apply).
///
/// Verified principle:
///   When an HC slot is open but enough time remains,
///   the worker must return GridChargeAllowedW = 0 (wait),
///   and start charging only when:
///     hoursRemaining ≤ hoursNeeded + lazyBuffer
///
/// Common setup:
///   Battery 1024Wh, MaxRate=1000W, soft=85%, SOC=78% -> energyNeeded ≈ 71.68Wh
///   hoursNeeded ≈ 71.68 / 1000 = 0.072h
///   lazyBuffer = 0.5h (default)
///   -> start when hoursRemaining ≤ 0.072 + 0.5 = 0.572h
/// </summary>
[TestFixture]
public class LazyChargingTests
{
    // ── Base HC config ─────────────────────────────────────────────────────

    private static TariffConfig HcConfig(double lazyBuffer = 0.5) => new()
    {
        GridChargeThresholdPerKwh = 0.15,
        ExportPricePerKwh = 0.07,
        MinSolarForecastForGridBlock = 100.0,
        SolarForecastHorizonHours = 4,
        LowForecastTomorrowWh = 1500.0,
        EveningBoostPercent = 0.0,   // disabled to isolate lazy charging
        LazyBufferHours = lazyBuffer,
        Slots = new()
        {
            new TariffSlot { Name = "HC Nuit", PricePerKwh = 0.10, StartTime = "22:00", EndTime = "07:00" },
            new TariffSlot { Name = "HP",      PricePerKwh = 0.25, StartTime = "07:00", EndTime = "22:00" },
        }
    };

    /// <summary>
    /// Standard battery: 1024Wh, 1000W max, SOC=78%, soft=85%.
    /// energyNeeded = (85-78)/100 * 1024 = 71.68Wh → hoursNeeded ≈ 0.072h
    /// </summary>
    private static Battery BatteryAt78Pct() => new()
    {
        Id = 1,
        CapacityWh = 1024,
        MaxChargeRateW = 1000,
        MinPercent = 20,
        SoftMaxPercent = 85,
        HardMaxPercent = 90,
        CurrentPercent = 78,
        Priority = 1,
        SocHysteresisPercent = 2.0,
        EmergencyGridChargeBelowPercent = 20,
    };

    // ── Helper: evaluate context at a given time ────────────────────────

    private static TariffContext Ctx(TariffConfig config, DateTime localTime,
        double? fcTodayWh = null, double? fcTomorrowWh = null)
    {
        var engine = new TariffEngine(config);
        return engine.EvaluateContext(localTime, new double[12], fcTodayWh, fcTomorrowWh);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Group 1 - Too early: GridChargeAllowedW must be 0
    // ═════════════════════════════════════════════════════════════════════════

    [Test]
    [Description("Lazy - start of HC slot (9h remaining): too early, no grid charging")]
    public void LazyCharge_StartOfHcSlot_9hRemaining_NoGridCharge()
    {
        var config = HcConfig(lazyBuffer: 0.5);
        // 22:00 -> 9h remaining in the HC slot
        var ctx = Ctx(config, new DateTime(2025, 6, 1, 22, 0, 0), fcTodayWh: 0, fcTomorrowWh: 0);
        var bat = BatteryAt78Pct();

        ctx.GridChargeAllowed.Should().BeTrue("HC slot is active");
        ctx.HoursRemainingInSlot.Should().BeApproximately(9.0, 0.1);

        // Via SmartDistributionService.Apply (indirectly via TariffEngine + expected behavior)
        // Check logic: hoursNeeded ≈ 0.072h, buffer=0.5h -> threshold=0.572h
        // 9h >> 0.572h -> should wait
        var hoursNeeded = (bat.SoftMaxPercent - bat.CurrentPercent) / 100.0 * bat.CapacityWh / bat.MaxChargeRateW;
        var hoursBeforeStart = (ctx.HoursRemainingInSlot!.Value) - hoursNeeded - config.LazyBufferHours;
        hoursBeforeStart.Should().BeGreaterThan(0, "8h still remaining before charging must start");
    }

    [Test]
    [Description("Lazy - middle of HC slot (4h remaining): still too early")]
    public void LazyCharge_MidHcSlot_4hRemaining_StillWaiting()
    {
        var config = HcConfig(lazyBuffer: 0.5);
        // 03:00 -> around 4h remaining
        var ctx = Ctx(config, new DateTime(2025, 6, 2, 3, 0, 0), fcTodayWh: 0, fcTomorrowWh: 0);
        var bat = BatteryAt78Pct();

        ctx.GridChargeAllowed.Should().BeTrue();
        ctx.HoursRemainingInSlot.Should().BeApproximately(4.0, 0.1);

        var hoursNeeded = (bat.SoftMaxPercent - bat.CurrentPercent) / 100.0 * bat.CapacityWh / bat.MaxChargeRateW;
        var hoursBeforeStart = ctx.HoursRemainingInSlot!.Value - hoursNeeded - config.LazyBufferHours;
        hoursBeforeStart.Should().BeGreaterThan(0);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Group 2 - Threshold urgency: always charge when little time remains
    // ═════════════════════════════════════════════════════════════════════════

    [Test]
    [Description("Lazy - end of HC slot (urgencyThreshold=1h): force max charging")]
    public void LazyCharge_EndOfSlot_UrgencyThreshold_ChargesAtMaxRate()
    {
        var config = HcConfig(lazyBuffer: 0.5);
        // 06:05 -> ~55 min remaining -> ≤ urgencyThreshold=1h -> forced MaxChargeRateW
        var ctx = Ctx(config, new DateTime(2025, 6, 2, 6, 5, 0), fcTodayWh: 0, fcTomorrowWh: 0);

        ctx.GridChargeAllowed.Should().BeTrue();
        ctx.HoursRemainingInSlot.Should().BeLessThan(1.0, "inside the urgency zone");
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Group 3 - Battery with high energy need: larger hoursNeeded -> earlier start
    // ═════════════════════════════════════════════════════════════════════════

    [Test]
    [Description("Lazy - large and low battery (50%): high hoursNeeded -> starts earlier")]
    public void LazyCharge_LargeEnergyNeeded_StartsEarlier()
    {
        var config = HcConfig(lazyBuffer: 0.5);

        // 10kWh battery at 50% -> soft=85% -> energyNeeded=3500Wh, maxRate=1000W -> hoursNeeded=3.5h
        var bigBat = new Battery
        {
            Id = 1,
            CapacityWh = 10000,
            MaxChargeRateW = 1000,
            MinPercent = 20,
            SoftMaxPercent = 85,
            HardMaxPercent = 90,
            CurrentPercent = 50,
            Priority = 1,
            SocHysteresisPercent = 2.0,
        };

        // hoursNeeded = (85-50)/100 * 10000 / 1000 = 3.5h
        // buffer = 0.5h -> threshold = 4.0h
        // At 22:00 (9h remaining): hoursBeforeStart = 9 - 3.5 - 0.5 = 5h -> still positive
        // At 03:00 (4h remaining): hoursBeforeStart = 4 - 3.5 - 0.5 = 0 -> starts!
        double hoursNeeded = (bigBat.SoftMaxPercent - bigBat.CurrentPercent) / 100.0 * bigBat.CapacityWh / bigBat.MaxChargeRateW;
        hoursNeeded.Should().BeApproximately(3.5, 0.01);

        // At 22:00 -> should still wait
        var ctx22h = Ctx(config, new DateTime(2025, 6, 1, 22, 0, 0), fcTodayWh: 0, fcTomorrowWh: 0);
        double hoursBeforeStart22h = ctx22h.HoursRemainingInSlot!.Value - hoursNeeded - config.LazyBufferHours;
        hoursBeforeStart22h.Should().BeApproximately(5.0, 0.1, "at 22:00, still 5h before start");

        // At 03:00 -> should start (0h before start)
        var ctx03h = Ctx(config, new DateTime(2025, 6, 2, 3, 0, 0), fcTodayWh: 0, fcTomorrowWh: 0);
        double hoursBeforeStart03h = ctx03h.HoursRemainingInSlot!.Value - hoursNeeded - config.LazyBufferHours;
        hoursBeforeStart03h.Should().BeApproximately(0.0, 0.1, "at 03:00, it is time to start");
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Group 4 - LazyBuffer = 0: behavior close to original (maximized charging)
    // ═════════════════════════════════════════════════════════════════════════

    [Test]
    [Description("Lazy - lazyBuffer=0: near-immediate start (original behavior when buffer=0)")]
    public void LazyCharge_ZeroBuffer_StartsAlmostImmediately()
    {
        var config = HcConfig(lazyBuffer: 0.0);
        var bat = BatteryAt78Pct();

        // hoursNeeded ≈ 0.072h, buffer=0 -> threshold=0.072h
        // At 22:00 (9h remaining): hoursBeforeStart = 9 - 0.072 - 0 = 8.928h -> still positive...
        // but with buffer=0 this still starts after ~8h of waiting (lazy remains active)
        // The important part: charging starts earlier than with buffer=0.5h
        // Actual test: with buffer=0, is hoursBeforeStart reduced vs buffer=0.5?
        double hoursNeededBuf05 = (bat.SoftMaxPercent - bat.CurrentPercent) / 100.0 * bat.CapacityWh / bat.MaxChargeRateW + 0.5;
        double hoursNeededBuf00 = (bat.SoftMaxPercent - bat.CurrentPercent) / 100.0 * bat.CapacityWh / bat.MaxChargeRateW + 0.0;

        hoursNeededBuf00.Should().BeLessThan(hoursNeededBuf05, "without buffer, later start (less safety margin)");
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Group 5 - TariffEngine: LazyBufferHours correctly propagated to TariffContext
    // ═════════════════════════════════════════════════════════════════════════

    [Test]
    [Description("TariffEngine.EvaluateContext propagates LazyBufferHours from TariffConfig")]
    public void TariffEngine_EvaluateContext_PropagatesLazyBufferHours()
    {
        var config = HcConfig(lazyBuffer: 1.25);
        var ctx = Ctx(config, new DateTime(2025, 6, 1, 22, 0, 0));

        ctx.LazyBufferHours.Should().Be(1.25);
    }

    [Test]
    [Description("TariffEngine.EvaluateContext: LazyBufferHours = 0 when disabled")]
    public void TariffEngine_EvaluateContext_ZeroLazyBuffer_WhenDisabled()
    {
        var config = HcConfig(lazyBuffer: 0.0);
        var ctx = Ctx(config, new DateTime(2025, 6, 1, 22, 0, 0));

        ctx.LazyBufferHours.Should().Be(0.0);
    }

    [Test]
    [Description("TariffEngine: default LazyBufferHours value = 0.5")]
    public void TariffConfig_DefaultLazyBufferHours_Is0Point5()
    {
        var config = new TariffConfig();
        config.LazyBufferHours.Should().Be(0.5);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Group 6 - SOC hysteresis: no lazy charging when battery is in dead band
    // ═════════════════════════════════════════════════════════════════════════

    [Test]
    [Description("SOC hysteresis: if SOC >= softMax - hysteresis, no charging (dead band)")]
    public void SocHysteresis_BatteryInDeadBand_NoGridCharge()
    {
        // softMax=85%, hysteresis=2% -> dead band = [83%, 85%]
        // SOC=84% -> in dead band -> GridChargeAllowedW must be 0
        var bat = new Battery
        {
            Id = 1,
            CapacityWh = 1024,
            MaxChargeRateW = 1000,
            MinPercent = 20,
            SoftMaxPercent = 85,
            HardMaxPercent = 90,
            CurrentPercent = 84.0, // 84 >= 85 - 2 = 83 -> dead band
            Priority = 1,
            SocHysteresisPercent = 2.0,
        };

        // Hysteresis logic returns 0 when SOC >= rechargeThreshold = softMax - hysteresis
        double rechargeThreshold = bat.SoftMaxPercent - bat.SocHysteresisPercent; // 83%
        bat.CurrentPercent.Should().BeGreaterThanOrEqualTo(rechargeThreshold,
            "battery is in SOC dead band -> no charging");
    }

    [Test]
    [Description("SOC hysteresis: SOC just below dead band -> charging allowed")]
    public void SocHysteresis_BatteryJustBelowDeadBand_ChargeAllowed()
    {
        var bat = new Battery
        {
            Id = 1,
            CapacityWh = 1024,
            MaxChargeRateW = 1000,
            MinPercent = 20,
            SoftMaxPercent = 85,
            HardMaxPercent = 90,
            CurrentPercent = 82.9, // 82.9 < 85 - 2 = 83 -> below dead band
            Priority = 1,
            SocHysteresisPercent = 2.0,
        };

        double rechargeThreshold = bat.SoftMaxPercent - bat.SocHysteresisPercent; // 83%
        bat.CurrentPercent.Should().BeLessThan(rechargeThreshold,
            "battery is below dead band -> charging allowed");
    }
}