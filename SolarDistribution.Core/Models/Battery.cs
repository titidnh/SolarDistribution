namespace SolarDistribution.Core.Models;

/// <summary>
/// Represents a physical battery with its configuration and current state.
/// </summary>
public class Battery
{
    public int    Id             { get; set; }
    public double CapacityWh     { get; set; }
    public double MaxChargeRateW { get; set; }
    public double MinPercent     { get; set; }
    public double SoftMaxPercent { get; set; } = 80;
    public double HardMaxPercent { get; set; } = 100;
    public double CurrentPercent { get; set; }
    public int    Priority       { get; set; }

    /// <summary>
    /// Maximum allowed power from the grid for this battery (W).
    ///   0  → grid charging forbidden (normal mode, solar surplus only).
    ///   >0 → grid charging permitted (off-peak favorable tariff periods).
    /// Calculated by SmartDistributionService according to tariff context.
    /// </summary>
    public double GridChargeAllowedW { get; set; } = 0;

    // ── Computed ──────────────────────────────────────────────────────────────

    /// <summary>If SOC < MinPercent → URGENT → priority 0, goes first.</summary>
    public int  EffectivePriority => CurrentPercent < MinPercent ? 0 : Priority;
    public bool IsUrgent          => CurrentPercent < MinPercent;

    public double SpaceToSoftMaxWh =>
        Math.Max(0, (SoftMaxPercent - CurrentPercent) / 100.0 * CapacityWh);

    public double SpaceToHardMaxWh =>
        Math.Max(0, (HardMaxPercent - CurrentPercent) / 100.0 * CapacityWh);
}
