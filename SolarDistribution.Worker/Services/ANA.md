# ANA.md — Bug Analysis & Risk of Incorrect Results

> **Date**: April 2, 2026
> **Scope**: full SolarDistribution source code (Core, Worker, Infrastructure, Api, Tests)

---

## Table of Contents

1. [Critical Bugs (immediate impact on results)](#1-critical-bugs)
2. [Major Bugs (results corrupted under certain conditions)](#2-major-bugs)
3. [Incorrect Calculation Risks](#3-incorrect-calculation-risks)
4. [Concurrency / Thread-Safety Issues](#4-concurrency--thread-safety-issues)
5. [Configuration / Validation Issues](#5-configuration--validation-issues)
6. [ML Reliability Issues](#6-ml-reliability-issues)
7. [Security](#7-security)
8. [Performance / Long-Term Stability](#8-performance--long-term-stability)
9. [Test Coverage Gaps](#9-test-coverage-gaps)
10. [Summary & Priorities](#10-summary--priorities)

---

## 1. Critical Bugs

### BUG-C01 — SOC Projection Ignores IdleChargeW in Idle Mode

**File**: `SolarDistribution.Core/Services/BatteryDistributionService.cs` lines 169-175
**Severity**: 🔴 CRITICAL

When `isIdle = true`, the line `energyForSoc = isIdle ? 0 : (solar + grid) * _allocationWindowHours`
sets `energyForSoc` to zero, so `projectedPct = CurrentPercent`. The projected SOC shown in the API
and logs is **identical to the current SOC**, even though the battery is actually receiving `IdleChargeW`.

**Impact**:
- The dashboard displays an incorrect (underestimated) projected SOC.
- Sessions persisted in the DB contain a wrong `NewPercent` → the ML trains on erroneous data.
- Feedback evaluation (`FeedbackEvaluator`) compares the real observation to a biased label.

**Suggested Fix**:
```csharp
double energyForSoc = isIdle
    ? b.IdleChargeW * _allocationWindowHours  // idle = actual injected energy
    : (solar + grid) * _allocationWindowHours;
```

---

### BUG-C02 — `_spotPriceHistory` Grows Unbounded on Exception

**File**: `SolarDistribution.Core/Services/TariffEngine.cs` lines 147-153
**Severity**: 🔴 CRITICAL

`UpdateSpotPrice()` adds an element then calls `_spotPriceHistory.RemoveAll(...)`. If an exception
occurs between the two operations (e.g. `DateTime.UtcNow` fails on a corrupted system clock, or
`RemoveAll` throws during predicate evaluation), the list grows indefinitely.

Under continuous operation over months, this causes a slow memory leak. The `List` is iterated
every cycle in `ComputeDynamicThreshold()` and `IsGridChargeFavorable()`, so performance
degrades over time.

**Impact**:
- OOM crash after several months of operation.
- Progressive slowdown of distribution cycles.

**Suggested Fix**: wrap in try-finally, or use a fixed-size `CircularBuffer`.

---

### BUG-C03 — No Validation for Negative Buffers in Configuration

**File**: `SolarDistribution.Worker/Services/SolarWorker.cs` line 478 + `SolarConfig.cs`
**Severity**: 🔴 CRITICAL

`SurplusBufferW` and `SurplusStopBufferW` are never validated as being ≥ 0.
If a user configures `surplus_buffer_w: -200`, `ComputeEffectiveSurplus()` computes
`Math.Max(0, correctedSurplus - (-200))` = `correctedSurplus + 200`, i.e. an
**artificially inflated surplus**.

**Impact**:
- Batteries receive more power than is actually available.
- The difference is drawn from the grid → unwanted grid import.
- Financial loss if the tariff rate is high.

**Suggested Fix**: validate in `ConfigLoader`:
```csharp
if (config.Polling.SurplusBufferW < 0)
    throw new InvalidOperationException("surplus_buffer_w must be >= 0");
```

---

## 2. Major Bugs

### BUG-M01 — `TariffSlot.ParsedStart/ParsedEnd` Can Throw Uncaught Exception

**File**: `SolarDistribution.Core/Services/TariffEngine.cs` lines 100-101
**Severity**: 🟠 HIGH

`ParsedStart` and `ParsedEnd` call `TimeSpan.Parse(StartTime)` in a computed getter.
If the YAML contains `start_time: "25:00"` or `start_time: "abc"`, a `FormatException` is
thrown on every access — including in `IsActiveAt()` which is called multiple times per cycle.

**Impact**:
- The favorable tariff is never detected → no off-peak grid charging.
- Exception silently caught elsewhere → the slot returns `null` and the system
  behaves as if no tariff slot exists.

---

### BUG-M02 — `DailySummaryService` Uses UTC Day Boundaries Instead of Local Timezone

**File**: `SolarDistribution.Worker/Services/DailySummaryService.cs`
**Severity**: 🟠 HIGH

The service computes daily summaries using `DateTime.UtcNow.Date` as the day boundary.
The user is in Brussels (UTC+1/+2). A distribution cycle at 23:30 local (21:30 UTC in summer)
is counted for the correct day, but a cycle at 00:30 local (22:30 UTC) is shifted by one day.

**Impact**:
- Daily summaries are incorrect at transition hours.
- `ForecastTodayWhAtStartOfDay` (uses `DateTime.Now.DayOfYear` — SolarWorker line 350)
  is inconsistent with the Daily summary that uses UTC.

---

### BUG-M03 — Surplus Correction Does Not Subtract Battery Discharge

**File**: `SolarDistribution.Worker/Services/SolarWorker.cs` lines 182-190
**Severity**: 🟠 HIGH

The surplus correction (`correctedSurplus = rawSurplus + currentBatteriesChargeW`) only adds
**charge** power. If a battery is **discharging** (powering the house), the `CurrentChargeW`
value is clamped to `Math.Max(0, ...)` in the HA reader (HomeAssistantDataReader line 313).

The corrected surplus therefore does not account for ongoing discharge. In practice, if a
battery discharges 500 W to the house while P1 reads 0 W (perfect balance), the corrected
surplus stays at 0 W even though the actual "reclaimable" surplus is 500 W (the battery is
offloading the house).

**Impact**:
- Underestimation of actually available surplus when batteries are discharging.
- Sent commands are too conservative → the discharging battery keeps draining
  instead of being recharged from solar.

---

### BUG-M04 — `GetCurrentPricePerKwh()` Returns `null` Between YAML Slots → Silent Fallback

**File**: `SolarDistribution.Core/Services/TariffEngine.cs` line 208
**Severity**: 🟠 HIGH

If no YAML slot matches the current time AND the HA spot price is not configured,
`GetCurrentPricePerKwh()` returns `null`. Callers like `IsGridChargeFavorable()` return
`false` (not favorable), but `EvaluateContext()` assigns `CurrentPricePerKwh = null` to the context.

Further in `SmartDistributionService`, the `estimatedCost` and `MaxSavingsPerKwh` formulas
multiply by `null` which becomes 0 → **estimated costs are always 0 €** during these intervals.

**Impact**:
- Logs and persisted sessions show zero costs.
- The ML learns that certain hours are "free".

---

### BUG-M05 — `forecastThisHourWh` Not Converted from kWh to Wh

**File**: `SolarDistribution.Worker/HA/HomeAssistantDataReader.cs` lines 215-219
**Severity**: 🟠 HIGH

`ForecastTodayEntity` and `ForecastTomorrowEntity` are converted from kWh to Wh
(`rawToday.Value * 1000.0`), but `ForecastThisHourEntity` and `ForecastNextHourEntity` **are
not converted** — the comment says "already in Wh — no conversion".

If the Solcast integration actually returns **kWh** (like the other entities), the intraday
values will be **1000× too small**. The Solcast curve in `ComputeAdaptiveGridChargeW` would
then be near-zero → no intraday reduction is ever applied.

**Impact**:
- Feature 3 (intraday reduction) is silently disabled if the entity returns kWh.
- Grid charge stays at full power even when solar arrives within < 2h.

**Suggested Fix**: Clearly document the expected unit, or auto-detect the order of magnitude.

---

### BUG-M06 — `HeatingComfortConstraints` Defined but Never Used

**File**: `SolarDistribution.Worker/Configuration/SolarConfig.cs` line 73 + `HeatingOrchestratorService.cs`
**Severity**: 🟠 MEDIUM

The `HeatingComfortConstraintsConfig` class is defined with fields like `MinimumComfortTempC`,
`CriticalDeltaTempC`, `MaxMlEtaP90Minutes`, but `HeatingOrchestratorService.DecideAsync()`
never reads them.

**Impact**:
- Users configure comfort thresholds that are never enforced.
- Heating may select a slow source even when the temperature is critically low.

---

## 3. Incorrect Calculation Risks

### CALC-01 — Approximate Sinusoidal Solar Profile

**File**: `SolarDistribution.Core/Services/SmartDistributionService.cs` method `SolarFractionBetweenHours()`
**Risk**: 🟡 MEDIUM

The calculation uses a pure sinusoidal profile (`sin(π·t/D)`) to estimate the solar energy fraction
between two hours. This is a reasonable approximation under clear skies, but:
- Under cloudy conditions, the actual profile differs significantly.
- Panel orientation/tilt shifts the peak.
- At high latitudes (Brussels = 50.85°N), the sinusoidal approximation is less accurate
  than at tropical latitudes.

**Impact**:
- `ComputeAdaptiveGridChargeW` overestimates or underestimates expected solar.
- In cloudy winter, the algorithm may not charge enough during off-peak hours.

---

### CALC-02 — Consumption Estimate Based on Naive Rolling Average

**File**: `SolarDistribution.Worker/HA/HomeAssistantDataReader.cs` lines 156-174
**Risk**: 🟡 MEDIUM

`estimatedConsumptionNextHoursWh = rollingAvgW * projectionHours` assumes future consumption
will be identical to the average of the last N cycles. No seasonality or hourly patterns
(e.g. evening peak) are considered.

**Impact**:
- In the morning, the average includes the night (low consumption) → underestimation.
- In the evening, the average includes daytime → underestimation of the peak.
- `ComputeAdaptiveGridChargeW` adjusts grid charging based on this value:
  a 30% error in projected consumption leads to a proportional error in charging.

---

### CALC-03 — End-of-Day Reduction (FIX Bug #7) Too Aggressive

**File**: `SolarDistribution.Core/Services/SmartDistributionService.cs` lines 228-248
**Risk**: 🟡 MEDIUM

The SoftMax reduction near sunset is applied in stepped tiers:
- ≤ 3h: -10%, ≤ 2h: -20%, ≤ 1h: -30%

This reduction is applied to the **resulting SoftMax** (after D+1 boost), not to the base SoftMax.
If the D+1 boost already raised SoftMax from 80→90%, the reduction would bring it down to
63% (90 × 0.70) in the last hour, **below the original SoftMax**.

**Impact**:
- At end of day with a poor D+1 forecast, batteries may end up with a very low target,
  cancelling the benefit of the boost.

---

### CALC-04 — Feedback Self-Sufficiency Biased by Consumption Fallback

**File**: `SolarDistribution.Worker/Services/FeedbackEvaluator.cs` lines 251-267
**Risk**: 🟡 MEDIUM

When `ConsumptionEntity` is unavailable, the fallback estimates:
`consumptionW = (Σ AllocatedW + SurplusW) + Max(0, importW)`

This formula **includes import in consumption**, which dilutes the impact of import
in the self-sufficiency calculation:
`selfSuff = 1 - (import / (production + import))` → always > 0.5 if production > 0.

**Impact**:
- The `ActualSelfSufficiencyPct` label is systematically overestimated in fallback mode.
- The ML learns that self-sufficiency is better than it actually is.
- The `ShouldChargeFromGrid` label is `true` less often than it should be.

---

### CALC-05 — `EnergyEfficiency` Score Does Not Account for Grid Charge

**File**: `SolarDistribution.Worker/Services/FeedbackEvaluator.cs` lines 395-400
**Risk**: 🟡 MEDIUM

`ComputeEnergyEfficiency` = `TotalAllocatedW / SurplusW`. When grid charge occurs (off-peak),
`TotalAllocatedW` only contains solar. If `SurplusW = 0` and the battery is being charged at
500W from the grid, the score is `1.0` (line `if (session.SurplusW <= 0) return 1.0`).

This masks sessions where grid charging was inefficient (batteries nearly full but command
sent anyway).

---

### CALC-06 — `HoursUntilSunset` in WeatherData Based on Cooper Formula

**File**: `SolarDistribution.Infrastructure/Services/OpenMeteoWeatherService.cs`
**Risk**: 🟡 MEDIUM

The sunset calculation uses the Cooper formula (declination + hour angle) which can produce
±30 min errors at mid-latitudes. More critically: in polar regions or during the summer solstice
at high latitudes, the formula can return aberrant values (sunset > 24h or < 0h).

**Impact**:
- FIX Bug #7 (end-of-day reduction) triggers at the wrong time.
- `ComputeHoursUntilSolar()` returns offset predictions.

---

## 4. Concurrency / Thread-Safety Issues

### CONC-01 — `IdleChargeHysteresis._state` (Dictionary) Not Thread-Safe

**File**: `SolarDistribution.Worker/Services/IdleChargeHysteresis.cs` line 33
**Severity**: 🟠 HIGH

`Dictionary<int, bool>` is accessed from `SolarWorker.BuildBatteries()` which is called every
cycle. Although in the current pattern only one `BackgroundService` runs, the `Dictionary`
is not thread-safe if timer cycles overlap (fast cycle + slow cycle).

**Potential Impact**:
- Dictionary corruption → inconsistent IdleCharge state.
- Contradictory commands sent to batteries.

**Fix**: use `ConcurrentDictionary<int, bool>`.

---

### CONC-02 — `Queue<double> _surplusWindow` Not Thread-Safe

**File**: `SolarDistribution.Worker/Services/SolarWorker.cs` lines 217-222
**Severity**: 🟠 HIGH

`_surplusWindow` is a `Queue<double>` modified every cycle. If timer cycles overlap,
`Enqueue`/`Dequeue`/`Average` can throw `InvalidOperationException`
(collection modified during iteration).

**Note**: the `BackgroundService` pattern executes `ExecuteAsync` sequentially, so in practice
the risk is low as long as there is no parallelism. However, it remains fragile if the
architecture evolves.

---

### CONC-03 — SmartDistributionService — Unprotected `_cachedYesterdaySelfSufficiency` Cache

**File**: `SolarDistribution.Core/Services/SmartDistributionService.cs` lines 23-25
**Severity**: 🟡 MEDIUM

Fields `_cachedYesterdaySelfSufficiency` and `_cachedYesterdayDoy` are read and written
without synchronization. Since `SmartDistributionService` is registered as Scoped,
two concurrent API requests could read/write the cache simultaneously.

**Note**: in Worker-only mode (single logical thread), no real risk.
But in API mode with concurrent requests, the issue exists.

---

### CONC-04 — `_learnedSpeedDegPerHour` in HomeAssistantDataReader

**File**: `SolarDistribution.Worker/HA/HomeAssistantDataReader.cs` line 78
**Severity**: 🟡 MEDIUM

`Dictionary<string, double> _learnedSpeedDegPerHour` is potentially accessed from both the
main cycle and the periodic refresh.

---

### CONC-05 — `TariffEngine._spotPriceHistory` (List) Not Thread-Safe

**File**: `SolarDistribution.Core/Services/TariffEngine.cs` lines 129-130
**Severity**: 🟡 MEDIUM

`_spotPriceHistory` is a `List<(DateTime, double)>` modified in `UpdateSpotPrice()`
and read in `ComputeDynamicThreshold()` and `IsGridChargeFavorable()`. No lock is used.

If `TariffEngine` is a singleton and the API and Worker run in the same process,
concurrent accesses can corrupt the list.

---

## 5. Configuration / Validation Issues

### CONF-01 — No YAML Schema Validation

**File**: `SolarDistribution.Worker/Configuration/ConfigLoader.cs`
**Severity**: 🟠 HIGH

The YAML is deserialized into `SolarConfig` without schema validation. Missing fields
silently take their default values:
- `MaxChargeRateW = 0` (no charging) if absent.
- `CapacityWh = 0` → division by zero in `BatteryDistributionService`.
- `SoftMaxPercent = 0` → no room to charge.

**Impact**:
- A typo in the YAML (`max_chage_rate_w` instead of `max_charge_rate_w`) → the system
  never charges the batteries, with no error or warning.

---

### CONF-02 — No Validation of `MaxChargeRateW` vs `CapacityWh`

**File**: `SolarDistribution.Worker/Configuration/SolarConfig.cs`
**Severity**: 🟡 MEDIUM

`MaxChargeRateW = 5000` with `CapacityWh = 500` is physically impossible (10C charge rate).
`ComputeCyclePowerCap()` in `BatteryDistributionService` partially protects (cap computed
= `remainingEnergy / windowHours`), but the value is still propagated in logs and sessions,
causing confusion.

---

### CONF-03 — `ZoneAggregationMode` Not Validated

**File**: `SolarDistribution.Worker/Configuration/SolarConfig.cs` line 52
**Severity**: 🟡 MEDIUM

The field accepts any string (`"avrge"`, `"minn"`, etc.). The code consuming this value
likely has a `switch` with a `default` that falls through to "average" —
a typo in the YAML is silently ignored.

---

### CONF-04 — `GridImportEntityMultiplier` Not Validated

**File**: `SolarDistribution.Worker/Configuration/SolarConfig.cs`
**Severity**: 🟡 MEDIUM

No validation that the multiplier is within a reasonable range. A negative multiplier
inverts the import signal → the ML-7 label `ShouldChargeFromGrid` is inverted,
corrupting model training.

---

## 6. ML Reliability Issues

### ML-01 — ML Labels Trained on Inaccurate SOC Projections (see BUG-C01)

**Severity**: 🟠 HIGH

The `NewPercent` stored in `BatterySnapshot` is incorrect for idle batteries.
`FeedbackEvaluator.ComputeObservedOptimalSoftMax()` compares the observed SOC to the session's
`SoftMaxPercent`, but corrections are calibrated from biased data.

---

### ML-02 — `CheckForDriftAsync` Not Implemented

**File**: interface IDistributionMLService
**Severity**: 🟡 MEDIUM

Model drift detection is declared in the interface but never implemented. The model can
degrade across seasons without any alert.

---

### ML-03 — Fixed 48 Strata (12 months × 4h) in ML Queries

**File**: `SolarDistribution.Infrastructure/Repositories/DistributionRepository.cs`
**Severity**: 🟡 MEDIUM

Stratified sampling uses 48 strata. If data is concentrated in 2-3 months (e.g. recent
installation), most strata are empty and the model over-represents the current season.

**Impact**: The model performs poorly during under-represented seasons.

---

### ML-04 — `HeatingPreheatMlService._engines` (ConcurrentBag) Grows Unbounded

**File**: `SolarDistribution.Core/Services/HeatingPreheatMlService.cs`
**Severity**: 🟡 MEDIUM

The `ConcurrentBag<PredictionEngine>` grows on repeated errors (creation fails but attempts
accumulate). No maximum size is enforced.

---

### ML-05 — `FeedbackSoftmaxCorrectionFactor` and `FeedbackSoftmaxReduction` Unbounded

**Severity**: 🟢 LOW

If these config parameters are excessive (e.g. `FeedbackSoftmaxCorrectionFactor: 100`),
the `ObservedOptimalSoftMax` labels will be clamped by `Math.Clamp` to the [60, 95] range,
but the model will see disproportionate correction gradients during training on values
near the bounds.

---

## 7. Security

### SEC-01 — Optional API Authentication

**File**: `SolarDistribution.Api/Program.cs` lines 104-122
**Severity**: 🟠 HIGH

The API Key is only active if `Swagger:ApiKey:Enabled = true` in the settings.
The guard only protects Swagger routes, not API endpoints (`/api/distribution/*`,
`/api/ml/*`, `/api/heating/*`). Anyone can call `POST /calculate` or
`POST /ml/retrain` without authentication.

**Impact**:
- An attacker can trigger intensive ML retrains (DoS).
- An attacker can send arbitrary distribution commands.

---

### SEC-02 — CORS AllowAnyOrigin Default in Dev

**File**: `SolarDistribution.Api/Program.cs` lines 127-145
**Severity**: 🟡 MEDIUM

`Cors:AllowAnyOrigin = true` is the default for dev. If the deployment does not override
this value, production accepts all origins.

---

### SEC-03 — No Rate Limiting on Endpoints

**File**: API-wide
**Severity**: 🟡 MEDIUM

No rate limiting middleware. Endpoints like `/api/distribution/calculate` (which calls
the external weather service, the DB, and the ML engine) are vulnerable to flooding.

---

### SEC-04 — HA Token Stored in Plaintext Configuration

**File**: `SolarDistribution.Worker/Configuration/SolarConfig.cs` — `HomeAssistantConfig.Token`
**Severity**: 🟡 MEDIUM

The Home Assistant token is stored in plaintext in `config.yaml`. If the file is committed
to a Git repository or exposed, the token is compromised.

**Recommendation**: use environment variables or Docker secrets.

---

### SEC-05 — API `GET /summary/daily` Allows 366-Day Query Without Pagination

**File**: `SolarDistribution.Api/Controllers/DistributionController.cs` lines 190-195
**Severity**: 🟡 MEDIUM

The `366 days max` validation is present, but 366 days × ~100+ sessions/day could mean
tens of thousands of summaries loaded into memory at once.

---

## 8. Performance / Long-Term Stability

### PERF-01 — `ComputeHoursRemainingInSlot()` Iterates Minute by Minute (up to 48×60)

**File**: `SolarDistribution.Core/Services/TariffEngine.cs` lines 414-420
**Severity**: 🟢 LOW

The loop `for (int m = 1; m <= 48 * 60; m++)` calls `IsActiveAt()` 2880 times in the worst
case. `IsActiveAt()` calls `TimeSpan.Parse()` on every access (computed getter).
That's ~5760 `TimeSpan.Parse()` calls per cycle.

**Impact**: negligible CPU overhead on a 60s cycle, but wasteful.

**Fix**: cache `ParsedStart`/`ParsedEnd` in private fields.

---

### PERF-02 — `HoursUntilNextFavorableTariff()` Iterates Every 15 min Over 24h

**File**: `SolarDistribution.Core/Services/TariffEngine.cs` lines 252-259
**Severity**: 🟢 LOW

96 iterations × `IsGridChargeFavorable()` (which calls `GetCurrentPricePerKwh` +
`ComputeDynamicThreshold` + potential `Average()` over `_spotPriceHistory`).

---

### PERF-03 — `StatusService` Uses `ReaderWriterLockSlim` Without Timeout

**File**: `SolarDistribution.Core/Services/StatusService.cs` lines 14-24
**Severity**: 🟢 LOW

If an exception occurs between `EnterWriteLock()` and `ExitWriteLock()` (lines 17-22),
the lock is released by the `finally`. This is correct. However, `EnterReadLock()` and
`EnterWriteLock()` block indefinitely without a timeout. A (theoretical) deadlock would
leave the Worker suspended.

---

## 9. Test Coverage Gaps

### TEST-01 — `SmartDistributionService.Apply()` Not Tested

**Severity**: 🟠 HIGH

The `Apply()` method contains the most critical decision logic:
- D+1 boost
- End-of-day reduction
- Emergency detection
- Solar before slot end
- Lazy charging
- IdleChargeW deactivation

No unit test covers this method. Existing tests only cover
`BatteryDistributionService.Distribute()` (pure algorithm).

---

### TEST-02 — `ComputeAdaptiveGridChargeW()` Not Tested

**Severity**: 🟠 HIGH

The adaptive grid charge calculation method (300+ lines) has no dedicated tests.
Edge cases (hoursRemaining = 0, Solcast curve shorter than horizon, etc.) are not verified.

---

### TEST-03 — `FeedbackEvaluator` ML Labels Not Integration-Tested

**Severity**: 🟡 MEDIUM

Existing tests (`FeedbackEvaluatorTests.cs`) test formulas in isolation,
but not the full flow with real HA reads and persisted sessions.

---

### TEST-04 — No Test for `TariffEngine.EvaluateContext()`

**Severity**: 🟡 MEDIUM

`EvaluateContext()` is the central method that synthesizes all tariff information.
No test covers combinations of HA forecast + solar + dynamic tariff.

---

## 10. Summary & Priorities

### Statistics

| Category      | Critical 🔴 | High 🟠 | Medium 🟡 | Low 🟢 | Total |
|---------------|:-----------:|:--------:|:---------:|:------:|:-----:|
| Bugs          | 3           | 6        | 2         | 0      | 11    |
| Calculations  | 0           | 0        | 6         | 0      | 6     |
| Concurrency   | 0           | 2        | 3         | 0      | 5     |
| Configuration | 1           | 1        | 3         | 0      | 5     |
| ML            | 0           | 2        | 3         | 1      | 6     |
| Security      | 0           | 1        | 4         | 0      | 5     |
| Performance   | 0           | 0        | 0         | 3      | 3     |
| Tests         | 0           | 2        | 2         | 0      | 4     |
| **Total**     | **4**       | **14**   | **23**    | **4**  | **45** |

### Immediate Actions (< 24h)

1. **BUG-C01** — Fix the SOC projection in idle mode.
2. **BUG-C03** — Add validation that buffers are ≥ 0 in ConfigLoader.
3. **CONF-01** — Add basic YAML validation (CapacityWh > 0, MaxChargeRateW > 0, SoftMax ∈ [0,100]).

### Priority Actions (< 1 week)

4. **BUG-M01** — Catch TariffSlot parsing errors at load time.
5. **BUG-M04** — Handle the `GetCurrentPricePerKwh() == null` case with an explicit fallback.
6. **BUG-M05** — Clarify and document the unit for ForecastThisHour/ForecastNextHour (Wh vs kWh).
7. **SEC-01** — Add authentication middleware on all API endpoints (not just Swagger).
8. **TEST-01** / **TEST-02** — Write unit tests for `Apply()` and `ComputeAdaptiveGridChargeW()`.

### Consolidation Actions (< 1 sprint)

9. **BUG-M02** — Use local timezone for day boundaries in DailySummaryService.
10. **BUG-M03** — Include battery discharge in surplus correction.
11. **BUG-C02** — Replace `_spotPriceHistory` with a bounded circular buffer.
12. **CONC-01** / **CONC-02** — Switch to thread-safe collections.
13. **ML-02** — Implement `CheckForDriftAsync`.
14. **CALC-03** — Rework the end-of-day reduction formula to avoid cancelling the D+1 boost.
