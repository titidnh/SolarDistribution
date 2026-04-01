namespace SolarDistribution.Api.Models;

public class HeatingSimulateRequestDto
{
    public double CurrentTempC { get; set; }
    public double TargetTempC { get; set; }
    public double OutsideTempC { get; set; }
    public DateTime? DesiredReadyAtLocal { get; set; }
    public string PresenceMode { get; set; } = "home";
    public bool IsOffPeak { get; set; }
    public double CurrentPricePerKwh { get; set; }
    public bool IsWeatherWarmingSoon { get; set; }
}

public class GasMeterReadingCreateDto
{
    public DateTime? ReadAtUtc { get; set; }
    public double ReadingM3 { get; set; }
    public string? Note { get; set; }
}

public class GasMeterReadingResponseDto
{
    public long Id { get; set; }
    public DateTime ReadAtUtc { get; set; }
    public double ReadingM3 { get; set; }
    public string Source { get; set; } = string.Empty;
    public string? Note { get; set; }
    public double? DeltaM3FromPrevious { get; set; }
}
