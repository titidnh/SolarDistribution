using Microsoft.EntityFrameworkCore;
using SolarDistribution.Core.Data.Entities;

namespace SolarDistribution.Infrastructure.Data;

public class SolarDbContext : DbContext
{
    public SolarDbContext(DbContextOptions<SolarDbContext> options) : base(options) { }

    public DbSet<DistributionSession> DistributionSessions => Set<DistributionSession>();
    public DbSet<BatterySnapshot>     BatterySnapshots     => Set<BatterySnapshot>();
    public DbSet<WeatherSnapshot>     WeatherSnapshots     => Set<WeatherSnapshot>();
    public DbSet<MLPredictionLog>     MLPredictionLogs     => Set<MLPredictionLog>();
    public DbSet<SessionFeedback>     SessionFeedbacks     => Set<SessionFeedback>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        // ── DistributionSession ───────────────────────────────────────────────
        model.Entity<DistributionSession>(e =>
        {
            e.ToTable("distribution_sessions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(x => x.RequestedAt).HasColumnName("requested_at").IsRequired();
            e.Property(x => x.SurplusW).HasColumnName("surplus_w").HasPrecision(10, 3);
            e.Property(x => x.TotalAllocatedW).HasColumnName("total_allocated_w").HasPrecision(10, 3);
            e.Property(x => x.UnusedSurplusW).HasColumnName("unused_surplus_w").HasPrecision(10, 3);
            e.Property(x => x.GridChargedW).HasColumnName("grid_charged_w").HasPrecision(10, 3);
            e.Property(x => x.DecisionEngine).HasColumnName("decision_engine").HasMaxLength(30).IsRequired();
            e.Property(x => x.MlConfidenceScore).HasColumnName("ml_confidence_score").HasPrecision(5, 4);

            // Tariff standard
            e.Property(x => x.TariffSlotName).HasColumnName("tariff_slot_name").HasMaxLength(80);
            e.Property(x => x.TariffPricePerKwh).HasColumnName("tariff_price_per_kwh").HasPrecision(6, 4);
            e.Property(x => x.WasGridChargeFavorable).HasColumnName("was_grid_charge_favorable");
            e.Property(x => x.SolarExpectedSoon).HasColumnName("solar_expected_soon");
            e.Property(x => x.HoursToNextFavorableTariff).HasColumnName("hours_to_next_favorable_tariff").HasPrecision(5, 2);
            e.Property(x => x.AvgSolarForecastWm2).HasColumnName("avg_solar_forecast_wm2").HasPrecision(7, 2);
            e.Property(x => x.TariffMaxSavingsPerKwh).HasColumnName("tariff_max_savings_per_kwh").HasPrecision(6, 4);

            // ML-7: adaptive context
            e.Property(x => x.HoursRemainingInSlot).HasColumnName("hours_remaining_in_slot").HasPrecision(5, 2);
            e.Property(x => x.HoursUntilSolar).HasColumnName("hours_until_solar").HasPrecision(5, 2);
            e.Property(x => x.HadEmergencyGridCharge).HasColumnName("had_emergency_grid_charge");
            e.Property(x => x.EffectiveGridChargeW).HasColumnName("effective_grid_charge_w").HasPrecision(8, 2);

            // ML-8: HA installation-specific forecasts
            e.Property(x => x.ForecastTodayWh).HasColumnName("forecast_today_wh").HasPrecision(10, 2);
            e.Property(x => x.ForecastTomorrowWh).HasColumnName("forecast_tomorrow_wh").HasPrecision(10, 2);

            // Load forecasting
            e.Property(x => x.MeasuredConsumptionW).HasColumnName("measured_consumption_w").HasPrecision(10, 2);
            e.Property(x => x.EstimatedConsumptionNextHoursWh).HasColumnName("estimated_consumption_next_hours_wh").HasPrecision(10, 2);

            // Intraday + bilan journalier (Feature 3 & 4)
            e.Property(x => x.ForecastRemainingTodayWh).HasColumnName("forecast_remaining_today_wh").HasPrecision(10, 2);
            e.Property(x => x.EnergyDeficitTodayWh).HasColumnName("energy_deficit_today_wh").HasPrecision(10, 2);
            e.Property(x => x.DailySolarConsumedWh).HasColumnName("daily_solar_consumed_wh").HasPrecision(10, 2);

            // Indexes
            e.HasIndex(x => x.RequestedAt).HasDatabaseName("idx_session_requested_at");
            e.HasIndex(x => x.DecisionEngine).HasDatabaseName("idx_session_engine");
            e.HasIndex(x => x.TariffSlotName).HasDatabaseName("idx_session_tariff");

            // Relations
            e.HasMany(x => x.BatterySnapshots).WithOne(x => x.Session)
             .HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Weather).WithOne(x => x.Session)
             .HasForeignKey<WeatherSnapshot>(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.MlPrediction).WithOne(x => x.Session)
             .HasForeignKey<MLPredictionLog>(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Feedback).WithOne(x => x.Session)
             .HasForeignKey<SessionFeedback>(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
        });

        // ── BatterySnapshot ───────────────────────────────────────────────────
        model.Entity<BatterySnapshot>(e =>
        {
            e.ToTable("battery_snapshots");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(x => x.SessionId).HasColumnName("session_id");
            e.Property(x => x.BatteryId).HasColumnName("battery_id");
            e.Property(x => x.CapacityWh).HasColumnName("capacity_wh").HasPrecision(10, 2);
            e.Property(x => x.MaxChargeRateW).HasColumnName("max_charge_rate_w").HasPrecision(8, 2);
            e.Property(x => x.MinPercent).HasColumnName("min_percent").HasPrecision(5, 2);
            e.Property(x => x.SoftMaxPercent).HasColumnName("soft_max_percent").HasPrecision(5, 2);
            e.Property(x => x.CurrentPercentBefore).HasColumnName("current_percent_before").HasPrecision(5, 2);
            e.Property(x => x.CurrentPercentAfter).HasColumnName("current_percent_after").HasPrecision(5, 2);
            e.Property(x => x.Priority).HasColumnName("priority");
            e.Property(x => x.WasUrgent).HasColumnName("was_urgent");
            e.Property(x => x.AllocatedW).HasColumnName("allocated_w").HasPrecision(8, 2);
            e.Property(x => x.IsGridCharge).HasColumnName("is_grid_charge");
            // ML-7: emergency + adaptive charge per battery
            e.Property(x => x.IsEmergencyGridCharge).HasColumnName("is_emergency_grid_charge");
            e.Property(x => x.GridChargeAllowedW).HasColumnName("grid_charge_allowed_w").HasPrecision(8, 2);
            e.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(300);
            e.HasIndex(x => new { x.SessionId, x.BatteryId }).HasDatabaseName("idx_snapshot_session_battery");
        });

        // ── WeatherSnapshot ───────────────────────────────────────────────────
        model.Entity<WeatherSnapshot>(e =>
        {
            e.ToTable("weather_snapshots");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(x => x.SessionId).HasColumnName("session_id");
            e.Property(x => x.FetchedAt).HasColumnName("fetched_at");
            e.Property(x => x.Latitude).HasColumnName("latitude");
            e.Property(x => x.Longitude).HasColumnName("longitude");
            e.Property(x => x.TemperatureC).HasColumnName("temperature_c").HasPrecision(5, 2);
            e.Property(x => x.CloudCoverPercent).HasColumnName("cloud_cover_percent").HasPrecision(5, 2);
            e.Property(x => x.PrecipitationMmH).HasColumnName("precipitation_mm_h").HasPrecision(6, 3);
            e.Property(x => x.DirectRadiationWm2).HasColumnName("direct_radiation_wm2").HasPrecision(7, 2);
            e.Property(x => x.DiffuseRadiationWm2).HasColumnName("diffuse_radiation_wm2").HasPrecision(7, 2);
            e.Property(x => x.DaylightHours).HasColumnName("daylight_hours").HasPrecision(4, 2);
            e.Property(x => x.HoursUntilSunset).HasColumnName("hours_until_sunset").HasPrecision(4, 2);
            e.Property(x => x.RadiationForecast12hJson).HasColumnName("radiation_forecast_12h_json").HasMaxLength(1000);
            e.Property(x => x.CloudForecast12hJson).HasColumnName("cloud_forecast_12h_json").HasMaxLength(500);
        });

        // ── MLPredictionLog ───────────────────────────────────────────────────
        model.Entity<MLPredictionLog>(e =>
        {
            e.ToTable("ml_prediction_logs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(x => x.SessionId).HasColumnName("session_id");
            e.Property(x => x.ModelVersion).HasColumnName("model_version").HasMaxLength(30);
            e.Property(x => x.ConfidenceScore).HasColumnName("confidence_score").HasPrecision(5, 4);
            e.Property(x => x.EfficiencyScore).HasColumnName("efficiency_score").HasPrecision(5, 4);
            e.Property(x => x.PredictedSoftMaxJson).HasColumnName("predicted_soft_max_json").HasMaxLength(200);
            e.Property(x => x.PredictedPreventiveThreshold).HasColumnName("predicted_preventive_threshold").HasPrecision(5, 2);
            e.Property(x => x.WasApplied).HasColumnName("was_applied");
            e.Property(x => x.PredictedAt).HasColumnName("predicted_at");
        });

        // ── SessionFeedback ───────────────────────────────────────────────────
        model.Entity<SessionFeedback>(e =>
        {
            e.ToTable("session_feedbacks");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(x => x.SessionId).HasColumnName("session_id");
            e.Property(x => x.CollectedAt).HasColumnName("collected_at");
            e.Property(x => x.FeedbackDelayHours).HasColumnName("feedback_delay_hours");
            e.Property(x => x.ObservedSocJson).HasColumnName("observed_soc_json").HasMaxLength(500);
            e.Property(x => x.AvgSocAtFeedback).HasColumnName("avg_soc_at_feedback");
            e.Property(x => x.MinSocAtFeedback).HasColumnName("min_soc_at_feedback");
            e.Property(x => x.EnergyEfficiencyScore).HasColumnName("energy_efficiency_score").HasPrecision(5, 4);
            e.Property(x => x.AvailabilityScore).HasColumnName("availability_score").HasPrecision(5, 4);
            e.Property(x => x.ObservedOptimalSoftMax).HasColumnName("observed_optimal_soft_max").HasPrecision(5, 2);
            e.Property(x => x.ObservedOptimalPreventive).HasColumnName("observed_optimal_preventive").HasPrecision(5, 2);
            e.Property(x => x.CompositeScore).HasColumnName("composite_score").HasPrecision(5, 4);
            e.Property(x => x.Status).HasColumnName("status").HasConversion<byte>();
            e.Property(x => x.InvalidReason).HasColumnName("invalid_reason").HasMaxLength(200);
            e.HasIndex(x => x.Status).HasDatabaseName("idx_feedback_status");
            e.HasIndex(x => x.CollectedAt).HasDatabaseName("idx_feedback_collected");
        });
    }
}
