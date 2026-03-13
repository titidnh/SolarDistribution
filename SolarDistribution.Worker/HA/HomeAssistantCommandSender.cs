using Microsoft.Extensions.Logging;
using SolarDistribution.Core.Models;
using SolarDistribution.Worker.Configuration;

namespace SolarDistribution.Worker.HA;

/// <summary>
/// Envoie les commandes de recharge vers HA après chaque calcul de distribution.
///
/// Pour chaque batterie :
///   1. Détecte le changement de zone 0W ↔ >0W (AVANT le delta check sur la puissance)
///   2. Si zone 0W→>0W : exécute NonZeroWActions (ex: désactiver self-powered EcoFlow)
///   3. Si zone >0W→0W : exécute ZeroWActions   (ex: activer self-powered EcoFlow)
///   4. Met à jour le cache de zone immédiatement (indépendamment du delta check)
///   5. Delta check sur la puissance W : si le changement est trop faible → skip l'envoi HA
///   6. Si ChargeSwitch configuré → turn_on / turn_off selon AllocatedW
///   7. Appelle number.set_value avec la puissance calculée (× ValueMultiplier)
///
/// Fix Bug : le delta check (étape 5) ne doit PAS bloquer les actions conditionnelles
/// de transition de zone (étapes 2-3). Avant ce fix, si rawValue ne changeait pas
/// suffisamment (ex: batterie déjà à 0W depuis plusieurs cycles), le delta check
/// retournait false avant que zoneChanged soit évalué → les ZeroWActions / NonZeroWActions
/// n'étaient jamais déclenchées sur la batterie dont le SOC stagnait.
///
/// DryRun : log les commandes sans les envoyer.
/// MinChangeTriggerW : ignore les changements de puissance inférieurs au seuil (évite le flooding HA).
///
/// État persistant : last sent values + zones sont sauvegardés sur disque via CommandStateCache
/// pour survivre aux redémarrages Docker / reboot host.
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
    /// Envoie les commandes de recharge pour toutes les batteries.
    /// Retourne le nombre de commandes de puissance effectivement envoyées à HA.
    /// Note : les actions conditionnelles de zone peuvent être exécutées même si ce compteur
    /// n'augmente pas (delta check sur la puissance ignoré pour les transitions de zone).
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

        // ── Détection de changement de zone 0W ↔ charge active ──────────────
        // Évaluée EN PREMIER, avant le delta check sur la puissance W.
        // "charge active" = surplus solaire alloué OU recharge réseau d'urgence.
        // Fix : le delta check ne doit pas empêcher les actions de transition de zone.
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

            // Log les actions conditionnelles uniquement si la zone change
            if (zoneChanged)
                LogConditionalActions(alloc.AllocatedW, battConfig);

            _cache.Update(battConfig.Id, rawValue, currentIsZero);
            return true;
        }

        // ── 1. Actions de transition de zone — AVANT le delta check ──────────
        //
        // Ces actions doivent se déclencher dès la transition, quelle que soit
        // la variation de puissance W. Si on les plaçait après le delta check,
        // une batterie dont le SOC stagne (rawValue inchangé) ne déclencherait
        // jamais ses ZeroWActions / NonZeroWActions.

        if (zoneChanged)
        {
            // 1a. NonZeroWActions : passage de 0W → >0W (avant d'activer la charge)
            if (!currentIsZero && battConfig.Entities.NonZeroWActions.Count > 0)
            {
                _logger.LogDebug(
                    "Battery {Id} ({Name}): zone 0W→>0W — executing {Count} NonZeroW action(s)",
                    battConfig.Id, battConfig.Name, battConfig.Entities.NonZeroWActions.Count);
                await ExecuteConditionalActionsAsync(battConfig.Entities.NonZeroWActions, battConfig, ct);
            }

            // 1b. ZeroWActions : passage de >0W → 0W (avant d'écrire la puissance 0W)
            if (currentIsZero && battConfig.Entities.ZeroWActions.Count > 0)
            {
                _logger.LogDebug(
                    "Battery {Id} ({Name}): zone >0W→0W — executing {Count} ZeroW action(s)",
                    battConfig.Id, battConfig.Name, battConfig.Entities.ZeroWActions.Count);
                await ExecuteConditionalActionsAsync(battConfig.Entities.ZeroWActions, battConfig, ct);
            }

            // Persiste immédiatement le nouvel état de zone, indépendamment de l'envoi W.
            // Sans ce UpdateZoneOnly, si le delta check skippait l'envoi au cycle suivant,
            // zoneChanged resterait true indéfiniment → actions exécutées en boucle.
            _cache.UpdateZoneOnly(battConfig.Id, currentIsZero);
        }

        // ── 2. Delta check sur la puissance W ─────────────────────────────────
        // Placé APRÈS les actions de zone : n'affecte que l'envoi de la valeur à HA,
        // pas les transitions de zone déjà traitées ci-dessus.
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

        // ── 3. Activer / désactiver le ChargeSwitch ───────────────────────────
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

        // ── 4. Écrire la puissance ─────────────────────────────────────────────
        bool success = await _client.SetNumberValueAsync(
            battConfig.Entities.ChargePower, rawValue, ct);

        if (success)
        {
            // Update complet : valeur W + zone (redondant pour la zone si déjà fait via
            // UpdateZoneOnly, mais garantit la cohérence de lastSentValue)
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

    /// <summary>
    /// Crée une notification persistante dans Home Assistant (service persistent_notification.create).
    /// Utilisé pour alerter l'utilisateur si plusieurs cycles consécutifs montrent
    /// un surplus manifestement erroné (anomalie compteur P1 / onduleur).
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