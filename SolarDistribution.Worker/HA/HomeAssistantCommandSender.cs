using Microsoft.Extensions.Logging;
using SolarDistribution.Core.Models;
using SolarDistribution.Worker.Configuration;

namespace SolarDistribution.Worker.HA;

/// <summary>
/// Envoie les commandes de recharge vers HA après chaque calcul de distribution.
///
/// Pour chaque batterie :
///   1. Si AllocatedW > 0 → exécute NonZeroWActions (ex: désactiver self-powered EcoFlow)
///   2. Si ChargeSwitch configuré et AllocatedW > 0 → turn_on le switch
///   3. Appelle number.set_value avec la puissance calculée (× ValueMultiplier)
///   4. Si AllocatedW == 0 et ChargeSwitch configuré → turn_off le switch
///   5. Si AllocatedW == 0 → exécute ZeroWActions (ex: activer self-powered EcoFlow)
///
/// DryRun : log les commandes sans les envoyer.
/// MinChangeTriggerW : ignore les changements inférieurs au seuil (évite le flooding HA).
/// </summary>
public class HomeAssistantCommandSender
{
    private readonly IHomeAssistantClient _client;
    private readonly SolarConfig          _config;
    private readonly ILogger<HomeAssistantCommandSender> _logger;

    // Dernières valeurs envoyées par batterie — pour le delta check
    private readonly Dictionary<int, double> _lastSentValues = new();

    // Dernier état "0W ou non" par batterie — les actions conditionnelles ne
    // se déclenchent QUE lors d'un changement de zone (0W → >0W ou >0W → 0W).
    // Absent = premier cycle → on déclenche systématiquement.
    private readonly Dictionary<int, bool> _lastWasZero = new();

    public HomeAssistantCommandSender(
        IHomeAssistantClient client,
        SolarConfig config,
        ILogger<HomeAssistantCommandSender> logger)
    {
        _client = client;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Envoie les commandes de recharge pour toutes les batteries.
    /// Retourne le nombre de commandes effectivement envoyées.
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

        // ── Delta check : évite d'envoyer si la valeur n'a pas assez changé ──
        if (_lastSentValues.TryGetValue(battConfig.Id, out double lastValue))
        {
            double delta = Math.Abs(rawValue - lastValue);
            if (delta < _config.Polling.MinChangeTriggerW * battConfig.Entities.ValueMultiplier)
            {
                _logger.LogDebug(
                    "Battery {Id} ({Name}): change {Delta:F2}{Unit} < threshold {Threshold:F2} — skipping",
                    battConfig.Id, battConfig.Name, delta, battConfig.Entities.ValueUnit,
                    _config.Polling.MinChangeTriggerW);
                return false;
            }
        }

        // ── Détection de changement de zone 0W ↔ charge active ──────────────
        // "charge active" = surplus solaire alloué OU recharge réseau d'urgence
        bool currentIsZero = alloc.AllocatedW == 0;
        bool zoneChanged   = !_lastWasZero.TryGetValue(battConfig.Id, out bool prevWasZero)
                             || prevWasZero != currentIsZero;

        if (_config.Polling.DryRun)
        {
            _logger.LogInformation(
                "[DRY-RUN] Battery {Id} ({Name}): would set {Entity} = {Value}{Unit} (allocated {Alloc}W)",
                battConfig.Id, battConfig.Name,
                battConfig.Entities.ChargePower, rawValue, battConfig.Entities.ValueUnit,
                alloc.AllocatedW);

            // Log les actions conditionnelles uniquement si la zone change
            if (zoneChanged)
                LogConditionalActions(alloc.AllocatedW, battConfig);

            _lastSentValues[battConfig.Id] = rawValue;
            _lastWasZero[battConfig.Id]    = currentIsZero;
            return true;
        }

        // ── 1. NonZeroWActions : avant d'activer la charge ────────────────────
        //    Déclenchées UNIQUEMENT si on passe de 0W → >0W (changement de zone)
        if (!currentIsZero && zoneChanged && battConfig.Entities.NonZeroWActions.Count > 0)
        {
            _logger.LogDebug("Battery {Id} ({Name}): zone 0W→>0W — executing {Count} NonZeroW action(s)",
                battConfig.Id, battConfig.Name, battConfig.Entities.NonZeroWActions.Count);
            await ExecuteConditionalActionsAsync(battConfig.Entities.NonZeroWActions, battConfig, ct);
        }

        // ── 2. Activer le switch si nécessaire ────────────────────────────────
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

        // ── 3. Écrire la puissance ─────────────────────────────────────────
        bool success = await _client.SetNumberValueAsync(
            battConfig.Entities.ChargePower, rawValue, ct);

        if (success)
        {
            _lastSentValues[battConfig.Id] = rawValue;
            _lastWasZero[battConfig.Id]    = currentIsZero;

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

        // ── 4. ZeroWActions : après avoir envoyé 0W ───────────────────────────
        //    Déclenchées UNIQUEMENT si on passe de >0W → 0W (changement de zone)
        if (currentIsZero && zoneChanged && battConfig.Entities.ZeroWActions.Count > 0)
        {
            _logger.LogDebug("Battery {Id} ({Name}): zone >0W→0W — executing {Count} ZeroW action(s)",
                battConfig.Id, battConfig.Name, battConfig.Entities.ZeroWActions.Count);
            await ExecuteConditionalActionsAsync(battConfig.Entities.ZeroWActions, battConfig, ct);
        }

        return success;
    }

    // ── Exécution des actions conditionnelles ────────────────────────────────

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
}
