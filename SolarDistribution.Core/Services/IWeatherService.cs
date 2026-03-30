namespace SolarDistribution.Core.Services;

/// <summary>Enriched weather data for a location at a given point in time.</summary>
public record WeatherData(
    double Latitude,
    double Longitude,
    DateTime FetchedAt,
    double TemperatureC,
    double CloudCoverPercent,
    double PrecipitationMmH,
    double DirectRadiationWm2,
    double DiffuseRadiationWm2,
    double DaylightHours,
    double HoursUntilSunset,
    double[] RadiationForecast12h,   // W/m² per hour over the next 12 hours
    double[] CloudForecast12h        // % per hour over the next 12 hours
);

public interface IWeatherService
{
    /// <summary>
    /// Fetches current weather conditions + 12h forecast from Open-Meteo.
    /// </summary>
    Task<WeatherData?> GetCurrentWeatherAsync(double latitude, double longitude,
        CancellationToken ct = default);
}
