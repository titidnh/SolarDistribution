# SolarDistribution — Autonomous Docker Agent

Autonomous agent that reads solar surplus from Home Assistant and intelligently distributes charging power to batteries, with adaptive ML.NET and MariaDB persistence.

## Architecture

```
Docker Container
├── SolarWorker (BackgroundService)     ← autonomous loop
│   ├── HomeAssistantDataReader         ← GET /api/states/* (reads SOC + surplus)
│   ├── SmartDistributionService        ← ML or deterministic calculation
│   └── HomeAssistantCommandSender      ← POST /api/services/number/set_value
│
├── /config/config.yaml                 ← YOUR configuration (mounted volume)
└── /data/
    ├── ml_models/                      ← persisted ML models
    └── logs/                           ← rotating logs

.NET 10 Projects:
├── SolarDistribution.Core              # Deterministic algo, models, interfaces
├── SolarDistribution.Infrastructure    # EF Core + MariaDB, ML.NET, Open-Meteo
├── SolarDistribution.Worker            # Autonomous Docker worker ← ENTRY POINT
├── SolarDistribution.Api               # REST API (optional, for debug/integration)
└── SolarDistribution.Tests             # NUnit + NSubstitute
```

## Quick Start

### 1. Configure

```bash
# Copy and edit the config file
cp config/config.yaml config/config.yaml  # already present
nano config/config.yaml
```

Required configuration items:
- `home_assistant.url` → your HA URL
- `home_assistant.token` → Long-Lived Access Token (HA → Profile → Security)
- `solar.surplus_mode` → `p1_invert` if using a P1/DSMR meter (recommended), `direct` if you have a dedicated surplus sensor
- `solar.surplus_entity` → entity_id of your P1 meter (e.g. `sensor.p1_power`) or your surplus sensor
- `batteries[].entities.soc` → entity_id for each battery's state of charge (%)
- `batteries[].entities.charge_power` → `number.*` entity id to control charge power

### 2. Environment Variables

```bash
cp .env.example .env
nano .env    # set DB_ROOT_PASSWORD and DB_PASSWORD
```

### 3. Run

```bash
# Simulation mode (DRY RUN — no commands sent to HA)
# Edit config.yaml: polling.dry_run: true

docker compose up --build
```

```bash
# Production
docker compose up -d --build
docker compose logs -f solar_worker
```

## How It Works

Every N seconds (configurable):
1. **Reads** from HA: solar surplus (W) + each battery's SOC (%)
2. **Calculates** the optimal distribution (ML if ≥50 sessions, otherwise deterministic algo)
3. **Sends** `number.set_value` to HA for each battery
4. **Persists** the session to MariaDB with Open-Meteo weather data

## Home Assistant — Dashboard templates (example)

You can expose the worker live status inside Home Assistant using the API endpoint `GET /api/distribution/status/live`.

Example using the `rest` sensor to fetch JSON and create template sensors:

```yaml
# Fetch the whole JSON object once
sensor:
  - platform: rest
    name: solar_worker_status_raw
    resource: "http://<worker_host>:<port>/api/distribution/status/live"
    method: GET
    # Polling interval in seconds. Recommend >= worker polling interval (default 60s).
    scan_interval: 60
    value_template: "{{ value_json.lastDecision }}"
    json_attributes:
      - effectiveSurplusW
      - gridChargeAllowed
      - nextGridChargeStartUtc

# Expose individual sensors using templates reading attributes
template:
  - sensor:
      - name: "solar_worker_last_decision"
        state: "{{ states('sensor.solar_worker_status_raw') }}"
      - name: "solar_worker_effective_surplus"
        state: "{{ state_attr('sensor.solar_worker_status_raw', 'effectiveSurplusW') | float }}"
        unit_of_measurement: "W"
      - name: "solar_worker_grid_charge_allowed"
        state: "{{ state_attr('sensor.solar_worker_status_raw', 'gridChargeAllowed') }}"
      - name: "solar_worker_next_grid_charge_start"
        state: "{{ state_attr('sensor.solar_worker_status_raw', 'nextGridChargeStartUtc') }}"
```

Notes:
- Replace `<worker_host>:<port>` with your API host/port (e.g. `192.168.1.50:5000`).
- Secure access to the API (CORS, firewall, or API key) if you expose it on your LAN.
 - `scan_interval`: set this to at least the worker `polling.interval_seconds` (default 60s). Avoid very low values (<15s) to prevent overloading the worker or HA API.
### Grid Charge Strategy (HC slots)

When off-peak tariff slots are active, the worker uses **Lazy Charging** to defer the charge toward the *end* of the cheap slot rather than starting immediately:

```
Start time = end_of_slot - hours_needed - lazy_buffer_hours
```

**Example** — slot HC 22:00→07:00 (9h), battery at 78% (target 85%, 1024Wh, 1000W max):
- Energy needed ≈ 72Wh → `hours_needed` ≈ 0.07h
- With `lazy_buffer_hours: 0.5` → charge starts at ~06:25
- Between 22:00 and 06:25, batteries stay in **self-powered mode** (consuming their own stored energy)

Benefits: maximises self-consumption, reduces BMS partial-charge cycles, lower grid draw during night.

Log signatures:
```
⏳ Lazy charge — Battery 1: SOC 78.4% (target 85%), waiting for end of [HC Nuit] slot (8.4h remaining) — will charge later
🔋 Smart grid charge — Battery 1: SOC 78.4%→85%, 1000W/1000W (100% of max) over 0.6h [HC Nuit] [HA forecast]
```

Configure via `tariff.lazy_buffer_hours` (default `0.5`). Set to `0` to revert to the original eager-charge behaviour.

## HA Entity Configuration

### Finding Your entity_ids

In Home Assistant: **Developer Tools → States** → search for:
- `sensor.*battery*soc` or `sensor.*battery*charge`
- `number.*battery*power` or `number.*charge*power`
- P1 meter (mode `p1_invert`): `sensor.*power*` or `sensor.*puissance*` → **negative** value when exporting
- Dedicated sensor (mode `direct`): `sensor.*surplus*` or `sensor.*export*power`

### Surplus Entity Examples by Installation Type

| Installation | surplus_mode | surplus_entity |
|---|---|---|
| P1 / DSMR meter (P1 Dongle, ISKRA…) | `p1_invert` | `sensor.p1_power` |
| Shelly EM on grid cable | `p1_invert` | `sensor.shelly_em_channel_1_power` |
| Fronius with Smart Meter | `p1_invert` | `sensor.fronius_grid_power` |
| SolaX — dedicated export | `direct` | `sensor.solax_export_power` |
| SolarEdge | `direct` | `sensor.solaredge_grid_exported_power` |
| Custom HA template | `direct` | `sensor.solar_surplus_power` |

### Examples by Inverter/Battery Brand

| Hardware | SOC | Charge Power |
|----------|-----|--------------|
| SolaX | `sensor.solax_battery_capacity` | `number.solax_battery_charge_max_current` |
| GivEnergy | `sensor.givtcp_soc` | `number.givtcp_charge_target_soc` |
| Huawei SUN2000 | `sensor.battery_state_of_capacity` | `number.battery_maximum_charging_power` |
| Generic MQTT | `sensor.battery_1_soc` | `number.battery_1_charge_power` |

> **Note on Amperes:** If your HA entity expects Amperes (A) instead of Watts (W),
> use `value_multiplier: 0.02083` for a 48V battery (= 1/48).

## ML.NET — Progressive Learning

| Phase | Sessions | Behaviour |
|-------|----------|-----------|
| Startup | < 50 | Deterministic algo only |
| Learning | 50–200 | ML active, growing confidence |
| Maturity | > 200 | ML driven by weather + history |

The model automatically adjusts:
- The **SoftMax%** (charge target) based on weather forecasts
- The **preventive threshold** (MinPercent) based on remaining sunlight hours

## Logs

```bash
# Live console output
docker compose logs -f solar_worker

# Rotating log file in the volume
docker compose exec solar_worker tail -f /data/logs/solar-worker.log
```

Normal output example:
```
[10:32:01 INF] HA snapshot: surplus=1200W, production=2800W | batteries=[Main:52.3%, Secondary:48.1%]
[10:32:01 INF] Distribution: engine=Deterministic, allocated=1200W/1200W, unused=0W | session#42
[10:32:02 INF] 2/2 charge commands sent to HA
[10:32:02 INF]   [Main]       52.3% → 80.0% | 800.0W | Reached soft max 80%
[10:32:02 INF]   [Secondary]  48.1% → 80.0% | 400.0W | Reached soft max 80%
```

## Mounted File Structure

```
./config/config.yaml    → /config/config.yaml  (read-only)
volume solar_data       → /data/
  ├── ml_models/        → ML.NET models (.zip)
  └── logs/             → rotating logs (14 days)
volume mariadb_data     → MariaDB data
```