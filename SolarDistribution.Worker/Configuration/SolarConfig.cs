using SolarDistribution.Core.Services;
namespace SolarDistribution.Worker.Configuration;

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

public class WeatherConfig { public int RefreshIntervalMinutes { get; set; } = 15; }

public class HomeAssistantConfig
{
    public string Url { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 10;
    public int RetryCount { get; set; } = 3;
}

public class PollingConfig
{
    public int IntervalSeconds { get; set; } = 60;
    public bool DryRun { get; set; } = false;
    public double MinChangeTriggerW { get; set; } = 10;

    /// <summary>
    /// Safety buffer in W subtracted from surplus before battery distribution.
    ///
    /// WHY: home consumption constantly fluctuates (devices turning on,
    /// load variations). Without a buffer, if 100% of surplus is sent to batteries
    /// and consumption suddenly rises by 200 W, temporary grid import occurs
    /// until the next cycle recalculates.
    ///
    /// HOW: effectiveSurplus = rawSurplus - SurplusBufferW
    ///   → remaining 200 W continue feeding the house directly
    ///   → batteries receive only truly available surplus
    ///
    /// EXAMPLE (default 200 W):
    ///   HA surplus = 912 W
    ///   distributed to batteries = 912 - 200 = 712 W
    ///   remaining 200 W absorb consumption spikes without grid import
    ///
    /// Set to 0 to disable (all power goes to batteries).
    /// </summary>
    public double SurplusBufferW { get; set; } = 200;

    /// <summary>
    /// Threshold BELOW which solar charging is STOPPED (Fix Bug #3 — anti-oscillation).
    ///
    /// Dual-threshold logic (surplus hysteresis):
    ///   · Start : surplus > SurplusBufferW      (200 W)
    ///   · Stop  : surplus &lt; SurplusStopBufferW  (80 W default)
    ///   · Zone [80W–200W]: keep previous state (neither start nor stop)
    ///
    /// Prevents ON/OFF every 5 min when sunlight fluctuates around
    /// the start threshold (short cloud passages).
    ///
    /// Must be &lt; SurplusBufferW. Set to 0 to disable (original behavior).
    /// </summary>
    public double SurplusStopBufferW { get; set; } = 80;

    /// <summary>
    /// Minimum number of consecutive charging cycles before allowing a stop.
    ///
    /// Example: 3 cycles × 300 s = 15 min minimum charging time before stop.
    /// Protects against surplus false-negatives during a 5 min cloud burst.
    /// Set to 0 to disable.
    /// </summary>
    public int MinChargeDurationCycles { get; set; } = 3;

    /// <summary>
    /// Number of consecutive surplus-anomaly cycles before triggering
    /// a persistent notification in Home Assistant.
    /// Default: 3
    /// </summary>
    public int MaxConsecutiveAnomaliesBeforeAlert { get; set; } = 3;

    /// <summary>
    /// Enables sending persistent notifications in Home Assistant
    /// when several consecutive cycles show abnormal surplus.
    /// Set to false to disable these notifications (Observed surplus).
    /// Default: true
    /// </summary>
    public bool EnableSurplusAnomalyNotifications { get; set; } = true;
}

public class LocationConfig
{
    public double Latitude { get; set; } = 50.85;
    public double Longitude { get; set; } = 4.35;
}

public class SolarConfig_Solar
{
    public string SurplusMode { get; set; } = "direct";
    public string SurplusEntity { get; set; } = string.Empty;
    public string? ProductionEntity { get; set; }

    /// <summary>
    /// [OPTIONAL] HA entity representing total household consumption (W).
    /// Ex: "sensor.power_consumption" or "sensor.shellyem_channel_1_power"
    /// Used to compute the rolling consumption average used to project
    /// EstimatedConsumptionNextHoursWh dans TariffContext.
    /// </summary>
    public string? ConsumptionEntity { get; set; }

    /// <summary>
    /// [OPTIONAL] HA entities for zone/device consumption (W).
    /// Allows reading consumption from specific zones (oven, EV, water heater...)
    /// when a global consumption entity is not available or to
    /// refine future load projection.
    ///
    /// Ex:
    ///   - "sensor.ev_charger_power"
    ///   - "sensor.oven_power"
    ///   - "sensor.water_heater_power"
    ///
    /// Values are summed to estimate total household consumption
    /// when ConsumptionEntity is absent. If ConsumptionEntity IS configured,
    /// zones are ignored (redundancy avoided).
    /// </summary>
    public List<string> ZoneConsumptionEntities { get; set; } = new();

    /// <summary>
    /// Number of recent cycles used to compute rolling average consumption
    /// from MariaDB. This average projects future load in
    /// ComputeAdaptiveGridChargeW to refine the grid-charging decision.
    ///
    /// Ex: 12 cycles × 60s = 10 min rolling average
    /// Default: 12 cycles. Set to 0 to disable (uses only live HA readings).
    /// </summary>
    public int ConsumptionRollingWindowCycles { get; set; } = 12;

    /// <summary>
    /// Projection horizon for estimated consumption (in hours).
    /// EstimatedConsumptionNextHoursWh = avgConsumptionW × ConsumptionProjectionHours
    /// This value is subtracted from expected solar energy in
    /// adaptive grid charging (ComputeAdaptiveGridChargeW).
    /// Default: 4h (matches SolarForecastHorizonHours).
    /// </summary>
    public double ConsumptionProjectionHours { get; set; } = 4.0;

    /// <summary>
    /// [OPTIONAL — STRONGLY RECOMMENDED]
    /// HA entity: estimated solar production TODAY (Wh).
    /// Ex: "sensor.solcast_pv_forecast_forecast_today"
    /// </summary>
    public string? ForecastTodayEntity { get; set; }

    /// <summary>
    /// [OPTIONAL — STRONGLY RECOMMENDED]
    /// HA entity: estimated solar production TOMORROW (Wh).
    /// Ex: "sensor.solcast_pv_forecast_forecast_tomorrow"
    /// </summary>
    public string? ForecastTomorrowEntity { get; set; }

    // ── Intraday Solcast forecasts ───────────────────────────────────────────
    // These three entities provide the real hourly production curve.
    // They replace the simplified sinusoidal profile in ComputeAdaptiveGridChargeW
    // and show WHEN solar will ramp up, not just HOW MUCH.

    /// <summary>
    /// [OPTIONAL] HA entity: estimated solar production THIS HOUR (Wh).
    /// Ex: "sensor.solcast_pv_forecast_forecast_this_hour"
    /// Helps detect whether solar is ramping up RIGHT NOW (e.g., 09:00, partial cloud).
    /// </summary>
    public string? ForecastThisHourEntity { get; set; }

    /// <summary>
    /// [OPTIONAL] HA entity: estimated solar production NEXT HOUR (Wh).
    /// Ex: "sensor.solcast_pv_forecast_forecast_next_hour"
    /// If this value is high → no need to charge from grid now,
    /// solar will take over in &lt; 1h.
    /// </summary>
    public string? ForecastNextHourEntity { get; set; }

    /// <summary>
    /// [OPTIONAL] HA entity: estimated REMAINING solar production TODAY (Wh).
    /// Ex: "sensor.solcast_pv_forecast_forecast_remaining_today"
    /// Used in daily energy balance computation (Feature 4):
    /// if remaining solar covers battery deficit → no need for grid charging.
    /// </summary>
    public string? ForecastRemainingTodayEntity { get; set; }

    /// <summary>
    /// (OPTIONAL) Upper plausibility threshold for surplus (W).
    /// Ex: peak_installation_power × 1.1. If observed surplus exceeds
    /// this value, the cycle is considered anomalous and ignored.
    /// Null = disabled.
    /// </summary>
    public double? MaxPlausibleSurplusW { get; set; }

    /// <summary>
    /// [ML-7 OPTIONAL] HA entity exposing instantaneous grid import power (W).
    /// Ex: "sensor.p1_grid_import_power" or "sensor.shellyem_channel_1_power_import"
    ///
    /// WHY: allows FeedbackEvaluator to detect whether power was imported
    /// from the grid in the N hours after a session (DidImportFromGrid).
    /// This binary label feeds the ShouldChargeFromGrid classification model.
    ///
    /// CONVENTION: value should be positive when importing, zero or negative when
    /// exporting. Use GridImportEntityMultiplier = -1 if the signal is inverted.
    /// </summary>
    public string? GridImportEntity { get; set; }

    /// <summary>
    /// Multiplier applied to value read from GridImportEntity (default 1.0).
    /// Set -1.0 if value is negative when importing.
    /// </summary>
    public double GridImportEntityMultiplier { get; set; } = 1.0;

    /// <summary>
    /// Threshold in W above which grid import is considered significant (default 50W).
    /// Filters sensor noise (P1 offset, micro-imports due to smoothing).
    /// </summary>
    public double GridImportSignificantThresholdW { get; set; } = 50.0;
}

public class BatteryConfig
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Priority { get; set; } = 1;
    public double CapacityWh { get; set; }
    public double MaxChargeRateW { get; set; }
    public double MinPercent { get; set; } = 20;
    public double SoftMaxPercent { get; set; } = 80;
    public double HardMaxPercent { get; set; } = 100;

    /// <summary>
    /// Keep-alive power sent to battery when target is reached (SoftMax or HardMax).
    /// Instead of sending 0 W — which can confuse some BMS — this symbolic value is sent
    /// to indicate "charging allowed, but almost nothing to absorb".
    ///
    /// Benefits:
    ///   • Avoids BMS on/off cycling on some inverters
    ///   • Lets HA UI show power > 0 = "charging"
    ///   • Absorbs residual micro-surplus (rounding, meter noise)
    ///
    /// Default: 100 W (overridable per battery)
    /// Set to 0 for standard behavior (stop charging at target).
    /// </summary>
    /// <summary>
    /// Minimum power below which the battery does not accept charging (W).
    ///
    /// Hardware constraint: some batteries (e.g., EcoFlow Delta) reject or ignore
    /// any setpoint below this threshold. Sending 50W to a battery with a 100W minimum
    /// produces no real charging.
    ///
    /// Impact on distribution (solar surplus):
    ///   · PASS 1/2: if surplusW &lt; HardwareMinChargeW → battery skipped
    ///   · IdleCharge: same guard (replaces old condition surplusW >= IdleChargeW)
    ///   · Emergency grid charge: ignores this threshold — always charges
    ///   · Off-peak grid charge (PASS 3): GridChargeAllowedW already computed ≥ this threshold
    ///
    /// Default 0 → disabled. For EcoFlow Delta 3: set to 100.
    /// </summary>
    public double HardwareMinChargeW { get; set; } = 0;

    public double IdleChargeW { get; set; } = 100;

    /// <summary>
    /// SOC dead-band (%) around SoftMax target for off-peak grid charge (Fix Bug #1).
    ///
    /// Example: SoftMaxPercent=90, SocHysteresisPercent=2
    ///   → grid charging allowed only if SOC &lt; 88%
    ///   → between 88% and 90%: 0W grid (EcoFlow self-discharge accepted in this zone)
    ///   → SOC drops to 87.9% → normal charging resumes ≥ 100W
    ///
    /// Recommended value: 2.0. Set to 0 to disable.
    /// </summary>
    public double SocHysteresisPercent { get; set; } = 0.0;

    /// <summary>
    /// Hysteresis on IdleChargeW start/stop threshold (IdleCharge anti-oscillation).
    ///
    /// Problem: with IdleChargeW=100W, if surplus oscillates around 100W, it causes
    /// ON/OFF each cycle: surplus=110W → IdleCharge ON (self-powered OFF),
    /// surplus=90W → IdleCharge OFF (self-powered ON), etc.
    /// Each transition triggers HA actions (ZeroWActions / NonZeroWActions)
    /// that can stress the EcoFlow BMS.
    ///
    /// Dual-threshold solution:
    ///   · Activation  : surplus >= IdleChargeW                         (ex: 100W)
    ///   · Stop        : surplus &lt; IdleChargeW - IdleStopBufferW        (ex: 100 - 30 = 70W)
    ///   · Dead-band   : [70W – 100W] → previous state maintained
    ///
    /// Recommended value: 30W (≈ 30% of IdleChargeW=100W).
    /// Set to 0 to disable (single threshold at IdleChargeW, original Bug #5 behavior).
    /// </summary>
    public double IdleStopBufferW { get; set; } = 30.0;

    public BatteryEntitiesConfig Entities { get; set; } = new();
    public double? EmergencyGridChargeBelowPercent { get; set; }
    public double? EmergencyGridChargeTargetPercent { get; set; }

    /// <summary>
    /// [ML-8 OPTIONAL] Cycle threshold above which an HA alert is emitted.
    ///
    /// When cycle counter (Entities.CycleCountEntity) exceeds this value,
    /// the Worker:
    ///   1. Emits a continuous LogWarning each cycle (visible in Grafana/Loki)
    ///   2. Sends a persistent notification in HA (persistent_notification.create)
    ///   3. Reduces battery effective priority via CycleAgingFactor
    ///
    /// Recommended value by chemistry:
    ///   LiFePO4 (EcoFlow, Pylontech): 3000-6000 cycles depending on manufacturer specs
    ///   Classic Li-ion             : 500-1000 cycles
    ///   Null = disabled (no alert)
    /// </summary>
    public int? MaxRecommendedCycles { get; set; }

    /// <summary>
    /// [ML-8] Priority reduction factor per lifecycle cycle (default 0.0001).
    ///
    /// Effective priority is modulated as follows:
    ///   effectivePriority = basePriority × (1 − CycleAgingFactor × cycleCount)
    ///   → clamped to [basePriority × 0.5, basePriority] to avoid excessive gap
    ///
    /// Example with CycleAgingFactor = 0.0001 and cycleCount = 2000:
    ///   reduction = 0.0001 × 2000 = 20% → priority reduced by 20%
    ///   A new battery (0 cycles) and a battery at 2000 cycles with priority 2:
    ///   new battery      : effectivePriority = 2
    ///   older battery    : effectivePriority = 2 × (1 − 0.2) = 1.6
    ///   → surplus is prioritized toward the newer battery.
    ///
    /// Set to 0 to disable cycle-based weighting (equal treatment).
    /// </summary>
    public double CycleAgingFactor { get; set; } = 0.0001;
}

public class HaConditionalAction
{
    public string Type { get; set; } = "turn_on";
    public string? EntityId { get; set; }
    public string? Domain { get; set; }
    public string? Service { get; set; }
    public Dictionary<string, object>? Data { get; set; }
    public string? Label { get; set; }
}

public class BatteryEntitiesConfig
{
    public string Soc { get; set; } = string.Empty;
    public string ChargePower { get; set; } = string.Empty;

    /// <summary>
    /// [OPTIONAL — STRONGLY RECOMMENDED]
    /// HA entity exposing currently measured REAL charging power (W).
    ///
    /// WHY THIS IS CRITICAL:
    ///   HA surplus (P1 or dedicated sensor) is already NET of current battery charging.
    ///   If battery is already charging at 200 W and P1 = -912 W:
    ///     → surplus brut apparent = 912 W
    ///     → but 200 W from this battery are ALREADY included
    ///   Without this entity, Worker will command 912 W → real gain = only 712 W.
    ///   With this entity, Worker computes: corrected_surplus = 912 + 200 = 1112 W
    ///   → commands 1112 W to batteries → real gain = 912 W (correct).
    ///
    /// EXAMPLES by hardware:
    ///   EcoFlow (MQTT/HA)   : sensor.delta3_salon_ac_charge_power_w
    ///   Victron             : sensor.victron_battery_charge_power
    ///   Solis/SolarEdge     : sensor.inverter_battery_charge_power
    ///   Generic             : Search "charge power" in HA → States
    ///
    /// If missing → used surplus is raw HA surplus (may underestimate available power).
    /// </summary>
    public string? CurrentChargePowerEntity { get; set; }

    /// <summary>
    /// Multiplier applied to value read from CurrentChargePowerEntity.
    /// Default 1.0 (W). Set -1.0 if value is negative when battery is charging.
    /// </summary>
    public double CurrentChargePowerMultiplier { get; set; } = 1.0;

    /// <summary>
    /// [ML-8 OPTIONAL] HA entity exposing number of full battery charge cycles.
    ///
    /// WHY:
    ///   A battery with more cycles is more degraded and has reduced effective capacity.
    ///   By reading this counter, the algorithm can modulate charge priority
    ///   to preserve lifetime of the most used batteries.
    ///
    /// EXAMPLES by hardware:
    ///   EcoFlow Delta 3     : sensor.delta3_salon_battery_cycles
    ///   Victron BMS         : sensor.victron_battery_cycles
    ///   Pylontech           : sensor.pylontech_cycle_count
    ///   Generic             : Search "cycle" or "cycles" in HA battery entities
    ///
    /// If missing → CycleCount = 0, no cycle-based weighting.
    /// </summary>
    public string? CycleCountEntity { get; set; }

    public string? MaxChargeRateEntity { get; set; }
    public string? ChargeSwitch { get; set; }
    public double ValueMultiplier { get; set; } = 1.0;
    public double MaxRateReadMultiplier { get; set; } = 1.0;
    public string ValueUnit { get; set; } = "W";
    public List<HaConditionalAction> ZeroWActions { get; set; } = new();
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
    public double FeedbackDelayHours { get; set; } = 4.0;
    public double FeedbackCheckIntervalHours { get; set; } = 1.0;
    public string RetrainCron { get; set; } = "0 3 * * 0";
    public int MinFeedbackForRetrain { get; set; } = 50;
    public double FeedbackSoftmaxCorrectionFactor { get; set; } = 15.0;
    public double FeedbackSoftmaxReduction { get; set; } = 5.0;
    public double FeedbackPreventiveFactor { get; set; } = 1.5;
    public double FeedbackMaxPreventiveCorrection { get; set; } = 20.0;
    public double FeedbackPreventiveReduction { get; set; } = 3.0;
    public double DriftDetectionR2Threshold { get; set; } = 0.15;
    public int DriftDetectionWindowSize { get; set; } = 100;

    // ── Training window and calendar sampling ───────────────────────────────

    /// <summary>
    /// Maximum data window used for training (days).
    /// 730 = 2 years — covers 2 full seasonal cycles for weather/calendar patterns.
    /// </summary>
    public int TrainingWindowDays { get; set; } = 730;

    /// <summary>
    /// Target number of sessions to load for training.
    /// Stratified sampling guarantees uniform distribution over the window,
    /// independent of actual DB volume.
    /// Recommended: 15,000-25,000 for a good memory/quality balance.
    /// </summary>
    public int TrainingTargetSamples { get; set; } = 20_000;

    /// <summary>
    /// Temporal decay half-life in days (τ for exp(-age/τ)).
    /// 180 = sessions older than 6 months have weight ~37% of a recent session.
    /// Floor <see cref="TrainingDecayFloor"/> prevents old data from being
    /// completely ignored (useful for rare seasonal patterns).
    /// </summary>
    public double TrainingDecayHalfLifeDays { get; set; } = 180.0;

    /// <summary>
    /// Guaranteed minimum weight after decay (0.0-1.0).
    /// 0.25 = even a 2-year session counts at least 25% of a recent session.
    /// Required so ML sees both winters in the 2-year window.
    /// </summary>
    public double TrainingDecayFloor { get; set; } = 0.25;

    // ── Automatic purge and compression ─────────────────────────────────────

    /// <summary>
    /// Age after which sessions become eligible for compression (days).
    /// More recent sessions are always kept in full.
    /// Default: 90 days.
    /// </summary>
    public int PurgeCompressionAgeDays { get; set; } = 90;

    /// <summary>
    /// After compression: keep 1 session per N-minute slot in
    /// non-critical time windows (sessions with normal quality weight).
    /// Default: 30 min → roughly divides old data volume by 30.
    /// High-weight sessions (surplusWasted, grid import) are always retained.
    /// </summary>
    public int PurgeCompressionSlotMinutes { get; set; } = 30;

    /// <summary>
    /// Age beyond which sessions are permanently deleted (days).
    /// Must be ≥ TrainingWindowDays to avoid losing ML-useful data.
    /// Default: 750 days (~2 years + margin).
    /// </summary>
    public int PurgeHardDeleteAgeDays { get; set; } = 750;
}

public class LoggingConfig
{
    public string Level { get; set; } = "Information";
    public string? FilePath { get; set; } = "/data/logs/solar-worker.log";

    /// <summary>
    /// [OPTIONAL] URL of Grafana Loki instance where JSON logs are pushed.
    /// Ex: "http://loki:3100"
    /// Leave null/empty to disable sending to Loki.
    /// </summary>
    public string? LokiUrl { get; set; }

    /// <summary>
    /// Loki labels added to each log stream (key=value).
    /// Allow filtering logs in Grafana via LogQL:
    ///   {app="solar-worker", env="prod"}
    /// </summary>
    public Dictionary<string, string> LokiLabels { get; set; } = new()
    {
        ["job"] = "solar-worker"
    };
}