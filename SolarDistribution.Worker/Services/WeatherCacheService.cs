using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SolarDistribution.Core.Services;
using SolarDistribution.Worker.Configuration;

namespace SolarDistribution.Worker.Services;

/// <summary>
/// Service de cache météo avec rafraîchissement périodique indépendant du cycle de distribution.
///
/// Problème résolu :
///   Open-Meteo est appelé toutes les 60s dans le cycle principal → inutile et gourmand en API.
///   Les prévisions météo changent au mieux toutes les heures.
///
/// Solution :
///   Ce BackgroundService tourne en parallèle et rafraîchit les données météo
///   selon weather.refresh_interval_minutes (défaut: 15 min).
///   SmartDistributionService lit simplement le dernier WeatherData disponible
///   via GetCurrent() — sans attente réseau dans le cycle principal.
///
/// Comportement au démarrage :
///   - Pré-charge immédiatement avant que le premier cycle ne démarre.
///   - Si la première tentative échoue → retente toutes les 30s jusqu'au succès.
///   - Si la météo est indisponible → WeatherData = null (le cycle continue sans météo).
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
    /// Retourne le dernier WeatherData disponible (jamais null si au moins une fetch a réussi).
    /// Thread-safe — lecture sans lock (snapshot immutable).
    /// </summary>
    public WeatherData? GetCurrent() => _current;

    /// <summary>Age des données météo actuelles. null si jamais récupérées.</summary>
    public TimeSpan? DataAge => _current is null
        ? null
        : DateTime.UtcNow - _lastFetchUtc;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int refreshMinutes = _config.Weather?.RefreshIntervalMinutes ?? 15;
        _logger.LogInformation(
            "WeatherCacheService starting — refresh every {Min} min (lat={Lat}, lon={Lon})",
            refreshMinutes, _config.Location.Latitude, _config.Location.Longitude);

        // ── Pré-charge initiale (avec retries) ───────────────────────────────
        // Retente toutes les 30s jusqu'à succès pour que le premier cycle ait des données.
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

        // ── Rafraîchissement périodique ───────────────────────────────────────
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
            // Arrêt propre
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