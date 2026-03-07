# SolarDistribution — Agent Autonome Docker

Agent autonome qui lit le surplus solaire depuis Home Assistant et distribue intelligemment la puissance de recharge vers les batteries, avec ML.NET adaptatif et persistance MariaDB.

## Architecture

```
Docker Container
├── SolarWorker (BackgroundService)     ← boucle autonome
│   ├── HomeAssistantDataReader         ← GET /api/states/* (lecture SOC + surplus)
│   ├── SmartDistributionService        ← calcul ML ou déterministe
│   └── HomeAssistantCommandSender      ← POST /api/services/number/set_value
│
├── /config/config.yaml                 ← VOTRE configuration (volume monté)
└── /data/
    ├── ml_models/                      ← modèles ML persistés
    └── logs/                           ← logs rotatifs

Projects .NET 10 :
├── SolarDistribution.Core              # Algo déterministe, modèles, interfaces
├── SolarDistribution.Infrastructure    # EF Core + MariaDB, ML.NET, Open-Meteo
├── SolarDistribution.Worker            # Worker Docker autonome ← POINT D'ENTRÉE
├── SolarDistribution.Api               # API REST (optionnel, pour debug/integration)
└── SolarDistribution.Tests             # NUnit + NSubstitute
```

## Démarrage rapide

### 1. Configurer

```bash
# Copier et éditer le fichier de config
cp config/config.yaml config/config.yaml  # déjà présent
nano config/config.yaml
```

Éléments obligatoires à renseigner :
- `home_assistant.url` → URL de votre HA
- `home_assistant.token` → Long-Lived Access Token (HA → Profil → Sécurité)
- `solar.surplus_entity` → entity_id du capteur surplus solaire
- `batteries[].entities.soc` → entity_id du % charge pour chaque batterie
- `batteries[].entities.charge_power` → entity_id `number.*` de contrôle puissance

### 2. Variables d'environnement

```bash
cp .env.example .env
nano .env    # adapter DB_ROOT_PASSWORD et DB_PASSWORD
```

### 3. Lancer

```bash
# Mode simulation (DRY RUN — aucune commande envoyée à HA)
# Éditer config.yaml : polling.dry_run: true

docker compose up --build
```

```bash
# Production
docker compose up -d --build
docker compose logs -f solar_worker
```

## Cycle de fonctionnement

Toutes les N secondes (configurable) :
1. **Lit** depuis HA : surplus solaire (W) + SOC de chaque batterie (%)
2. **Calcule** la distribution optimale (ML si ≥50 sessions, sinon algo déterministe)
3. **Envoie** `number.set_value` à HA pour chaque batterie
4. **Persiste** la session en MariaDB avec données météo Open-Meteo

## Configuration des entités HA

### Trouver vos entity_ids

Dans Home Assistant : **Outils de développement → États** → chercher :
- `sensor.*battery*soc` ou `sensor.*battery*charge`
- `number.*battery*power` ou `number.*charge*power`
- `sensor.*solar*surplus` ou `sensor.*export*power`

### Exemples par onduleur/batterie

| Matériel | SOC | Charge Power |
|----------|-----|--------------|
| SolaX | `sensor.solax_battery_capacity` | `number.solax_battery_charge_max_current` |
| GivEnergy | `sensor.givtcp_soc` | `number.givtcp_charge_target_soc` |
| Huawei SUN2000 | `sensor.battery_state_of_capacity` | `number.battery_maximum_charging_power` |
| Générique MQTT | `sensor.battery_1_soc` | `number.battery_1_charge_power` |

> **Note sur les Ampères :** Si votre entité HA attend des Ampères (A) plutôt que des Watts (W),
> utilisez `value_multiplier: 0.02083` pour une batterie 48V (= 1/48).

## ML.NET — Apprentissage progressif

| Phase | Sessions | Comportement |
|-------|----------|--------------|
| Démarrage | < 50 | Algo déterministe uniquement |
| Apprentissage | 50-200 | ML actif, confiance croissante |
| Maturité | > 200 | ML piloté par météo + historique |

Le modèle ajuste automatiquement :
- Le **SoftMax%** (cible de charge) selon les prévisions météo
- Le **seuil préventif** (MinPercent) selon les heures restantes de soleil

## Logs

```bash
# Console en temps réel
docker compose logs -f solar_worker

# Fichier rotatif dans le volume
docker compose exec solar_worker tail -f /data/logs/solar-worker.log
```

Exemple de sortie normale :
```
[10:32:01 INF] HA snapshot: surplus=1200W, production=2800W | batteries=[Principale:52.3%, Secondaire:48.1%]
[10:32:01 INF] Distribution: engine=Deterministic, allocated=1200W/1200W, unused=0W | session#42
[10:32:02 INF] 2/2 charge commands sent to HA
[10:32:02 INF]   [Principale]  52.3% → 80.0% | 800.0W | Reached soft max 80%
[10:32:02 INF]   [Secondaire]  48.1% → 80.0% | 400.0W | Reached soft max 80%
```

## Structure des fichiers montés

```
./config/config.yaml    → /config/config.yaml  (lecture seule)
volume solar_data       → /data/
  ├── ml_models/        → modèles ML.NET (.zip)
  └── logs/             → logs rotatifs (14 jours)
volume mariadb_data     → données MariaDB
```
