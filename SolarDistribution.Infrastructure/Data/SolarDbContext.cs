using Microsoft.EntityFrameworkCore;
using SolarDistribution.Core.Data.Entities;
using System.Text;

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
            e.Property(x => x.GridChargedW).HasPrecision(10, 3);
            e.Property(x => x.DecisionEngine).HasMaxLength(30).IsRequired();
            e.Property(x => x.MlConfidenceScore).HasPrecision(5, 4);

            // Contexte tarifaire
            e.Property(x => x.TariffSlotName).HasMaxLength(80);
            e.Property(x => x.TariffPricePerKwh).HasPrecision(6, 4);
            e.Property(x => x.HoursToNextFavorableTariff).HasPrecision(5, 2);
            e.Property(x => x.AvgSolarForecastWm2)
             .HasColumnName("avg_solar_forecast_wm2")
             .HasPrecision(7, 2);
            e.Property(x => x.TariffMaxSavingsPerKwh).HasPrecision(6, 4);

            e.HasIndex(x => x.RequestedAt).HasDatabaseName("idx_session_requested_at");
            e.HasIndex(x => x.DecisionEngine).HasDatabaseName("idx_session_engine");
            e.HasIndex(x => x.TariffSlotName).HasDatabaseName("idx_session_tariff");

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
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.CapacityWh).HasPrecision(10, 2);
            e.Property(x => x.MaxChargeRateW).HasPrecision(8, 2);
            e.Property(x => x.MinPercent).HasPrecision(5, 2);
            e.Property(x => x.SoftMaxPercent).HasPrecision(5, 2);
            e.Property(x => x.CurrentPercentBefore).HasPrecision(5, 2);
            e.Property(x => x.CurrentPercentAfter).HasPrecision(5, 2);
            e.Property(x => x.AllocatedW).HasPrecision(8, 2);
            e.Property(x => x.Reason).HasMaxLength(300);
            e.HasIndex(x => new { x.SessionId, x.BatteryId }).HasDatabaseName("idx_snapshot_session_battery");
        });

        // ── WeatherSnapshot ───────────────────────────────────────────────────
        model.Entity<WeatherSnapshot>(e =>
        {
            e.ToTable("weather_snapshots");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.TemperatureC).HasPrecision(5, 2);
            e.Property(x => x.CloudCoverPercent).HasPrecision(5, 2);
            e.Property(x => x.PrecipitationMmH).HasPrecision(6, 3);
            e.Property(x => x.DirectRadiationWm2).HasPrecision(7, 2);
            e.Property(x => x.DiffuseRadiationWm2).HasPrecision(7, 2);
            e.Property(x => x.DaylightHours).HasPrecision(4, 2);
            e.Property(x => x.HoursUntilSunset).HasPrecision(4, 2);
            e.Property(x => x.RadiationForecast12hJson).HasMaxLength(1000);
            e.Property(x => x.CloudForecast12hJson).HasMaxLength(500);
        });

        // ── MLPredictionLog ───────────────────────────────────────────────────
        model.Entity<MLPredictionLog>(e =>
        {
            e.ToTable("ml_prediction_logs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.ModelVersion).HasMaxLength(30);
            e.Property(x => x.ConfidenceScore).HasPrecision(5, 4);
            e.Property(x => x.PredictedSoftMaxJson).HasMaxLength(200);
            e.Property(x => x.PredictedPreventiveThreshold).HasPrecision(5, 2);
        });

        // ── SessionFeedback ───────────────────────────────────────────────────
        model.Entity<SessionFeedback>(e =>
        {
            e.ToTable("session_feedbacks");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.ObservedSocJson).HasMaxLength(500);
            e.Property(x => x.EnergyEfficiencyScore).HasPrecision(5, 4);
            e.Property(x => x.AvailabilityScore).HasPrecision(5, 4);
            e.Property(x => x.ObservedOptimalSoftMax).HasPrecision(5, 2);
            e.Property(x => x.ObservedOptimalPreventive).HasPrecision(5, 2);
            e.Property(x => x.CompositeScore).HasPrecision(5, 4);
            e.Property(x => x.InvalidReason).HasMaxLength(200);
            e.HasIndex(x => x.Status).HasDatabaseName("idx_feedback_status");
            e.HasIndex(x => x.CollectedAt).HasDatabaseName("idx_feedback_collected");
        });

        // Apply snake_case naming convention for all columns to match existing DB schema
        foreach (var entityType in model.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                property.SetColumnName(ToSnakeCase(property.Name));
            }
        }
    }

    private static string ToSnakeCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        var sb = new StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            char prev = i > 0 ? name[i - 1] : '\0';

            if (char.IsUpper(c))
            {
                if (i > 0 && (char.IsLower(prev) || char.IsDigit(prev))) sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else if (char.IsDigit(c))
            {
                if (i > 0 && !char.IsDigit(prev) && prev != '_') sb.Append('_');
                sb.Append(c);
            }
            else // lower-case letter or other
            {
                if (i > 0 && char.IsDigit(prev)) sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
        }
        return sb.ToString();
    }
}
