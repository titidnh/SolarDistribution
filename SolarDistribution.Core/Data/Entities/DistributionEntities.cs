namespace SolarDistribution.Core.Data.Entities;

public class DistributionSession
{
    public long     Id              { get; set; }
    public DateTime RequestedAt     { get; set; } = DateTime.UtcNow;
    public double   SurplusW        { get; set; }
    public double   TotalAllocatedW { get; set; }
    public double   UnusedSurplusW  { get; set; }
    public double   GridChargedW    { get; set; }
    public string   DecisionEngine  { get; set; } = "Deterministic";
    public double?  MlConfidenceScore { get; set; }

    // Contexte tarifaire standard
    public string? TariffSlotName            { get; set; }
    public double? TariffPricePerKwh         { get; set; }
    public bool    WasGridChargeFavorable     { get; set; }
    public bool    SolarExpectedSoon          { get; set; }
    public double? HoursToNextFavorableTariff { get; set; }
    public double? AvgSolarForecastWm2        { get; set; }
    public double? TariffMaxSavingsPerKwh     { get; set; }

    // Contexte adaptatif étendu (ML-7)
    /// <summary>Heures restantes dans le créneau HC au moment de la session.</summary>
    public double? HoursRemainingInSlot       { get; set; }
    /// <summary>Heures avant le prochain ensoleillement (null si pas prévu ou nuit totale).</summary>
    public double? HoursUntilSolar            { get; set; }
    /// <summary>True si au moins une batterie était en charge d'urgence réseau.</summary>
    public bool    HadEmergencyGridCharge     { get; set; }
    /// <summary>Puissance réseau adaptative effective moyenne (W), hors urgence.</summary>
    public double? EffectiveGridChargeW       { get; set; }

    // Prévisions HA installation-spécifiques (ML-8)
    /// <summary>Production solaire estimée aujourd'hui depuis HA (Wh). Null si non configuré.</summary>
    public double? ForecastTodayWh            { get; set; }
    /// <summary>Production solaire estimée demain depuis HA (Wh). Null si non configuré.</summary>
    public double? ForecastTomorrowWh         { get; set; }

    // Load forecasting (consommation estimée)
    /// <summary>
    /// Consommation maison mesurée au moment de la session (W).
    /// Null si ConsumptionEntity et ZoneConsumptionEntities ne sont pas configurés.
    /// </summary>
    public double? MeasuredConsumptionW       { get; set; }
    /// <summary>
    /// Consommation estimée sur les prochaines heures (Wh) — moyenne roulante × horizon.
    /// Nulle si insuffisamment de cycles historiques ou entités non configurées.
    /// Utilisée par ComputeAdaptiveGridChargeW pour affiner la charge réseau.
    /// </summary>
    public double? EstimatedConsumptionNextHoursWh { get; set; }

    // Intraday + bilan journalier (Feature 3 & 4)
    /// <summary>
    /// Production Solcast restante aujourd'hui (Wh) au moment de la session.
    /// Alimente le calcul du bilan énergétique journalier et le ML model.
    /// </summary>
    public double? ForecastRemainingTodayWh   { get; set; }
    /// <summary>
    /// Déficit énergétique journalier (Wh) : capacité × (softMax - avgSOC) − solaire_restant.
    /// Positif → charge réseau justifiée. Négatif/nul → solaire suffit → charge bloquée.
    /// Null si ForecastRemainingTodayWh absent.
    /// </summary>
    public double? EnergyDeficitTodayWh       { get; set; }
    /// <summary>
    /// Énergie solaire réellement consommée (autoconsommation) depuis minuit (Wh).
    /// Calculé via : ForecastTodayWh(début_journée) − ForecastRemainingTodayWh(maintenant).
    /// Feature ML : permet d'apprendre l'écart entre le forecast et la réalité de consommation.
    /// Null si ForecastRemainingTodayWh ou ForecastTodayWh sont absents.
    /// </summary>
    public double? DailySolarConsumedWh       { get; set; }

    public ICollection<BatterySnapshot> BatterySnapshots { get; set; } = new List<BatterySnapshot>();
    public WeatherSnapshot?  Weather      { get; set; }
    public MLPredictionLog?  MlPrediction { get; set; }
    public SessionFeedback?  Feedback     { get; set; }
}

public class SessionFeedback
{
    public long     Id          { get; set; }
    public long     SessionId   { get; set; }
    public DateTime CollectedAt { get; set; } = DateTime.UtcNow;
    public double   FeedbackDelayHours    { get; set; }
    public string   ObservedSocJson       { get; set; } = "{}";
    public double   AvgSocAtFeedback      { get; set; }
    public double   MinSocAtFeedback      { get; set; }
    public double   EnergyEfficiencyScore { get; set; }
    public double   AvailabilityScore     { get; set; }
    public double   ObservedOptimalSoftMax    { get; set; }
    public double   ObservedOptimalPreventive { get; set; }
    public double   CompositeScore            { get; set; }
    public FeedbackStatus Status        { get; set; } = FeedbackStatus.Pending;
    public string?        InvalidReason { get; set; }
    public DistributionSession Session  { get; set; } = null!;
}

public enum FeedbackStatus { Pending, Valid, Invalid, Skipped }

public class BatterySnapshot
{
    public long   Id        { get; set; }
    public long   SessionId { get; set; }
    public int    BatteryId { get; set; }
    public double CapacityWh           { get; set; }
    public double MaxChargeRateW       { get; set; }
    public double MinPercent           { get; set; }
    public double SoftMaxPercent       { get; set; }
    public double CurrentPercentBefore { get; set; }
    public double CurrentPercentAfter  { get; set; }
    public int    Priority             { get; set; }
    public bool   WasUrgent            { get; set; }
    public double AllocatedW           { get; set; }
    public bool   IsGridCharge         { get; set; }
    /// <summary>True si cette charge réseau était déclenchée par urgence SOC.</summary>
    public bool   IsEmergencyGridCharge { get; set; }
    /// <summary>Puissance réseau adaptative autorisée pour cette batterie (W).</summary>
    public double GridChargeAllowedW   { get; set; }
    public string Reason               { get; set; } = string.Empty;
    public DistributionSession Session { get; set; } = null!;
}

public class WeatherSnapshot
{
    public long     Id          { get; set; }
    public long     SessionId   { get; set; }
    public DateTime FetchedAt   { get; set; }
    public double   Latitude    { get; set; }
    public double   Longitude   { get; set; }
    public double   TemperatureC        { get; set; }
    public double   CloudCoverPercent   { get; set; }
    public double   PrecipitationMmH    { get; set; }
    public double   DirectRadiationWm2  { get; set; }
    public double   DiffuseRadiationWm2 { get; set; }
    public double   DaylightHours       { get; set; }
    public double   HoursUntilSunset    { get; set; }
    public string   RadiationForecast12hJson { get; set; } = "[]";
    public string   CloudForecast12hJson     { get; set; } = "[]";
    public DistributionSession Session { get; set; } = null!;
}

public class MLPredictionLog
{
    public long     Id          { get; set; }
    public long     SessionId   { get; set; }
    public string   ModelVersion                 { get; set; } = string.Empty;
    public double   ConfidenceScore              { get; set; }
    public double   EfficiencyScore              { get; set; }
    public string   PredictedSoftMaxJson         { get; set; } = string.Empty;
    public double   PredictedPreventiveThreshold { get; set; }
    public bool     WasApplied                   { get; set; }
    public DateTime PredictedAt                  { get; set; }
    public DistributionSession Session { get; set; } = null!;
}
