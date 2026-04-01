using Microsoft.EntityFrameworkCore;
using SolarDistribution.Core.Data.Entities;

namespace SolarDistribution.Infrastructure.Data;

public class SolarDbContext : DbContext
{
    public SolarDbContext(DbContextOptions<SolarDbContext> options) : base(options) { }

    public DbSet<DistributionSession> DistributionSessions => Set<DistributionSession>();
    public DbSet<BatterySnapshot> BatterySnapshots => Set<BatterySnapshot>();
    public DbSet<WeatherSnapshot> WeatherSnapshots => Set<WeatherSnapshot>();
    public DbSet<MLPredictionLog> MLPredictionLogs => Set<MLPredictionLog>();
    public DbSet<SessionFeedback> SessionFeedbacks => Set<SessionFeedback>();
    public DbSet<DailySummary> DailySummaries => Set<DailySummary>();
    public DbSet<HeatingSample> HeatingSamples => Set<HeatingSample>();
    public DbSet<GasMeterReading> GasMeterReadings => Set<GasMeterReading>();

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
            // ML-8: lifecycle - number of charge cycles at session time
            e.Property(x => x.CycleCount).HasColumnName("cycle_count").HasDefaultValue(0);
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
            // ML-7: labels enriched with real feedback
            e.Property(x => x.ActualSelfSufficiencyPct).HasColumnName("actual_self_sufficiency_pct").HasPrecision(6, 3);
            e.Property(x => x.DidImportFromGrid).HasColumnName("did_import_from_grid");
            e.Property(x => x.ShouldChargeFromGrid).HasColumnName("should_charge_from_grid");
            e.Property(x => x.SurplusWasted).HasColumnName("surplus_wasted");
            e.Property(x => x.TrainingWeight).HasColumnName("training_weight").HasPrecision(5, 3);
            e.HasIndex(x => x.Status).HasDatabaseName("idx_feedback_status");
            e.HasIndex(x => x.CollectedAt).HasDatabaseName("idx_feedback_collected");
            e.HasIndex(x => x.ShouldChargeFromGrid).HasDatabaseName("idx_sf_should_charge");
        });

        // ── DailySummary ──────────────────────────────────────────────────────
        // One row per UTC calendar date. Upsert via UpsertDailySummaryAsync.
        model.Entity<DailySummary>(e =>
        {
            e.ToTable("daily_summaries");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(x => x.Date).HasColumnName("date").IsRequired();
            e.Property(x => x.SolarConsumedWh).HasColumnName("solar_consumed_wh").HasPrecision(12, 2);
            e.Property(x => x.GridConsumedWh).HasColumnName("grid_consumed_wh").HasPrecision(12, 2);
            e.Property(x => x.GridChargedWh).HasColumnName("grid_charged_wh").HasPrecision(12, 2);
            e.Property(x => x.SolarAllocatedWh).HasColumnName("solar_allocated_wh").HasPrecision(12, 2);
            e.Property(x => x.UnusedSurplusWh).HasColumnName("unused_surplus_wh").HasPrecision(12, 2);
            e.Property(x => x.EstimatedSavingsEur).HasColumnName("estimated_savings_eur").HasPrecision(8, 4);
            e.Property(x => x.SelfSufficiencyPct).HasColumnName("self_sufficiency_pct").HasPrecision(5, 2);
            e.Property(x => x.SessionCount).HasColumnName("session_count");
            e.Property(x => x.ComputedAt).HasColumnName("computed_at");

            // Unique constraint on date (business key)
            e.HasIndex(x => x.Date).IsUnique().HasDatabaseName("uq_daily_summary_date");
            // Range-query index (GET /api/summary/daily?from=&to=)
            e.HasIndex(x => x.Date).HasDatabaseName("idx_daily_summary_date");
        });

        // ── HeatingSample ────────────────────────────────────────────────────
        model.Entity<HeatingSample>(e =>
        {
            e.ToTable("heating_samples");
            e.HasKey(x => x.Id);

            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(x => x.SampledAtUtc).HasColumnName("sampled_at_utc").IsRequired();

            e.Property(x => x.IndoorTempC).HasColumnName("indoor_temp_c").HasPrecision(5, 2);
            e.Property(x => x.TargetTempC).HasColumnName("target_temp_c").HasPrecision(5, 2);
            e.Property(x => x.OutdoorTempC).HasColumnName("outdoor_temp_c").HasPrecision(5, 2);
            e.Property(x => x.OutdoorHumidityPct).HasColumnName("outdoor_humidity_pct").HasPrecision(5, 2);
            e.Property(x => x.WindSpeedMs).HasColumnName("wind_speed_ms").HasPrecision(6, 2);
            e.Property(x => x.SolarIrradianceWm2).HasColumnName("solar_irradiance_wm2").HasPrecision(7, 2);

            e.Property(x => x.ForecastOutdoorTempNextHoursJson)
                .HasColumnName("forecast_outdoor_temp_next_hours_json")
                .HasMaxLength(500);

            e.Property(x => x.ThermostatMode).HasColumnName("thermostat_mode").HasMaxLength(40);
            e.Property(x => x.HvacAction).HasColumnName("hvac_action").HasMaxLength(40);
            e.Property(x => x.IsHeatingOn).HasColumnName("is_heating_on");

            e.Property(x => x.PresenceMode).HasColumnName("presence_mode").HasMaxLength(40);
            e.Property(x => x.IsNearHome).HasColumnName("is_near_home");

            e.Property(x => x.IsOffPeak).HasColumnName("is_off_peak");
            e.Property(x => x.CurrentPricePerKwh).HasColumnName("current_price_per_kwh").HasPrecision(8, 4);

            // Multi-source heating fields
            e.Property(x => x.ActiveSourceName).HasColumnName("active_source_name").HasMaxLength(80);
            e.Property(x => x.ActiveSourceType).HasColumnName("active_source_type").HasMaxLength(20);
            e.Property(x => x.GasConsumptionM3h).HasColumnName("gas_consumption_m3h").HasPrecision(8, 4);
            e.Property(x => x.HeatPumpCop).HasColumnName("heat_pump_cop").HasPrecision(5, 3);
            e.Property(x => x.EstimatedCostPerKwhThermal)
                .HasColumnName("estimated_cost_per_kwh_thermal").HasPrecision(8, 4);

            e.HasIndex(x => x.SampledAtUtc).HasDatabaseName("idx_heating_sampled_at");
            e.HasIndex(x => new { x.SampledAtUtc, x.PresenceMode }).HasDatabaseName("idx_heating_time_presence");
        });

        // ── GasMeterReading ──────────────────────────────────────────────────
        model.Entity<GasMeterReading>(e =>
        {
            e.ToTable("gas_meter_readings");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(x => x.ReadAtUtc).HasColumnName("read_at_utc").IsRequired();
            e.Property(x => x.ReadingM3).HasColumnName("reading_m3").HasPrecision(12, 3).IsRequired();
            e.Property(x => x.Source).HasColumnName("source").HasMaxLength(20).IsRequired();
            e.Property(x => x.Note).HasColumnName("note").HasMaxLength(255);
            e.HasIndex(x => x.ReadAtUtc).HasDatabaseName("idx_gas_meter_read_at");
        });
    }
}