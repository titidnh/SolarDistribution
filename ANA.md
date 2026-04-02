# ANA.md — Bug Analysis & Remaining Open Issues

> **Date**: April 2, 2026
> **Scope**: full SolarDistribution source code (Core, Worker, Infrastructure, Api, Tests)
> **Status**: Post-fix review — only unresolved items remain.

---

## Resolved Items (for reference)

The following categories have been **fully resolved**:

- **Critical Bugs** (3/3): BUG-C01 (idle SOC projection), BUG-C02 (unbounded spot history), BUG-C03 (negative buffer validation)
- **Major Bugs** (6/6): BUG-M01 (TariffSlot parsing), BUG-M02 (UTC day boundaries), BUG-M03 (discharge correction), BUG-M04 (null price fallback), BUG-M05 (kWh/Wh unit config), BUG-M06 (comfort constraints)
- **Concurrency** (5/5): CONC-01 (ConcurrentDictionary), CONC-02 (surplus lock), CONC-03 (cache lock), CONC-04 (volatile swap), CONC-05 (spot history lock)
- **ML-01** (resolved by BUG-C01), **PERF-01** (resolved by BUG-M01 cached ParsedStart/ParsedEnd)
- **Calculations** (6/6): CALC-01 (cloud-weighted solar profile blend), CALC-02 (time-of-day consumption multiplier), CALC-03 (end-of-day reduction on boost delta only), CALC-04 (self-sufficiency fallback without import in denominator), CALC-05 (EnergyEfficiency accounts for GridChargedW), CALC-06 (Cooper formula radian conversion fixed)
- **ML** (4/4): ML-02 (CheckForDriftAsync fully implemented), ML-03 (dynamic strata based on populated count), ML-04 (ConcurrentBag capped at MaxPoolSize=8), ML-05 (feedback config params validated in ConfigLoader)
- **Security**: SEC-02 (CORS now configurable; `AllowAnyOrigin=false` default in prod `appsettings.json`, `true` only in `appsettings.Development.json`)

---

## Table of Contents

1. [Configuration / Validation Issues](#1-configuration--validation-issues)
2. [Security](#2-security)
3. [Performance / Long-Term Stability](#3-performance--long-term-stability)
4. [Test Coverage Gaps](#4-test-coverage-gaps)
5. [Summary & Priorities](#5-summary--priorities)

---

## 1. Configuration / Validation Issues

### CONF-01 — `SoftMaxPercent` Not Validated in ConfigLoader

**File**: `SolarDistribution.Worker/Configuration/ConfigLoader.cs`
**Severity**: 🟠 HIGH

`SoftMaxPercent` is never validated against the `[0, 100]` range. A value of `0`
means no room to charge; a value > 100 is physically impossible and causes
`BatteryDistributionService` to set targets beyond full capacity.

**Note**: `CapacityWh > 0` and `MaxChargeRateW > 0` are now validated, but `SoftMaxPercent`
and `HardMaxPercent` are not.

**Impact**:
- `SoftMaxPercent = 0` → batteries never charge from solar.
- A YAML typo (`soft_max_percent: 800` instead of `80`) is silently accepted.

---

### CONF-02 — No Validation of `MaxChargeRateW` vs `CapacityWh`

**File**: `SolarDistribution.Worker/Configuration/SolarConfig.cs`
**Severity**: 🟡 MEDIUM

`MaxChargeRateW = 5000` with `CapacityWh = 500` is physically impossible (10C charge rate).
`ComputeCyclePowerCap()` in `BatteryDistributionService` partially protects (cap computed
= `remainingEnergy / windowHours`), but the value is still propagated in logs and sessions,
causing confusion.

---

### CONF-03 — `ZoneAggregationMode` Not Validated (non-heating context)

**File**: `SolarDistribution.Worker/Configuration/SolarConfig.cs` line 52
**Severity**: 🟡 MEDIUM

The `ZoneAggregationMode` field on `SolarConfig_Solar` (if used outside heating) accepts any
string. A typo in the YAML is silently ignored and falls through to a default.

**Note**: the `HeatingConfig.ZoneAggregationMode` is now validated as `average|min|max` in
ConfigLoader, but only when `heating.enabled = true`.

---

### CONF-04 — `GridImportEntityMultiplier` Not Validated

**File**: `SolarDistribution.Worker/Configuration/SolarConfig.cs`
**Severity**: 🟡 MEDIUM

No validation that the multiplier is within a reasonable range. A negative multiplier
inverts the import signal → the ML-7 label `ShouldChargeFromGrid` is inverted,
corrupting model training.

---

## 2. Security

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

## 3. Performance / Long-Term Stability

### PERF-02 — `HoursUntilNextFavorableTariff()` Iterates Every 15 min Over 24h

**File**: `SolarDistribution.Core/Services/TariffEngine.cs` lines 252-259
**Severity**: 🟢 LOW

96 iterations × `IsGridChargeFavorable()` (which calls `GetCurrentPricePerKwh` +
`ComputeDynamicThreshold` + potential `Average()` over `_spotPriceHistory`).

---

### PERF-03 — `StatusService` Uses `ReaderWriterLockSlim` Without Timeout

**File**: `SolarDistribution.Core/Services/StatusService.cs` lines 14-24
**Severity**: 🟢 LOW

`EnterReadLock()` and `EnterWriteLock()` block indefinitely without a timeout.
The `finally` correctly releases the lock on exception, but a (theoretical) deadlock
would leave the Worker suspended.

---

## 4. Test Coverage Gaps

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

## 5. Summary & Priorities

### Statistics (remaining open)

| Category      | High 🟠 | Medium 🟡 | Low 🟢 | Total |
|---------------|:--------:|:---------:|:------:|:-----:|
| Configuration | 1        | 3         | 0      | 4     |
| Security      | 1        | 3         | 0      | 4     |
| Performance   | 0        | 0         | 2      | 2     |
| Tests         | 2        | 2         | 0      | 4     |
| **Total**     | **4**    | **8**     | **2**  | **14** |

### Previously Resolved: 27 items

| Category      | Resolved |
|---------------|:--------:|
| Critical Bugs | 3 (C01, C02, C03) |
| Major Bugs    | 6 (M01–M06) |
| Concurrency   | 5 (CONC-01–05) |
| Calculations  | 6 (CALC-01–06) |
| ML            | 5 (ML-01–05) |
| Security      | 1 (SEC-02) |
| Performance   | 1 (PERF-01 via M01) |
| **Total**     | **27** |

### Priority Actions

1. **CONF-01** — Add `SoftMaxPercent ∈ [0, 100]` and `HardMaxPercent ∈ [0, 100]` validation in ConfigLoader.
2. **SEC-01** — Add authentication middleware on all API endpoints (not just Swagger).
3. **TEST-01** / **TEST-02** — Write unit tests for `Apply()` and `ComputeAdaptiveGridChargeW()`.
4. **SEC-03** — Add rate limiting middleware.

### Consolidation Actions

5. **CONF-02** — Add C-rate sanity check (MaxChargeRateW / CapacityWh ≤ 5C).
6. **CONF-04** — Validate `GridImportEntityMultiplier > 0`.
7. **SEC-04** — Support environment variable / Docker secret override for HA token.
8. **SEC-05** — Add pagination to `GET /summary/daily`.
