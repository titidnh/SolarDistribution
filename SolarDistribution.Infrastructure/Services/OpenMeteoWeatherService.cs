using System.Text.Json;
using Microsoft.Extensions.Logging;
using SolarDistribution.Core.Services;

namespace SolarDistribution.Infrastructure.Services;

/// <summary>
/// Calls the Open-Meteo API (free, no key) to retrieve:
///   - Current conditions: temperature, clouds, precipitation, radiation
///   - Hourly forecast for 12h: direct radiation + cloud cover
///
/// Endpoint used: https://api.open-meteo.com/v1/forecast
/// </summary>
public class OpenMeteoWeatherService : IWeatherService
{
    private readonly HttpClient _http;
    private readonly ILogger<OpenMeteoWeatherService> _logger;

    // Approximate sunset calculations (±20min) — avoids an external dependency
    private static readonly TimeSpan DefaultSunsetOffset = TimeSpan.FromHours(19);

    public OpenMeteoWeatherService(HttpClient http, ILogger<OpenMeteoWeatherService> logger)
    {
        _http   = http;
        _logger = logger;
    }

    public async Task<WeatherData?> GetCurrentWeatherAsync(
        double latitude, double longitude, CancellationToken ct = default)
    {
        // Variables requested from Open-Meteo
        const string current = "temperature_2m,cloud_cover,precipitation,direct_radiation,diffuse_radiation";
        const string hourly  = "direct_radiation,cloud_cover";

        var url = $"https://api.open-meteo.com/v1/forecast"
            + $"?latitude={latitude:F4}&longitude={longitude:F4}"
            + $"&current={current}"
            + $"&hourly={hourly}"
            + $"&forecast_days=2"   // 2 jours pour voir la production du lendemain matin
            + $"&timezone=auto";

        try
        {
            var response = await _http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // ── Current conditions ────────────────────────────────────────────
            var cur  = root.GetProperty("current");
            double temp         = cur.GetProperty("temperature_2m").GetDouble();
            double clouds       = cur.GetProperty("cloud_cover").GetDouble();
            double precip       = cur.GetProperty("precipitation").GetDouble();
            double directRad    = cur.GetProperty("direct_radiation").GetDouble();
            double diffuseRad   = cur.GetProperty("diffuse_radiation").GetDouble();

            // ── Hourly forecast — next 12 hours ───────────────────────────────
            var hourly_data      = root.GetProperty("hourly");
            var times            = hourly_data.GetProperty("time").EnumerateArray().ToList();
            var directRadHourly  = hourly_data.GetProperty("direct_radiation").EnumerateArray().ToList();
            var cloudHourly      = hourly_data.GetProperty("cloud_cover").EnumerateArray().ToList();

            var now = DateTime.UtcNow;
            var radiationForecast = new List<double>();
            var cloudForecast     = new List<double>();

            for (int i = 0; i < times.Count && radiationForecast.Count < 12; i++)
            {
                if (DateTime.TryParse(times[i].GetString(), out var t) && t >= now)
                {
                    radiationForecast.Add(directRadHourly[i].GetDouble());
                    cloudForecast.Add(cloudHourly[i].GetDouble());
                }
            }

            // ── Daylight & sunset estimate ────────────────────────────────────
            double daylightHours    = EstimateDaylightHours(latitude, now);
            double hoursUntilSunset = EstimateHoursUntilSunset(latitude, longitude, now);

            _logger.LogDebug(
                "Open-Meteo fetched for ({Lat},{Lon}): {Temp}°C, clouds={Clouds}%, radiation={Rad}W/m²",
                latitude, longitude, temp, clouds, directRad);

            return new WeatherData(
                Latitude:              latitude,
                Longitude:             longitude,
                FetchedAt:             now,
                TemperatureC:          temp,
                CloudCoverPercent:     clouds,
                PrecipitationMmH:      precip,
                DirectRadiationWm2:    directRad,
                DiffuseRadiationWm2:   diffuseRad,
                DaylightHours:         daylightHours,
                HoursUntilSunset:      hoursUntilSunset,
                RadiationForecast12h:  radiationForecast.ToArray(),
                CloudForecast12h:      cloudForecast.ToArray()
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch weather from Open-Meteo for ({Lat},{Lon})",
                latitude, longitude);
            return null;
        }
    }

    // ── Simple astronomical helpers ─────────────────────────────────────────

    /// <summary>
    /// Estimate daylight duration in hours (Cooper formula, accuracy ~±15min).
    /// </summary>
    private static double EstimateDaylightHours(double latitude, DateTime date)
    {
        int dayOfYear = date.DayOfYear;
        double declination = 23.45 * Math.Sin(2 * Math.PI * (284 + dayOfYear) / 365.0 * Math.PI / 180);
        double latRad  = latitude * Math.PI / 180;
        double declRad = declination * Math.PI / 180;
        double cosHa   = -Math.Tan(latRad) * Math.Tan(declRad);
        cosHa = Math.Clamp(cosHa, -1, 1);
        double hourAngle = Math.Acos(cosHa) * 180 / Math.PI;
        return 2 * hourAngle / 15.0;
    }

    /// <summary>
    /// Estimate hours remaining until sunset. Returns 0 if already after sunset.
    /// Note: solar noon is corrected by longitude to avoid a systematic error
    /// that can reach ±2h depending on the installation timezone.
    /// Formula: solar noon UTC ≈ 12h − (longitude / 15)
    /// Example: Paris (longitude ≈ 2.35°) → solar noon ≈ 11:51 UTC
    /// </summary>
    private static double EstimateHoursUntilSunset(double latitude, double longitude, DateTime utcNow)
    {
        double daylightHours = EstimateDaylightHours(latitude, utcNow);
        // Midi solaire UTC corrigé par la longitude (15° = 1 heure)
        double solarNoon   = 12.0 - longitude / 15.0;
        double sunset      = solarNoon + daylightHours / 2.0;
        double currentHour = utcNow.Hour + utcNow.Minute / 60.0;
        return Math.Max(0, sunset - currentHour);
    }
}
