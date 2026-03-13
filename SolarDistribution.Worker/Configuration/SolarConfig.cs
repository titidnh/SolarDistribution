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

    /// <summary>
    /// Seuil EN DESSOUS duquel la charge solaire est STOPPÉE (Fix Bug #3 — anti-oscillation).
    ///
    /// Logique double-seuil (hystérésis de surplus) :
    ///   · Démarrage : surplus > SurplusBufferW      (200 W)
    ///   · Arrêt     : surplus &lt; SurplusStopBufferW  (80 W par défaut)
    ///   · Zone [80W–200W] : on maintient l'état précédent (ni démarrage, ni arrêt)
    ///
    /// Évite les ON/OFF toutes les 5 min quand le soleil fluctue autour du seuil
    /// de démarrage (passages nuageux ponctuels).
    ///
    /// Doit être &lt; SurplusBufferW. Mettre à 0 pour désactiver (comportement original).
    /// </summary>
    public double SurplusStopBufferW { get; set; } = 80;

    /// <summary>
    /// Nombre minimum de cycles consécutifs en charge avant d'autoriser un arrêt.
    ///
    /// Exemple : 3 cycles × 300 s = 15 min minimum de charge avant arrêt possible.
    /// Protège contre les faux-négatifs de surplus sur une rafale nuageuse de 5 min.
    /// Mettre à 0 pour désactiver.
    /// </summary>
    public int MinChargeDurationCycles { get; set; } = 3;

    /// <summary>
    /// Nombre de cycles consécutifs d'anomalie du surplus avant de déclencher
    /// une notification persistante dans Home Assistant.
    /// Défaut : 3
    /// </summary>
    public int MaxConsecutiveAnomaliesBeforeAlert { get; set; } = 3;
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
    /// [OPTIONNEL] Entité HA représentant la consommation totale du foyer (W).
    /// Ex: "sensor.power_consumption" ou "sensor.shellyem_channel_1_power"
    /// Utilisée pour calculer la moyenne roulante de consommation servant à projeter
    /// EstimatedConsumptionNextHoursWh dans TariffContext.
    /// </summary>
    public string? ConsumptionEntity { get; set; }

    /// <summary>
    /// [OPTIONNEL] Entités HA de consommation par zone/appareil (W).
    /// Permet de lire la conso de zones spécifiques (four, EV, chauffe-eau…)
    /// quand une entité de consommation globale n'est pas disponible ou pour
    /// affiner la projection de charge future.
    ///
    /// Ex:
    ///   - "sensor.ev_charger_power"
    ///   - "sensor.oven_power"
    ///   - "sensor.water_heater_power"
    ///
    /// Les valeurs sont sommées pour estimer la consommation totale du foyer
    /// quand ConsumptionEntity est absent. Si ConsumptionEntity EST configuré,
    /// les zones sont ignorées (redondance évitée).
    /// </summary>
    public List<string> ZoneConsumptionEntities { get; set; } = new();

    /// <summary>
    /// Nombre de cycles récents utilisés pour calculer la moyenne roulante de
    /// consommation depuis MariaDB. Cette moyenne projette la charge future dans
    /// ComputeAdaptiveGridChargeW pour affiner la décision de charge réseau.
    ///
    /// Ex: 12 cycles × 60s = 10 min de rolling average
    /// Défaut: 12 cycles. Mettre à 0 pour désactiver (utilise uniquement la lecture HA live).
    /// </summary>
    public int ConsumptionRollingWindowCycles { get; set; } = 12;

    /// <summary>
    /// Horizon de projection de la consommation estimée (en heures).
    /// EstimatedConsumptionNextHoursWh = avgConsumptionW × ConsumptionProjectionHours
    /// Cette valeur est soustraite de l'énergie solaire attendue dans le calcul
    /// de la charge réseau adaptative (ComputeAdaptiveGridChargeW).
    /// Défaut: 4h (correspond à SolarForecastHorizonHours).
    /// </summary>
    public double ConsumptionProjectionHours { get; set; } = 4.0;

    /// <summary>
    /// [OPTIONNEL — FORTEMENT RECOMMANDÉ]
    /// Entité HA : production solaire estimée AUJOURD'HUI (Wh).
    /// Ex: "sensor.solcast_pv_forecast_forecast_today"
    /// </summary>
    public string? ForecastTodayEntity { get; set; }

    /// <summary>
    /// [OPTIONNEL — FORTEMENT RECOMMANDÉ]
    /// Entité HA : production solaire estimée DEMAIN (Wh).
    /// Ex: "sensor.solcast_pv_forecast_forecast_tomorrow"
    /// </summary>
    public string? ForecastTomorrowEntity { get; set; }

    // ── Prévisions Solcast intra-journalières ──────────────────────────────────
    // Ces trois entités fournissent la courbe horaire réelle de production.
    // Elles remplacent le profil sinusoïdal simplifié dans ComputeAdaptiveGridChargeW
    // et permettent de savoir QUAND le solaire va monter, pas seulement COMBIEN.

    /// <summary>
    /// [OPTIONNEL] Entité HA : production solaire estimée CETTE HEURE (Wh).
    /// Ex: "sensor.solcast_pv_forecast_forecast_this_hour"
    /// Permet de savoir si le solaire monte EN CE MOMENT (ex: 09h, nuage partiel).
    /// </summary>
    public string? ForecastThisHourEntity { get; set; }

    /// <summary>
    /// [OPTIONNEL] Entité HA : production solaire estimée L'HEURE SUIVANTE (Wh).
    /// Ex: "sensor.solcast_pv_forecast_forecast_next_hour"
    /// Si cette valeur est élevée → inutile de charger depuis le réseau maintenant,
    /// le solaire prend le relais dans &lt; 1h.
    /// </summary>
    public string? ForecastNextHourEntity { get; set; }

    /// <summary>
    /// [OPTIONNEL] Entité HA : production solaire restante AUJOURD'HUI (Wh).
    /// Ex: "sensor.solcast_pv_forecast_forecast_remaining_today"
    /// Utilisé dans le calcul du bilan énergétique journalier (Feature 4) :
    /// si le solaire restant couvre le déficit batterie → pas besoin de charger du réseau.
    /// </summary>
    public string? ForecastRemainingTodayEntity { get; set; }

    /// <summary>
    /// (OPTIONNEL) Seuil de plausibilité supérieur pour le surplus (W).
    /// Ex: peak_installation_power × 1.1. Si le surplus observé dépasse
    /// cette valeur, le cycle est considéré comme anomal et ignoré.
    /// Null = désactivé.
    /// </summary>
    public double? MaxPlausibleSurplusW { get; set; }
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
    /// <summary>
    /// Puissance minimale en dessous de laquelle la batterie n'accepte pas la charge (W).
    ///
    /// Contrainte hardware : certaines batteries (ex: EcoFlow Delta) refusent ou ignorent
    /// toute consigne inférieure à ce seuil. Envoyer 50W à une batterie dont le minimum
    /// est 100W ne produit aucune charge réelle.
    ///
    /// Impact sur la distribution (surplus solaire) :
    ///   · PASS 1/2 : si surplusW &lt; HardwareMinChargeW → batterie skippée
    ///   · IdleCharge : même garde (remplace l'ancienne condition surplusW >= IdleChargeW)
    ///   · Emergency grid charge : ignore ce seuil — charge toujours
    ///   · Grid charge HC (PASS 3) : GridChargeAllowedW déjà calculé ≥ ce seuil
    ///
    /// Défaut 0 → désactivé. Pour EcoFlow Delta 3 : mettre à 100.
    /// </summary>
    public double HardwareMinChargeW { get; set; } = 0;

    public double IdleChargeW { get; set; } = 100;

    /// <summary>
    /// Zone morte SOC (%) autour de la cible SoftMax pour la charge réseau HC (Fix Bug #1).
    ///
    /// Exemple : SoftMaxPercent=90, SocHysteresisPercent=2
    ///   → recharge réseau autorisée seulement si SOC &lt; 88%
    ///   → entre 88% et 90% : 0W réseau (auto-décharge EcoFlow acceptée dans cette zone)
    ///   → SOC descend à 87.9% → recharge normale ≥ 100W
    ///
    /// Valeur recommandée : 2.0. Mettre à 0 pour désactiver.
    /// </summary>
    public double SocHysteresisPercent { get; set; } = 0.0;

    /// <summary>
    /// Hystérésis sur le seuil d'activation/arrêt de IdleChargeW (Anti-oscillation IdleCharge).
    ///
    /// Problème : avec IdleChargeW=100W, si le surplus oscille autour de 100W, on obtient
    /// un ON/OFF à chaque cycle : surplus=110W → IdleCharge ON (self-powered OFF),
    /// surplus=90W → IdleCharge OFF (self-powered ON), etc.
    /// Chaque transition déclenche des actions HA (ZeroWActions / NonZeroWActions)
    /// qui peuvent stresser le BMS EcoFlow.
    ///
    /// Solution double-seuil :
    ///   · Activation  : surplus >= IdleChargeW                         (ex: 100W)
    ///   · Arrêt       : surplus &lt; IdleChargeW - IdleStopBufferW        (ex: 100 - 30 = 70W)
    ///   · Zone morte  : [70W – 100W] → état précédent maintenu
    ///
    /// Valeur recommandée : 30W (≈ 30% de IdleChargeW=100W).
    /// Mettre à 0 pour désactiver (seuil unique à IdleChargeW, comportement original Bug #5).
    /// </summary>
    public double IdleStopBufferW { get; set; } = 30.0;

    public BatteryEntitiesConfig Entities { get; set; } = new();
    public double? EmergencyGridChargeBelowPercent { get; set; }
    public double? EmergencyGridChargeTargetPercent { get; set; }
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
    public double CurrentChargePowerMultiplier { get; set; } = 1.0;

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
}

public class LoggingConfig
{
    public string Level { get; set; } = "Information";
    public string? FilePath { get; set; } = "/data/logs/solar-worker.log";

    /// <summary>
    /// [OPTIONNEL] URL de l'instance Grafana Loki vers laquelle pousser les logs en JSON.
    /// Ex: "http://loki:3100"
    /// Laisser null/vide pour désactiver l'envoi vers Loki.
    /// </summary>
    public string? LokiUrl { get; set; }

    /// <summary>
    /// Labels Loki ajoutés à chaque log stream (key=value).
    /// Permettent de filtrer les logs dans Grafana via LogQL :
    ///   {app="solar-worker", env="prod"}
    /// </summary>
    public Dictionary<string, string> LokiLabels { get; set; } = new()
    {
        ["job"] = "solar-worker"
    };
}