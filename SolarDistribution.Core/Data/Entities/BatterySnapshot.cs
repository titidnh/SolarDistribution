namespace SolarDistribution.Core.Data.Entities;

public class BatterySnapshot
{
    public long Id { get; set; }
    public long SessionId { get; set; }
    public int BatteryId { get; set; }
    public double CapacityWh { get; set; }
    public double MaxChargeRateW { get; set; }
    public double MinPercent { get; set; }
    public double SoftMaxPercent { get; set; }
    public double CurrentPercentBefore { get; set; }
    public double CurrentPercentAfter { get; set; }
    public int Priority { get; set; }
    public bool WasUrgent { get; set; }
    public double AllocatedW { get; set; }
    public bool IsGridCharge { get; set; }
    /// <summary>True if this grid charge was triggered by a SOC emergency.</summary>
    public bool IsEmergencyGridCharge { get; set; }
    /// <summary>Adaptive grid charge power allowed for this battery (W).</summary>
    public double GridChargeAllowedW { get; set; }
    public string Reason { get; set; } = string.Empty;
    /// <summary>
    /// ML-8: Number of charge cycles for this battery at the time of the session.
    /// 0 if CycleCountEntity is not configured in SolarConfig.
    /// </summary>
    public int CycleCount { get; set; } = 0;
    public DistributionSession Session { get; set; } = null!;
}
