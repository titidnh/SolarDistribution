-- ═══════════════════════════════════════════════════════════════════════════════
-- Migration v3 — ML-7 (charge adaptative) + ML-8 (prévisions HA)
-- À appliquer sur les bases existantes.
-- Les nouvelles installations utilisent directement init_db.sql.
-- ═══════════════════════════════════════════════════════════════════════════════

-- ── ML-7 : contexte adaptatif étendu ─────────────────────────────────────────
ALTER TABLE distribution_sessions
    ADD COLUMN IF NOT EXISTS hours_remaining_in_slot   DECIMAL(5,2)  NULL
        COMMENT 'Heures restantes dans le créneau HC au moment de la session',
    ADD COLUMN IF NOT EXISTS hours_until_solar         DECIMAL(5,2)  NULL
        COMMENT 'Heures avant prochain ensoleillement suffisant (null=non prévu)',
    ADD COLUMN IF NOT EXISTS had_emergency_grid_charge TINYINT(1)    NOT NULL DEFAULT 0
        COMMENT 'True si au moins une batterie était en charge urgence réseau',
    ADD COLUMN IF NOT EXISTS effective_grid_charge_w   DECIMAL(8,2)  NULL
        COMMENT 'Puissance réseau adaptative effective moyenne hors urgence (W)',

-- ── ML-8 : prévisions HA installation-spécifiques ────────────────────────────
    ADD COLUMN IF NOT EXISTS forecast_today_wh         DECIMAL(10,2) NULL
        COMMENT 'Production solaire estimée aujourd hui depuis HA (Wh) — Solcast/Forecast.solar',
    ADD COLUMN IF NOT EXISTS forecast_tomorrow_wh      DECIMAL(10,2) NULL
        COMMENT 'Production solaire estimée demain depuis HA (Wh) — Solcast/Forecast.solar';

-- Index pour filtrer rapidement les sessions avec prévisions HA disponibles
ALTER TABLE distribution_sessions
    ADD INDEX IF NOT EXISTS idx_session_has_forecast (forecast_today_wh, forecast_tomorrow_wh);

-- ── ML-7 : contexte urgence par batterie ─────────────────────────────────────
ALTER TABLE battery_snapshots
    ADD COLUMN IF NOT EXISTS is_emergency_grid_charge TINYINT(1)   NOT NULL DEFAULT 0
        COMMENT 'True si cette charge réseau était déclenchée par urgence SOC critique',
    ADD COLUMN IF NOT EXISTS grid_charge_allowed_w    DECIMAL(8,2) NOT NULL DEFAULT 0
        COMMENT 'Puissance réseau adaptative autorisée pour cette batterie lors de la session (W)';
