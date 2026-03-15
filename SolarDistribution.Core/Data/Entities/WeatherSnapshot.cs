using System;

namespace SolarDistribution.Core.Data.Entities;

public class WeatherSnapshot
{
    public long Id { get; set; }
    public long SessionId { get; set; }
    public DateTime FetchedAt { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double TemperatureC { get; set; }
    public double CloudCoverPercent { get; set; }
    public double PrecipitationMmH { get; set; }
    public double DirectRadiationWm2 { get; set; }
    public double DiffuseRadiationWm2 { get; set; }
    public double DaylightHours { get; set; }
    public double HoursUntilSunset { get; set; }
    public string RadiationForecast12hJson { get; set; } = "[]";
    public string CloudForecast12hJson { get; set; } = "[]";
    public DistributionSession Session { get; set; } = null!;
}
