using Microsoft.Extensions.Logging;

namespace SolarDistribution.Core.Services;

public class HeatingOrchestratorService : IHeatingOrchestratorService
{
    private readonly IHeatingPreheatMlService _preheatMl;
    private readonly ILogger<HeatingOrchestratorService> _logger;

    public HeatingOrchestratorService(
        IHeatingPreheatMlService preheatMl,
        ILogger<HeatingOrchestratorService> logger)
    {
        _preheatMl = preheatMl;
        _logger = logger;
    }

    public async Task<HeatingDecision> DecideAsync(HeatingOrchestratorContext context, CancellationToken ct = default)
    {
        var estimate = await _preheatMl.EstimateAsync(context, ct);

        var estimatedKwh = Math.Max(0.1, estimate.EstimatedMinutes / 60.0 * 2.0);
        var estimatedCost = estimatedKwh * Math.Max(0, context.CurrentPricePerKwh);

        if (context.PresenceMode is HeatingPresenceMode.Away or HeatingPresenceMode.Sleep)
        {
            return new HeatingDecision(
                Action: HeatingActionType.EcoHold,
                StartAtLocal: null,
                TargetTempC: Math.Min(context.TargetTempC, 17.0),
                Reason: "Occupancy mode is away/sleep: hold eco setpoint",
                EstimatedMinutesToTarget: estimate.EstimatedMinutes,
                EstimatedCostEur: estimatedCost);
        }

        if (context.PresenceMode == HeatingPresenceMode.NearHome && context.DesiredReadyAtLocal.HasValue)
        {
            var startAt = context.DesiredReadyAtLocal.Value.AddMinutes(-estimate.P90Minutes);
            var action = startAt <= context.NowLocal ? HeatingActionType.HeatNow : HeatingActionType.DelayUntil;
            return new HeatingDecision(
                Action: action,
                StartAtLocal: action == HeatingActionType.DelayUntil ? startAt : context.NowLocal,
                TargetTempC: context.TargetTempC,
                Reason: "Near-home mode: schedule restart to hit target temperature on arrival",
                EstimatedMinutesToTarget: estimate.EstimatedMinutes,
                EstimatedCostEur: estimatedCost);
        }

        if (context.CurrentTempC < context.TargetTempC - 0.2)
        {
            return new HeatingDecision(
                Action: HeatingActionType.HeatNow,
                StartAtLocal: context.NowLocal,
                TargetTempC: context.TargetTempC,
                Reason: "Current temperature below comfort target",
                EstimatedMinutesToTarget: estimate.EstimatedMinutes,
                EstimatedCostEur: estimatedCost);
        }

        _logger.LogDebug("Heating decision: comfort already reached, keeping eco hold");

        return new HeatingDecision(
            Action: HeatingActionType.ResumeComfort,
            StartAtLocal: null,
            TargetTempC: context.TargetTempC,
            Reason: "Comfort already reached; maintain thermostat state",
            EstimatedMinutesToTarget: estimate.EstimatedMinutes,
            EstimatedCostEur: estimatedCost);
    }
}
