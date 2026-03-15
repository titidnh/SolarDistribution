namespace SolarDistribution.Infrastructure.Data.Entities;

public class BatterySnapshot
{
    public long   Id        { get; set; }
    public long   SessionId { get; set; }
    public int    BatteryId { get; set; }
    public double CapacityWh           { get; set; }
    public double MaxChargeRateW       { get; set; }
    public double MinPercent           { get; set; }
    public double SoftMaxPercent       { get; set; }
    public double CurrentPercentBefore { get; set; }
    public double CurrentPercentAfter  { get; set; }
    public int    Priority             { get; set; }
    public bool   WasUrgent            { get; set; }
    public double AllocatedW           { get; set; }
    public bool   IsGridCharge         { get; set; }
    /// <summary>True si cette charge réseau était déclenchée par urgence SOC.</summary>
    public bool   IsEmergencyGridCharge { get; set; }
    /// <summary>Puissance réseau adaptative autorisée pour cette batterie (W).</summary>
    public double GridChargeAllowedW   { get; set; }
    public string Reason               { get; set; } = string.Empty;
    public DistributionSession Session { get; set; } = null!;
}
