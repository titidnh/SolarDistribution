using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SolarDistribution.Core.Services;

public enum HeatingPresenceMode
{
    Home,
    Away,
    Sleep,
    NearHome
}

public enum HeatingActionType
{
    HeatNow,
    DelayUntil,
    EcoHold,
    ResumeComfort
}

public sealed record HeatingPreheatEstimate(
    double EstimatedMinutes,
    double P50Minutes,
    double P90Minutes,
    double Confidence,
    string ModelVersion,
    string Reason);

public sealed record HeatingOrchestratorContext(
    double CurrentTempC,
    double TargetTempC,
    double OutsideTempC,
    DateTime NowLocal,
    DateTime? DesiredReadyAtLocal,
    HeatingPresenceMode PresenceMode,
    bool IsOffPeak,
    double CurrentPricePerKwh,
    bool IsWeatherWarmingSoon);

public sealed record HeatingDecision(
    HeatingActionType Action,
    DateTime? StartAtLocal,
    double TargetTempC,
    string Reason,
    double EstimatedMinutesToTarget,
    double EstimatedCostEur);

public sealed record HeatingStatusSnapshot(
    HeatingPresenceMode PresenceMode,
    double CurrentTempC,
    double TargetTempC,
    DateTime? NextStartAtLocal,
    double EstimatedMinutesToTarget,
    string LastDecision,
    DateTime UpdatedAtUtc);

public sealed record HeatingMlTrainingResult(
    bool Success,
    int TrainingSamples,
    double RSquared,
    double MeanAbsoluteErrorMinutes,
    string ModelVersion,
    string? ErrorMessage = null);

public sealed record HeatingMlModelStatus(
    bool IsAvailable,
    string? ModelVersion,
    int TrainingSamples,
    double? RSquared,
    double? MeanAbsoluteErrorMinutes,
    DateTime? TrainedAt,
    int SamplesInDb,
    int MinSamplesRequired);

public sealed class HeatingMlOptions
{
    public int TrainingWindowDays { get; set; } = 180;
    public int TargetSamples { get; set; } = 20000;
    public int MinSamplesForRetrain { get; set; } = 120;
    // Optional: explicit model directory for heating ML (e.g. "/data/ml_models_heating")
    public string? ModelDirectory { get; set; } = null;
}

/// <summary>
/// Comfort-first safeguards injected into HeatingOrchestratorService.
/// When temperature is critically low and the estimated heating time is too high,
/// the orchestrator overrides the normal decision and forces immediate heating.
/// </summary>
public sealed class HeatingComfortOptions
{
    public bool Enabled { get; set; } = true;
    public double MinimumComfortTempC { get; set; } = 19.0;
    public double CriticalDeltaTempC { get; set; } = 1.5;
    public double MaxMlEtaP90Minutes { get; set; } = 90.0;
}

public interface IHeatingPreheatMlService
{
    Task<HeatingPreheatEstimate> EstimateAsync(HeatingOrchestratorContext context, CancellationToken ct = default);
    Task<HeatingMlTrainingResult> RetrainAsync(CancellationToken ct = default);
    Task<HeatingMlModelStatus> GetStatusAsync(CancellationToken ct = default);
}

public interface IHeatingOrchestratorService
{
    Task<HeatingDecision> DecideAsync(HeatingOrchestratorContext context, CancellationToken ct = default);
}

public interface IHeatingStatusService
{
    void Update(HeatingStatusSnapshot snapshot);
    HeatingStatusSnapshot GetSnapshot();
}

// ── Multi-source heating: source selection ────────────────────────────────────

/// <summary>
/// Immutable descriptor of one heating appliance as seen by the Core selector service.
/// Built by the Worker from HeatingSourceConfig at runtime.
/// </summary>
public sealed record HeatingSourceDefinition(
    string Name,
    string Type,            // gas | heat_pump | electric
    bool Enabled,
    int Priority,
    // Heat pump COP curve: COP(T) = CopAtRefTemp + CopSlopePerDegC × (T − CopRefTempC) ≥ CopMinValue
    double CopRefTempC,
    double CopAtRefTemp,
    double CopSlopePerDegC,
    double CopMinValue,
    // Gas boiler
    double BoilerEfficiency);

public sealed record HeatingSourceCostResult(
    string? BestSourceName,
    string? BestSourceType,
    double BestCop,
    double BestCostPerKwhThermal,
    IReadOnlyList<HeatingSourceBreakdown> AllSources,
    string Reason);

public sealed record HeatingSourceBreakdown(
    string Name,
    string Type,
    double Cop,
    double CostPerKwhThermal);

public interface IHeatingSourceSelectorService
{
    double ComputeCop(HeatingSourceDefinition source, double outdoorTempC);
    double ComputeCostPerKwhThermal(HeatingSourceDefinition source, double outdoorTempC, double elecPricePerKwh, double gasPricePerKwh);
    HeatingSourceCostResult SelectOptimalSource(IReadOnlyList<HeatingSourceDefinition> sources, double outdoorTempC, double elecPricePerKwh, double gasPricePerKwh);
}
