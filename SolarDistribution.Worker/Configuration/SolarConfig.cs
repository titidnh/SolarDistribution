namespace SolarDistribution.Worker.Configuration;

/// <summary>
/// Racine de la configuration — mappée depuis config/config.yaml monté dans le container.
/// </summary>
public class SolarConfig
{
    public HomeAssistantConfig  HomeAssistant { get; set; } = new();
    public PollingConfig        Polling       { get; set; } = new();
    public LocationConfig       Location      { get; set; } = new();
    public SolarConfig_Solar    Solar         { get; set; } = new();
    public List<BatteryConfig>  Batteries     { get; set; } = [];
    public MariaDbConfig        Database      { get; set; } = new();
    public MlConfig             Ml            { get; set; } = new();
    public LoggingConfig        Logging       { get; set; } = new();
}

public class HomeAssistantConfig
{
    /// <summary>URL de l'instance HA ex: http://192.168.1.100:8123</summary>
    public string Url   { get; set; } = string.Empty;

    /// <summary>Long-Lived Access Token généré dans le profil HA</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>Timeout HTTP en secondes pour les appels HA (défaut: 10)</summary>
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>Nombre de tentatives en cas d'erreur HA (défaut: 3)</summary>
    public int RetryCount { get; set; } = 3;
}

public class PollingConfig
{
    /// <summary>Intervalle entre chaque cycle de lecture/calcul/commande en secondes (défaut: 60)</summary>
    public int IntervalSeconds { get; set; } = 60;

    /// <summary>
    /// Mode simulation : lit les valeurs HA mais n'envoie PAS les commandes.
    /// Utile pour tester sans risque. (défaut: false)
    /// </summary>
    public bool DryRun { get; set; } = false;

    /// <summary>
    /// Différence minimale en W pour déclencher une nouvelle commande.
    /// Évite d'envoyer des commandes inutiles si la valeur n'a presque pas changé. (défaut: 10W)
    /// </summary>
    public double MinChangeTriggerW { get; set; } = 10;
}

public class LocationConfig
{
    /// <summary>Latitude pour Open-Meteo (ex: 50.85 pour Bruxelles)</summary>
    public double Latitude  { get; set; } = 50.85;

    /// <summary>Longitude pour Open-Meteo (ex: 4.35 pour Bruxelles)</summary>
    public double Longitude { get; set; } = 4.35;
}

public class SolarConfig_Solar
{
    /// <summary>
    /// Entité HA exposant la puissance surplus disponible en W.
    /// ex: sensor.solar_surplus_power, sensor.solax_export_power
    /// </summary>
    public string SurplusEntity { get; set; } = string.Empty;

    /// <summary>
    /// Entité HA pour la puissance PV totale (optionnel — pour le logging/ML)
    /// ex: sensor.solar_production_power
    /// </summary>
    public string? ProductionEntity { get; set; }

    /// <summary>
    /// Entité HA pour la consommation maison (optionnel — pour le logging/ML)
    /// ex: sensor.home_consumption_power
    /// </summary>
    public string? ConsumptionEntity { get; set; }
}

public class BatteryConfig
{
    public int    Id       { get; set; }
    public string Name     { get; set; } = string.Empty;
    public int    Priority { get; set; } = 1;

    /// <summary>Capacité totale en Wh</summary>
    public double CapacityWh     { get; set; }

    /// <summary>Puissance max de recharge en W</summary>
    public double MaxChargeRateW { get; set; }

    /// <summary>% minimum — en-dessous = URGENT</summary>
    public double MinPercent     { get; set; } = 20;

    /// <summary>Cible soft max % (défaut 80%)</summary>
    public double SoftMaxPercent { get; set; } = 80;

    /// <summary>Plafond absolu %</summary>
    public double HardMaxPercent { get; set; } = 100;

    public BatteryEntitiesConfig Entities { get; set; } = new();
}

public class BatteryEntitiesConfig
{
    /// <summary>Entité HA exposant le % de charge actuel (lecture)</summary>
    public string Soc          { get; set; } = string.Empty;

    /// <summary>
    /// Entité HA de type 'number' pour définir la puissance de recharge en W (écriture).
    /// Utilise le service number.set_value.
    /// ex: number.battery_1_charge_power, number.solax_battery_charge_max_current
    /// </summary>
    public string ChargePower  { get; set; } = string.Empty;

    /// <summary>
    /// Entité HA pour activer/désactiver la recharge (optionnel).
    /// Si défini, le switch est activé avant d'écrire la puissance.
    /// ex: switch.battery_1_charge_enable
    /// </summary>
    public string? ChargeSwitch { get; set; }

    /// <summary>
    /// Multiplicateur appliqué à la valeur W avant envoi à HA.
    /// Utile si l'entité HA attend des Ampères plutôt que des Watts.
    /// ex: 0.004167 pour convertir W → A sur 240V (A = W / 240)
    /// Défaut: 1.0 (W direct)
    /// </summary>
    public double ValueMultiplier { get; set; } = 1.0;

    /// <summary>
    /// Unité de la valeur envoyée à HA (pour le logging).
    /// Défaut: "W"
    /// </summary>
    public string ValueUnit { get; set; } = "W";
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
    /// Délai en heures après une session avant de collecter le feedback réel.
    /// Ex: 4.0 → on relit le SOC 4h après la décision pour voir l'effet réel.
    /// Trop court (< 1h) = on ne voit pas l'effet. Trop long (> 12h) = bruit.
    /// Recommandé : 3-6h.
    /// </summary>
    public double FeedbackDelayHours { get; set; } = 4.0;

    /// <summary>
    /// Fréquence de vérification des feedbacks en attente (en heures).
    /// Ex: 1.0 → vérifie toutes les heures si des sessions ont un feedback à collecter.
    /// Défaut : 1h
    /// </summary>
    public double FeedbackCheckIntervalHours { get; set; } = 1.0;

    /// <summary>
    /// Expression cron pour le ré-entraînement automatique.
    /// Syntaxe standard 5 champs : "minute heure jourMois mois jourSemaine"
    /// Ex :
    ///   "0 3 * * 0"   → dimanche à 3h00 UTC
    ///   "0 3 * * *"   → tous les jours à 3h00 UTC
    ///   "0 2 * * 1"   → lundi à 2h00 UTC
    /// Défaut : dimanche 3h
    /// </summary>
    public string RetrainCron { get; set; } = "0 3 * * 0";

    /// <summary>
    /// Nombre minimum de feedbacks VALIDES en base avant de déclencher l'entraînement.
    /// En dessous → algo déterministe seul.
    /// Recommandé : 50 minimum, idéalement 100+.
    /// </summary>
    public int MinFeedbackForRetrain { get; set; } = 50;
}

public class LoggingConfig
{
    public string Level     { get; set; } = "Information";
    public string? FilePath { get; set; } = "/data/logs/solar-worker.log";
}
