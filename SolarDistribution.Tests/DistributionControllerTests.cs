using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using NUnit.Framework;
using SolarDistribution.Api.Models;

namespace SolarDistribution.Tests.Integration;

/// <summary>
/// Tests d'intégration — lance l'API complète en mémoire via <see cref="WebApplicationFactory{TEntryPoint}"/>.
/// Valide le pipeline HTTP de bout en bout (routing, validation, sérialisation JSON).
/// </summary>
[TestFixture]
public class DistributionControllerIntegrationTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _factory = new WebApplicationFactory<Program>();
        _client  = _factory.CreateClient();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static BatteryInputDto Battery(int id, double cap, double rate, double min,
        double pct, int prio) => new()
    {
        Id             = id,
        CapacityWh     = cap,
        MaxChargeRateW = rate,
        MinPercent     = min,
        CurrentPercent = pct,
        Priority       = prio
    };

    // ── Cas nominaux ─────────────────────────────────────────────────────────

    [Test]
    [Description("UC4 intégration — B1 urgente reçoit tout le surplus, B2+B3 = 0W")]
    public async Task Calculate_UC4_B1Urgent_ReturnsCorrectAllocation()
    {
        var request = new DistributionRequestDto
        {
            SurplusW  = 400,
            Batteries =
            [
                Battery(1, 1024, 500, 20, 18, 1),
                Battery(2, 1024, 500, 20, 50, 2),
                Battery(3, 2048, 500, 20, 50, 2),
            ]
        };

        var response = await _client.PostAsJsonAsync("/api/distribution/calculate", request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<DistributionResponseDto>();
        result.Should().NotBeNull();
        result!.UnusedSurplusW.Should().BeApproximately(0, 1.0);

        var b1 = result.Allocations.First(a => a.BatteryId == 1);
        b1.AllocatedW.Should().BeApproximately(400, 1.0);
        b1.WasUrgent.Should().BeTrue();
        b1.Reason.Should().Contain("URGENT");

        result.Allocations.First(a => a.BatteryId == 2).AllocatedW.Should().Be(0);
        result.Allocations.First(a => a.BatteryId == 3).AllocatedW.Should().Be(0);
    }

    [Test]
    [Description("UC1 intégration — distribution proportionnelle correcte")]
    public async Task Calculate_UC1_500W_AllAt50Pct_ReturnsProportionalSplit()
    {
        var request = new DistributionRequestDto
        {
            SurplusW  = 500,
            Batteries =
            [
                Battery(1, 1024, 500, 20, 50, 1),
                Battery(2, 1024, 500, 20, 50, 2),
                Battery(3, 2048, 500, 20, 50, 2),
            ]
        };

        var response = await _client.PostAsJsonAsync("/api/distribution/calculate", request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<DistributionResponseDto>();
        result!.TotalAllocatedW.Should().BeApproximately(500, 1.0);
        result.UnusedSurplusW.Should().BeApproximately(0, 1.0);
    }

    [Test]
    [Description("GET /examples retourne la liste des 5 use cases")]
    public async Task GetExamples_ReturnsAllUseCases()
    {
        var response = await _client.GetAsync("/api/distribution/examples");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("UC1");
        body.Should().Contain("UC5");
    }

    // ── Validation 400 ───────────────────────────────────────────────────────

    [Test]
    [Description("IDs de batteries dupliqués → 400 Bad Request")]
    public async Task Calculate_DuplicateBatteryIds_Returns400()
    {
        var request = new DistributionRequestDto
        {
            SurplusW  = 500,
            Batteries =
            [
                Battery(1, 1024, 500, 20, 50, 1),
                Battery(1, 1024, 500, 20, 50, 2), // ID dupliqué
            ]
        };

        var response = await _client.PostAsJsonAsync("/api/distribution/calculate", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Test]
    [Description("Surplus négatif → 400 Bad Request (Data Annotation [Range])")]
    public async Task Calculate_NegativeSurplus_Returns400()
    {
        var request = new DistributionRequestDto
        {
            SurplusW  = -100,
            Batteries = [ Battery(1, 1024, 500, 20, 50, 1) ]
        };

        var response = await _client.PostAsJsonAsync("/api/distribution/calculate", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Test]
    [Description("Liste de batteries vide → 400 Bad Request (Data Annotation [MinLength])")]
    public async Task Calculate_EmptyBatteries_Returns400()
    {
        var request = new DistributionRequestDto
        {
            SurplusW  = 500,
            Batteries = []
        };

        var response = await _client.PostAsJsonAsync("/api/distribution/calculate", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Test]
    [Description("SoftMaxPercent > HardMaxPercent → 400 Bad Request (validation métier)")]
    public async Task Calculate_SoftMaxExceedsHardMax_Returns400()
    {
        var request = new DistributionRequestDto
        {
            SurplusW  = 500,
            Batteries =
            [
                new BatteryInputDto
                {
                    Id = 1, CapacityWh = 1024, MaxChargeRateW = 500,
                    MinPercent = 20, CurrentPercent = 50, Priority = 1,
                    SoftMaxPercent = 95, HardMaxPercent = 80 // incohérent
                }
            ]
        };

        var response = await _client.PostAsJsonAsync("/api/distribution/calculate", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}

