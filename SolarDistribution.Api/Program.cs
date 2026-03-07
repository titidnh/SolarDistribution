using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using SolarDistribution.Core.Services;
using SolarDistribution.Core.Services.ML;
using SolarDistribution.Infrastructure.Data;
using SolarDistribution.Infrastructure.Repositories;
using SolarDistribution.Core.Repositories;
using SolarDistribution.Infrastructure.Services;

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

// ── ML.NET ────────────────────────────────────────────────────────────────────
builder.Services.AddSingleton<IDistributionMLService>(sp =>
{
    var repo   = sp.GetRequiredService<IDistributionRepository>();
    var logger = sp.GetRequiredService<ILogger<DistributionMLService>>();
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
            Distribue intelligemment le surplus solaire vers les batteries.

            **Moteurs de décision :**
            - `Deterministic` — algorithme priorité/proportionnel (toujours disponible)
            - `ML` — modèle ML.NET (actif après 50 sessions, R² ≥ 0.65)
            - `ML-Fallback` — ML actif mais confiance partielle

            **Données persistées** (MariaDB) : chaque appel `/calculate` stocke
            la session, les états batteries, la météo Open-Meteo et le log ML.

            **Ré-entraînement** : `POST /api/ml/retrain` — manuel, sur demande.
            """
    });

    var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
    if (File.Exists(xmlPath))
        options.IncludeXmlComments(xmlPath);
});

// ── CORS ──────────────────────────────────────────────────────────────────────
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

// ── Build ─────────────────────────────────────────────────────────────────────
var app = builder.Build();

// Migration automatique au démarrage (dev uniquement — utiliser dotnet ef en prod)
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

app.Run();

public partial class Program { }
