-- ═══════════════════════════════════════════════════════════════════════════════
-- SolarDistribution — Migration Feature 6 : Bilan énergétique journalier
-- Créer la table daily_summaries (une ligne par date calendaire UTC).
--
-- À appliquer sur une base existante (v5+).
-- Idempotent : utilise CREATE TABLE IF NOT EXISTS.
-- ═══════════════════════════════════════════════════════════════════════════════

-- ── daily_summaries ───────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS daily_summaries (
    id                      BIGINT          NOT NULL AUTO_INCREMENT,

    -- Clé métier : date calendaire UTC (sans heure)
    date                    DATE            NOT NULL,

    -- Énergie solaire autoconsommée (Wh)
    -- NULL si Solcast non configuré (ForecastRemainingTodayWh absent)
    solar_consumed_wh       DECIMAL(12,2)   NULL
        COMMENT 'Énergie solaire autoconsommée depuis minuit (Wh) — ForecastToday(début) − Remaining(fin). NULL si Solcast absent',

    -- Énergie réseau totale consommée (approximation conservative = grid_charged_wh)
    grid_consumed_wh        DECIMAL(12,2)   NOT NULL DEFAULT 0
        COMMENT 'Énergie totale soutirée du réseau sur la journée (Wh)',

    -- Énergie réseau chargée dans les batteries
    grid_charged_wh         DECIMAL(12,2)   NOT NULL DEFAULT 0
        COMMENT 'Énergie réseau → batteries (Wh) = somme GridChargedW × durée_cycle',

    -- Énergie solaire distribuée aux batteries
    solar_allocated_wh      DECIMAL(12,2)   NOT NULL DEFAULT 0
        COMMENT 'Énergie surplus solaire → batteries (Wh) = somme TotalAllocatedW × durée_cycle',

    -- Surplus non utilisé
    unused_surplus_wh       DECIMAL(12,2)   NOT NULL DEFAULT 0
        COMMENT 'Surplus solaire non distribué (batteries pleines ou sans éligibles) (Wh)',

    -- Économies estimées
    estimated_savings_eur   DECIMAL(8,4)    NULL
        COMMENT 'Économies estimées (€) = GridChargedWh × avg(MaxSavingsPerKwh). NULL si pas de contexte tarifaire',

    -- Taux d autosuffisance (%)
    self_sufficiency_pct    DECIMAL(5,2)    NULL
        COMMENT 'SolarConsumedWh / (Solar + Grid) × 100. NULL si Solcast absent. Feature ML YesterdaySelfSufficiencyPct',

    -- Compteur de sessions
    session_count           INT             NOT NULL DEFAULT 0
        COMMENT 'Nombre de sessions de distribution sur cette journée',

    -- Metadata
    computed_at             DATETIME(6)     NOT NULL
        COMMENT 'Timestamp UTC du dernier calcul / recalcul de ce bilan',

    PRIMARY KEY (id),

    -- Contrainte unicité sur la date (clé métier — upsert via date)
    UNIQUE KEY uq_daily_summary_date (date),

    -- Index pour les requêtes de plage (GET /api/summary/daily?from=&to=)
    INDEX idx_daily_summary_date (date),

    -- Index pour les requêtes ML (GetYesterdaySelfSufficiencyAsync)
    INDEX idx_daily_summary_self_sufficiency (date, self_sufficiency_pct)

) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci
  COMMENT='Bilan énergétique journalier agrégé. Une ligne par date calendaire UTC. Calculé par DailySummaryService en fin de journée solaire.';

-- ── Vérification (optionnel — commenter en prod) ──────────────────────────────
-- SELECT 'daily_summaries created' AS status;
-- DESCRIBE daily_summaries;
