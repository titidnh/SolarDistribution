using SolarDistribution.Core.Services;
namespace SolarDistribution.Worker.Configuration;

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
    public WeatherConfig        Weather       { get; set; } = new();
    public LoggingConfig        Logging       { get; set; } = new();
}

public class WeatherConfig { public int RefreshIntervalMinutes { get; set; } = 15; }

public class HomeAssistantConfig
{
    public string Url            { get; set; } = string.Empty;
    public string Token          { get; set; } = string.Empty;
    public int    TimeoutSeconds { get; set; } = 10;
    public int    RetryCount     { get; set; } = 3;
}

public class PollingConfig
{
    public int    IntervalSeconds    { get; set; } = 60;
    public bool   DryRun             { get; set; } = false;
    public double MinChangeTriggerW  { get; set; } = 10;

    /// <summary>
    /// Buffer de sécurité en W soustrait du surplus avant distribution aux batteries.
    ///
    /// POURQUOI : la consommation maison fluctue en permanence (appareils qui s'allument,
    /// variations de charge). Sans buffer, si on envoie 100 % du surplus aux batteries
    /// et que la consommation monte de 200 W soudainement, on importe momentanément
    /// depuis le réseau le temps que le cycle suivant recalcule.
    ///
    /// COMMENT : surplusEffectif = surplusBrut - SurplusBufferW
    ///   → les 200 W restants continuent d'alimenter la maison directement
    ///   → les batteries ne reçoivent que le surplus vraiment disponible
    ///
    /// EXEMPLE (défaut 200 W) :
    ///   surplus HA = 912 W
    ///   distribué aux batteries = 912 - 200 = 712 W
    ///   les 200 W restants absorbent les pics de consommation sans import réseau
    ///
    /// Mettre à 0 pour désactiver (toute la puissance va aux batteries).
    /// </summary>
    public double SurplusBufferW { get; set; } = 200;
}

public class LocationConfig
{
    public double Latitude  { get; set; } = 50.85;
    public double Longitude { get; set; } = 4.35;
}

public class SolarConfig_Solar
{
    public string  SurplusMode       { get; set; } = "direct";
    public string  SurplusEntity     { get; set; } = string.Empty;
    public string? ProductionEntity  { get; set; }
    public string? ConsumptionEntity { get; set; }

    /// <summary>
    /// [OPTIONNEL — FORTEMENT RECOMMANDÉ]
    /// Entité HA : production solaire estimée AUJOURD'HUI (Wh).
    /// Ex: "sensor.solcast_pv_forecast_forecast_today"
    /// </summary>
    public string? ForecastTodayEntity    { get; set; }

    /// <summary>
    /// [OPTIONNEL — FORTEMENT RECOMMANDÉ]
    /// Entité HA : production solaire estimée DEMAIN (Wh).
    /// Ex: "sensor.solcast_pv_forecast_forecast_tomorrow"
    /// </summary>
    public string? ForecastTomorrowEntity { get; set; }
}

public class BatteryConfig
{
    public int    Id             { get; set; }
    public string Name           { get; set; } = string.Empty;
    public int    Priority       { get; set; } = 1;
    public double CapacityWh     { get; set; }
    public double MaxChargeRateW { get; set; }
    public double MinPercent     { get; set; } = 20;
    public double SoftMaxPercent { get; set; } = 80;
    public double HardMaxPercent { get; set; } = 100;

    /// <summary>
    /// Puissance de maintien envoyée à la batterie quand elle a atteint sa cible (SoftMax ou HardMax).
    /// Au lieu d'envoyer 0 W — ce qui peut dérouter certains BMS — on envoie cette valeur symbolique
    /// pour indiquer "charge autorisée, mais quasi rien à absorber".
    ///
    /// Avantages :
    ///   • Évite le cycling on/off du BMS sur certains onduleurs
    ///   • Permet à l'interface HA de voir une puissance > 0 = "en charge"
    ///   • Absorbe les micro-surplus résiduels (arrondi, bruit du compteur)
    ///
    /// Défaut : 100 W (override possible par batterie)
    /// Mettre à 0 pour comportement standard (coupe la charge à la cible).
    /// </summary>
    public double IdleChargeW    { get; set; } = 100;

    public BatteryEntitiesConfig Entities { get; set; } = new();
    public double? EmergencyGridChargeBelowPercent   { get; set; }
    public double? EmergencyGridChargeTargetPercent  { get; set; }
}

public class HaConditionalAction
{
    public string  Type     { get; set; } = "turn_on";
    public string? EntityId { get; set; }
    public string? Domain   { get; set; }
    public string? Service  { get; set; }
    public Dictionary<string, object>? Data { get; set; }
    public string? Label    { get; set; }
}

public class BatteryEntitiesConfig
{
    public string  Soc                    { get; set; } = string.Empty;
    public string  ChargePower            { get; set; } = string.Empty;

    /// <summary>
    /// [OPTIONNEL — FORTEMENT RECOMMANDÉ]
    /// Entité HA exposant la puissance de charge RÉELLE actuellement mesurée (W).
    ///
    /// POURQUOI C'EST CRITIQUE :
    ///   Le surplus HA (P1 ou sensor dédié) est déjà NET de la charge batterie actuelle.
    ///   Si la batterie charge déjà à 200 W et que P1 = -912 W :
    ///     → surplus brut apparent = 912 W
    ///     → mais 200 W de cette batterie sont DÉJÀ comptés dedans
    ///   Sans cette entité, le Worker va ordonner 912 W → gain réel = seulement 712 W.
    ///   Avec cette entité, le Worker fait : surplus_corrigé = 912 + 200 = 1112 W
    ///   → il ordonne 1112 W aux batteries → gain réel = 912 W (correct).
    ///
    /// EXEMPLES selon matériel :
    ///   EcoFlow (MQTT/HA)   : sensor.delta3_salon_ac_charge_power_w
    ///   Victron             : sensor.victron_battery_charge_power
    ///   Solis/SolarEdge     : sensor.inverter_battery_charge_power
    ///   Générique           : Chercher "charge power" / "puissance charge" dans HA → États
    ///
    /// Si absent → le surplus utilisé est le surplus brut HA (peut sous-estimer le disponible).
    /// </summary>
    public string? CurrentChargePowerEntity { get; set; }

    /// <summary>
    /// Multiplicateur appliqué à la valeur lue depuis CurrentChargePowerEntity.
    /// Défaut 1.0 (W). Mettre -1.0 si la valeur est négative quand la batterie charge.
    /// </summary>
    public double  CurrentChargePowerMultiplier { get; set; } = 1.0;

    public string? MaxChargeRateEntity    { get; set; }
    public string? ChargeSwitch           { get; set; }
    public double  ValueMultiplier        { get; set; } = 1.0;
    public double  MaxRateReadMultiplier  { get; set; } = 1.0;
    public string  ValueUnit              { get; set; } = "W";
    public List<HaConditionalAction> ZeroWActions    { get; set; } = new();
    public List<HaConditionalAction> NonZeroWActions { get; set; } = new();
}

public class MariaDbConfig
{
    public string ConnectionString { get; set; } =
        "Server=localhost;Port=3306;Database=solar_distribution;User=solar_user;Password=CHANGE_ME;CharSet=utf8mb4;";
}

public class MlConfig
{
    public string ModelDirectory               { get; set; } = "/data/ml_models";
    public double FeedbackDelayHours           { get; set; } = 4.0;
    public double FeedbackCheckIntervalHours   { get; set; } = 1.0;
    public string RetrainCron                  { get; set; } = "0 3 * * 0";
    public int    MinFeedbackForRetrain        { get; set; } = 50;
    public double FeedbackSoftmaxCorrectionFactor   { get; set; } = 15.0;
    public double FeedbackSoftmaxReduction          { get; set; } = 5.0;
    public double FeedbackPreventiveFactor          { get; set; } = 1.5;
    public double FeedbackMaxPreventiveCorrection   { get; set; } = 20.0;
    public double FeedbackPreventiveReduction       { get; set; } = 3.0;
    public double DriftDetectionR2Threshold         { get; set; } = 0.15;
    public int    DriftDetectionWindowSize          { get; set; } = 100;
}

public class LoggingConfig
{
    public string  Level    { get; set; } = "Information";
    public string? FilePath { get; set; } = "/data/logs/solar-worker.log";
}
