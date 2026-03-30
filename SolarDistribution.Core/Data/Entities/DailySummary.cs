using System;

namespace SolarDistribution.Core.Data.Entities;

/// <summary>
/// Aggregated daily energy summary — one row per calendar date (UTC).
///
/// Computed at the end of the solar day (sunset or midnight) by
/// DailySummaryService, triggered from MlRetrainScheduler.
///
/// Allows answering:
///   "How much solar energy did I self-consume this month?"
///   "What was my self-sufficiency rate yesterday?"
///   "Should the ML be more or less aggressive given the J-1 ratio?"
/// </summary>
public class DailySummary
{
    public long Id { get; set; }

    /// <summary>UTC calendar date (no time component). Unique business key.</summary>
    public DateTime Date { get; set; }

    /// <summary>
    /// Solar energy actually self-consumed (Wh).
    /// Computed as: ForecastTodayWh(start_of_day) − ForecastRemainingTodayWh(end_of_day).
    /// Null if Solcast entities are not configured.
    /// </summary>
    public double? SolarConsumedWh { get; set; }

    /// <summary>
    /// Total energy drawn from the grid over the day (Wh).
    /// Sum of GridChargedW × cycle_duration across all sessions of the day.
    /// </summary>
    public double GridConsumedWh { get; set; }

    /// <summary>
    /// Energy charged into batteries from the grid (Wh).
    /// Sum of GridChargedW × cycle_duration — subset of GridConsumedWh.
    /// </summary>
    public double GridChargedWh { get; set; }

    /// <summary>
    /// Total energy distributed to batteries from solar surplus (Wh).
    /// Sum of TotalAllocatedW × cycle_duration across all sessions of the day.
    /// </summary>
    public double SolarAllocatedWh { get; set; }

    /// <summary>
    /// Unused solar surplus (batteries full or no eligible battery) (Wh).
    /// Sum of UnusedSurplusW × cycle_duration.
    /// </summary>
    public double UnusedSurplusWh { get; set; }

    /// <summary>
    /// Estimated savings in EUR = GridChargedWh × (peak_rate − off_peak_rate).
    /// Simplified calculation: GridChargedWh / 1000 × average MaxSavingsPerKwh for the day.
    /// Null if no tariff context is available for the day.
    /// </summary>
    public double? EstimatedSavingsEur { get; set; }

    /// <summary>
    /// Self-sufficiency rate (%) = SolarConsumedWh / (SolarConsumedWh + GridConsumedWh) × 100.
    /// Null if SolarConsumedWh is absent (Solcast not configured).
    /// ML feature YesterdaySelfSufficiencyPct: allows the model to learn from
    /// the actual performance of the previous day.
    /// </summary>
    public double? SelfSufficiencyPct { get; set; }

    /// <summary>Number of distribution sessions for this day.</summary>
    public int SessionCount { get; set; }

    /// <summary>UTC timestamp of the last update of this record.</summary>
    public DateTime ComputedAt { get; set; } = DateTime.UtcNow;
}
