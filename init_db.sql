-- ============================================================================
-- SolarDistribution — Script d'initialisation MariaDB (Production)
-- À exécuter une seule fois sur la base de données cible :
--   mysql -u root -p solar_distribution < init_db.sql
--
-- Ce script est IDEMPOTENT : utilise IF NOT EXISTS + CREATE OR REPLACE.
-- Compatible MariaDB 10.5+
-- ============================================================================

CREATE DATABASE IF NOT EXISTS solar_distribution
    CHARACTER SET utf8mb4
    COLLATE utf8mb4_unicode_ci;

USE solar_distribution;

-- ── distribution_sessions ─────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS distribution_sessions (
    id                          BIGINT          NOT NULL AUTO_INCREMENT,
    requested_at                DATETIME(6)     NOT NULL,
    surplus_w                   DECIMAL(10,3)   NOT NULL DEFAULT 0,
    total_allocated_w           DECIMAL(10,3)   NOT NULL DEFAULT 0,
    unused_surplus_w            DECIMAL(10,3)   NOT NULL DEFAULT 0,
    grid_charged_w              DECIMAL(10,3)   NOT NULL DEFAULT 0,
    decision_engine             VARCHAR(30)     NOT NULL,
    ml_confidence_score         DECIMAL(5,4)    NULL,

    -- Contexte tarifaire
    tariff_slot_name            VARCHAR(80)     NULL,
    tariff_price_per_kwh        DECIMAL(6,4)    NULL,
    was_grid_charge_favorable   TINYINT(1)      NOT NULL DEFAULT 0,
    solar_expected_soon         TINYINT(1)      NOT NULL DEFAULT 0,
    hours_to_next_favorable_tariff DECIMAL(5,2) NULL,
    avg_solar_forecast_wm2      DECIMAL(7,2)    NULL,
    tariff_max_savings_per_kwh  DECIMAL(6,4)    NULL,

    PRIMARY KEY (id),
    INDEX idx_session_requested_at  (requested_at),
    INDEX idx_session_engine        (decision_engine),
    INDEX idx_session_tariff        (tariff_slot_name)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- ── battery_snapshots ─────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS battery_snapshots (
    id                      BIGINT          NOT NULL AUTO_INCREMENT,
    session_id              BIGINT          NOT NULL,
    battery_id              INT             NOT NULL,
    capacity_wh             DECIMAL(10,2)   NOT NULL DEFAULT 0,
    max_charge_rate_w       DECIMAL(8,2)    NOT NULL DEFAULT 0,
    min_percent             DECIMAL(5,2)    NOT NULL DEFAULT 0,
    soft_max_percent        DECIMAL(5,2)    NOT NULL DEFAULT 80,
    current_percent_before  DECIMAL(5,2)    NOT NULL DEFAULT 0,
    current_percent_after   DECIMAL(5,2)    NOT NULL DEFAULT 0,
    priority                INT             NOT NULL DEFAULT 0,
    was_urgent              TINYINT(1)      NOT NULL DEFAULT 0,
    allocated_w             DECIMAL(8,2)    NOT NULL DEFAULT 0,
    is_grid_charge          TINYINT(1)      NOT NULL DEFAULT 0,
    reason                  VARCHAR(300)    NOT NULL DEFAULT '',

    PRIMARY KEY (id),
    INDEX idx_snapshot_session_battery (session_id, battery_id),
    CONSTRAINT fk_snapshot_session
        FOREIGN KEY (session_id) REFERENCES distribution_sessions(id)
        ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- ── weather_snapshots ─────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS weather_snapshots (
    id                          BIGINT          NOT NULL AUTO_INCREMENT,
    session_id                  BIGINT          NOT NULL UNIQUE,
    fetched_at                  DATETIME(6)     NOT NULL,
    latitude                    DOUBLE          NOT NULL DEFAULT 0,
    longitude                   DOUBLE          NOT NULL DEFAULT 0,
    temperature_c               DECIMAL(5,2)    NOT NULL DEFAULT 0,
    cloud_cover_percent         DECIMAL(5,2)    NOT NULL DEFAULT 0,
    precipitation_mm_h          DECIMAL(6,3)    NOT NULL DEFAULT 0,
    direct_radiation_wm2        DECIMAL(7,2)    NOT NULL DEFAULT 0,
    diffuse_radiation_wm2       DECIMAL(7,2)    NOT NULL DEFAULT 0,
    daylight_hours              DECIMAL(4,2)    NOT NULL DEFAULT 0,
    hours_until_sunset          DECIMAL(4,2)    NOT NULL DEFAULT 0,
    -- JSON stocké en TEXT avec contrainte CHECK (MariaDB 10.4.3+)
    radiation_forecast_12h_json TEXT            NOT NULL DEFAULT '[]'
                                CHECK (JSON_VALID(radiation_forecast_12h_json)),
    cloud_forecast_12h_json     TEXT            NOT NULL DEFAULT '[]'
                                CHECK (JSON_VALID(cloud_forecast_12h_json)),

    PRIMARY KEY (id),
    CONSTRAINT fk_weather_session
        FOREIGN KEY (session_id) REFERENCES distribution_sessions(id)
        ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- ── ml_prediction_logs ────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS ml_prediction_logs (
    id                              BIGINT          NOT NULL AUTO_INCREMENT,
    session_id                      BIGINT          NOT NULL UNIQUE,
    model_version                   VARCHAR(30)     NOT NULL DEFAULT '',
    confidence_score                DECIMAL(5,4)    NOT NULL DEFAULT 0,
    efficiency_score                DECIMAL(5,4)    NOT NULL DEFAULT 0,
    predicted_soft_max_json         VARCHAR(200)    NOT NULL DEFAULT '',
    predicted_preventive_threshold  DECIMAL(5,2)    NOT NULL DEFAULT 0,
    was_applied                     TINYINT(1)      NOT NULL DEFAULT 0,
    predicted_at                    DATETIME(6)     NOT NULL,

    PRIMARY KEY (id),
    CONSTRAINT fk_mllog_session
        FOREIGN KEY (session_id) REFERENCES distribution_sessions(id)
        ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- ── session_feedbacks ─────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS session_feedbacks (
    id                          BIGINT          NOT NULL AUTO_INCREMENT,
    session_id                  BIGINT          NOT NULL UNIQUE,
    collected_at                DATETIME(6)     NOT NULL,
    feedback_delay_hours        DOUBLE          NOT NULL DEFAULT 0,
    observed_soc_json           TEXT            NOT NULL DEFAULT '{}'
                                CHECK (JSON_VALID(observed_soc_json)),
    avg_soc_at_feedback         DOUBLE          NOT NULL DEFAULT 0,
    min_soc_at_feedback         DOUBLE          NOT NULL DEFAULT 0,
    energy_efficiency_score     DECIMAL(5,4)    NOT NULL DEFAULT 0,
    availability_score          DECIMAL(5,4)    NOT NULL DEFAULT 0,
    observed_optimal_soft_max   DECIMAL(5,2)    NOT NULL DEFAULT 80,
    observed_optimal_preventive DECIMAL(5,2)    NOT NULL DEFAULT 20,
    composite_score             DECIMAL(5,4)    NOT NULL DEFAULT 0,
    -- Enum FeedbackStatus : 0=Pending, 1=Valid, 2=Invalid, 3=Skipped
    status                      TINYINT         NOT NULL DEFAULT 0,
    invalid_reason              VARCHAR(200)    NULL,

    PRIMARY KEY (id),
    INDEX idx_feedback_status    (status),
    INDEX idx_feedback_collected (collected_at),
    CONSTRAINT fk_feedback_session
        FOREIGN KEY (session_id) REFERENCES distribution_sessions(id)
        ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- ── EF Core Migrations table (optionnel — évite que dotnet ef essaie de recréer) ──
CREATE TABLE IF NOT EXISTS __EFMigrationsHistory (
    MigrationId     VARCHAR(150)    NOT NULL,
    ProductVersion  VARCHAR(32)     NOT NULL,
    PRIMARY KEY (MigrationId)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- Marquer le schéma comme migré pour qu'EF ne tente pas de le réappliquer
INSERT IGNORE INTO __EFMigrationsHistory (MigrationId, ProductVersion)
VALUES ('20250101000000_InitialCreate', '9.0.0');

SELECT 'SolarDistribution schema initialized successfully.' AS result;
