using System;
using System.Collections.Generic;

namespace SolarDistribution.Infrastructure.Data.Entities;

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

    public ICollection<BatterySnapshot> BatterySnapshots { get; set; } = new List<BatterySnapshot>();
    public WeatherSnapshot?  Weather      { get; set; }
    public MLPredictionLog?  MlPrediction { get; set; }
    public SessionFeedback?  Feedback     { get; set; }
}
