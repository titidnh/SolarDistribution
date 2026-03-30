namespace SolarDistribution.Core.Models;

/// <summary>Allocation result for an individual battery.</summary>
public record BatteryChargeResult(
    int    BatteryId,
    double AllocatedW,
    double PreviousPercent,
    double NewPercent,
    bool   WasUrgent,
    bool   IsGridCharge          = false,   // true = power coming from the grid (Pass 3)
    bool   IsEmergencyGridCharge = false,   // true = grid charge forced by SOC emergency
    string Reason = ""
);

/// <summary>Full distribution result for a given cycle.</summary>
public record DistributionResult(
    double SurplusInputW,
    double TotalAllocatedW,    // total allocated from solar surplus
    double UnusedSurplusW,     // surplus not absorbed
    double GridChargedW,       // total charged from the grid (Pass 3)
    List<BatteryChargeResult> Allocations
);
