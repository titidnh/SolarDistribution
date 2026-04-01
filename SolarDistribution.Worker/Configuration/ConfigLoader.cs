using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SolarDistribution.Worker.Configuration;

public static class ConfigLoader
{
    private const string DefaultConfigPath = "/config/config.yaml";

    /// <summary>
    /// Loads config.yaml from the specified path (default /config/config.yaml).
    /// The path can be overridden via the CONFIG_PATH environment variable.
    /// </summary>
    public static SolarConfig Load(string? overridePath = null)
    {
        string path = overridePath
            ?? Environment.GetEnvironmentVariable("CONFIG_PATH")
            ?? DefaultConfigPath;

        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"Configuration file not found at '{path}'. " +
                $"Mount your config.yaml to {DefaultConfigPath} or set CONFIG_PATH env var.", path);

        string yaml = File.ReadAllText(path);

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var config = deserializer.Deserialize<SolarConfig>(yaml);

        Validate(config, path);

        return config;
    }

    private static void Validate(SolarConfig config, string path)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(config.HomeAssistant.Url))
            errors.Add("homeassistant.url is required");

        if (string.IsNullOrWhiteSpace(config.HomeAssistant.Token))
            errors.Add("homeassistant.token is required");

        if (string.IsNullOrWhiteSpace(config.Solar.SurplusEntity))
            errors.Add("solar.surplus_entity is required");

        if (!config.Batteries.Any())
            errors.Add("At least one battery must be configured in 'batteries'");

        foreach (var b in config.Batteries)
        {
            if (b.CapacityWh <= 0)
                errors.Add($"Battery {b.Id} ({b.Name}): capacity_wh must be > 0");
            if (b.MaxChargeRateW <= 0)
                errors.Add($"Battery {b.Id} ({b.Name}): max_charge_rate_w must be > 0");
            if (string.IsNullOrWhiteSpace(b.Entities.Soc))
                errors.Add($"Battery {b.Id} ({b.Name}): entities.soc is required");
            if (string.IsNullOrWhiteSpace(b.Entities.ChargePower))
                errors.Add($"Battery {b.Id} ({b.Name}): entities.charge_power is required");
        }

        if (config.Heating.Enabled)
        {
            bool hasCentralThermostat = !string.IsNullOrWhiteSpace(config.Heating.ThermostatEntity);
            bool hasRoomThermostats = config.Heating.ThermostatEntities.Count > 0;
            bool hasCentralTemp = !string.IsNullOrWhiteSpace(config.Heating.IndoorTemperatureEntity);
            bool hasRoomTemps = config.Heating.IndoorTemperatureEntities.Count > 0;

            if (!hasCentralThermostat && !hasRoomThermostats && !hasCentralTemp && !hasRoomTemps)
            {
                errors.Add("heating: configure thermostat_entity|thermostat_entities or indoor_temperature_entity|indoor_temperature_entities when heating.enabled=true");
            }

            if (config.Heating.SamplingIntervalSeconds < 60)
                errors.Add("heating.sampling_interval_seconds must be >= 60");

            if (config.Heating.MlRetrainIntervalHours < 1)
                errors.Add("heating.ml_retrain_interval_hours must be >= 1");
            if (config.Heating.MlTrainingWindowDays < 30)
                errors.Add("heating.ml_training_window_days must be >= 30");
            if (config.Heating.MlMinSamplesForRetrain < 50)
                errors.Add("heating.ml_min_samples_for_retrain must be >= 50");
            if (config.Heating.PurgeHardDeleteAgeDays < config.Heating.PurgeCompressionAgeDays)
                errors.Add("heating.purge_hard_delete_age_days must be >= heating.purge_compression_age_days");

            var mode = (config.Heating.ZoneAggregationMode ?? string.Empty).Trim().ToLowerInvariant();
            if (mode is not ("average" or "min" or "max"))
                errors.Add("heating.zone_aggregation_mode must be one of: average|min|max");

                foreach (var src in config.Heating.Sources)
                {
                    if (string.IsNullOrWhiteSpace(src.Name))
                        errors.Add("heating.sources[].name is required");

                    var srcType = (src.Type ?? string.Empty).Trim().ToLowerInvariant();
                    if (srcType != "gas" && srcType != "heat_pump" && srcType != "electric")
                        errors.Add($"heating.sources[{src.Name}].type must be one of: gas|heat_pump|electric");

                    if (srcType == "gas" && src.BoilerEfficiency <= 0)
                        errors.Add($"heating.sources[{src.Name}].boiler_efficiency must be > 0");
                }

                if (config.Heating.Gas.Enabled)
                {
                    var meterMode = (config.Heating.Gas.MeterMode ?? string.Empty).Trim().ToLowerInvariant();
                    if (meterMode != "ha_entity" && meterMode != "ha_sensor" && meterMode != "manual")
                        errors.Add("heating.gas.meter_mode must be one of: ha_entity|ha_sensor|manual");

                    if (meterMode == "ha_entity" && string.IsNullOrWhiteSpace(config.Heating.Gas.MeterEntity))
                        errors.Add("heating.gas.meter_entity is required when heating.gas.meter_mode=ha_entity");

                    if (config.Heating.Gas.CalorificValueKwhPerM3 <= 0)
                        errors.Add("heating.gas.calorific_value_kwh_per_m3 must be > 0");

                    if (config.Heating.Gas.GasPricePerKwh <= 0 && string.IsNullOrWhiteSpace(config.Heating.Gas.GasPriceEntity))
                        errors.Add("heating.gas.gas_price_per_kwh must be > 0 or heating.gas.gas_price_entity must be configured");
                }

                foreach (var rule in config.Heating.SourceTimeRules)
                {
                    if (!rule.Enabled)
                        continue;

                    if (string.IsNullOrWhiteSpace(rule.PreferredSourceName))
                        errors.Add("heating.source_time_rules[].preferred_source_name is required when rule is enabled");

                    if (rule.StartHourLocal < 0 || rule.StartHourLocal > 23)
                        errors.Add("heating.source_time_rules[].start_hour_local must be in [0..23]");

                    if (rule.EndHourLocal < 0 || rule.EndHourLocal > 23)
                        errors.Add("heating.source_time_rules[].end_hour_local must be in [0..23]");

                    if (rule.MaxOverBestCostPct < 0)
                        errors.Add("heating.source_time_rules[].max_over_best_cost_pct must be >= 0");
                }

                var cc = config.Heating.ComfortConstraints;
                if (cc.CriticalDeltaTempC < 0)
                    errors.Add("heating.comfort_constraints.critical_delta_temp_c must be >= 0");
                if (cc.MaxMlEtaP90Minutes <= 0)
                    errors.Add("heating.comfort_constraints.max_ml_eta_p90_minutes must be > 0");
                if (cc.MaxComfortOverrideOverBestCostPct < 0)
                    errors.Add("heating.comfort_constraints.max_comfort_override_over_best_cost_pct must be >= 0");
                if (cc.MinSamplesForLearning < 1)
                    errors.Add("heating.comfort_constraints.min_samples_for_learning must be >= 1");
                if (cc.LearningRefreshMinutes < 1)
                    errors.Add("heating.comfort_constraints.learning_refresh_minutes must be >= 1");
        }

        if (errors.Any())
            throw new InvalidOperationException(
                $"Configuration errors in '{path}':\n  - " + string.Join("\n  - ", errors));
    }
}
