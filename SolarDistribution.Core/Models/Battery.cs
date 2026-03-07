namespace SolarDistribution.Core.Models;

/// <summary>
/// Represents a physical battery with its configuration and current state.
/// </summary>
public class Battery
{
    public int Id { get; set; }

    /// <summary>Total capacity in Wh (e.g. 1024Wh)</summary>
    public double CapacityWh { get; set; }

    /// <summary>Maximum charge rate in W (e.g. 500W)</summary>
    public double MaxChargeRateW { get; set; }

    /// <summary>
    /// Minimum charge level in %. If CurrentPercent drops below this,
    /// the battery becomes URGENT and gets EffectivePriority = 0.
    /// </summary>
    public double MinPercent { get; set; }

    /// <summary>
    /// Soft target maximum in % (default 80%).
    /// Pass 1 fills batteries proportionally toward this level.
    /// Can be exceeded if surplus remains after all batteries reach SoftMaxPercent.
    /// </summary>
    public double SoftMaxPercent { get; set; } = 80;

    /// <summary>Absolute hard ceiling in % (default 100%)</summary>
    public double HardMaxPercent { get; set; } = 100;

    /// <summary>Current charge level in %</summary>
    public double CurrentPercent { get; set; }

    /// <summary>
    /// User-defined priority. Lower number = higher priority.
    /// Priority 1 is served before priority 2, etc.
    /// </summary>
    public int Priority { get; set; }

    // ── Computed ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Effective priority used by the algorithm.
    /// If CurrentPercent &lt; MinPercent → battery is URGENT → priority 0 (overrides all).
    /// </summary>
    public int EffectivePriority => CurrentPercent < MinPercent ? 0 : Priority;

    public bool IsUrgent => CurrentPercent < MinPercent;

    /// <summary>
    /// Remaining space to SoftMaxPercent in Wh.
    /// Used as proportional weight within a priority group.
    /// Returns 0 if already at or above soft max.
    /// </summary>
    public double SpaceToSoftMaxWh =>
        Math.Max(0, (SoftMaxPercent - CurrentPercent) / 100.0 * CapacityWh);

    /// <summary>
    /// Remaining space to HardMaxPercent in Wh.
    /// Used in Pass 2 when soft max has been reached by all batteries.
    /// </summary>
    public double SpaceToHardMaxWh =>
        Math.Max(0, (HardMaxPercent - CurrentPercent) / 100.0 * CapacityWh);
}
