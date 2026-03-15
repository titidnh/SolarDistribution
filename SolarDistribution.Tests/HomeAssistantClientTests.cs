using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using SolarDistribution.Worker.HA;

namespace SolarDistribution.Tests.Unit;

public class FakeHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
    public FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(_responder(request));
}

[TestFixture]
public class HomeAssistantClientTests
{
    [Test]
    public async Task GetStateAsync_Parses_HaState()
    {
        var haState = new
        {
            entity_id = "sensor.test",
            state = "12.34",
            attributes = new { },
            last_updated = "2025-01-01T00:00:00Z"
        };

        var handler = new FakeHttpHandler(req =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(haState)
            });

        var client = new HttpClient(handler) { BaseAddress = new System.Uri("http://ha/") };
        var svc = new HomeAssistantClient(client, NullLogger<HomeAssistantClient>.Instance);

        var state = await svc.GetStateAsync("sensor.test");

        state.Should().NotBeNull();
        state!.EntityId.Should().Be("sensor.test");
        state.State.Should().Be("12.34");
    }

    [Test]
    public async Task GetNumericStateAsync_Returns_Number_When_State_Is_Numeric()
    {
        var haState = new { entity_id = "sensor.n", state = "42.5", attributes = new { }, last_updated = "2025-01-01T00:00:00Z" };
        var handler = new FakeHttpHandler(req => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(haState) });
        var client = new HttpClient(handler) { BaseAddress = new System.Uri("http://ha/") };
        var svc = new HomeAssistantClient(client, NullLogger<HomeAssistantClient>.Instance);

        var val = await svc.GetNumericStateAsync("sensor.n");
        val.Should().BeApproximately(42.5, 1e-6);
    }

    [Test]
    public async Task GetNumericStateAsync_Returns_Null_When_State_Not_Numeric()
    {
        var haState = new { entity_id = "sensor.x", state = "unknown", attributes = new { }, last_updated = "2025-01-01T00:00:00Z" };
        var handler = new FakeHttpHandler(req => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(haState) });
        var client = new HttpClient(handler) { BaseAddress = new System.Uri("http://ha/") };
        var svc = new HomeAssistantClient(client, NullLogger<HomeAssistantClient>.Instance);

        var val = await svc.GetNumericStateAsync("sensor.x");
        val.Should().BeNull();
    }

    [Test]
    public async Task SetNumberValueAsync_Returns_True_On_Success()
    {
        var handler = new FakeHttpHandler(req =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.StartsWith("/api/services/number/set_value"))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var client = new HttpClient(handler) { BaseAddress = new System.Uri("http://ha/") };
        var svc = new HomeAssistantClient(client, NullLogger<HomeAssistantClient>.Instance);

        var ok = await svc.SetNumberValueAsync("number.test", 123.0);
        ok.Should().BeTrue();
    }

    [Test]
    public async Task CallServiceGenericAsync_Returns_False_On_NonSuccess()
    {
        var handler = new FakeHttpHandler(req => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var client = new HttpClient(handler) { BaseAddress = new System.Uri("http://ha/") };
        var svc = new HomeAssistantClient(client, NullLogger<HomeAssistantClient>.Instance);

        var ok = await svc.CallServiceGenericAsync("domain", "service", null);
        ok.Should().BeFalse();
    }
}
