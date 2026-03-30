using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SolarDistribution.Core.Services;
using SolarDistribution.Worker.Configuration;

namespace SolarDistribution.Worker.Services;

/// <summary>
/// Weather cache service with periodic refresh independent of the distribution cycle.
///
/// Problem solved:
///   Open-Meteo was called every 60s in the main cycle → unnecessary and API-expensive.
///   Weather forecasts change at best hourly.
///
/// Solution:
///   This BackgroundService runs in parallel and refreshes weather data
///   according to weather.refresh_interval_minutes (default: 15 min).
///   SmartDistributionService simply reads the latest WeatherData
///   through GetCurrent() — no network wait in main cycle.
///
/// Startup behavior:
///   - Preloads immediately before first cycle starts.
///   - If first attempt fails → retries every 30s until success.
///   - If weather is unavailable → WeatherData = null (cycle continues without weather).
/// </summary>
public class WeatherCacheService : BackgroundService
{
    private readonly IWeatherService _weather;
    private readonly SolarConfig _config;
    private readonly ILogger<WeatherCacheService> _logger;

    private WeatherData? _current;
    private DateTime _lastFetchUtc = DateTime.MinValue;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public WeatherCacheService(
        IWeatherService weather,
        SolarConfig config,
        ILogger<WeatherCacheService> logger)
    {
        _weather = weather;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Returns latest available WeatherData (never null if at least one fetch succeeded).
    /// Thread-safe — lock-free read (immutable snapshot).
    /// </summary>
    public WeatherData? GetCurrent() => _current;

    /// <summary>Age of current weather data. null if never fetched.</summary>
    public TimeSpan? DataAge => _current is null
        ? null
        : DateTime.UtcNow - _lastFetchUtc;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int refreshMinutes = _config.Weather?.RefreshIntervalMinutes ?? 15;
        _logger.LogInformation(
            "WeatherCacheService starting — refresh every {Min} min (lat={Lat}, lon={Lon})",
            refreshMinutes, _config.Location.Latitude, _config.Location.Longitude);

        // ── Initial preload (with retries) ───────────────────────────────────
        // Retry every 30s until success so first cycle has data.
        while (!stoppingToken.IsCancellationRequested && _current is null)
        {
            await FetchAsync(stoppingToken);
            if (_current is null)
            {
                _logger.LogWarning(
                    "WeatherCacheService: initial fetch failed — retrying in 30s");
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }

        // ── Periodic refresh ─────────────────────────────────────────────────
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(refreshMinutes), stoppingToken);
            await FetchAsync(stoppingToken);
        }

        _logger.LogInformation("WeatherCacheService stopped");
    }

    private async Task FetchAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var wx = await _weather.GetCurrentWeatherAsync(
                _config.Location.Latitude, _config.Location.Longitude, ct);

            if (wx is not null)
            {
                _current = wx;
                _lastFetchUtc = DateTime.UtcNow;
                _logger.LogInformation(
                    "Weather refreshed — {Temp:F1}°C, clouds={Clouds:F0}%, " +
                    "radiation={Rad:F0}W/m², forecast[0]={F0:F0}W/m²",
                    wx.TemperatureC, wx.CloudCoverPercent, wx.DirectRadiationWm2,
                    wx.RadiationForecast12h.FirstOrDefault());
            }
            else
            {
                _logger.LogWarning("WeatherCacheService: fetch returned null — keeping previous data");
            }
        }
        catch (OperationCanceledException)
        {
            // Graceful shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WeatherCacheService: unexpected error during fetch");
        }
        finally
        {
            _lock.Release();
        }
    }
}