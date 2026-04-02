# SolarDistribution - TODO 2026 (Actionable Roadmap)

Primary objective:
- Maximize solar self-consumption.
- Reduce total EUR/month cost (grid imports + grid charging) without degrading batteries.
- Make decisions explainable, testable, and robust.

---

## 0) Success definition (KPIs to track daily)

- Self-consumption (%)
- Self-sufficiency (%)
- kWh imported from grid (excluding battery charging)
- kWh charged from grid
- kWh surplus wasted
- EUR saved vs baseline scenario
- Number of battery cycles and emergency session rate

Done criteria:
- All 7 KPIs are exposed via API + HA dashboard.
- A daily summary is stored in the DB and available for a 30-day rolling window.

---

## 1) Immediate priorities (Week 1)

### 1.1 Simulation API (no HA commands)

Goal:
- Test a configuration without waiting for a real cycle.

Tasks:
- [ ] Add `POST /api/simulate` with payload: surplus, battery SOCs, tariff context, short-term forecast.
- [ ] Return: target power per battery, grid charge allowed/blocked, decision rationale.
- [ ] Guarantee zero side-effects (no HA commands, no runtime DB writes).
- [ ] Add unit tests and API contract tests.

Done criteria:
- 10 known scenarios pass (normal, night, zero surplus, emergency, high tariff, etc.).
- The endpoint responds < 150 ms locally.

Target files:
- `SolarDistribution.Api/Controllers/DistributionController.cs`
- `SolarDistribution.Core/Services/SmartDistributionService.cs`
- `SolarDistribution.Tests/*Simulation*Tests.cs`

### 1.2 Historical scenario simulation

Goal:
- Compare "current config" vs "candidate config" on N past sessions.

Tasks:
- [ ] Add `POST /api/simulate/scenario`.
- [ ] Replay the last N sessions from the DB.
- [ ] Return a diff: self-consumption, grid import, grid charging, wasted surplus, estimated cost.
- [ ] Add a "top 3 recommended settings" mode (variations of buffers/thresholds).

Done criteria:
- Clear comparative report for N=288 (24h @ 5 min) without timeouts.

Target files:
- `SolarDistribution.Api/Controllers/DistributionController.cs`
- `SolarDistribution.Core/Services/SmartDistributionService.cs`
- `SolarDistribution.Infrastructure/Repositories/DistributionRepository.cs`

---

## 2) High economic value (Week 2)

### 2.1 Consider export price

Goal:
- Avoid charging the battery at all costs when exporting is profitable.

Tasks:
- [ ] Integrate `export_price_per_kwh` into the marginal value calculation.
- [ ] Add a rule: if large surplus and exporting is profitable, reduce/stop forced charging.
- [ ] Log the economic comparison (charge vs export).

Done criteria:
- Logs clearly explain the economic choice.
- At least 3 decision tests "export > charge" pass.

Target files:
- `SolarDistribution.Core/Services/TariffEngine.cs`
- `SolarDistribution.Core/Services/SmartDistributionService.cs`

### 2.2 Peak-shaving strategy

Goal:
- Limit grid imports during high-tariff periods.

Tasks:
- [ ] Add a configurable cap for import power during expensive periods.
- [ ] Prioritize covering home consumption before filling batteries.
- [ ] Add a configurable "peak shaving" logic.

Done criteria:
- Measurable reduction of imports during high tariff hours over 7 days.

Target files:
- `SolarDistribution.Core/Services/SmartDistributionService.cs`
- `config/config.yaml`

---

## 3) Reliability and alerting (Week 3)

### 3.1 Useful HA alerts (no spam)

Tasks:
- [ ] Alert if emergency grid charge >= 3 times / 24h.
- [ ] Watchdog alert if no order sent > 2 × polling interval.
- [ ] Alert if average SOC at 08:00 < min + 10.
- [ ] Add `notify_service` in config.

Done criteria:
- Each alert has a cooldown and an explicit reason.
- Unit tests for thresholds and anti-spam behavior.

Target files:
- `SolarDistribution.Worker/Services/SolarWorker.cs`
- `SolarDistribution.Worker/Services/HomeAssistantCommandSender.cs`
- `SolarDistribution.Core/Models/SolarConfig.cs`

### 3.2 Degraded mode (HA/DB unavailable)

Tasks:
- [ ] If HA is unavailable: fallback to a conservative local decision.
- [ ] If DB is unavailable: persist last state to local JSON.
- [ ] Exponential retry + clear logs of the active strategy.

Done criteria:
- The worker continues to make safe decisions for 30 minutes of unavailability.

Target files:
- `SolarDistribution.Worker/Services/SolarWorker.cs`
- `SolarDistribution.Infrastructure/*`
- `README.md`

---

## 4) Software quality (Week 4)

### 4.1 Priority test campaign

Tasks:
- [ ] 24h E2E: verify final energy balance.
- [ ] Regression: stable decisions on frozen historical data.
- [ ] Fuzz `BatteryDistributionService.Distribute()` (extreme bounds).
- [ ] Midnight/slot-transition tests in `TariffEngine`.

Done criteria:
- Critical services coverage >= 80%.
- No flaky tests across 20 CI runs.

Target files:
- `SolarDistribution.Tests/*.cs`

### 4.2 Configuration documentation

Tasks:
- [ ] Add 3 commented config profiles: small, medium, large.
- [ ] Document "recommended values" per installed peak power.
- [ ] Add a minimal `config.example.yaml` for quick onboarding.

Done criteria:
- A new user can get the system running in < 20 minutes.

Target files:
- `README.md`
- `config/config.yaml`
- `config/config.example.yaml`

---

## 5) Advanced optimization backlog (next iteration)

- [ ] Multi-objective optimization (cost + battery wear + comfort).
- [ ] Sensor drift detection with confidence scores per HA entity.
- [ ] Auto-tune thresholds (`lazy_buffer_hours`, `surplus_buffer_w`) via offline Bayesian search.
- [ ] Schedule flexible loads (water heater, EV, heat pump) based on surplus forecast and spot price.
- [ ] Short-term household consumption predictor (15 min / 1 h) by day type.

---

## 6) New ML block - Predictive heating (building thermal inertia)

Vision:
- Make heating predictive instead of reactive.
- Reach the target temperature exactly at the right time at minimal cost.
- Exploit off-peak slots, weather, occupancy and building thermal inertia.

### 6.1 Data to collect (heating ML foundation)

Tasks:
- [x] Add HA thermostat entities: indoor temperature, setpoint, HVAC mode, heating ON/OFF state.
- [x] Add outdoor temperature, humidity, wind, solar irradiation and hourly weather forecasts.
- [x] Add presence signals: `home`, `away`, `sleep`, `near_home` (geofencing + presence sensors).
- [x] Add hourly energy price (fixed slot or spot) and off-peak indicator.
- [x] Persist a fixed-step history (5 min) for training and evaluation.

Done criteria:
- Heating dataset available for at least 21 days with no major gaps.

Target files:
- `SolarDistribution.Core/Models/SolarConfig.cs`
- `SolarDistribution.Worker/Services/HomeAssistantDataReader.cs`
- `SolarDistribution.Infrastructure/Repositories/DistributionRepository.cs`
- `SolarDistribution.Infrastructure/Data/Entities/*`

### 6.2 "Time-To-Target" ML model (preheat ETA)

Goal:
- Predict "how many minutes until the target temperature" given context.

Tasks:
- [x] Create a regression model `MinutesToTargetTemperature`.
- [x] Minimum features: delta temperature, outdoor temperature, 3h weather trend, HVAC mode, hour, day, presence, recent ON/OFF history.
- [x] Label: observed time to go from `T_current` to `T_target`.
- [x] Add confidence intervals (`p50`, `p90`) to avoid late restarts.
- [x] Periodic retraining (daily or weekly) with temporal validation.

Done criteria:
- Median error <= 10 min on validation week.
- p90 error <= 20 min in normal conditions.

Target files:
- `SolarDistribution.Core/Services/ML/*`
- `SolarDistribution.Core/Services/HeatingPreheatMlService.cs` (new)
- `SolarDistribution.Tests/*HeatingMl*Tests.cs`

### 6.3 Intelligent heating orchestrator

Product rules:
- If mode is `sleep` or `away`, apply a reduced setpoint automatically.
- If mode is `near_home`, compute the optimal restart time to reach comfort at arrival.
- If energy price is high and the building has enough inertia, preheat during off-peak.
- If a strong outdoor temperature rise is forecast, avoid unnecessary preheating.

Tasks:
- [ ] Add `HeatingOrchestratorService` with an explainable decision.
- [ ] Integrate a forward-looking cost score over 6–12h horizon.
- [ ] Add comfort constraints (min/max bounds, anti-yo-yo, min ON/OFF times).
- [ ] Add a heuristic fallback if the model is unavailable.

Done criteria:
- The engine always returns an explainable action: `heat_now`, `delay_until`, `eco_hold`, `resume_comfort`.

Target files:
- `SolarDistribution.Core/Services/HeatingOrchestratorService.cs` (new)
- `SolarDistribution.Worker/Services/SolarWorker.cs`
- `SolarDistribution.Worker/Services/HomeAssistantCommandSender.cs`

### 6.4 Heating API and observability

Tasks:
- [ ] Add `GET /api/heating/status/live` (current mode, indoor temp, setpoint, ETA).
- [ ] Add `POST /api/heating/simulate` (scenario with no HA commands).
- [ ] Add `GET /api/heating/preheat-plan?arrival=` (recommended restart time, estimated cost).
- [ ] Add human-friendly domain logs: "Restart at 17:20 to reach 20.5°C by 18:00".

Done criteria:
- HA dashboard with heating ETA and next restart event.

Target files:
- `SolarDistribution.Api/Controllers/DistributionController.cs`
- `SolarDistribution.Api/Controllers/HeatingController.cs` (new)
- `README.md`

### 6.5 Heating KPIs and impact measurement

KPIs:
- [ ] EUR/day heating before vs after.
- [ ] On-time arrival rate to target temperature.
- [ ] Number of over/under-heats.
- [ ] Perceived comfort (proxy): time spent outside comfort band.
- [ ] Reduction of consumption during `away` and `sleep`.

Done criteria:
- 10–20% heating consumption reduction over 4 weeks (comparable weather) without significant comfort loss.

---

## Recommended execution plan

1. Simulation API (`/simulate`, `/simulate/scenario`)
2. Export pricing + peak shaving
3. HA alerts + degraded mode
4. Heating ML block (collection + ETA model + orchestrator)
5. E2E/regression tests
6. Documentation and example configs

Why this order:
- Reduce the risk of bad settings quickly.
- Capture concrete economic gains early.
- Then harden reliability and maintainability.

---

## Governance notes

- Any new decision rule must include:
  - [ ] a business justification,
  - [ ] an explicit log entry,
  - [ ] at least 2 tests (nominal case + edge case).
- Any new config option must be documented in `README.md` and `config.example.yaml`.
