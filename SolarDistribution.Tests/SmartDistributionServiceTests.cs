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
    [Description("Preventive HC charge is skipped when fleet reserve can hold until the first meaningful solar window")]
    public void Apply_FleetCanHoldUntilMeaningfulSolar_SkipsPreventiveSoftMaxCharge()
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
            "40% SOC still leaves enough reserve above the 15% emergency floor to cover 4h at 40W");
    }

    [Test]
    [Description("Preventive HC charge remains allowed when fleet reserve cannot hold until meaningful solar")]
    public void Apply_FleetCannotHoldUntilMeaningfulSolar_AllowsPreventiveCharge()
    {
        var battery = Battery(currentPercent: 40, softMax: 80, hardMax: 90, hardwareMinChargeW: 100);
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
            "40% SOC does not leave enough reserve above the 15% emergency floor to cover 4h at 80W");
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
        double hardwareMinChargeW) => new()
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