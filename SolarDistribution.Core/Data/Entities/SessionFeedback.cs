using System;

namespace SolarDistribution.Core.Data.Entities;

public class SessionFeedback
{
    public long Id { get; set; }
    public long SessionId { get; set; }
    public DateTime CollectedAt { get; set; } = DateTime.UtcNow;
    public double FeedbackDelayHours { get; set; }
    public string ObservedSocJson { get; set; } = "{}";
    public double AvgSocAtFeedback { get; set; }
    public double MinSocAtFeedback { get; set; }
    public double EnergyEfficiencyScore { get; set; }
    public double AvailabilityScore { get; set; }
    public double ObservedOptimalSoftMax { get; set; }
    public double ObservedOptimalPreventive { get; set; }
    public double CompositeScore { get; set; }
    public FeedbackStatus Status { get; set; } = FeedbackStatus.Pending;
    public string? InvalidReason { get; set; }

    // ── ML-7 : real labels measured N hours after the session ────────────────────

    /// <summary>
    /// Actual self-sufficiency rate measured N hours after the session (0–1).
    /// solar_consumed / (solar_consumed + grid_consumed).
    /// Null if the HA consumption/import entities are not configured.
    /// </summary>
    public double? ActualSelfSufficiencyPct { get; set; }

    /// <summary>
    /// True if current was imported from the grid in the N hours
    /// following the session (read from the grid_import_entity in HA).
    /// Null if the entity is not configured.
    /// </summary>
    public bool? DidImportFromGrid { get; set; }

    /// <summary>
    /// Classification label: should the session have triggered a grid charge?
    /// Derived from DidImportFromGrid and self-sufficiency.
    /// Null before computation or if insufficient data.
    /// </summary>
    public bool? ShouldChargeFromGrid { get; set; }

    /// <summary>
    /// True if solar surplus was wasted (batteries full, surplus not absorbed).
    /// Used as a weighting factor in ML training:
    /// these sessions must carry more weight to learn not to miss surplus absorption.
    /// </summary>
    public bool SurplusWasted { get; set; } = false;

    /// <summary>
    /// ML training weight computed for this session (1.0 = normal weight).
    /// Increased for sessions with wasted surplus or unwanted grid import.
    /// </summary>
    public double TrainingWeight { get; set; } = 1.0;

    public DistributionSession Session { get; set; } = null!;
}
