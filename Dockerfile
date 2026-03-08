# ── Stage 1 : Build ───────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copie des .csproj d'abord pour cache Docker optimal
COPY SolarDistribution.Core/SolarDistribution.Core.csproj               ./SolarDistribution.Core/
COPY SolarDistribution.Infrastructure/SolarDistribution.Infrastructure.csproj ./SolarDistribution.Infrastructure/
COPY SolarDistribution.Worker/SolarDistribution.Worker.csproj            ./SolarDistribution.Worker/

# Restore NuGet
RUN dotnet restore SolarDistribution.Worker/SolarDistribution.Worker.csproj

# Copie du code source
COPY SolarDistribution.Core/           ./SolarDistribution.Core/
COPY SolarDistribution.Infrastructure/ ./SolarDistribution.Infrastructure/
COPY SolarDistribution.Worker/         ./SolarDistribution.Worker/

# Build + publish
RUN dotnet publish SolarDistribution.Worker/SolarDistribution.Worker.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

# ── Stage 2 : Runtime ─────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Utilisateur non-root pour la sécurité
# Installer l'outil `adduser` puis créer l'utilisateur système `solar`
RUN apt-get update \
    && apt-get install -y --no-install-recommends adduser \
    && rm -rf /var/lib/apt/lists/* \
    && addgroup --system solar \
    && adduser --system --ingroup solar solar

# Dossiers persistés (montés via volumes)
RUN mkdir -p /config /data/ml_models /data/logs \
 && chown -R solar:solar /config /data

# Copie du binaire
COPY --from=build /app/publish .
RUN chown -R solar:solar /app

USER solar

# Variables d'environnement par défaut (surchargées via docker-compose)
ENV CONFIG_PATH=/config/config.yaml \
    DOTNET_ENVIRONMENT=Production \
    TZ=Europe/Brussels

ENTRYPOINT ["dotnet", "SolarDistribution.Worker.dll"]
