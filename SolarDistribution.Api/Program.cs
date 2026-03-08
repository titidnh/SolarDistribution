using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;  // Fix CS1061 : AddDbContextCheck
using Microsoft.OpenApi.Models;
using SolarDistribution.Core.Services;
using SolarDistribution.Core.Services.ML;
using SolarDistribution.Infrastructure.Data;
using SolarDistribution.Infrastructure.Repositories;
using SolarDistribution.Core.Repositories;
using SolarDistribution.Infrastructure.Services;
using SolarDistribution.Infrastructure.Mapping;

var builder = WebApplication.CreateBuilder(args);

// ── Controllers ───────────────────────────────────────────────────────────────
builder.Services.AddControllers();

// ── MariaDB / EF Core ─────────────────────────────────────────────────────────
var connectionString = builder.Configuration.GetConnectionString("MariaDb")
    ?? throw new InvalidOperationException("Connection string 'MariaDb' not found.");

builder.Services.AddDbContext<SolarDbContext>(options =>
    options.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString),
        mysql => mysql.EnableRetryOnFailure(3)));

// ── Repositories ──────────────────────────────────────────────────────────────
builder.Services.AddScoped<IDistributionRepository, DistributionRepository>();

// ── Core services ─────────────────────────────────────────────────────────────
builder.Services.AddSingleton<IBatteryDistributionService, BatteryDistributionService>();
builder.Services.AddScoped<SmartDistributionService>();
// Fix #6 : factory de sessions (mapping métier → entités EF)
builder.Services.AddScoped<IDistributionSessionFactory, DistributionSessionFactory>();
// TariffEngine depends on a TariffConfig; provide a default config so DI can
// resolve SmartDistributionService without depending on the Worker project.
builder.Services.AddSingleton<TariffEngine>(sp => new TariffEngine(new TariffEngine.TariffConfig()));

// ── ML.NET ────────────────────────────────────────────────────────────────────
// Register ML service as scoped because it depends on a scoped repository.
builder.Services.AddScoped<IDistributionMLService>(sp =>
{
    var repo     = sp.GetRequiredService<IDistributionRepository>();
    var logger   = sp.GetRequiredService<ILogger<DistributionMLService>>();
    var modelDir = builder.Configuration["ML:ModelDirectory"] ?? "ml_models";
    return new DistributionMLService(repo, logger, modelDir);
});

// ── Météo Open-Meteo ──────────────────────────────────────────────────────────
builder.Services.AddHttpClient<IWeatherService, OpenMeteoWeatherService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.Add("User-Agent", "SolarDistribution/1.0");
});

// ── Swagger ───────────────────────────────────────────────────────────────────
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title       = "Solar Distribution API",
        Version     = "v1",
        Description = """
            Intelligently distributes solar surplus to batteries.

            **Decision engines:**
            - `Deterministic` — priority/proportional algorithm (always available)
            - `ML` — ML.NET model (active after 50 sessions, R² ≥ 0.65)
            - `ML-Fallback` — ML active but partial confidence

            **Persisted data** (MariaDB): each `/calculate` call stores
            the session, battery states, Open-Meteo weather and the ML log.

            **Retraining:** `POST /api/ml/retrain` — manual, on demand.
            """
    });

    var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
    if (File.Exists(xmlPath))
        options.IncludeXmlComments(xmlPath);
});

// ── CORS — Fix #3 : configurable via appsettings.json ────────────────────────
// Dev  : Cors:AllowAnyOrigin = true  → tous les origines autorisés (pratique local)
// Prod : Cors:AllowAnyOrigin = false → uniquement les origines listées dans Cors:AllowedOrigins
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy =>
    {
        bool allowAny = builder.Configuration.GetValue<bool>("Cors:AllowAnyOrigin");

        if (allowAny)
        {
            policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
        }
        else
        {
            var origins = builder.Configuration
                .GetSection("Cors:AllowedOrigins")
                .Get<string[]>() ?? Array.Empty<string>();

            policy.WithOrigins(origins)
                  .AllowAnyHeader()
                  .AllowAnyMethod();
        }
    }));

// ── Health Checks — Fix #7 ────────────────────────────────────────────────────
// Expose /health (liveness) et /health/ready (readiness + DB check)
builder.Services.AddHealthChecks()
    .AddDbContextCheck<SolarDbContext>("mariadb");

// ── Build ─────────────────────────────────────────────────────────────────────
var app = builder.Build();

// Migration automatique au démarrage (dev uniquement — utiliser le script SQL en prod)
if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<SolarDbContext>();
    await db.Database.MigrateAsync();
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Solar Distribution API v1");
        c.RoutePrefix = string.Empty;
    });
}

app.UseCors();
app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

// Fix #7 : endpoints health check
app.MapHealthChecks("/health");

app.Run();

public partial class Program { }
