using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;
using SolarDistribution.Core.Services;
using SolarDistribution.Core.Services.ML;
using SolarDistribution.Infrastructure.Data;
using SolarDistribution.Infrastructure.Repositories;
using SolarDistribution.Infrastructure.Services;
using SolarDistribution.Core.Repositories;
using SolarDistribution.Worker.Configuration;
using SolarDistribution.Worker.HA;
using SolarDistribution.Worker.Services;

// ── Chargement config YAML ────────────────────────────────────────────────────
SolarConfig config;
try
{
    config = ConfigLoader.Load();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[FATAL] Configuration error: {ex.Message}");
    Environment.Exit(1);
    return;
}

// ── Serilog ───────────────────────────────────────────────────────────────────
var logLevel = config.Logging.Level.ToLower() switch
{
    "debug"   => LogEventLevel.Debug,
    "warning" => LogEventLevel.Warning,
    "error"   => LogEventLevel.Error,
    _         => LogEventLevel.Information
};

var loggerConfig = new LoggerConfiguration()
    .MinimumLevel.Is(logLevel)
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}");

if (config.Logging.FilePath is not null)
{
    loggerConfig.WriteTo.File(
        config.Logging.FilePath,
        rollingInterval:  RollingInterval.Day,
        retainedFileCountLimit: 14,
        outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}");
}

Log.Logger = loggerConfig.CreateLogger();

// ── Host Builder ──────────────────────────────────────────────────────────────
var host = Host.CreateDefaultBuilder(args)
    .UseSerilog()
    .ConfigureServices(services =>
    {
        // Config YAML en singleton
        services.AddSingleton(config);

        // ── MariaDB / EF Core ─────────────────────────────────────────────────
        services.AddDbContext<SolarDbContext>(options =>
            options.UseMySql(
                config.Database.ConnectionString,
                ServerVersion.AutoDetect(config.Database.ConnectionString),
                mysql => mysql.EnableRetryOnFailure(3)),
            ServiceLifetime.Singleton);   // Singleton car le Worker est long-running

        services.AddSingleton<IDistributionRepository, DistributionRepository>();

        // ── Home Assistant HTTP Client ────────────────────────────────────────
        services
            .AddHttpClient<IHomeAssistantClient, HomeAssistantClient>(client =>
            {
                client.BaseAddress = new Uri(config.HomeAssistant.Url);
                client.Timeout     = TimeSpan.FromSeconds(config.HomeAssistant.TimeoutSeconds);
                client.DefaultRequestHeaders.Add(
                    "Authorization", $"Bearer {config.HomeAssistant.Token}");
                client.DefaultRequestHeaders.Add(
                    "Content-Type", "application/json");
            })
            .AddStandardResilienceHandler(opts =>
            {
                // Retry : 3 tentatives avec jitter
                opts.Retry.MaxRetryAttempts = config.HomeAssistant.RetryCount;
                // Circuit breaker : ouvre après 5 échecs en 30s
                opts.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
            });

        // ── Services HA ───────────────────────────────────────────────────────
        services.AddSingleton<HomeAssistantDataReader>();
        services.AddSingleton<HomeAssistantCommandSender>();

        // ── Core : algo déterministe ──────────────────────────────────────────
        services.AddSingleton<IBatteryDistributionService, BatteryDistributionService>();

        // ── ML.NET ────────────────────────────────────────────────────────────
        services.AddSingleton<IDistributionMLService>(sp =>
        {
            var repo   = sp.GetRequiredService<IDistributionRepository>();
            var logger = sp.GetRequiredService<ILogger<DistributionMLService>>();
            return new DistributionMLService(repo, logger, config.Ml.ModelDirectory);
        });

        // ── Feedback & Retrain planifié ───────────────────────────────────────
        services.AddSingleton(config.Ml);  // MlConfig en singleton pour MlRetrainScheduler
        services.AddSingleton<FeedbackEvaluator>();
        services.AddHostedService<MlRetrainScheduler>();

        // ── Météo Open-Meteo ──────────────────────────────────────────────────
        services.AddHttpClient<IWeatherService, OpenMeteoWeatherService>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.Add("User-Agent", "SolarDistribution-Worker/1.0");
        });

        // ── SmartDistributionService ──────────────────────────────────────────
        services.AddSingleton<SmartDistributionService>();

        // ── Worker principal ──────────────────────────────────────────────────
        services.AddHostedService<SolarWorker>();
    })
    .Build();

// ── Migration automatique au démarrage ───────────────────────────────────────
try
{
    using var scope = host.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<SolarDbContext>();

    Log.Information("Applying EF Core migrations...");
    await db.Database.MigrateAsync();
    Log.Information("Database ready");
}
catch (Exception ex)
{
    Log.Fatal(ex, "Database migration failed — worker cannot start");
    Environment.Exit(1);
}

// ── Démarrage ─────────────────────────────────────────────────────────────────
try
{
    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Worker terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
