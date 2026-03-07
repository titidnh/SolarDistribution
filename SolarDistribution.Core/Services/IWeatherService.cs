namespace SolarDistribution.Core.Services;

/// <summary>Données météo enrichies pour une localisation à un instant donné.</summary>
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
    double[] RadiationForecast12h,   // W/m² par heure sur les 12 prochaines heures
    double[] CloudForecast12h        // % par heure sur les 12 prochaines heures
);

public interface IWeatherService
{
    /// <summary>
    /// Récupère les conditions météo actuelles + prévisions 12h depuis Open-Meteo.
    /// </summary>
    Task<WeatherData?> GetCurrentWeatherAsync(double latitude, double longitude,
        CancellationToken ct = default);
}
