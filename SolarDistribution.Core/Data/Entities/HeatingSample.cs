using System;

namespace SolarDistribution.Core.Data.Entities;

public class HeatingSample
{
    public long Id { get; set; }
    public DateTime SampledAtUtc { get; set; } = DateTime.UtcNow;

    public double? IndoorTempC { get; set; }
    public double? TargetTempC { get; set; }
    public double? OutdoorTempC { get; set; }
    public double? OutdoorHumidityPct { get; set; }
    public double? WindSpeedMs { get; set; }
    public double? SolarIrradianceWm2 { get; set; }

    // JSON payload with short-horizon hourly forecast points from HA entities.
    public string? ForecastOutdoorTempNextHoursJson { get; set; }

    public string? ThermostatMode { get; set; }
    public string? HvacAction { get; set; }
    public bool? IsHeatingOn { get; set; }

    public string? PresenceMode { get; set; }
    public bool? IsNearHome { get; set; }

    public bool? IsOffPeak { get; set; }
    public double? CurrentPricePerKwh { get; set; }

    // ── Active heating source (computed at sampling time by HeatingSourceSelectorService) ──
    /// <summary>Name of the source selected as optimal at this sample (matches HeatingSourceConfig.Name).</summary>
    public string? ActiveSourceName { get; set; }

    /// <summary>Type of the selected source: gas | heat_pump | electric</summary>
    public string? ActiveSourceType { get; set; }

    /// <summary>Instantaneous gas consumption at sampling time (m³/h). Null if no gas source active or no sensor.</summary>
    public double? GasConsumptionM3h { get; set; }

    /// <summary>COP of the heat pump at sampling time (computed from outdoor temp + config curve). Null if not heat pump.</summary>
    public double? HeatPumpCop { get; set; }

    /// <summary>Estimated cost of the active source at this sample (€/kWh thermal). Used as ML feature.</summary>
    public double? EstimatedCostPerKwhThermal { get; set; }
}