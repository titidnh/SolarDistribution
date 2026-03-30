using Microsoft.Extensions.Logging;
using SolarDistribution.Core.Models;
using SolarDistribution.Worker.Configuration;

namespace SolarDistribution.Worker.HA;

/// <summary>
/// Sends charge commands to HA after each distribution calculation.
///
/// For each battery:
///   1. Detects 0W ↔ >0W zone change (BEFORE power delta check)
///   2. If 0W→>0W: executes NonZeroWActions (e.g., disable EcoFlow self-powered mode)
///   3. If >0W→0W: executes ZeroWActions   (e.g., enable EcoFlow self-powered mode)
///   4. Updates zone cache immediately (independent of delta check)
///   5. Power delta check in W: if change is too small → skip HA power write
///   6. If ChargeSwitch is configured → turn_on / turn_off based on AllocatedW
///   7. Calls number.set_value with computed power (× ValueMultiplier)
///
/// Bug fix: delta check (step 5) must NOT block conditional zone-transition actions
/// (steps 2-3). Before this fix, if rawValue did not change enough (e.g., battery
/// already at 0W for several cycles), delta check returned false before zoneChanged
/// was evaluated → ZeroWActions / NonZeroWActions were never triggered for a battery
/// whose SOC was stagnating.
///
/// DryRun: logs commands without sending them.
/// MinChangeTriggerW: ignores power changes below threshold (avoids HA flooding).
///
/// Persistent state: last sent values + zones are saved to disk via CommandStateCache
/// to survive Docker restarts / host reboot.
/// </summary>
public class HomeAssistantCommandSender
{
    private readonly IHomeAssistantClient _client;
    private readonly SolarConfig _config;
    private readonly CommandStateCache _cache;
    private readonly ILogger<HomeAssistantCommandSender> _logger;

    public HomeAssistantCommandSender(
        IHomeAssistantClient client,
        SolarConfig config,
        CommandStateCache cache,
        ILogger<HomeAssistantCommandSender> logger)
    {
        _client = client;
        _config = config;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// Sends charge commands for all batteries.
    /// Returns the number of power commands effectively sent to HA.
    /// Note: conditional zone actions may be executed even if this counter
    /// does not increase (power delta check is ignored for zone transitions).
    /// </summary>
    public async Task<int> SendCommandsAsync(
        IEnumerable<BatteryChargeResult> allocations,
        CancellationToken ct = default)
    {
        int sent = 0;

        foreach (var alloc in allocations)
        {
            var battConfig = _config.Batteries.FirstOrDefault(b => b.Id == alloc.BatteryId);
            if (battConfig is null)
            {
                _logger.LogWarning("No config found for battery {Id} — skipping command", alloc.BatteryId);
                continue;
            }

            bool commandSent = await SendBatteryCommandAsync(alloc, battConfig, ct);
            if (commandSent) sent++;
        }

        return sent;
    }

    private async Task<bool> SendBatteryCommandAsync(
        BatteryChargeResult alloc,
        BatteryConfig battConfig,
        CancellationToken ct)
    {
        double rawValue = alloc.AllocatedW * battConfig.Entities.ValueMultiplier;
        rawValue = Math.Round(rawValue, 2);

        // ── Detect 0W ↔ active-charge zone change ───────────────────────────
        // Evaluated FIRST, before the power delta check.
        // "active charge" = allocated solar surplus OR emergency grid charge.
        // Fix: delta check must not block zone transition actions.
        bool currentIsZero = alloc.AllocatedW == 0;
        bool? prevWasZero = _cache.GetLastWasZero(battConfig.Id);
        bool zoneChanged = prevWasZero is null || prevWasZero.Value != currentIsZero;

        if (_config.Polling.DryRun)
        {
            _logger.LogInformation(
                "[DRY-RUN] Battery {Id} ({Name}): would set {Entity} = {Value}{Unit} (allocated {Alloc}W)",
                battConfig.Id, battConfig.Name,
                battConfig.Entities.ChargePower, rawValue, battConfig.Entities.ValueUnit,
                alloc.AllocatedW);

            // Log conditional actions only if zone changes
            if (zoneChanged)
                LogConditionalActions(alloc.AllocatedW, battConfig);

            _cache.Update(battConfig.Id, rawValue, currentIsZero);
            return true;
        }

        // ── 1. Zone transition actions — BEFORE delta check ─────────────────
        //
        // These actions must trigger immediately on transition regardless of
        // power variation in W. If placed after delta check, a battery whose
        // SOC is stagnant (unchanged rawValue) would never trigger
        // ZeroWActions / NonZeroWActions.

        if (zoneChanged)
        {
            // 1a. NonZeroWActions: transition from 0W → >0W (before enabling charge)
            if (!currentIsZero && battConfig.Entities.NonZeroWActions.Count > 0)
            {
                _logger.LogDebug(
                    "Battery {Id} ({Name}): zone 0W→>0W — executing {Count} NonZeroW action(s)",
                    battConfig.Id, battConfig.Name, battConfig.Entities.NonZeroWActions.Count);
                await ExecuteConditionalActionsAsync(battConfig.Entities.NonZeroWActions, battConfig, ct);
            }

            // 1b. ZeroWActions: transition from >0W → 0W (before writing 0W)
            if (currentIsZero && battConfig.Entities.ZeroWActions.Count > 0)
            {
                _logger.LogDebug(
                    "Battery {Id} ({Name}): zone >0W→0W — executing {Count} ZeroW action(s)",
                    battConfig.Id, battConfig.Name, battConfig.Entities.ZeroWActions.Count);
                await ExecuteConditionalActionsAsync(battConfig.Entities.ZeroWActions, battConfig, ct);
            }

            // Persist the new zone state immediately, independent of W write.
            // Without UpdateZoneOnly, if delta check skipped the next-cycle write,
            // zoneChanged would stay true indefinitely → actions executed in a loop.
            _cache.UpdateZoneOnly(battConfig.Id, currentIsZero);
        }

        // ── 2. Power delta check in W ────────────────────────────────────────
        // Placed AFTER zone actions: affects only HA value write,
        // not already-processed zone transitions above.
        double? lastValue = _cache.GetLastSentValue(battConfig.Id);
        if (lastValue.HasValue)
        {
            double delta = Math.Abs(rawValue - lastValue.Value);
            if (delta < _config.Polling.MinChangeTriggerW * battConfig.Entities.ValueMultiplier)
            {
                _logger.LogDebug(
                    "Battery {Id} ({Name}): change {Delta:F2}{Unit} < threshold {Threshold:F2} — skipping power write",
                    battConfig.Id, battConfig.Name, delta, battConfig.Entities.ValueUnit,
                    _config.Polling.MinChangeTriggerW);
                return false;
            }
        }

        // ── 3. Enable / disable ChargeSwitch ─────────────────────────────────
        if (battConfig.Entities.ChargeSwitch is not null)
        {
            if (alloc.AllocatedW > 0)
            {
                _logger.LogDebug("Battery {Id}: enabling charge switch {Switch}",
                    battConfig.Id, battConfig.Entities.ChargeSwitch);
                await _client.TurnOnSwitchAsync(battConfig.Entities.ChargeSwitch, ct);
            }
            else
            {
                _logger.LogDebug("Battery {Id}: disabling charge switch {Switch} (0W allocated)",
                    battConfig.Id, battConfig.Entities.ChargeSwitch);
                await _client.TurnOffSwitchAsync(battConfig.Entities.ChargeSwitch, ct);
            }
        }

        // ── 4. Write power ───────────────────────────────────────────────────
        bool success = await _client.SetNumberValueAsync(
            battConfig.Entities.ChargePower, rawValue, ct);

        if (success)
        {
            // Full update: W value + zone (redundant for zone if already set via
            // UpdateZoneOnly, but guarantees lastSentValue consistency)
            _cache.Update(battConfig.Id, rawValue, currentIsZero);

            _logger.LogInformation(
                "Battery {Id} ({Name}): set charge power {Value}{Unit} " +
                "(allocated {Alloc}W, SOC {Soc:F1}% → {NewSoc:F1}%) [{Reason}]",
                battConfig.Id, battConfig.Name,
                rawValue, battConfig.Entities.ValueUnit,
                alloc.AllocatedW, alloc.PreviousPercent, alloc.NewPercent,
                alloc.Reason);
        }
        else
        {
            _logger.LogError(
                "Battery {Id} ({Name}): FAILED to set charge power to {Value}{Unit}",
                battConfig.Id, battConfig.Name, rawValue, battConfig.Entities.ValueUnit);
        }

        return success;
    }

    // ── Conditional action execution ─────────────────────────────────────────

    private async Task ExecuteConditionalActionsAsync(
        List<HaConditionalAction> actions,
        BatteryConfig battConfig,
        CancellationToken ct)
    {
        foreach (var action in actions)
        {
            await ExecuteSingleActionAsync(action, battConfig, ct);
        }
    }

    private async Task ExecuteSingleActionAsync(
        HaConditionalAction action,
        BatteryConfig battConfig,
        CancellationToken ct)
    {
        string label = action.Label ?? action.EntityId ?? $"{action.Domain}.{action.Service}";

        try
        {
            bool ok = action.Type.ToLowerInvariant() switch
            {
                "turn_on" when action.EntityId is not null =>
                    await _client.TurnOnSwitchAsync(action.EntityId, ct),

                "turn_off" when action.EntityId is not null =>
                    await _client.TurnOffSwitchAsync(action.EntityId, ct),

                "service" when action.Domain is not null && action.Service is not null =>
                    await _client.CallServiceGenericAsync(action.Domain, action.Service, action.Data, ct),

                _ => LogInvalidAction(action, battConfig)
            };

            if (ok)
                _logger.LogInformation(
                    "Battery {Id} ({Name}): conditional action '{Label}' executed successfully",
                    battConfig.Id, battConfig.Name, label);
            else
                _logger.LogWarning(
                    "Battery {Id} ({Name}): conditional action '{Label}' returned failure",
                    battConfig.Id, battConfig.Name, label);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Battery {Id} ({Name}): exception executing conditional action '{Label}'",
                battConfig.Id, battConfig.Name, label);
        }
    }

    private void LogConditionalActions(double allocatedW, BatteryConfig battConfig)
    {
        var actions = allocatedW == 0
            ? battConfig.Entities.ZeroWActions
            : battConfig.Entities.NonZeroWActions;

        string trigger = allocatedW == 0 ? "ZeroW" : "NonZeroW";

        foreach (var action in actions)
        {
            string label = action.Label ?? action.EntityId ?? $"{action.Domain}.{action.Service}";
            _logger.LogInformation(
                "[DRY-RUN] Battery {Id} ({Name}): would execute {Trigger} action '{Label}' (type={Type})",
                battConfig.Id, battConfig.Name, trigger, label, action.Type);
        }
    }

    private bool LogInvalidAction(HaConditionalAction action, BatteryConfig battConfig)
    {
        _logger.LogWarning(
            "Battery {Id} ({Name}): invalid conditional action — type='{Type}' entity='{Entity}' domain='{Domain}' service='{Service}'. " +
            "Valid types: turn_on (requires entity_id), turn_off (requires entity_id), service (requires domain + service).",
            battConfig.Id, battConfig.Name,
            action.Type, action.EntityId, action.Domain, action.Service);
        return false;
    }

    /// <summary>
    /// Creates a persistent notification in Home Assistant (persistent_notification.create service).
    /// Used to alert the user if several consecutive cycles show
    /// clearly erroneous surplus values (P1 meter / inverter anomaly).
    /// </summary>
    public async Task<bool> CreatePersistentNotificationAsync(string title, string message, CancellationToken ct = default)
    {
        var data = new Dictionary<string, object>
        {
            ["title"] = title,
            ["message"] = message
        };

        return await _client.CallServiceGenericAsync("persistent_notification", "create", data, ct);
    }
}