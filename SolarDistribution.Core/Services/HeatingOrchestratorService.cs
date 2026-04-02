using Microsoft.Extensions.Logging;

namespace SolarDistribution.Core.Services;

public class HeatingOrchestratorService : IHeatingOrchestratorService
{
    private readonly IHeatingPreheatMlService _preheatMl;
    private readonly HeatingComfortOptions _comfort;
    private readonly ILogger<HeatingOrchestratorService> _logger;

    public HeatingOrchestratorService(
        IHeatingPreheatMlService preheatMl,
        HeatingComfortOptions comfort,
        ILogger<HeatingOrchestratorService> logger)
    {
        _preheatMl = preheatMl;
        _comfort = comfort;
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

        // ── Comfort constraints override (BUG-M06) ──────────────────────────
        // When temperature is critically low AND the ML-estimated ETA is too high,
        // force immediate heating regardless of other conditions.
        if (_comfort.Enabled
            && context.CurrentTempC < _comfort.MinimumComfortTempC
            && (context.TargetTempC - context.CurrentTempC) >= _comfort.CriticalDeltaTempC
            && estimate.P90Minutes > _comfort.MaxMlEtaP90Minutes)
        {
            _logger.LogWarning(
                "Comfort override: temp={Current:F1}°C < {Min:F1}°C, delta={Delta:F1}°C >= {Critical:F1}°C, " +
                "p90 ETA={P90:F0}min > {Max:F0}min → forcing immediate heating",
                context.CurrentTempC, _comfort.MinimumComfortTempC,
                context.TargetTempC - context.CurrentTempC, _comfort.CriticalDeltaTempC,
                estimate.P90Minutes, _comfort.MaxMlEtaP90Minutes);

            return new HeatingDecision(
                Action: HeatingActionType.HeatNow,
                StartAtLocal: context.NowLocal,
                TargetTempC: context.TargetTempC,
                Reason: $"Comfort override: critically low temperature ({context.CurrentTempC:F1}°C), " +
                        $"delta={context.TargetTempC - context.CurrentTempC:F1}°C, p90 ETA={estimate.P90Minutes:F0}min",
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
