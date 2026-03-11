# SolarDistribution — TODO & Improvements

> **Core goal**: maximize solar self-consumption.  
> Charge batteries from solar surplus whenever the sun is available.  
> Only charge from the grid when energy is genuinely cheap AND solar won't be enough.  
> Never exceed the available surplus. Make decisions ahead of time rather than reacting.

---

## 🔴 High Priority — Direct impact on self-consumption

### 1. Predictable home consumption (load forecasting)
**Problem**: the algorithm has no idea what you'll consume in the next few hours.  
A 300W surplus at 2pm might be enough — or not — depending on whether the oven fires up at 6pm.  
**Impact**: wrong decision on "charge from grid now or wait for solar".

- [ ] Read HA consumption entities per zone (oven, EV charger, water heater) when available
- [ ] Add `consumption_entity` to `SolarConfig_Solar` (current home consumption HA entity)
- [ ] Use rolling average consumption from the last N cycles (from MariaDB) to project future load
- [ ] Feed `EstimatedConsumptionNextHoursWh` into `TariffContext` to refine `ComputeAdaptiveGridChargeW`

**Files**: `SolarConfig.cs`, `TariffEngine.cs`, `SmartDistributionService.cs`, `HomeAssistantDataReader.cs`

---

### 2. Real surplus = Production − Consumption (improved P1 correction)
**Problem**: the HA surplus signal is sometimes noisy or delayed (P1 meter latency ~10s).  
If consumption spikes suddenly, the algorithm sends too much power to batteries → grid import.  
The current correction (`current_charge_power_entity`) is optional and often not configured.

- [ ] Make `current_charge_power_entity` mandatory or emit a loud warning if absent
- [ ] Add a rolling-window surplus smoother (3-cycle moving average) to filter P1 spikes
- [ ] Add `production_entity` + `consumption_entity` as an alternative to the raw P1 surplus: `surplus = production − consumption`
- [ ] Log a warning if `surplusW` is negative after correction (signals a misconfiguration)

**Files**: `SolarWorker.cs`, `HomeAssistantDataReader.cs`, `SolarConfig.cs`

---

### 3. Intraday solar forecast (short-term horizon)
**Problem**: the algorithm knows total J and J+1 forecasts in Wh, but not the hour-by-hour curve.  
It cannot tell whether solar will ramp up in 1 hour or 6 hours.

- [ ] Consume Solcast entities: `forecast_this_hour`, `forecast_next_hour`, `forecast_remaining_today`
- [ ] Add to `TariffContext`: `ForecastNextHourWh`, `ForecastNext3HoursWh`
- [ ] Use these values in `ComputeAdaptiveGridChargeW` to reduce grid charge if solar arrives in < 2h
- [ ] Replace the simplified sinusoidal profile in `SolarFractionBetweenHours()` with the real Solcast hourly curve

**Files**: `TariffEngine.cs`, `SmartDistributionService.cs`, `HomeAssistantDataReader.cs`

---

### 4. Grid charge decision based on net daily energy balance
**Problem**: the "charge from grid tonight" decision ignores what has already been consumed today and what remains to be consumed.

- [ ] Compute `EnergyDeficitTodayWh = total_capacity × (softMax − avg_soc) − forecast_remaining_today`
- [ ] If deficit > 0 AND off-peak slot AND bad forecast tomorrow → allow grid charge
- [ ] If deficit ≤ 0 (remaining solar is enough) → block grid charge even during off-peak
- [ ] Persist `daily_solar_consumed_wh` in DB to feed the ML model with real balance data

**Files**: `SmartDistributionService.cs`, `TariffEngine.cs`, `DistributionEntities.cs`

---

## 🟠 Medium Priority — Robustness & accuracy

### 5. Dynamic tariff support (Day-Ahead / SPOT pricing)
**Problem**: tariff slots are hardcoded in `config.yaml`. Switching to a dynamic contract (Tibber, Eneco variable, Belpower...) requires manual updates every time prices change.

- [ ] Add support for a HA entity exposing the current spot price (`current_price_entity`)
- [ ] If configured, replace the YAML slot lookup with the live HA price
- [ ] Compute `grid_charge_threshold_per_kwh` dynamically as rolling 24h average × configurable factor
- [ ] Log decisions with the actual price used

**Files**: `TariffEngine.cs`, `SolarConfig.cs`, `HomeAssistantDataReader.cs`

---

### 6. Daily energy balance in the database
**Problem**: individual sessions are stored in DB but there is no daily aggregated view.  
No way to know "how much did I save this month" or to detect drifts over time.

- [ ] Add a `daily_summary` table: date, solar_consumed_wh, grid_consumed_wh, grid_charged_wh, estimated_savings_eur
- [ ] Compute and insert a summary each night (CRON or end-of-solar-day trigger)
- [ ] Expose a `GET /api/summary/daily?from=&to=` endpoint
- [ ] Use the previous-day balance as an additional ML feature (`YesterdaySelfSufficiencyPct`)

**Files**: `DistributionEntities.cs`, `IDistributionRepository.cs`, `DistributionController.cs`

---

### 7. Improved ML feedback (more meaningful labels)
**Problem**: current ML labels (`OptimalSoftMaxPercent`, `OptimalPreventiveThreshold`) are derived from heuristic rules in `FeedbackEvaluator`. The model learns to mimic rules, not to optimize actual measured self-consumption.

- [ ] Add `ActualSelfSufficiencyPct` as a label, measured 4h after the session (already planned via `feedback_delay_hours`)
- [ ] Add `DidImportFromGrid` as a boolean label: did we import from the grid in the 4h following the session?
- [ ] Train a binary classification model `ShouldChargeFromGrid` in addition to the existing regressions
- [ ] Weight sessions where solar was wasted (battery full, surplus unused) more heavily in training

**Files**: `FeedbackEvaluator.cs`, `IDistributionMLService.cs`, `DistributionMLService.cs`

---

### 8. Multi-battery distribution considering battery lifecycle
**Problem**: both batteries receive identical logic. An older or more degraded battery should ideally be stressed less.

- [ ] Add optional `cycle_count_entity` (HA entity exposing the BMS cycle count)
- [ ] Use cycle count as a weighting factor in `DistributeSurplusToGroup` (more cycles → lower effective priority)
- [ ] Log a warning if a battery exceeds a configurable cycle threshold (`max_recommended_cycles`)

**Files**: `Battery.cs`, `BatteryConfig.cs`, `BatteryDistributionService.cs`

---

### 9. Surplus anomaly detection
**Problem**: if the P1 meter or inverter returns a spurious value (e.g. 5000W surplus at night), the algorithm sends nonsensical commands to the batteries.

- [ ] Add `max_plausible_surplus_w` validation in config (e.g. peak installation power × 1.1)
- [ ] If `surplusW > max_plausible_surplus_w` → log a warning and skip the cycle (no command sent)
- [ ] If 3 consecutive anomalous cycles → trigger a HA alert via `persistent_notification` or `input_boolean`
- [ ] Validate that `surplusW` cannot exceed the `production_entity` reading when both are configured

**Files**: `SolarWorker.cs`, `HomeAssistantCommandSender.cs`

---

## 🟡 Low Priority — Comfort & observability

### 10. Native HA dashboard (template sensors)
**Problem**: no visual feedback inside HA about what the worker is doing.  
You must read Docker logs to understand decisions.

- [ ] Create HA template sensors (documented) exposing:
  - `sensor.solar_worker_last_decision` (text: "Charging 312W solar", "Grid charge delayed", etc.)
  - `sensor.solar_worker_effective_surplus`
  - `sensor.solar_worker_grid_charge_allowed` (bool)
  - `sensor.solar_worker_next_grid_charge_start` (datetime)
- [ ] Expose these values via `GET /api/status/live`
- [ ] Document the YAML templates in the README

**Files**: `DistributionController.cs`, `README.md`

---

### 11. "What if" simulation via the API
**Problem**: impossible to test a config change without restarting the worker and waiting for a real cycle.

- [ ] `POST /api/simulate`: accepts surplus + battery SoC + tariff context → returns the computed distribution without sending anything to HA
- [ ] `POST /api/simulate/scenario`: replays the last N sessions with a different config → compares results side by side
- [ ] Useful for safely tuning `surplus_buffer_w`, `hardware_min_charge_w`, `lazy_buffer_hours`

**Files**: `DistributionController.cs`, `SmartDistributionService.cs`

---

### 12. Grid export handling (when export tariff > 0)
**Problem**: `export_price_per_kwh` is in config but never factored into decisions.  
If export is remunerated (e.g. 0.06€/kWh), it may sometimes be better to export than to charge a battery already at 78%.

- [ ] Integrate `export_price_per_kwh` into the `MaxSavingsPerKwh` calculation
- [ ] If `surplus > total_charge_needed` AND `export_price > grid_charge_threshold × factor` → skip forcing off-peak grid charge (export compensates)
- [ ] Add opportunity value to the log: "exporting 200W @ 0.06€/kWh — better than charging at current SoC"

**Files**: `TariffEngine.cs`, `SmartDistributionService.cs`

---

### 13. HA alerts and notifications
**Problem**: no alert if batteries stay empty, if the worker crashes, or if an emergency session repeats too often.

- [ ] Notify via HA (`notify` service) if emergency grid charge triggers 3+ times in 24h (undersized battery or fault)
- [ ] Notify if the worker has sent no command for > 2× `polling_interval` (watchdog)
- [ ] Notify if average SoC at 8am (start of solar day) is < `min_percent` + 10% (batteries not charging enough overnight)
- [ ] Add `notify_service` to `config.yaml` (e.g. `notify.mobile_app_...`)

**Files**: `SolarWorker.cs`, `HomeAssistantCommandSender.cs`, `SolarConfig.cs`

---

## 🔵 Technical / Tech debt

### 14. End-to-end integration tests
- [ ] E2E test: simulate 24h of data (real hourly surplus + SoC) and verify the final energy balance is consistent
- [ ] Regression test: "given this historical data, does the algorithm make the same decisions as before?"
- [ ] Fuzz `BatteryDistributionService.Distribute()` with extreme inputs (surplus=0, surplus=10000, SoC=0, SoC=100)
- [ ] Test `TariffEngine` across midnight transitions (slot spanning 23:00→01:00)

### 15. HA failure resilience
- [ ] If HA is unavailable during an off-peak slot: make a local decision to charge or not (offline mode)
- [ ] Persist last known state to a local JSON file (fallback if DB is unavailable)
- [ ] Exponential backoff retry already in place — document exact behavior in README

### 16. config.yaml documentation
- [ ] Add an `# INSTALLATION TYPE` block at the top with commented examples for 3 profiles: small (1×1kWh), medium (2×1kWh), large (2×5kWh + EV)
- [ ] Document each parameter with a recommended value based on installed peak power
- [ ] Create a minimal `config.example.yaml` (~10 lines) for quick onboarding

---

## Priority legend

| Symbol | Description |
|--------|-------------|
| 🔴 | Direct impact on the core goal (self-consumption) |
| 🟠 | Improves decision accuracy or robustness |
| 🟡 | Comfort, observability, edge cases |
| 🔵 | Technical debt, tests, documentation |