namespace SolarDistribution.Core.Models;

/// <summary>
/// Charge allocation result for a single battery.
/// </summary>
public record BatteryChargeResult(
    int BatteryId,
    double AllocatedW,
    double PreviousPercent,
    double NewPercent,
    bool WasUrgent,
    string Reason
);

/// <summary>
/// Full distribution result returned by the algorithm for a given surplus input.
/// </summary>
public record DistributionResult(
    double SurplusInputW,
    double TotalAllocatedW,
    double UnusedSurplusW,
    List<BatteryChargeResult> Allocations
);
