using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using SolarDistribution.Api.Controllers;
using SolarDistribution.Api.Models;
using SolarDistribution.Core.Models;
using SolarDistribution.Core.Repositories;
using SolarDistribution.Core.Services;

namespace SolarDistribution.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="DistributionController"/> isolated from its service
/// using NSubstitute to mock <see cref="IBatteryDistributionService"/>.
///
/// Note: Calculate() is async since Fix #2 (removal of GetAwaiter().GetResult()).
/// Tests therefore use await and check actionResult.Result.
/// </summary>
[TestFixture]
public class DistributionControllerTests
{
    private IBatteryDistributionService _serviceMock = null!;
    private ILogger<DistributionController> _loggerMock = null!;
    private IDistributionRepository _repoMock = null!;
    private DistributionController _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _serviceMock = Substitute.For<IBatteryDistributionService>();
        _loggerMock  = Substitute.For<ILogger<DistributionController>>();
        _repoMock    = Substitute.For<IDistributionRepository>();
        _sut         = new DistributionController(_serviceMock, _repoMock, _loggerMock);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static BatteryInputDto Dto(int id, double pct = 50, int prio = 1,
        double softMax = 80, double hardMax = 100, double min = 20) => new()
    {
        Id             = id,
        CapacityWh     = 1024,
        MaxChargeRateW = 500,
        MinPercent     = min,
        SoftMaxPercent = softMax,
        HardMaxPercent = hardMax,
        CurrentPercent = pct,
        Priority       = prio
    };

    private static DistributionResult FakeResult(double surplus, double allocated, double unused,
        params (int id, double w)[] allocs) => new(
        SurplusInputW:   surplus,
        TotalAllocatedW: allocated,
        UnusedSurplusW:  unused,
        GridChargedW:    0,
        Allocations: allocs.Select(a => new BatteryChargeResult(
            BatteryId:       a.id,
            AllocatedW:      a.w,
            PreviousPercent: 50,
            NewPercent:      50 + a.w / 1024 * 100,
            WasUrgent:       false,
            Reason:          "Proportional share — surplus exhausted"
        )).ToList()
    );

    // ── Tests nominaux ───────────────────────────────────────────────────────

    [Test]
    [Description("Calculate returns 200 OK with result mapped from service")]
    public async Task Calculate_ValidRequest_Returns200WithMappedResult()
    {
        var request = new DistributionRequestDto
        {
            SurplusW  = 500,
            Batteries = new List<BatteryInputDto> { Dto(1, prio: 1), Dto(2, prio: 2) }
        };

        _serviceMock
            .Distribute(Arg.Is(500d), Arg.Any<IEnumerable<Battery>>())
            .Returns(FakeResult(500, 500, 0, (1, 307.2), (2, 192.8)));

        // Act — Calculate is async since Fix #2
        var actionResult = await _sut.Calculate(request);

        // Assert — ActionResult<T>.Result contains the effective IActionResult
        var ok       = actionResult.Result.Should().BeOfType<OkObjectResult>().Subject;
        var response = ok.Value.Should().BeOfType<DistributionResponseDto>().Subject;

        response.SurplusInputW.Should().Be(500);
        response.TotalAllocatedW.Should().Be(500);
        response.UnusedSurplusW.Should().Be(0);
        response.Allocations.Should().HaveCount(2);
        response.Allocations.First(a => a.BatteryId == 1).AllocatedW.Should().Be(307.2);
    }

    [Test]
    [Description("Calculate calls service with correct parameters (NSubstitute verification)")]
    public async Task Calculate_ValidRequest_CallsServiceWithCorrectSurplus()
    {
        var request = new DistributionRequestDto
        {
            SurplusW  = 1200,
            Batteries = new List<BatteryInputDto> { Dto(1), Dto(2) }
        };

        _serviceMock
            .Distribute(Arg.Any<double>(), Arg.Any<IEnumerable<Battery>>())
            .Returns(FakeResult(1200, 1200, 0, (1, 600), (2, 600)));

        await _sut.Calculate(request);

        _serviceMock.Received(1).Distribute(
            Arg.Is(1200d),
            Arg.Any<IEnumerable<Battery>>()
        );
    }

    [Test]
    [Description("Calculate correctly maps DTOs to Battery domain objects (Id, Capacity, Priority)")]
    public async Task Calculate_ValidRequest_MapsDtosToCorrectDomainBatteries()
    {
        var request = new DistributionRequestDto
        {
            SurplusW  = 300,
            Batteries = new List<BatteryInputDto>
            {
                new() { Id=1, CapacityWh=2048, MaxChargeRateW=1000,
                    MinPercent=15, SoftMaxPercent=85, HardMaxPercent=100,
                    CurrentPercent=30, Priority=1 }
            }
        };

        _serviceMock
            .Distribute(Arg.Any<double>(), Arg.Do<IEnumerable<Battery>>(batteries =>
            {
                var b = batteries.First();
                b.Id.Should().Be(1);
                b.CapacityWh.Should().Be(2048);
                b.MaxChargeRateW.Should().Be(1000);
                b.MinPercent.Should().Be(15);
                b.SoftMaxPercent.Should().Be(85);
                b.CurrentPercent.Should().Be(30);
                b.Priority.Should().Be(1);
            }))
            .Returns(FakeResult(300, 300, 0, (1, 300)));

        await _sut.Calculate(request);

        _serviceMock.Received(1).Distribute(Arg.Any<double>(), Arg.Any<IEnumerable<Battery>>());
    }

    [Test]
    [Description("Calculate returns service data with UnusedSurplus > 0 when batteries are full")]
    public async Task Calculate_AllBatteriesFull_ReturnsUnusedSurplus()
    {
        var request = new DistributionRequestDto
        {
            SurplusW  = 9999,
            Batteries = new List<BatteryInputDto> { Dto(1, pct: 100) }
        };

        _serviceMock
            .Distribute(Arg.Any<double>(), Arg.Any<IEnumerable<Battery>>())
            .Returns(FakeResult(9999, 0, 9999, (1, 0)));

        var actionResult = await _sut.Calculate(request);

        var ok       = actionResult.Result.Should().BeOfType<OkObjectResult>().Subject;
        var response = ok.Value.Should().BeOfType<DistributionResponseDto>().Subject;
        response.UnusedSurplusW.Should().Be(9999);
        response.TotalAllocatedW.Should().Be(0);
    }

    // ── Tests validation 400 ─────────────────────────────────────────────────

    [Test]
    [Description("Duplicate IDs → 400 Bad Request, service not called")]
    public async Task Calculate_DuplicateIds_Returns400_ServiceNotCalled()
    {
        var request = new DistributionRequestDto
        {
            SurplusW  = 500,
            Batteries = new List<BatteryInputDto> { Dto(1), Dto(1) }
        };

        var result = await _sut.Calculate(request);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        _serviceMock.DidNotReceive().Distribute(Arg.Any<double>(), Arg.Any<IEnumerable<Battery>>());
    }

    [Test]
    [Description("SoftMaxPercent > HardMaxPercent → 400 Bad Request, service not called")]
    public async Task Calculate_SoftMaxExceedsHardMax_Returns400_ServiceNotCalled()
    {
        var request = new DistributionRequestDto
        {
            SurplusW  = 500,
            Batteries = new List<BatteryInputDto> { Dto(1, softMax: 95, hardMax: 80) }
        };

        var result = await _sut.Calculate(request);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        _serviceMock.DidNotReceive().Distribute(Arg.Any<double>(), Arg.Any<IEnumerable<Battery>>());
    }

    [Test]
    [Description("MinPercent >= SoftMaxPercent → 400 Bad Request, service not called")]
    public async Task Calculate_MinPercentAboveSoftMax_Returns400_ServiceNotCalled()
    {
        var request = new DistributionRequestDto
        {
            SurplusW  = 500,
            Batteries = new List<BatteryInputDto> { Dto(1, min: 85, softMax: 80) }
        };

        var result = await _sut.Calculate(request);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        _serviceMock.DidNotReceive().Distribute(Arg.Any<double>(), Arg.Any<IEnumerable<Battery>>());
    }

    // ── Test GetExamples ─────────────────────────────────────────────────────

    [Test]
    [Description("GetExamples returns 200 OK with 5 entries")]
    public void GetExamples_Returns200_WithFiveEntries()
    {
        var result = _sut.GetExamples();

        var ok   = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var list = ok.Value.Should().BeAssignableTo<IEnumerable<object>>().Subject;
        list.Should().HaveCount(5);
    }
}
