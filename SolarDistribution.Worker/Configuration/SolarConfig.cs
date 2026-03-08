namespace SolarDistribution.Worker.Configuration;

/// <summary>
/// Root configuration — mapped from the mounted config/config.yaml inside the container.
/// </summary>
public class SolarConfig
{
    public HomeAssistantConfig  HomeAssistant { get; set; } = new();
    public PollingConfig        Polling       { get; set; } = new();
    public LocationConfig       Location      { get; set; } = new();
    public SolarConfig_Solar    Solar         { get; set; } = new();
    public List<BatteryConfig>  Batteries     { get; set; } = new List<BatteryConfig>();
    public TariffConfig         Tariff        { get; set; } = new();
    public MariaDbConfig        Database      { get; set; } = new();
    public MlConfig             Ml            { get; set; } = new();
    public LoggingConfig        Logging       { get; set; } = new();
}

public class HomeAssistantConfig
{
    /// <summary>HA instance URL e.g. http://192.168.1.100:8123</summary>
    public string Url   { get; set; } = string.Empty;

    /// <summary>Long-Lived Access Token generated in the HA profile</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>HTTP timeout in seconds for HA calls (default: 10)</summary>
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>Number of retries on HA errors (default: 3)</summary>
    public int RetryCount { get; set; } = 3;
}

public class PollingConfig
{
    /// <summary>Interval between each read/compute/command cycle in seconds (default: 60)</summary>
    public int IntervalSeconds { get; set; } = 60;

    /// <summary>
    /// Simulation mode: reads HA values but does NOT send commands.
    /// Useful for safe testing. (default: false)
    /// </summary>
    public bool DryRun { get; set; } = false;

    /// <summary>
    /// Minimum W difference to trigger a new command.
    /// Prevents sending unnecessary commands if the value barely changed. (default: 10W)
    /// </summary>
    public double MinChangeTriggerW { get; set; } = 10;
}

public class LocationConfig
{
    /// <summary>Latitude for Open-Meteo (e.g. 50.85 for Brussels)</summary>
    public double Latitude  { get; set; } = 50.85;

    /// <summary>Longitude for Open-Meteo (e.g. 4.35 for Brussels)</summary>
    public double Longitude { get; set; } = 4.35;
}

public class SolarConfig_Solar
{
    /// <summary>
    /// HA entity exposing available surplus power in W.
    /// e.g.: sensor.solar_surplus_power, sensor.solax_export_power
    /// </summary>
    public string SurplusEntity { get; set; } = string.Empty;

    /// <summary>
    /// HA entity for total PV power (optional — for logging/ML)
    /// e.g.: sensor.solar_production_power
    /// </summary>
    public string? ProductionEntity { get; set; }

    /// <summary>
    /// HA entity for home consumption (optional — for logging/ML)
    /// e.g.: sensor.home_consumption_power
    /// </summary>
    public string? ConsumptionEntity { get; set; }
}

public class BatteryConfig
{
    public int    Id       { get; set; }
    public string Name     { get; set; } = string.Empty;
    public int    Priority { get; set; } = 1;

    /// <summary>Total capacity in Wh</summary>
    public double CapacityWh     { get; set; }

    /// <summary>Maximum charging power in W</summary>
    public double MaxChargeRateW { get; set; }

    /// <summary>Minimum % — below = URGENT</summary>
    public double MinPercent     { get; set; } = 20;

    /// <summary>Soft max target % (default 80%)</summary>
    public double SoftMaxPercent { get; set; } = 80;

    /// <summary>Absolute ceiling %</summary>
    public double HardMaxPercent { get; set; } = 100;

    public BatteryEntitiesConfig Entities { get; set; } = new();
}

public class BatteryEntitiesConfig
{
    /// <summary>HA entity exposing the current charge % (read).</summary>
    public string Soc { get; set; } = string.Empty;

    /// <summary>
    /// HA 'number' entity to set charging power in W (write).
    /// Uses HA service: number.set_value.
    /// e.g.: number.battery_1_charge_power, number.solax_battery_charge_max_current
    /// </summary>
    public string ChargePower { get; set; } = string.Empty;

    /// <summary>
    /// HA entity exposing the battery's maximum accepted charging power (read, OPTIONAL).
    ///
    /// WHY IT'S USEFUL:
    ///   Some inverters/BMS dynamically adjust their charge limit
    ///   based on temperature, state of health (SoH), or charge phase (CC/CV).
    ///   Without this entity, the algorithm uses the static max_charge_rate_w from config.yaml.
    ///   With this entity, the algorithm reads the real hardware limit every cycle.
    ///
    /// PRIORITY: if defined and readable → overrides the static max_charge_rate_w from config.
    ///           if null or read fails → falls back to static max_charge_rate_w.
    ///
    /// Examples per inverter:
    ///   SolaX    : sensor.solax_battery_max_charge_current  (→ multiplier = voltage V)
    ///   GivEnergy: sensor.givtcp_battery_charge_rate
    ///   Victron  : sensor.victron_max_charge_current
    ///   Generic  : sensor.battery_1_max_charge_power_w
    /// </summary>
    public string? MaxChargeRateEntity { get; set; }

    /// <summary>
    /// HA entity to enable/disable charging (optional).
    /// If defined: turn_on before writing power, turn_off if 0W allocated.
    /// e.g.: switch.battery_1_charge_enable
    /// </summary>
    public string? ChargeSwitch { get; set; }

    /// <summary>
    /// Multiplier applied to the W value before sending to HA via ChargePower.
    /// Useful if the HA entity expects Amperes rather than Watts.
    /// e.g.: 0.02083 for W → A on a 48V battery  (A = W / 48)
    /// Default: 1.0 (direct Watts)
    /// </summary>
    public double ValueMultiplier { get; set; } = 1.0;

    /// <summary>
    /// Inverse multiplier to read MaxChargeRateEntity and convert it to W.
    /// If MaxChargeRateEntity exposes Amperes for a 48V battery → 48.0
    /// If MaxChargeRateEntity already exposes Watts → 1.0 (default)
    /// </summary>
    public double MaxRateReadMultiplier { get; set; } = 1.0;

    /// <summary>Unit of the value sent to HA (for logging). Default: "W"</summary>
    public string ValueUnit { get; set; } = "W";
}

// ─────────────────────────────────────────────────────────────────────────────
// Tarification électrique
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Grid electricity tariff configuration.
/// Allows the system to decide whether it's profitable to charge from the grid
/// when there is no solar surplus.
/// </summary>
public class TariffConfig
{
    /// <summary>Currency for logs (e.g. "EUR", "USD").</summary>
    public string Currency { get; set; } = "EUR";

    /// <summary>
    /// Sell-back price for solar surplus in €/kWh.
    /// 0 if you are not compensated for grid injection.
    /// </summary>
    public double ExportPricePerKwh { get; set; } = 0.08;

    /// <summary>
    /// Price threshold in €/kWh below which grid charging is allowed.
    /// If current price < threshold AND solar forecast low → grid charging allowed.
    /// Set to 0 to completely disable grid charging.
    /// </summary>
    public double GridChargeThresholdPerKwh { get; set; } = 0.15;

    /// <summary>
    /// Solar forecast threshold (W/m²). Below → no production expected
    /// → grid charging allowed if tariff is favorable.
    /// </summary>
    public double MinSolarForecastForGridBlock { get; set; } = 100.0;

    /// <summary>Horizon in hours to evaluate solar forecast (default: 4h).</summary>
    public int SolarForecastHorizonHours { get; set; } = 4;

    /// <summary>
    /// Tariff slots. If empty → grid charging disabled.
    /// </summary>
    public List<TariffSlot> Slots { get; set; } = new List<TariffSlot>();
}

    /// <summary>
    /// A tariff slot with time ranges and price.
    /// Examples:
    ///   - Off-peak : StartTime="22:00", EndTime="06:00", PricePerKwh=0.10
    ///   - Peak     : StartTime="06:00", EndTime="22:00", PricePerKwh=0.28
    ///   - Weekend  : StartTime="00:00", EndTime="00:00", DaysOfWeek=[0,6], PricePerKwh=0.12
    /// </summary>
public class TariffSlot
{
    /// <summary>Name for logs (e.g. "Off-Peak", "Peak").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Price in €/kWh.</summary>
    public double PricePerKwh { get; set; }

    /// <summary>Inclusive start time in "HH:mm" format (e.g. "22:00").</summary>
    public string StartTime { get; set; } = "00:00";

    /// <summary>
    /// Exclusive end time in "HH:mm" format.
    /// Can be &lt; StartTime for slots overlapping midnight
    /// (e.g. StartTime="22:00", EndTime="06:00" = 22:00→06:00).
    /// </summary>
    public string EndTime { get; set; } = "00:00";

    /// <summary>
    /// Days of the week (0=Sunday, 1=Monday, ..., 6=Saturday).
    /// null/empty = all days.
    /// Example: [1,2,3,4,5] = Monday to Friday.
    /// </summary>
    public List<int>? DaysOfWeek { get; set; }

    public TimeSpan ParsedStart => TimeSpan.Parse(StartTime);
    public TimeSpan ParsedEnd   => TimeSpan.Parse(EndTime);

    /// <summary>
    /// Vérifie si ce créneau est actif à l'instant donné.
    /// Gère automatiquement les créneaux chevauchant minuit.
    /// </summary>
    public bool IsActiveAt(DateTime localTime)
    {
        if (DaysOfWeek is { Count: > 0 } && !DaysOfWeek.Contains((int)localTime.DayOfWeek))
            return false;

        var tod   = localTime.TimeOfDay;
        var start = ParsedStart;
        var end   = ParsedEnd;

        if (start == end) return true;           // toute la journée (00:00→00:00)
        if (start < end)  return tod >= start && tod < end;   // créneau normal
        return tod >= start || tod < end;        // créneau chevauchant minuit
    }
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
    /// E.g.: 4.0 → read SOC 4h after decision to observe the real effect.
    /// Too short (<1h) = effect not visible. Too long (>12h) = noisy.
    /// Recommended: 3-6h.
    /// </summary>
    public double FeedbackDelayHours { get; set; } = 4.0;

    /// <summary>
    /// Frequency to check pending feedbacks (in hours).
    /// E.g.: 1.0 → checks every hour if sessions have feedback to collect.
    /// Default: 1h
    /// </summary>
    public double FeedbackCheckIntervalHours { get; set; } = 1.0;

    /// <summary>
    /// Cron expression for automatic retraining.
    /// Standard 5-field syntax: "minute hour dayOfMonth month dayOfWeek"
    /// Examples:
    ///   "0 3 * * 0" → Sunday at 03:00 UTC
    ///   "0 3 * * *" → Every day at 03:00 UTC
    ///   "0 2 * * 1" → Monday at 02:00 UTC
    /// Default: Sunday 03:00
    /// </summary>
    public string RetrainCron { get; set; } = "0 3 * * 0";

    /// <summary>
    /// Minimum number of VALID feedbacks in the DB before triggering training.
    /// Below this → deterministic algorithm only.
    /// Recommended: minimum 50, ideally 100+.
    /// </summary>
    public int MinFeedbackForRetrain { get; set; } = 50;

    // ── Feedback label calibration (ML-3) ──────────────────────────────────
    // These parameters replace the magic constants hardcoded in
    // FeedbackEvaluator. They should be tuned per installation:
    // high-cycle batteries → higher correctionFactor; stable setup → lower.

    /// <summary>
    /// Maximum (+%) correction applied to SoftMax when batteries are too low.
    /// penalty (0→1) × SoftmaxCorrectionFactor = correction in percentage points.
    /// Default: 15.0 — means in severe shortage SoftMax can be increased up to 15%.
    /// </summary>
    public double FeedbackSoftmaxCorrectionFactor { get; set; } = 15.0;

    /// <summary>
    /// Reduction (%) applied to SoftMax when batteries remained unnecessarily high.
    /// Default: 5.0 — slight reduction to avoid overcharging if surplus was wasted.
    /// </summary>
    public double FeedbackSoftmaxReduction { get; set; } = 5.0;

    /// <summary>
    /// Amplification factor applied to the shortfall to compute the preventive threshold
    /// correction. correction = min(shortfall × factor, MaxPreventiveCorrection).
    /// Default: 1.5
    /// </summary>
    public double FeedbackPreventiveFactor { get; set; } = 1.5;

    /// <summary>
    /// Maximum (+%) correction applied to the preventive threshold when a battery fell too low.
    /// Default: 20.0
    /// </summary>
    public double FeedbackMaxPreventiveCorrection { get; set; } = 20.0;

    /// <summary>
    /// Reduction (%) applied to the preventive threshold when batteries stayed
    /// well above the minimum (margin > 20%).
    /// Default: 3.0
    /// </summary>
    public double FeedbackPreventiveReduction { get; set; } = 3.0;

    // ── Drift detection (ML-5) ─────────────────────────────────────────────

    /// <summary>
    /// R² degradation over the last N sessions that triggers a forced retrain.
    /// E.g.: 0.15 → if recent R² &lt; reference R² - 0.15 → immediate retrain.
    /// Set to 1.0 to disable drift detection.
    /// Default: 0.15
    /// </summary>
    public double DriftDetectionR2Threshold { get; set; } = 0.15;

    /// <summary>
    /// Nombre de sessions récentes utilisées pour calculer le R² de dérive.
    /// Défaut : 100
    /// </summary>
    public int DriftDetectionWindowSize { get; set; } = 100;
}

public class LoggingConfig
{
    public string Level     { get; set; } = "Information";
    public string? FilePath { get; set; } = "/data/logs/solar-worker.log";
}
