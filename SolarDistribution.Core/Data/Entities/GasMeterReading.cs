using System;

namespace SolarDistribution.Core.Data.Entities;

/// <summary>
/// A manual or automatic gas meter snapshot (m³ absolute reading).
/// Used to compute consumption between two readings and to cross-check sensor data.
/// In "manual" mode the user POSTs readings via the API; in "ha_entity" mode
/// the Worker persists them from a HA cumulative sensor at each heating sampling interval.
/// </summary>
public class GasMeterReading
{
    public long Id { get; set; }
    public DateTime ReadAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Absolute meter value in m³ (as displayed on the physical meter or HA sensor).</summary>
    public double ReadingM3 { get; set; }

    /// <summary>
    /// Source of the reading: "ha_auto" (written by Worker from HA entity),
    /// "manual" (entered by user via API).
    /// </summary>
    public string Source { get; set; } = "manual";

    /// <summary>Optional user note for manual readings (e.g. "monthly reading", "before maintenance").</summary>
    public string? Note { get; set; }
}
