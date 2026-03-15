using Microsoft.ML.Data;

namespace SolarDistribution.Core.Services.ML;

public class DistributionFeatures
{
    // ── Raw temporal ─────────────────────────────────────────────────────────
    [LoadColumn(0)] public float HourOfDay { get; set; }
    [LoadColumn(1)] public float DayOfWeek { get; set; }
    [LoadColumn(2)] public float MonthOfYear { get; set; }
    [LoadColumn(3)] public float DayOfYear { get; set; }

    // ── Cyclic encoding ──────────────────────────────────────────────────────
    [LoadColumn(4)] public float SinHour { get; set; }
    [LoadColumn(5)] public float CosHour { get; set; }
    [LoadColumn(6)] public float SinMonth { get; set; }
    [LoadColumn(7)] public float CosMonth { get; set; }

    // ── Seasonality ─────────────────────────────────────────────────────────
    [LoadColumn(8)] public float DaylightHours { get; set; }
    [LoadColumn(9)] public float HoursUntilSunset { get; set; }

    // ── Weather ─────────────────────────────────────────────────────────────
    [LoadColumn(10)] public float CloudCoverPercent { get; set; }
    [LoadColumn(11)] public float DirectRadiationWm2 { get; set; }
    [LoadColumn(12)] public float DiffuseRadiationWm2 { get; set; }
    [LoadColumn(13)] public float PrecipitationMmH { get; set; }
    [LoadColumn(14)] public float AvgForecastRadiation6h { get; set; }

    // ── Battery state ────────────────────────────────────────────────────────
    [LoadColumn(15)] public float AvgBatteryPercent { get; set; }
    [LoadColumn(16)] public float MinBatteryPercent { get; set; }
    [LoadColumn(17)] public float MaxBatteryPercent { get; set; }
    [LoadColumn(18)] public float TotalCapacityWh { get; set; }
    [LoadColumn(19)] public float UrgentBatteryCount { get; set; }
    [LoadColumn(20)] public float TotalMaxChargeRateW { get; set; }

    // ── ML-4: battery dispersion ─────────────────────────────────────────────
    [LoadColumn(21)] public float SocStdDev { get; set; }
    [LoadColumn(22)] public float CapacityRatio { get; set; }
    [LoadColumn(23)] public float NonUrgentBatteryCount { get; set; }

    // ── Solar surplus ────────────────────────────────────────────────────────
    [LoadColumn(24)] public float SurplusW { get; set; }

    // ── Tariff context ───────────────────────────────────────────────────────
    [LoadColumn(25)] public float NormalizedTariff { get; set; }
    [LoadColumn(26)] public float IsOffPeakHour { get; set; }
    [LoadColumn(27)] public float HoursToNextFavorable { get; set; }
    [LoadColumn(28)] public float AvgSolarForecastGrid { get; set; }
    [LoadColumn(29)] public float SolarExpectedSoon { get; set; }
    [LoadColumn(30)] public float MaxSavingsPerKwh { get; set; }

    // ── ML-7: extended adaptive context ─────────────────────────────────────
    [LoadColumn(31)] public float HoursRemainingInSlot { get; set; }
    [LoadColumn(32)] public float HoursUntilSolarCapped { get; set; }
    [LoadColumn(33)] public float WasEmergencySession { get; set; }
    [LoadColumn(34)] public float NormalizedGridChargeW { get; set; }

    // ── ML-8: HA solar forecasts (installation-specific) ─────────────────────
    [LoadColumn(35)] public float ForecastTodayNormalized { get; set; }
    [LoadColumn(36)] public float ForecastTomorrowNormalized { get; set; }
    [LoadColumn(37)] public float HasHaForecast { get; set; }

    // ── ML-9: tendance solaire J vs J+1 ──────────────────────────────────────
    [LoadColumn(38)] public float ForecastRatioTomorrowVsToday { get; set; }
    [LoadColumn(39)] public float SolarBlockedByHaForecast { get; set; }

    // ── Feature 6 — Bilan J-1 ────────────────────────────────────────────────
    [LoadColumn(42)] public float YesterdaySelfSufficiencyPct { get; set; }

    // ── ML-7 : labels enrichis de feedback réel ──────────────────────────────
    [LoadColumn(43)] public float ActualSelfSufficiencyNormalized { get; set; }
    [LoadColumn(44)] public float DidImportFromGrid { get; set; }
    [LoadColumn(45)] public float SampleWeight { get; set; }

    // ── Labels ───────────────────────────────────────────────────────────────
    [LoadColumn(40)] public float OptimalSoftMaxPercent { get; set; }
    [LoadColumn(41)] public float OptimalPreventiveThreshold { get; set; }

    [LoadColumn(46)] public bool ShouldChargeFromGrid { get; set; }
}
