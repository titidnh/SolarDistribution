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
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.RequestedAt).IsRequired();
            e.Property(x => x.SurplusW).HasPrecision(10, 3);
            e.Property(x => x.TotalAllocatedW).HasPrecision(10, 3);
            e.Property(x => x.UnusedSurplusW).HasPrecision(10, 3);
            e.Property(x => x.DecisionEngine).HasMaxLength(30).IsRequired();
            e.Property(x => x.MlConfidenceScore).HasPrecision(5, 4);

            e.HasIndex(x => x.RequestedAt).HasDatabaseName("idx_session_requested_at");
            e.HasIndex(x => x.DecisionEngine).HasDatabaseName("idx_session_engine");

            e.HasMany(x => x.BatterySnapshots)
             .WithOne(x => x.Session)
             .HasForeignKey(x => x.SessionId)
             .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(x => x.Weather)
             .WithOne(x => x.Session)
             .HasForeignKey<WeatherSnapshot>(x => x.SessionId)
             .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(x => x.MlPrediction)
             .WithOne(x => x.Session)
             .HasForeignKey<MLPredictionLog>(x => x.SessionId)
             .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(x => x.Feedback)
             .WithOne(x => x.Session)
             .HasForeignKey<SessionFeedback>(x => x.SessionId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // ── BatterySnapshot ───────────────────────────────────────────────────
        model.Entity<BatterySnapshot>(e =>
        {
            e.ToTable("battery_snapshots");
            e.HasKey(x => x.Id);
            e.Property(x => x.CapacityWh).HasPrecision(10, 3);
            e.Property(x => x.MaxChargeRateW).HasPrecision(10, 3);
            e.Property(x => x.MinPercent).HasPrecision(5, 2);
            e.Property(x => x.SoftMaxPercent).HasPrecision(5, 2);
            e.Property(x => x.CurrentPercentBefore).HasPrecision(5, 2);
            e.Property(x => x.CurrentPercentAfter).HasPrecision(5, 2);
            e.Property(x => x.AllocatedW).HasPrecision(10, 3);
            e.Property(x => x.Reason).HasMaxLength(255);

            e.HasIndex(x => new { x.SessionId, x.BatteryId })
             .HasDatabaseName("idx_snapshot_session_battery");
        });

        // ── WeatherSnapshot ───────────────────────────────────────────────────
        model.Entity<WeatherSnapshot>(e =>
        {
            e.ToTable("weather_snapshots");
            e.HasKey(x => x.Id);
            e.Property(x => x.Latitude).HasPrecision(9, 6);
            e.Property(x => x.Longitude).HasPrecision(9, 6);
            e.Property(x => x.TemperatureC).HasPrecision(5, 2);
            e.Property(x => x.CloudCoverPercent).HasPrecision(5, 2);
            e.Property(x => x.PrecipitationMmH).HasPrecision(6, 3);
            e.Property(x => x.DirectRadiationWm2).HasPrecision(8, 3);
            e.Property(x => x.DiffuseRadiationWm2).HasPrecision(8, 3);
            e.Property(x => x.DaylightHours).HasPrecision(4, 2);
            e.Property(x => x.HoursUntilSunset).HasPrecision(4, 2);
            e.Property(x => x.RadiationForecast12hJson).HasColumnType("json");
            e.Property(x => x.CloudForecast12hJson).HasColumnType("json");
        });

        // ── MLPredictionLog ───────────────────────────────────────────────────
        model.Entity<MLPredictionLog>(e =>
        {
            e.ToTable("ml_prediction_logs");
            e.HasKey(x => x.Id);
            e.Property(x => x.ModelVersion).HasMaxLength(50);
            e.Property(x => x.ConfidenceScore).HasPrecision(5, 4);
            e.Property(x => x.PredictedSoftMaxJson).HasColumnType("json");
            e.Property(x => x.PredictedPreventiveThreshold).HasPrecision(5, 2);
            e.Property(x => x.EfficiencyScore).HasPrecision(5, 4);

            e.HasIndex(x => x.PredictedAt).HasDatabaseName("idx_ml_predicted_at");
            e.HasIndex(x => x.WasApplied).HasDatabaseName("idx_ml_was_applied");
        });

        // ── SessionFeedback ───────────────────────────────────────────────────
        model.Entity<SessionFeedback>(e =>
        {
            e.ToTable("session_feedbacks");
            e.HasKey(x => x.Id);
            e.Property(x => x.CollectedAt).IsRequired();
            e.Property(x => x.FeedbackDelayHours).HasPrecision(5, 2);
            e.Property(x => x.ObservedSocJson).HasColumnType("json");
            e.Property(x => x.AvgSocAtFeedback).HasPrecision(5, 2);
            e.Property(x => x.MinSocAtFeedback).HasPrecision(5, 2);
            e.Property(x => x.EnergyEfficiencyScore).HasPrecision(5, 4);
            e.Property(x => x.AvailabilityScore).HasPrecision(5, 4);
            e.Property(x => x.ObservedOptimalSoftMax).HasPrecision(5, 2);
            e.Property(x => x.ObservedOptimalPreventive).HasPrecision(5, 2);
            e.Property(x => x.CompositeScore).HasPrecision(5, 4);
            e.Property(x => x.Status).HasConversion<int>();
            e.Property(x => x.InvalidReason).HasMaxLength(500);

            // Index pour la requête "sessions pending feedback"
            e.HasIndex(x => x.Status).HasDatabaseName("idx_feedback_status");
            e.HasIndex(x => x.CollectedAt).HasDatabaseName("idx_feedback_collected_at");
        });
    }
}

