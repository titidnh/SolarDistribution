using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SolarDistribution.Worker.Configuration;

public static class ConfigLoader
{
    private const string DefaultConfigPath = "/config/config.yaml";

    /// <summary>
    /// Charge config.yaml depuis le chemin spécifié (par défaut /config/config.yaml).
    /// Le chemin peut être surchargé via la variable d'environnement CONFIG_PATH.
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

        if (errors.Any())
            throw new InvalidOperationException(
                $"Configuration errors in '{path}':\n  - " + string.Join("\n  - ", errors));
    }
}
