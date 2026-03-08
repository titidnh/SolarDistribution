using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NUnit.Framework;
using SolarDistribution.Api.Controllers;
using SolarDistribution.Api.Models;
using SolarDistribution.Core.Models;
using SolarDistribution.Core.Services;

namespace SolarDistribution.Tests.Unit;

/// <summary>
/// Tests unitaires du <see cref="DistributionController"/> isolé de son service
/// grâce à NSubstitute pour mocker <see cref="IBatteryDistributionService"/>.
///
/// Ces tests vérifient :
///   - Le comportement HTTP du controller (200/400)
///   - Le mapping DTO ↔ domain
///   - La validation métier supplémentaire (IDs dupliqués, cohérence softMax/hardMax)
///   - Le comportement du controller face aux réponses du service (via mock)
/// </summary>
[TestFixture]
public class DistributionControllerTests
{
    private IBatteryDistributionService _serviceMock = null!;
    private ILogger<DistributionController> _loggerMock = null!;
    private DistributionController _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _serviceMock = Substitute.For<IBatteryDistributionService>();
        _loggerMock  = Substitute.For<ILogger<DistributionController>>();
        _sut         = new DistributionController(_serviceMock, _loggerMock);
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
    [Description("Calculate retourne 200 OK avec le résultat mappé depuis le service")]
    public void Calculate_ValidRequest_Returns200WithMappedResult()
    {
        // Arrange
        var request = new DistributionRequestDto
        {
            SurplusW  = 500,
            Batteries = new List<BatteryInputDto> { Dto(1, prio: 1), Dto(2, prio: 2) }
        };

        var fakeResult = FakeResult(500, 500, 0, (1, 307.2), (2, 192.8));

        _serviceMock
            .Distribute(Arg.Is(500d), Arg.Any<IEnumerable<Battery>>())
            .Returns(fakeResult);

        // Act
        var actionResult = _sut.Calculate(request);

        // Assert
        actionResult.Result.Should().BeOfType<OkObjectResult>();
        var ok = (OkObjectResult)actionResult.Result!;
        var response = ok.Value.Should().BeOfType<DistributionResponseDto>().Subject;

        response.SurplusInputW.Should().Be(500);
        response.TotalAllocatedW.Should().Be(500);
        response.UnusedSurplusW.Should().Be(0);
        response.Allocations.Should().HaveCount(2);
        response.Allocations.First(a => a.BatteryId == 1).AllocatedW.Should().Be(307.2);
    }

    [Test]
    [Description("Calculate appelle le service avec les bons paramètres (vérification NSubstitute)")]
    public void Calculate_ValidRequest_CallsServiceWithCorrectSurplus()
    {
        // Arrange
        var request = new DistributionRequestDto
        {
            SurplusW  = 1200,
            Batteries = new List<BatteryInputDto> { Dto(1), Dto(2) }
        };

        _serviceMock
            .Distribute(Arg.Any<double>(), Arg.Any<IEnumerable<Battery>>())
            .Returns(FakeResult(1200, 1200, 0, (1, 600), (2, 600)));

        // Act
        _sut.Calculate(request);

        // Assert — NSubstitute vérifie que Distribute a été appelé exactement 1 fois avec 1200W
        _serviceMock.Received(1).Distribute(
            Arg.Is(1200d),
            Arg.Any<IEnumerable<Battery>>()
        );
    }

    [Test]
    [Description("Calculate mappe correctement les DTOs vers les domaines Battery (Id, Capacity, Priority)")]
    public void Calculate_ValidRequest_MapsDtosToCorrectDomainBatteries()
    {
        // Arrange
        var request = new DistributionRequestDto
        {
            SurplusW  = 300,
            Batteries = new List<BatteryInputDto>
            {
                new BatteryInputDto { Id=1, CapacityWh=2048, MaxChargeRateW=1000,
                    MinPercent=15, SoftMaxPercent=85, HardMaxPercent=100,
                    CurrentPercent=30, Priority=1 }
            }
        };

        _serviceMock
            .Distribute(Arg.Any<double>(), Arg.Do<IEnumerable<Battery>>(batteries =>
            {
                // Capture et vérifie le domaine mappé
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

        // Act
        _sut.Calculate(request);

        // Assert — la capture ci-dessus valide le mapping, on vérifie juste que le service a été appelé
        _serviceMock.Received(1).Distribute(Arg.Any<double>(), Arg.Any<IEnumerable<Battery>>());
    }

    [Test]
    [Description("Calculate retourne les données du service avec UnusedSurplus > 0 quand batteries pleines")]
    public void Calculate_AllBatteriesFull_ReturnsUnusedSurplus()
    {
        // Arrange
        var request = new DistributionRequestDto
        {
            SurplusW  = 9999,
            Batteries = new List<BatteryInputDto> { Dto(1, pct: 100) }
        };

        _serviceMock
            .Distribute(Arg.Any<double>(), Arg.Any<IEnumerable<Battery>>())
            .Returns(FakeResult(9999, 0, 9999, (1, 0)));

        // Act
        var actionResult = _sut.Calculate(request);

        // Assert
        var ok       = actionResult.Result.Should().BeOfType<OkObjectResult>().Subject;
        var response = ok.Value.Should().BeOfType<DistributionResponseDto>().Subject;
        response.UnusedSurplusW.Should().Be(9999);
        response.TotalAllocatedW.Should().Be(0);
    }

    // ── Tests validation 400 ─────────────────────────────────────────────────

    [Test]
    [Description("IDs dupliqués → 400 Bad Request, service non appelé")]
    public void Calculate_DuplicateIds_Returns400_ServiceNotCalled()
    {
        var request = new DistributionRequestDto
        {
            SurplusW  = 500,
            Batteries = new List<BatteryInputDto> { Dto(1), Dto(1) } // doublon
        };

        var result = _sut.Calculate(request);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        _serviceMock.DidNotReceive().Distribute(Arg.Any<double>(), Arg.Any<IEnumerable<Battery>>());
    }

    [Test]
    [Description("SoftMaxPercent > HardMaxPercent → 400 Bad Request, service non appelé")]
    public void Calculate_SoftMaxExceedsHardMax_Returns400_ServiceNotCalled()
    {
        var request = new DistributionRequestDto
        {
            SurplusW  = 500,
            Batteries = new List<BatteryInputDto> { Dto(1, softMax: 95, hardMax: 80) } // incohérent
        };

        var result = _sut.Calculate(request);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        _serviceMock.DidNotReceive().Distribute(Arg.Any<double>(), Arg.Any<IEnumerable<Battery>>());
    }

    [Test]
    [Description("MinPercent >= SoftMaxPercent → 400 Bad Request, service non appelé")]
    public void Calculate_MinPercentAboveSoftMax_Returns400_ServiceNotCalled()
    {
        var request = new DistributionRequestDto
        {
            SurplusW  = 500,
            Batteries = [ Dto(1, min: 85, softMax: 80) ] // min > softMax
        };

        var result = _sut.Calculate(request);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        _serviceMock.DidNotReceive().Distribute(Arg.Any<double>(), Arg.Any<IEnumerable<Battery>>());
    }

    // ── Test GetExamples ─────────────────────────────────────────────────────

    [Test]
    [Description("GetExamples retourne 200 OK avec 5 entrées")]
    public void GetExamples_Returns200_WithFiveEntries()
    {
        var result = _sut.GetExamples();

        result.Result.Should().BeOfType<OkObjectResult>();
        var ok   = (OkObjectResult)result.Result!;
        var list = ok.Value.Should().BeAssignableTo<IEnumerable<object>>().Subject;
        list.Should().HaveCount(5);
    }
}
