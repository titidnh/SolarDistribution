using Microsoft.Extensions.Logging;
using SolarDistribution.Core.Models;
using SolarDistribution.Worker.Configuration;

namespace SolarDistribution.Worker.HA;

/// <summary>
/// Envoie les commandes de recharge vers HA après chaque calcul de distribution.
///
/// Pour chaque batterie :
///   1. Si ChargeSwitch configuré et AllocatedW > 0 → turn_on le switch
///   2. Appelle number.set_value avec la puissance calculée (× ValueMultiplier)
///   3. Si AllocatedW == 0 et ChargeSwitch configuré → turn_off le switch
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

        if (_config.Polling.DryRun)
        {
            _logger.LogInformation(
                "[DRY-RUN] Battery {Id} ({Name}): would set {Entity} = {Value}{Unit} (allocated {Alloc}W)",
                battConfig.Id, battConfig.Name,
                battConfig.Entities.ChargePower, rawValue, battConfig.Entities.ValueUnit,
                alloc.AllocatedW);
            _lastSentValues[battConfig.Id] = rawValue;
            return true;
        }

        // ── 1. Activer le switch si nécessaire ────────────────────────────────
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

        // ── 2. Écrire la puissance ─────────────────────────────────────────
        bool success = await _client.SetNumberValueAsync(
            battConfig.Entities.ChargePower, rawValue, ct);

        if (success)
        {
            _lastSentValues[battConfig.Id] = rawValue;

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
}
