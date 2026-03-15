using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using SolarDistribution.Core.Repositories;
using SolarDistribution.Worker.Configuration;
using SolarDistribution.Worker.HA;

namespace SolarDistribution.Tests.Unit;

[TestFixture]
public class HomeAssistantDataReaderTests
{
    [Test]
    public async System.Threading.Tasks.Task ReadAllAsync_ReturnsNull_WhenSurplusUnreadable()
    {
        var client = Substitute.For<IHomeAssistantClient>();
        var config = new SolarConfig();

        client.GetNumericStateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((double?)null);

        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IDistributionRepository>());
        var provider = services.BuildServiceProvider();

        var reader = new HomeAssistantDataReader(client, config, provider.GetRequiredService<IServiceScopeFactory>(),
            new SolarDistribution.Core.Services.TariffEngine(new SolarDistribution.Core.Services.TariffConfig()),
            Substitute.For<ILogger<HomeAssistantDataReader>>());

        var snapshot = await reader.ReadAllAsync();
        snapshot.Should().BeNull();
    }

    [Test]
    public async System.Threading.Tasks.Task ReadAllAsync_ComputesSurplusAndParsesForecasts_AndReadsBatteriesAndZones()
    {
        var client = Substitute.For<IHomeAssistantClient>();

        var config = new SolarConfig();
        config.Solar.SurplusEntity = "sensor.surplus";
        config.Solar.SurplusMode = "p1_invert"; // negative values inverted
        config.Solar.ForecastTodayEntity = "sensor.fc_today";
        config.Solar.ForecastRemainingTodayEntity = "sensor.fc_remain";
        config.Solar.ZoneConsumptionEntities = new List<string> { "sensor.zone.a", "sensor.zone.b" };

        // Battery config
        var bat = new BatteryConfig { Id = 1, Name = "Bat1" };
        bat.Entities.Soc = "sensor.bat1_soc";
        bat.Entities.MaxChargeRateEntity = "sensor.bat1_max";
        bat.Entities.CurrentChargePowerEntity = "sensor.bat1_now";
        bat.Entities.CycleCountEntity = "sensor.bat1_cycles";
        config.Batteries = new List<BatteryConfig> { bat };

        // Setup HA client returns
        // Surplus raw is -400 (p1_invert => 400)
        client.GetNumericStateAsync("sensor.surplus", Arg.Any<CancellationToken>()).Returns(-400.0);

        // Zone consumption
        client.GetNumericStateAsync("sensor.zone.a", Arg.Any<CancellationToken>()).Returns(100.0);
        client.GetNumericStateAsync("sensor.zone.b", Arg.Any<CancellationToken>()).Returns(50.0);

        // Battery readings
        client.GetNumericStateAsync("sensor.bat1_soc", Arg.Any<CancellationToken>()).Returns(55.0);
        client.GetNumericStateAsync("sensor.bat1_max", Arg.Any<CancellationToken>()).Returns(600.0);
        client.GetNumericStateAsync("sensor.bat1_now", Arg.Any<CancellationToken>()).Returns(200.0);
        client.GetNumericStateAsync("sensor.bat1_cycles", Arg.Any<CancellationToken>()).Returns(1234.0);

        // Forecasts: Today returns 1.5 kWh => 1500 Wh, remaining_today returns 0.8 kWh => 800 Wh
        client.GetNumericStateAsync("sensor.fc_today", Arg.Any<CancellationToken>()).Returns(1.5);
        client.GetNumericStateAsync("sensor.fc_remain", Arg.Any<CancellationToken>()).Returns(0.8);

        // Prepare DI scope with a dummy repo (rolling window disabled so repo not used)
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IDistributionRepository>());
        var provider = services.BuildServiceProvider();

        var tariffEngine = new SolarDistribution.Core.Services.TariffEngine(new SolarDistribution.Core.Services.TariffConfig());

        var reader = new HomeAssistantDataReader(client, config, provider.GetRequiredService<IServiceScopeFactory>(),
            tariffEngine, Substitute.For<ILogger<HomeAssistantDataReader>>());

        var snapshot = await reader.ReadAllAsync();
        snapshot.Should().NotBeNull();

        snapshot!.SurplusW.Should().BeApproximately(400.0, 0.001);
        snapshot.ForecastTodayWh.Should().BeApproximately(1500.0, 0.1);
        snapshot.ForecastRemainingTodayWh.Should().BeApproximately(800.0, 0.1);

        // Zone consumption aggregated
        snapshot.ConsumptionW.Should().BeApproximately(150.0, 0.001);
        snapshot.ZoneConsumptionW.Should().ContainKey("sensor.zone.a");
        snapshot.ZoneConsumptionW["sensor.zone.a"].Should().BeApproximately(100.0, 0.001);

        // Battery reading checks
        snapshot.Batteries.Should().HaveCount(1);
        var b = snapshot.Batteries.First();
        b.ReadSuccess.Should().BeTrue();
        b.SocPercent.Should().BeApproximately(55.0, 0.001);
        b.MaxChargeRateW.Should().BeApproximately(600.0, 0.001);
        b.CurrentChargeW.Should().BeApproximately(200.0, 0.001);
    }
}
