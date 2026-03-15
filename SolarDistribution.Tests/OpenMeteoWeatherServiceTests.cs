using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using SolarDistribution.Worker.HA;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using SolarDistribution.Infrastructure.Services;

namespace SolarDistribution.Tests.Unit;

public class FakeHttpHandler2 : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
    public FakeHttpHandler2(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
        => Task.FromResult(_responder(request));
}

[TestFixture]
public class OpenMeteoWeatherServiceTests
{
    [Test]
    public async Task GetCurrentWeatherAsync_ParsesOpenMeteoResponse()
    {
        string json = JsonSerializer.Serialize(new
        {
            current = new { temperature_2m = 10.5, cloud_cover = 20.0, precipitation = 0.0, direct_radiation = 123.0, diffuse_radiation = 12.0 },
            hourly = new { time = new[] { System.DateTime.UtcNow.ToString("o") }, direct_radiation = new[] { 100.0 }, cloud_cover = new[] { 10.0 } }
        });

        var handler = new FakeHttpHandler2(req => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        var client = new HttpClient(handler) { BaseAddress = new System.Uri("http://api.open-meteo/") };
        var svc = new OpenMeteoWeatherService(client, NullLogger<OpenMeteoWeatherService>.Instance);

        var res = await svc.GetCurrentWeatherAsync(50.0, 4.0);
        res.Should().NotBeNull();
        res!.TemperatureC.Should().BeApproximately(10.5, 0.001);
        res.DaylightHours.Should().BeGreaterThan(0);
    }
}
