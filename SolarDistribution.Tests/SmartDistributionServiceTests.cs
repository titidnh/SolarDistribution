using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using SolarDistribution.Core.Models;
using SolarDistribution.Core.Repositories;
using SolarDistribution.Core.Services;
using SolarDistribution.Core.Services.ML;

namespace SolarDistribution.Tests.Unit;

[TestFixture]
public class SmartDistributionServiceTests
{
    private SmartDistributionService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _sut = new SmartDistributionService(
            new BatteryDistributionService(),
            Substitute.For<IDistributionMLService>(),
            Substitute.For<IWeatherService>(),
            Substitute.For<IDistributionRepository>(),
            new TariffEngine(new TariffConfig()),
            Substitute.For<IDistributionSessionFactory>(),
            Substitute.For<ILogger<SmartDistributionService>>());
    }

    [Test]
    [Description("End of HC slot: keep soft max when battery is already stable and meaningful solar is still expected later")]
    public void Apply_EndOfSlotStableBattery_WithMeaningfulSolar_DoesNotForceHardMax()
    {
        var battery = Battery(currentPercent: 79.9, softMax: 80, hardMax: 90, hardwareMinChargeW: 100);
        var tariff = Tariff(
            hoursRemainingInSlot: 0.9,
            hoursUntilSolar: 7.0,
            hasLowForecastTomorrow: true,
            eveningBoostPercent: 10,
            forecastNext3HoursWh: 41,
            solcastCurveWh: [0, 22, 19]);

        var effective = InvokeApply(new[] { battery }, tariff, estimatedConsumptionAverageW: 0);
        var updated = effective.Should().ContainSingle().Subject;

        updated.GridChargeAllowedW.Should().Be(0, "the battery can wait for meaningful solar instead of charging from the grid");
    }

    [Test]
    [Description("End of HC slot: still force hard max when battery is well below target and no meaningful solar is expected")]
    public void Apply_EndOfSlotLowBattery_NoMeaningfulSolar_StillForcesHardMax()
    {
        var battery = Battery(currentPercent: 60, softMax: 80, hardMax: 90, hardwareMinChargeW: 100);
        var tariff = Tariff(
            hoursRemainingInSlot: 0.9,
            hoursUntilSolar: double.MaxValue,
            hasLowForecastTomorrow: true,
            eveningBoostPercent: 10,
            forecastNext3HoursWh: 0,
            solcastCurveWh: [0, 0, 0]);

        var effective = InvokeApply(new[] { battery }, tariff, estimatedConsumptionAverageW: 80);
        var updated = effective.Should().ContainSingle().Subject;

        updated.SoftMaxPercent.Should().Be(90, "the end-of-slot hard-max protection must remain active when solar will not help");
        updated.GridChargeAllowedW.Should().BeGreaterThan(0);
    }

    [Test]
    [Description("Urgency window: SOC hysteresis still blocks micro grid charges when battery is already in the dead band")]
    public void Apply_UrgencyWindow_RespectsSocHysteresisDeadBand()
    {
        var battery = Battery(currentPercent: 79.9, softMax: 80, hardMax: 90, hardwareMinChargeW: 100);
        var tariff = Tariff(
            hoursRemainingInSlot: 0.9,
            hoursUntilSolar: double.MaxValue,
            hasLowForecastTomorrow: false,
            eveningBoostPercent: 10,
            forecastNext3HoursWh: 0,
            solcastCurveWh: [0, 0, 0]);

        var effective = InvokeApply(new[] { battery }, tariff, estimatedConsumptionAverageW: 120);
        var updated = effective.Should().ContainSingle().Subject;

        updated.GridChargeAllowedW.Should().Be(0, "the urgency shortcut must not override the SOC dead band near soft max");
    }

    [Test]
    [Description("Preventive HC charge is skipped when projected SOC stays above MinPercent at meaningful solar")]
    public void Apply_ProjectedSocStaysAboveMinPercent_SkipsPreventiveSoftMaxCharge()
    {
        var battery = Battery(currentPercent: 40, softMax: 80, hardMax: 90, hardwareMinChargeW: 100);
        var tariff = Tariff(
            hoursRemainingInSlot: 0.8,
            hoursUntilSolar: 4.0,
            hasLowForecastTomorrow: true,
            eveningBoostPercent: 10,
            forecastNext3HoursWh: 0,
            solcastCurveWh: [0, 0, 0]);

        var effective = InvokeApply(new[] { battery }, tariff, estimatedConsumptionAverageW: 40);
        var updated = effective.Should().ContainSingle().Subject;

        updated.GridChargeAllowedW.Should().Be(0,
            "with self-discharge at 0%/h, 40% SOC is still above the 20% floor when solar returns");
        updated.IsPreventiveGridChargeSkippedUntilSolar.Should().BeTrue();
    }

    [Test]
    [Description("Preventive HC charge becomes allowed when self-discharge pushes projected SOC below MinPercent")]
    public void Apply_ProjectedSocBelowMinPercent_AllowsPreventiveCharge()
    {
        var battery = Battery(
            currentPercent: 22,
            softMax: 80,
            hardMax: 90,
            hardwareMinChargeW: 100,
            selfDischargePercentPerHour: 1.0);
        var tariff = Tariff(
            hoursRemainingInSlot: 0.8,
            hoursUntilSolar: 4.0,
            hasLowForecastTomorrow: true,
            eveningBoostPercent: 10,
            forecastNext3HoursWh: 0,
            solcastCurveWh: [0, 0, 0]);

        var effective = InvokeApply(new[] { battery }, tariff, estimatedConsumptionAverageW: 80);
        var updated = effective.Should().ContainSingle().Subject;

        updated.GridChargeAllowedW.Should().BeGreaterThan(0,
            "22% SOC with 1%/h self-discharge drops below the 20% floor before solar returns");
        updated.IsPreventiveGridChargeSkippedUntilSolar.Should().BeFalse();
    }

    [Test]
    [Description("Optional empty-before-solar mode skips preventive HC charge while projected SOC stays above 0%")]
    public void Apply_EmptyBeforeSolarMode_WithProjectedPercentRemaining_SkipsPreventiveCharge()
    {
        var battery = Battery(
            currentPercent: 5,
            softMax: 80,
            hardMax: 90,
            hardwareMinChargeW: 100,
            preventiveChargeOnlyIfEmptyBeforeSolar: true);
        var tariff = Tariff(
            hoursRemainingInSlot: 0.8,
            hoursUntilSolar: 4.0,
            hasLowForecastTomorrow: true,
            eveningBoostPercent: 10,
            forecastNext3HoursWh: 0,
            solcastCurveWh: [0, 0, 0]);

        var effective = InvokeApply(new[] { battery }, tariff, estimatedConsumptionAverageW: 80);
        var updated = effective.Should().ContainSingle().Subject;

        updated.GridChargeAllowedW.Should().Be(0,
            "the optional mode must not charge from HC while some projected SOC still remains before solar");
        updated.PreventiveChargeFloorPercent.Should().Be(0);
        updated.IsPreventiveGridChargeSkippedUntilSolar.Should().BeTrue();
    }

    [Test]
    [Description("Optional empty-before-solar mode allows preventive HC charge once projected SOC reaches 0%")]
    public void Apply_EmptyBeforeSolarMode_WhenProjectedEmpty_AllowsPreventiveCharge()
    {
        var battery = Battery(
            currentPercent: 5,
            softMax: 80,
            hardMax: 90,
            hardwareMinChargeW: 100,
            selfDischargePercentPerHour: 1.0,
            preventiveChargeOnlyIfEmptyBeforeSolar: true);
        var tariff = Tariff(
            hoursRemainingInSlot: 0.8,
            hoursUntilSolar: 6.0,
            hasLowForecastTomorrow: true,
            eveningBoostPercent: 10,
            forecastNext3HoursWh: 0,
            solcastCurveWh: [0, 0, 0]);

        var effective = InvokeApply(new[] { battery }, tariff, estimatedConsumptionAverageW: 80);
        var updated = effective.Should().ContainSingle().Subject;

        updated.GridChargeAllowedW.Should().BeGreaterThan(0,
            "once the projected SOC reaches 0% before solar, the battery should use HC preventively");
        updated.IsPreventiveGridChargeSkippedUntilSolar.Should().BeFalse();
    }

    private IList<Battery> InvokeApply(
        IList<Battery> batteries,
        TariffContext tariff,
        double? estimatedConsumptionAverageW = null)
    {
        var method = typeof(SmartDistributionService).GetMethod(
            "Apply",
            BindingFlags.Instance | BindingFlags.NonPublic);

        method.Should().NotBeNull();

        var result = method!.Invoke(_sut, new object?[] { batteries, null, tariff, 0d, null, estimatedConsumptionAverageW, 100d, 1d });
        return result.Should().BeAssignableTo<IList<Battery>>().Subject;
    }

    private static Battery Battery(
        double currentPercent,
        double softMax,
        double hardMax,
        double hardwareMinChargeW,
        double selfDischargePercentPerHour = 0,
        bool preventiveChargeOnlyIfEmptyBeforeSolar = false) => new()
    {
        Id = 1,
        CapacityWh = 1024,
        MaxChargeRateW = 1000,
        MinPercent = 20,
        SoftMaxPercent = softMax,
        HardMaxPercent = hardMax,
        CurrentPercent = currentPercent,
        Priority = 1,
        HardwareMinChargeW = hardwareMinChargeW,
        SelfDischargePercentPerHour = selfDischargePercentPerHour,
        PreventiveChargeOnlyIfEmptyBeforeSolar = preventiveChargeOnlyIfEmptyBeforeSolar,
        SocHysteresisPercent = 2.0,
        EmergencyGridChargeBelowPercent = 20,
        EmergencyGridChargeTargetPercent = 50,
    };

    private static TariffContext Tariff(
        double? hoursRemainingInSlot,
        double? hoursUntilSolar,
        bool hasLowForecastTomorrow,
        double eveningBoostPercent,
        double? forecastNext3HoursWh,
        double[] solcastCurveWh) => new(
        ActiveSlotName: "HC Nuit",
        CurrentPricePerKwh: 0.1057,
        IsFavorableForGrid: true,
        GridChargeAllowed: true,
        AvgSolarForecastWm2: 0,
        SolarExpectedSoon: false,
        SolarExpectedFromHa: false,
        HoursToNextFavorable: null,
        MaxSavingsPerKwh: 0.007,
        ExportPricePerKwh: 0.08,
        HoursRemainingInSlot: hoursRemainingInSlot,
        HoursUntilSolar: hoursUntilSolar,
        SolarForecastWm2: new double[12],
        ForecastTodayWh: 12598,
        ForecastTomorrowWh: hasLowForecastTomorrow ? 0 : 5000,
        HasLowForecastTomorrow: hasLowForecastTomorrow,
        EveningBoostPercent: eveningBoostPercent,
        LazyBufferHours: 0.5,
        EstimatedConsumptionNextHoursWh: null,
        ForecastThisHourWh: solcastCurveWh.Length > 0 ? solcastCurveWh[0] : null,
        ForecastNextHourWh: solcastCurveWh.Length > 1 ? solcastCurveWh[1] : null,
        ForecastNext3HoursWh: forecastNext3HoursWh,
        ForecastRemainingTodayWh: 12597,
        SolcastHourlyCurveWh: solcastCurveWh,
        EnergyDeficitTodayWh: -12597,
        GridChargeBlockedBySolarSufficiency: false,
        IsDynamicTariff: false,
        SpotPricePerKwh: null,
        DynamicThresholdPerKwh: null);
}