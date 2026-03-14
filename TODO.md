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

- [x] Read HA consumption entities per zone (oven, EV charger, water heater) when available  
  → `ZoneConsumptionEntities: List<string>` in `SolarConfig_Solar`; summed in `HomeAssistantDataReader.ReadAllAsync()` when `ConsumptionEntity` is absent.
- [x] Add `consumption_entity` to `SolarConfig_Solar` (current home consumption HA entity)  
  → `ConsumptionEntity: string?` property added; read live in `HomeAssistantDataReader.ReadAllAsync()`.
- [x] Use rolling average consumption from the last N cycles (from MariaDB) to project future load  
  → `IDistributionRepository.GetRecentConsumptionAvgWAsync(N)` called in `HomeAssistantDataReader`; window configurable via `ConsumptionRollingWindowCycles` (default 12 cycles). Live reading used as fallback when no DB history exists.
- [x] Feed `EstimatedConsumptionNextHoursWh` into `TariffContext` to refine `ComputeAdaptiveGridChargeW`  
  → `TariffContext.EstimatedConsumptionNextHoursWh` propagated from `HaSnapshot`; used in `ComputeAdaptiveGridChargeW()` to subtract estimated home load from expected solar before computing the grid charge volume.

**Files**: `SolarConfig.cs`, `TariffEngine.cs`, `SmartDistributionService.cs`, `HomeAssistantDataReader.cs`

---

### 2. Real surplus = Production − Consumption (improved P1 correction)
**Problem**: the HA surplus signal is sometimes noisy or delayed (P1 meter latency ~10s).  
If consumption spikes suddenly, the algorithm sends too much power to batteries → grid import.  
The current correction (`current_charge_power_entity`) is optional and often not configured.

- [x] Make `current_charge_power_entity` mandatory or emit a loud warning if absent  
  → `SolarWorker.ExecuteAsync()` now iterates all batteries and emits a `LogWarning` at startup for each battery whose `CurrentChargePowerEntity` is null, with an actionable message pointing to `config.yaml`.
- [x] Add a rolling-window surplus smoother (3-cycle moving average) to filter P1 spikes  
  → `SolarWorker` maintains a `Queue<double> _surplusWindow` (size 3, constant `SurplusSmootherWindowSize`). `correctedSurplus` is enqueued each cycle; `smoothedSurplus = _surplusWindow.Average()` is computed and passed to `ComputeEffectiveSurplus()` instead of the raw corrected value. A `LogDebug` traces the delta filtered when it exceeds 50W.
- [x] Add `production_entity` + `consumption_entity` as an alternative to the raw P1 surplus: `surplus = production − consumption`  
  → `ProductionEntity` and `ConsumptionEntity` both exist in `SolarConfig_Solar` and are read in `HomeAssistantDataReader`; the `p1_invert` surplus mode handles the sign convention.
- [x] Log a warning if `surplusW` is negative after correction (signals a misconfiguration)  
  → `SolarWorker.RunCycleAsync()` checks `correctedSurplus < 0` after adding `currentBatteriesChargeW`. If true, a `LogWarning` is emitted with the raw P1 and battery values, and `correctedSurplus` is clamped to 0 to prevent nonsensical commands.

**Files**: `SolarWorker.cs`, `HomeAssistantDataReader.cs`, `SolarConfig.cs`

---

### 3. Intraday solar forecast (short-term horizon)
**Problem**: the algorithm knows total J and J+1 forecasts in Wh, but not the hour-by-hour curve.  
It cannot tell whether solar will ramp up in 1 hour or 6 hours.

- [x] Consume Solcast entities: `forecast_this_hour`, `forecast_next_hour`, `forecast_remaining_today`  
  → `ForecastThisHourEntity`, `ForecastNextHourEntity`, `ForecastRemainingTodayEntity` added to `SolarConfig_Solar`; all three are read in `HomeAssistantDataReader.ReadAllAsync()`.
- [x] Add to `TariffContext`: `ForecastNextHourWh`, `ForecastNext3HoursWh`  
  → Both fields are present in the `TariffContext` record. `ForecastNext3HoursWh` is computed in `TariffEngine.EvaluateContext()` as `thisH + nextH + h2` (with a conservative `×0.85` decay for hour+2).
- [x] Use these values in `ComputeAdaptiveGridChargeW` to reduce grid charge if solar arrives in < 2h  
  → `intradaySolarReductionFactor` in `SmartDistributionService.ComputeAdaptiveGridChargeW()` proportionally reduces `targetW` when `ForecastNext3HoursWh` covers a significant fraction of `netEnergyNeededWh`, with a 30% floor to keep urgency headroom.
- [x] Replace the simplified sinusoidal profile in `SolarFractionBetweenHours()` with the real Solcast hourly curve  
  → When `tariff.HasIntradayForecast` is true, `ComputeAdaptiveGridChargeW()` integrates `SolcastHourlyCurveWh` hour-by-hour instead of the sinusoidal fallback. The sinusoidal `SolarFractionBetweenHours()` is used only when Solcast entities are absent or when the curve does not cover the full horizon.

**Files**: `TariffEngine.cs`, `SmartDistributionService.cs`, `HomeAssistantDataReader.cs`

---

### 4. Grid charge decision based on net daily energy balance
**Problem**: the "charge from grid tonight" decision ignores what has already been consumed today and what remains to be consumed.

- [x] Compute `EnergyDeficitTodayWh = total_capacity × (softMax − avg_soc) − forecast_remaining_today`  
  → Computed in `TariffEngine.EvaluateContext()` when `ForecastRemainingTodayWh` and `totalBatteryCapacityWh` are provided. Result stored in `TariffContext.EnergyDeficitTodayWh`.
- [x] If deficit > 0 AND off-peak slot AND bad forecast tomorrow → allow grid charge  
  → `gridChargeAllowed` in `TariffEngine` combines `isFavorable && !solarExpected && !gridChargeBlockedBySolarSufficiency`. When deficit > 0 the blocking flag stays false, so a favorable tariff slot will proceed to `ComputeAdaptiveGridChargeW`.
- [x] If deficit ≤ 0 (remaining solar is enough) → block grid charge even during off-peak  
  → `gridChargeBlockedBySolarSufficiency = true` when `energyDeficitTodayWh <= 0`, propagated into `TariffContext.GridChargeBlockedBySolarSufficiency` and factored into `gridChargeAllowed`.
- [x] Persist `daily_solar_consumed_wh` in DB to feed the ML model with real balance data  
  → `DistributionSession.DailySolarConsumedWh` column mapped in `SolarDbContext` (`daily_solar_consumed_wh`). Value computed in `SolarWorker` as `forecastTodayWhAtStartOfDay − forecastRemainingNow`; day reference reset each morning via `_lastDayOfYear` guard.

**Files**: `SmartDistributionService.cs`, `TariffEngine.cs`, `DistributionEntities.cs`

---

## 🟠 Medium Priority — Robustness & accuracy

### 5. Dynamic tariff support (Day-Ahead / SPOT pricing)
**Problem**: tariff slots are hardcoded in `config.yaml`. Switching to a dynamic contract (Tibber, Eneco variable, Belpower...) requires manual updates every time prices change.

- [x] Add support for a HA entity exposing the current spot price (`current_price_entity`)  
  → `TariffConfig.CurrentPriceEntity: string?` added. `HomeAssistantDataReader.ReadAllAsync()` reads the entity each cycle and calls `TariffEngine.UpdateSpotPrice()`. On read failure a `LogWarning` is emitted and the YAML slots are used as fallback for that cycle.
- [x] If configured, replace the YAML slot lookup with the live HA price  
  → `TariffEngine.GetCurrentPricePerKwh()` returns `_liveSpotPrice` when `CurrentPriceEntity` is set and a value is available, bypassing `GetActiveSlot()`. `IsGridChargeFavorable()` branches on the same flag and uses the dynamic threshold (or static fallback) accordingly.
- [x] Compute `grid_charge_threshold_per_kwh` dynamically as rolling 24h average × configurable factor  
  → `TariffEngine` maintains `_spotPriceHistory: List<(DateTime, double)>`, purged to 24h each cycle. `ComputeDynamicThreshold()` returns `avg24h × DynamicThresholdFactor` (default `0.8`) when ≥ 3 points exist, otherwise falls back to the static `GridChargeThresholdPerKwh`. `DynamicThresholdFactor` is a new field in `TariffConfig`, configurable via `dynamic_threshold_factor` in `config.yaml`.
- [x] Log decisions with the actual price used  
  → `SmartDistributionService.LogTariffContext()` builds `tariffModeInfo` showing `[SPOT {price}€/kWh | seuil={threshold}€/kWh dyn]` in dynamic mode, or `[slot 'name' YAML]` in static mode. All three log branches (ALLOWED / BLOCKED / not favorable) include this string. `TariffEngine.IsGridChargeFavorable()` also logs a `LogDebug` with spot price, dynamic threshold, avg24h and factor. `TariffContext` carries the three new fields `IsDynamicTariff`, `SpotPricePerKwh`, `DynamicThresholdPerKwh`.

**Files**: `TariffEngine.cs`, `SolarConfig.cs` (Core), `HomeAssistantDataReader.cs`, `SmartDistributionService.cs`, `config.yaml`

---

### 6. Daily energy balance in the database
**Problem**: individual sessions are stored in DB but there is no daily aggregated view.  
No way to know "how much did I save this month" or to detect drifts over time.

- [x] Add a `daily_summary` table: date, solar_consumed_wh, grid_consumed_wh, grid_charged_wh, estimated_savings_eur  
  → `DailySummary` entity added to `DistributionEntities.cs` (8 fields: `Date`, `SolarConsumedWh`, `GridConsumedWh`, `GridChargedWh`, `SolarAllocatedWh`, `UnusedSurplusWh`, `EstimatedSavingsEur`, `SelfSufficiencyPct`, `SessionCount`, `ComputedAt`). Full EF mapping in `SolarDbContext` (`daily_summaries` table, `DECIMAL` precisions, unique index on `date`). SQL migration in `migrations/migration_daily_summary.sql` (idempotent `CREATE TABLE IF NOT EXISTS`).
- [x] Compute and insert a summary each night (CRON or end-of-solar-day trigger)  
  → `DailySummaryService` (new singleton) exposes `CheckAndComputeYesterdayAsync()` (idempotent, daily date guard) and `ComputeForDateAsync()` (backfill). Aggregation logic in `DistributionRepository.UpsertDailySummaryAsync()`: loads all sessions for the target day, estimates cycle duration from inter-session gaps (capped at 10 min), integrates W×h for `SolarAllocatedWh`, `GridChargedWh`, `UnusedSurplusWh`, `EstimatedSavingsEur`, and reads `DailySolarConsumedWh` from the last session with Solcast data. Computes `SelfSufficiencyPct = solar / (solar + grid) × 100`. `MlRetrainScheduler` calls `CheckAndComputeYesterdayAsync()` as step 0 of its hourly loop (before feedback collection).
- [x] Expose a `GET /api/summary/daily?from=&to=` endpoint  
  → `DistributionController.GetDailySummaries()` added. `from`/`to` default to last 30 days / yesterday. Validates `from ≤ to` and range ≤ 366 days. Returns `List<DailySummaryDto>` (mapped from `DailySummary`). `IDistributionRepository` extended with `GetDailySummariesAsync(from, to)` and `GetYesterdaySelfSufficiencyAsync()`. `IDistributionRepository` now injected into `DistributionController`.
- [x] Use the previous-day balance as an additional ML feature (`YesterdaySelfSufficiencyPct`)  
  → `DistributionFeatures` has new field `[LoadColumn(42)] float YesterdaySelfSufficiencyPct` (normalized /100 → [0–1], 0 if no data). `SmartDistributionService` caches the value in `_cachedYesterdaySelfSufficiency` (refreshed once per UTC day via `_cachedYesterdayDoy` guard). `BuildFeatures()` receives it as optional parameter and maps it.

**Files**: `DistributionEntities.cs`, `SolarDbContext.cs`, `IDistributionRepository.cs`, `DistributionRepository.cs`, `DailySummaryService.cs` (new), `MlRetrainScheduler.cs`, `IDistributionMLService.cs`, `SmartDistributionService.cs`, `DistributionDtos.cs`, `DistributionController.cs`, `Program.cs`, `migrations/migration_daily_summary.sql` (new)

---

### 7. Improved ML feedback (more meaningful labels)
**Problem**: current ML labels (`OptimalSoftMaxPercent`, `OptimalPreventiveThreshold`) are derived from heuristic rules in `FeedbackEvaluator`. The model learns to mimic rules, not to optimize actual measured self-consumption.

- [x] Add `ActualSelfSufficiencyPct` as a label, measured 4h after the session (already planned via `feedback_delay_hours`)  
  → `SessionFeedback.ActualSelfSufficiencyPct` (nullable `double?`) added in `DistributionEntities.cs`. `FeedbackEvaluator.ComputeActualSelfSufficiencyAsync()` relit `GridImportEntity` + `ConsumptionEntity` dans HA au moment du feedback : `selfSufficiency = 1 − (import_W / consumption_W)`. Null si `GridImportEntity` non configurée. `SolarConfig_Solar` reçoit `GridImportEntity`, `GridImportEntityMultiplier` et `GridImportSignificantThresholdW`. Mappé dans `SolarDbContext` (`actual_self_sufficiency_pct DECIMAL(6,3)`). Feature `ActualSelfSufficiencyNormalized` ajoutée dans `DistributionFeatures` (LoadColumn 43).
- [x] Add `DidImportFromGrid` as a boolean label: did we import from the grid in the 4h following the session?  
  → `SessionFeedback.DidImportFromGrid` (nullable `bool?`) ajouté. `FeedbackEvaluator.ReadGridImportAsync()` relit `GridImportEntity` et compare à `GridImportSignificantThresholdW` (défaut 50W, filtre le bruit P1). Mappé en `did_import_from_grid TINYINT(1) NULL`. Feature `DidImportFromGrid` (float 0/1) ajoutée dans `DistributionFeatures` (LoadColumn 44).
- [x] Train a binary classification model `ShouldChargeFromGrid` in addition to the existing regressions  
  → `IDistributionMLService` : ajout de `GridChargePrediction` (PredictedLabel + Probability). `MLRecommendation` enrichi avec `ShouldChargeFromGridPrediction` et `GridChargeClassificationConfidence`. `DistributionMLService` : nouvelle méthode `TrainClassifier()` (FastTree binary classification, 100 arbres, `ExampleWeightColumnName = SampleWeight`). Entraîné conditionnellement si ≥ 40% des sessions ont un label binaire. `PredictAsync()` interroge le classifieur en parallèle des deux régresseurs (pool `_gcEngines`). Modèle persisté dans `ml_grid_charge_model.zip` et chargé depuis le disque au démarrage. `SessionFeedback.ShouldChargeFromGrid` calculé dans `FeedbackEvaluator.ComputeShouldChargeFromGrid()` selon 3 règles : import détecté + pas de charge réseau → true ; autosuffisance < 70% → true ; pas d'import + autosuffisance ≥ 90% → false ; sinon null.
- [x] Weight sessions where solar was wasted (battery full, surplus unused) more heavily in training  
  → `SessionFeedback.SurplusWasted` (bool, `UnusedSurplusW > 50W AND SOC ≥ 95%`). `SessionFeedback.TrainingWeight` calculé par `ComputeTrainingWeight()` : ×2.0 si surplus gaspillé, ×1.8 si import détecté, ×1.5 si autosuffisance < 50%, ×1.4 si session urgence, plafonné à ×3.5. `DistributionFeatures.SampleWeight` (LoadColumn 45) propage le poids vers ML.NET via `ExampleWeightColumnName` sur les deux régresseurs et le classifieur. Migration SQL `migration_v6_ml7_ml8.sql` : colonnes `actual_self_sufficiency_pct`, `did_import_from_grid`, `should_charge_from_grid`, `surplus_wasted`, `training_weight` dans `session_feedbacks`.

**Files**: `FeedbackEvaluator.cs`, `IDistributionMLService.cs`, `DistributionMLService.cs`, `DistributionEntities.cs` (Core + Infrastructure), `SolarConfig.cs`, `SolarDbContext.cs`, `migration_v6_ml7_ml8.sql`

---

### 8. Multi-battery distribution considering battery lifecycle
**Problem**: both batteries receive identical logic. An older or more degraded battery should ideally be stressed less.

- [x] Add optional `cycle_count_entity` (HA entity exposing the BMS cycle count)  
  → `BatteryEntitiesConfig.CycleCountEntity` (string?) ajouté dans `SolarConfig.cs`. `HomeAssistantDataReader` relit l'entité à chaque cycle dans la boucle batteries, convertit en `int` (arrondi, clamp ≥ 0) et trace un `LogDebug`. `BatteryReading` enrichi avec `CycleCount = 0` (paramètre optionnel). `SolarWorker.BuildBatteries()` propage `CycleCount` dans `Battery.CycleCount`.
- [x] Use cycle count as a weighting factor in `DistributeSurplusToGroup` (more cycles → lower effective priority)  
  → `Battery.CycleCount` (int) et `Battery.CycleAgingFactor` (double, défaut 0.0001) ajoutés dans `Battery.cs`. `Battery.EffectivePriority` (converti de `int` en `double`) intègre la pondération : `effectivePriority = Priority × (1 − CycleAgingFactor × CycleCount)`, clampé à `[Priority × 0.5, Priority]` pour éviter une inversion brutale. La règle urgence SOC < MinPercent → 0 reste prioritaire. `BatteryDistributionService` : les deux `GroupBy(b => b.EffectivePriority)` remplacés par `GroupBy(b => Math.Round(b.EffectivePriority, 2))` (évite les micro-groupes dus à l'arithmétique flottante). `BatteryConfig` reçoit `CycleAgingFactor` (double, défaut 0.0001). `BatterySnapshot.CycleCount` persisté en DB via le mapper, mappé dans `SolarDbContext` (`cycle_count INT DEFAULT 0`). Vue SQL `battery_lifecycle_summary` pour Grafana.
- [x] Log a warning if a battery exceeds a configurable cycle threshold (`max_recommended_cycles`)  
  → `BatteryConfig.MaxRecommendedCycles` (int?) ajouté. `SolarWorker.BuildBatteries()` émet un `LogWarning` à chaque cycle si `CycleCount ≥ MaxRecommendedCycles`. Une notification HA persistante (`persistent_notification.create`) est envoyée une seule fois par session du worker via `_cycleAlertSent : HashSet<int>` (guard anti-spam). Migration SQL `migration_v6_ml7_ml8.sql` : colonne `cycle_count INT NOT NULL DEFAULT 0` dans `battery_snapshots`, vue `battery_lifecycle_summary`.

**Files**: `Battery.cs`, `SolarConfig.cs`, `BatteryDistributionService.cs`, `HomeAssistantDataReader.cs`, `SolarWorker.cs`, `DistributionEntities.cs` (Core), `SolarDbContext.cs`, `DistributionSessionMapper.cs`, `migration_v6_ml7_ml8.sql`

---

### 9. Surplus anomaly detection
**Problem**: if the P1 meter or inverter returns a spurious value (e.g. 5000W surplus at night), the algorithm sends nonsensical commands to the batteries.

- [ ] Add `max_plausible_surplus_w` validation in config (e.g. peak installation power × 1.1)  
  → [x] Implemented: added `Solar.Solar.MaxPlausibleSurplusW` and `Polling.MaxConsecutiveAnomaliesBeforeAlert` in configuration; value read by `ConfigLoader`/deserializer and used by `SolarWorker`.
- [ ] If `surplusW > max_plausible_surplus_w` → log a warning and skip the cycle (no command sent)  
  → [x] Implemented: `SolarWorker.RunCycleAsync()` checks the smoothed surplus against `MaxPlausibleSurplusW`, logs a warning and skips the cycle when exceeded.
- [ ] If 3 consecutive anomalous cycles → trigger a HA alert via `persistent_notification` or `input_boolean`  
  → [x] Implemented: consecutive-anomaly counter `_consecutiveSurplusAnomalies` and `HomeAssistantCommandSender.CreatePersistentNotificationAsync()` to send `persistent_notification.create` after the configured threshold.
- [ ] Validate that `surplusW` cannot exceed the `production_entity` reading when both are configured  
  → [x] Implemented: when `production_entity` is available, `SolarWorker` validates `smoothedSurplus` ≤ `productionW` (with small tolerance); if violated the cycle is skipped and anomaly counter incremented.

**Files changed**: `SolarWorker.cs`, `HomeAssistantCommandSender.cs`, `SolarConfig.cs`, `config/config.yaml`, `SolarDistribution.Tests/ConfigLoaderTests.cs`

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
 - [x] Create HA template sensors (documented) exposing the four sensors above
 - [x] Expose these values via `GET /api/status/live` (see `DistributionController.GetLiveStatus`)
 - [x] Document the YAML templates in the README

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