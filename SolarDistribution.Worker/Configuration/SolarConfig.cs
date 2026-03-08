using SolarDistribution.Core.Services;  // TariffConfig, TariffSlot defined in Core
namespace SolarDistribution.Worker.Configuration;

/// <summary>
/// Root configuration — mapped from config/config.yaml mounted in the container.
/// </summary>
public class SolarConfig
{
    public HomeAssistantConfig HomeAssistant { get; set; } = new();
    public PollingConfig Polling { get; set; } = new();
    public LocationConfig Location { get; set; } = new();
    public SolarConfig_Solar Solar { get; set; } = new();
    public List<BatteryConfig> Batteries { get; set; } = new List<BatteryConfig>();
    public TariffConfig Tariff { get; set; } = new();
    public MariaDbConfig Database { get; set; } = new();
    public MlConfig Ml { get; set; } = new();
    public WeatherConfig Weather { get; set; } = new();
    public LoggingConfig Logging { get; set; } = new();
}

public class WeatherConfig
{
    /// <summary>
    /// Intervalle de rafraîchissement des données météo Open-Meteo (en minutes).
    /// Les prévisions Open-Meteo sont mises à jour toutes les heures au maximum.
    /// Valeur recommandée : 15 à 30 minutes.
    /// Défaut : 15 minutes.
    /// </summary>
    public int RefreshIntervalMinutes { get; set; } = 15;
}

public class HomeAssistantConfig
{
    /// <summary>URL of the HA instance e.g. http://192.168.1.100:8123</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Long-Lived Access Token generated in the HA profile</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>HTTP timeout in seconds for HA calls (default: 10)</summary>
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>Number of retries on HA error (default: 3)</summary>
    public int RetryCount { get; set; } = 3;
}

public class PollingConfig
{
    /// <summary>Interval between each read/compute/command cycle in seconds (default: 60)</summary>
    public int IntervalSeconds { get; set; } = 60;

    /// <summary>
    /// Simulation mode: reads HA values but does NOT send commands.
    /// Useful for risk-free testing. (default: false)
    /// </summary>
    public bool DryRun { get; set; } = false;

    /// <summary>
    /// Minimum difference in W to trigger a new command.
    /// Avoids sending commands when the value barely changed. (default: 10W)
    /// </summary>
    public double MinChangeTriggerW { get; set; } = 10;
}

public class LocationConfig
{
    /// <summary>Latitude for Open-Meteo (e.g. 50.85 for Brussels)</summary>
    public double Latitude { get; set; } = 50.85;

    /// <summary>Longitude for Open-Meteo (e.g. 4.35 for Brussels)</summary>
    public double Longitude { get; set; } = 4.35;
}

public class SolarConfig_Solar
{
    /// <summary>
    /// Mode to read solar surplus.
    ///
    ///   direct  (default): the entity directly exposes the surplus in W (positive value).
    ///                       e.g. sensor.solar_surplus_power, sensor.solax_export_power
    ///
    ///   p1_invert        : the entity exposes the grid power from the P1 meter (DSMR/P1).
    ///                       Negative = export to grid = available surplus.
    ///                       The Worker inverts the sign and clamps to a minimum of 0.
    ///                       e.g. sensor.p1_power  → -1360 W  ⟹  surplus = 1360 W
    ///                            sensor.p1_power  →  +800 W  ⟹  surplus = 0 W (import)
    /// </summary>
    public string SurplusMode { get; set; } = "direct";

    /// <summary>
    /// HA entity to read the surplus (or P1 power depending on SurplusMode).
    /// Required.
    /// </summary>
    public string SurplusEntity { get; set; } = string.Empty;

    /// <summary>
    /// HA entity for total PV power (optional — for logging/ML).
    /// e.g. sensor.solar_production_power, sensor.onduleur_puissance_active
    /// </summary>
    public string? ProductionEntity { get; set; }

    /// <summary>
    /// HA entity for home consumption (optional — for logging/ML).
    /// e.g. sensor.home_consumption_power
    /// Not required in p1_invert mode — consumption is implicit.
    /// </summary>
    public string? ConsumptionEntity { get; set; }
}

public class BatteryConfig
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Priority { get; set; } = 1;

    /// <summary>Total capacity in Wh</summary>
    public double CapacityWh { get; set; }

    /// <summary>Max charge power in W</summary>
    public double MaxChargeRateW { get; set; }

    /// <summary>Minimum % — below = URGENT</summary>
    public double MinPercent { get; set; } = 20;

    /// <summary>Soft target max % (default 80%)</summary>
    public double SoftMaxPercent { get; set; } = 80;

    /// <summary>Absolute ceiling %</summary>
    public double HardMaxPercent { get; set; } = 100;

    public BatteryEntitiesConfig Entities { get; set; } = new();

    /// <summary>
    /// Seuil d'urgence réseau : si SOC descend SOUS cette valeur,
    /// la batterie est rechargée depuis le réseau même hors heure creuse,
    /// SAUF si le soleil est attendu dans les prochaines heures (autoconsommation suffisante).
    ///
    /// null = fonctionnement normal (pas de recharge réseau forcée en urgence).
    /// Doit être > 0 et <= min_percent pour avoir du sens (typiquement = min_percent ou légèrement au-dessus).
    ///
    /// Exemple EcoFlow : emergency_grid_charge_below_percent: 20
    /// → si SOC < 20% et pas de soleil prévu → recharge forcée depuis le réseau
    /// </summary>
    public double? EmergencyGridChargeBelowPercent { get; set; }

    /// <summary>
    /// Cible de recharge en urgence réseau (en %).
    /// La batterie est rechargée jusqu'à ce seuil — pas plus (on garde de la place pour le solaire).
    ///
    /// null = utilise soft_max_percent comme cible.
    /// Exemple : emergency_grid_charge_target_percent: 50
    /// → recharge jusqu'à 50% depuis le réseau, puis attend le surplus solaire pour la suite.
    /// </summary>
    public double? EmergencyGridChargeTargetPercent { get; set; }
}

/// <summary>
/// Action HA à exécuter conditionnellement selon la puissance allouée à une batterie.
///
/// Exemples d'utilisation :
///   — 0W → activer le mode self-powered d'un EcoFlow
///   — 0W → activer un switch de bypass
///   — > 0W → désactiver le mode self-powered
///   — > 0W → appeler un script HA personnalisé
///
/// Les trois types d'action supportés (type) :
///   turn_on   → homeassistant/turn_on   (switch, input_boolean, script…)
///   turn_off  → homeassistant/turn_off
///   service   → appel libre : domain + service + data (JSON)
/// </summary>
public class HaConditionalAction
{
    /// <summary>
    /// Type d'action :
    ///   "turn_on"  → POST /api/services/homeassistant/turn_on  { entity_id }
    ///   "turn_off" → POST /api/services/homeassistant/turn_off { entity_id }
    ///   "service"  → POST /api/services/{domain}/{service} avec data libre
    /// </summary>
    public string Type { get; set; } = "turn_on";

    /// <summary>
    /// Entity ID HA ciblée (pour turn_on / turn_off).
    /// Ex : "switch.delta3_salon_self_powered_mode"
    /// </summary>
    public string? EntityId { get; set; }

    /// <summary>
    /// Domaine HA pour les appels "service".
    /// Ex : "input_boolean", "script", "notify", "automation"
    /// </summary>
    public string? Domain { get; set; }

    /// <summary>
    /// Service HA pour les appels "service".
    /// Ex : "turn_on", "toggle", "trigger", "set_value"
    /// </summary>
    public string? Service { get; set; }

    /// <summary>
    /// Données supplémentaires transmises au service HA (JSON libre).
    /// Ex : { "entity_id": "input_boolean.ecoflow_self_powered", "value": "1" }
    /// Si null → payload minimal { entity_id } déduit automatiquement.
    /// </summary>
    public Dictionary<string, object>? Data { get; set; }

    /// <summary>Label affiché dans les logs pour identifier l'action.</summary>
    public string? Label { get; set; }
}

public class BatteryEntitiesConfig
{
    /// <summary>HA entity exposing current state of charge % (read).</summary>
    public string Soc { get; set; } = string.Empty;

    /// <summary>
    /// HA 'number' entity to set charge power in W (write).
    /// Uses the HA service: number.set_value.
    /// e.g. number.battery_1_charge_power, number.solax_battery_charge_max_current
    /// </summary>
    public string ChargePower { get; set; } = string.Empty;

    /// <summary>
    /// HA entity exposing the battery's accepted max charge power (read, OPTIONAL).
    ///
    /// WHY THIS IS USEFUL:
    ///   Some inverters/BMS dynamically adjust their charge limit based on
    ///   temperature, state of health (SoH), or charging phase (CC/CV).
    ///   Without this entity the algorithm uses the static max_charge_rate_w from config.yaml.
    ///   With this entity the algorithm reads the actual hardware limit each cycle.
    ///
    /// PRIORITY: if defined and readable -> overrides max_charge_rate_w from config.
    ///           if null or read fails -> fallback to static max_charge_rate_w.
    ///
    /// Examples by inverter:
    ///   SolaX    : sensor.solax_battery_max_charge_current  (→ multiplier = voltage V)
    ///   GivEnergy: sensor.givtcp_battery_charge_rate
    ///   Victron  : sensor.victron_max_charge_current
    ///   Generic  : sensor.battery_1_max_charge_power_w
    /// </summary>
    public string? MaxChargeRateEntity { get; set; }

    /// <summary>
    /// HA entity to enable/disable charging (optional).
    /// If defined: turn_on before writing power, turn_off if 0W allocated.
    /// e.g. switch.battery_1_charge_enable
    /// </summary>
    public string? ChargeSwitch { get; set; }

    /// <summary>
    /// Multiplier applied to the W value before sending to HA via ChargePower.
    /// Useful if the HA entity expects Amps instead of Watts.
    /// e.g. 0.02083 for W -> A on a 48V battery  (A = W / 48)
    /// Default: 1.0 (Watts direct)
    /// </summary>
    public double ValueMultiplier { get; set; } = 1.0;

    /// <summary>
    /// Inverse multiplier to read MaxChargeRateEntity and convert it to W.
    /// If MaxChargeRateEntity exposes Amps for a 48V battery -> 48.0
    /// If MaxChargeRateEntity already exposes Watts -> 1.0 (default)
    /// </summary>
    public double MaxRateReadMultiplier { get; set; } = 1.0;

    /// <summary>Unit of the value sent to HA (for logging). Default: "W"</summary>
    public string ValueUnit { get; set; } = "W";

    /// <summary>
    /// Actions HA exécutées quand AllocatedW == 0 (aucune puissance allouée à cette batterie).
    ///
    /// Cas d'usage EcoFlow :
    ///   — activer le "self-powered mode" pour que l'EcoFlow alimente la maison
    ///     depuis sa propre batterie quand il n'y a pas de surplus solaire
    ///
    /// Exécution : après l'envoi de la commande 0W, dans l'ordre de la liste.
    /// Si DryRun = true → actions loggées mais non envoyées.
    /// </summary>
    public List<HaConditionalAction> ZeroWActions { get; set; } = new();

    /// <summary>
    /// Actions HA exécutées quand AllocatedW > 0 (puissance allouée à cette batterie).
    ///
    /// Cas d'usage EcoFlow :
    ///   — désactiver le "self-powered mode" pour laisser l'onduleur solaire
    ///     prendre la main et charger la batterie avec le surplus
    ///
    /// Exécution : avant l'envoi de la commande de puissance, dans l'ordre de la liste.
    /// Si DryRun = true → actions loggées mais non envoyées.
    /// </summary>
    public List<HaConditionalAction> NonZeroWActions { get; set; } = new();
}



public class MariaDbConfig
{
    public string ConnectionString { get; set; } =
        "Server=localhost;Port=3306;Database=solar_distribution;User=solar_user;Password=CHANGE_ME;CharSet=utf8mb4;";
}

public class MlConfig
{
    public string ModelDirectory { get; set; } = "/data/ml_models";

    /// <summary>
    /// Delay in hours after a session before collecting the real feedback.
    /// E.g. 4.0 -> read SOC 4h after the decision to see the real effect.
    /// Too short (<1h) = effect not visible. Too long (>12h) = noisy data.
    /// Recommended: 3-6h.
    /// </summary>
    public double FeedbackDelayHours { get; set; } = 4.0;

    /// <summary>
    /// Frequency to check pending feedbacks (in hours).
    /// E.g. 1.0 -> checks every hour if sessions have feedback to collect.
    /// Default: 1h
    /// </summary>
    public double FeedbackCheckIntervalHours { get; set; } = 1.0;

    /// <summary>
    /// Cron expression for automatic retraining.
    /// Standard 5-field syntax: "minute hour dayOfMonth month dayOfWeek"
    /// Ex:
    ///   "0 3 * * 0"   -> Sunday at 03:00 UTC
    ///   "0 3 * * *"   -> every day at 03:00 UTC
    ///   "0 2 * * 1"   -> Monday at 02:00 UTC
    /// Default: Sunday 03:00
    /// </summary>
    public string RetrainCron { get; set; } = "0 3 * * 0";

    /// <summary>
    /// Minimum number of VALID feedbacks in the database before triggering training.
    /// Below this -> deterministic algorithm only.
    /// Recommended: minimum 50, ideally 100+.
    /// </summary>
    public int MinFeedbackForRetrain { get; set; } = 50;

    // ── Feedback label calibration (ML-3) ─────────────────────────────
    // These parameters replace magic constants hard-coded in
    // FeedbackEvaluator. They should be tuned per installation:
    // high-cycle batteries -> higher correctionFactor; stable systems -> lower.

    /// <summary>
    /// Correction max (+%) appliquée à SoftMax quand les batteries sont trop basses.
    /// penalty (0→1) × SoftmaxCorrectionFactor = correction en points de %.
    /// Défaut : 15.0 — signifie qu'en cas de pénurie sévère on remonte SoftMax de jusqu'à 15%.
    /// </summary>
    public double FeedbackSoftmaxCorrectionFactor { get; set; } = 15.0;

    /// <summary>
    /// Réduction (%) appliquée à SoftMax quand les batteries sont restées inutilement hautes.
    /// Défaut : 5.0 — légère réduction pour éviter de sur-charger si le surplus était gâché.
    /// </summary>
    public double FeedbackSoftmaxReduction { get; set; } = 5.0;

    /// <summary>
    /// Facteur d'amplification appliqué au shortfall pour calculer la correction
    /// du seuil préventif. correction = min(shortfall × factor, MaxPreventiveCorrection).
    /// Défaut : 1.5
    /// </summary>
    public double FeedbackPreventiveFactor { get; set; } = 1.5;

    /// <summary>
    /// Correction max (+%) appliquée au seuil préventif quand une batterie est tombée trop bas.
    /// Défaut : 20.0
    /// </summary>
    public double FeedbackMaxPreventiveCorrection { get; set; } = 20.0;

    /// <summary>
    /// Réduction (%) appliquée au seuil préventif quand les batteries sont restées
    /// très au-dessus du minimum (marge > 20%).
    /// Défaut : 3.0
    /// </summary>
    public double FeedbackPreventiveReduction { get; set; } = 3.0;

    // ── Drift detection (ML-5) ────────────────────────────────────────────

    /// <summary>
    /// R² degradation over the last N sessions that triggers a forced retrain.
    /// E.g.: 0.15 -> if recent R² < reference R² - 0.15 -> retrain immediately.
    /// Set to 1.0 to disable drift detection.
    /// Default: 0.15
    /// </summary>
    public double DriftDetectionR2Threshold { get; set; } = 0.15;

    /// <summary>
    /// Number of recent sessions used to compute the drift R².
    /// Default: 100
    /// </summary>
    public int DriftDetectionWindowSize { get; set; } = 100;
}

public class LoggingConfig
{
    public string Level { get; set; } = "Information";
    public string? FilePath { get; set; } = "/data/logs/solar-worker.log";
}