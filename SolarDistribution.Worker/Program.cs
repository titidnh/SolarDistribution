using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;
using System.Net.Http.Headers;
using SolarDistribution.Core.Services;
using SolarDistribution.Core.Services.ML;
using SolarDistribution.Infrastructure.Data;
using SolarDistribution.Infrastructure.Repositories;
using SolarDistribution.Infrastructure.Services;
using SolarDistribution.Infrastructure.Mapping;
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
    // Silence les logs verbeux de la stack HTTP .NET (HttpClient lifecycle, Polly retries)
    .MinimumLevel.Override("Microsoft",                              LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore",          LogEventLevel.Warning)
    .MinimumLevel.Override("System.Net.Http",                        LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.Extensions.Http",              LogEventLevel.Warning)
    .MinimumLevel.Override("Polly",                                  LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.Extensions.Http.Resilience",   LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}");

if (config.Logging.FilePath is not null)
{
    loggerConfig.WriteTo.File(
        config.Logging.FilePath,
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}");
}

Log.Logger = loggerConfig.CreateLogger();

// ── Host Builder ──────────────────────────────────────────────────────────────
var host = Host.CreateDefaultBuilder(args)
    .UseSerilog()
    .ConfigureServices(services =>
    {
        services.AddSingleton(config);
        services.AddSingleton(config.Tariff);
        services.AddSingleton(config.Ml);
        services.AddSingleton(config.HomeAssistant);
        services.AddSingleton(config.Polling);

        // ── Database ──────────────────────────────────────────────────────────
        services.AddDbContext<SolarDbContext>(opts =>
            opts.UseMySql(
                config.Database.ConnectionString,
                ServerVersion.AutoDetect(config.Database.ConnectionString),
                mysqlOpts => mysqlOpts.CommandTimeout(30)));

        // ── Repositories & services ───────────────────────────────────────────
        services.AddScoped<IDistributionRepository, DistributionRepository>();
        services.AddSingleton<IBatteryDistributionService, BatteryDistributionService>();
        services.AddSingleton<IDistributionSessionFactory, DistributionSessionFactory>();
        services.AddSingleton<TariffEngine>();
        services.AddSingleton<SmartDistributionService>();

        // ── ML ────────────────────────────────────────────────────────────────
        services.AddSingleton<IDistributionMLService>(sp =>
            new DistributionMLService(
                sp.GetRequiredService<IDistributionRepository>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<DistributionMLService>>(),
                config.Ml.ModelDirectory));

        // ── Weather ───────────────────────────────────────────────────────────
        services.AddHttpClient<IWeatherService, OpenMeteoWeatherService>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.Add("User-Agent", "SolarDistribution-Worker/1.0");
        });
        services.AddSingleton<WeatherCacheService>();

        // ── Home Assistant ────────────────────────────────────────────────────
        services.AddHttpClient<IHomeAssistantClient, HomeAssistantClient>(client =>
            {
                client.BaseAddress = new Uri(config.HomeAssistant.Url);
                client.Timeout = TimeSpan.FromSeconds(config.HomeAssistant.TimeoutSeconds);
                client.DefaultRequestHeaders.Add(
                    "Authorization", $"Bearer {config.HomeAssistant.Token}");
                client.DefaultRequestHeaders.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("application/json"));
            })
            .AddStandardResilienceHandler(opts =>
            {
                opts.Retry.MaxRetryAttempts = config.HomeAssistant.RetryCount;
                opts.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
            });

        services.AddSingleton<HomeAssistantDataReader>();
        services.AddSingleton<CommandStateCache>();
        services.AddSingleton<HomeAssistantCommandSender>();

        // ── Workers ───────────────────────────────────────────────────────────
        services.AddHostedService<SolarWorker>();
        services.AddHostedService<WeatherCacheService>(sp =>
            sp.GetRequiredService<WeatherCacheService>());
        services.AddHostedService<MlRetrainScheduler>();
        services.AddSingleton<FeedbackEvaluator>();
    })
    .Build();

// ── Migrations EF auto ────────────────────────────────────────────────────────
using (var scope = host.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<SolarDbContext>();
    await db.Database.MigrateAsync();
}

await host.RunAsync();
