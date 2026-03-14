-- ============================================================
--  Migration v6 — ML-7 (meaningful feedback labels) + ML-8 (battery lifecycle)
--  Idempotent : peut être rejouée sans erreur.
-- ============================================================

-- ── ML-7 : labels de feedback enrichis ──────────────────────────────────────
-- Ajout des colonnes à session_feedbacks :
--   actual_self_sufficiency_pct  : taux d'autosuffisance réel mesuré N heures après la session
--   did_import_from_grid         : import réseau détecté après la session (boolean)
--   should_charge_from_grid      : label de classification binaire (nullable)
--   surplus_wasted               : surplus gaspillé (batteries pleines, surplus non absorbé)
--   training_weight              : poids d'entraînement ML (> 1 pour les sessions problématiques)

ALTER TABLE session_feedbacks
    ADD COLUMN IF NOT EXISTS actual_self_sufficiency_pct DECIMAL(6,3)  NULL    COMMENT 'Taux autosuffisance réel 0-1, mesuré N heures après la session via GridImportEntity + ConsumptionEntity HA. NULL si entités non configurées.',
    ADD COLUMN IF NOT EXISTS did_import_from_grid        TINYINT(1)    NULL    COMMENT '1 si import réseau significatif détecté dans les N heures suivant la session. NULL si GridImportEntity non configurée.',
    ADD COLUMN IF NOT EXISTS should_charge_from_grid     TINYINT(1)    NULL    COMMENT 'Label de classification ML-7c : 1 si la session aurait dû forcer la charge réseau. NULL si signal ambigu ou données insuffisantes.',
    ADD COLUMN IF NOT EXISTS surplus_wasted              TINYINT(1)    NOT NULL DEFAULT 0 COMMENT '1 si surplus solaire gaspillé (batteries pleines, UnusedSurplusW > 50W). Utilisé pour la pondération ML-7d.',
    ADD COLUMN IF NOT EXISTS training_weight             DECIMAL(5,3)  NOT NULL DEFAULT 1.0 COMMENT 'Poids entraînement ML-7d. > 1 pour sessions avec surplus gaspillé ou import non voulu. Plafonné à 3.5.';

-- Index pour filtrer rapidement les sessions avec classification binaire disponible
-- (utilisé par DistributionMLService pour entraîner le classifieur ML-7c)
CREATE INDEX IF NOT EXISTS idx_sf_should_charge
    ON session_feedbacks (should_charge_from_grid)
    WHERE should_charge_from_grid IS NOT NULL;

-- ── ML-8 : cycle de vie batterie ────────────────────────────────────────────
-- Ajout de la colonne cycle_count dans battery_snapshots.
-- Permet de tracer l'évolution du compteur de cycles au fil du temps
-- et de reconstruire la pondération historique pour le ML.

ALTER TABLE battery_snapshots
    ADD COLUMN IF NOT EXISTS cycle_count INT NOT NULL DEFAULT 0
        COMMENT 'ML-8 : Nombre de cycles de charge de cette batterie au moment de la session. 0 si CycleCountEntity non configurée. Utilisé pour pondérer EffectivePriority dans BatteryDistributionService.';

-- Vue utilitaire : résumé cycle de vie par batterie
-- Utilisable dans Grafana / HA pour suivre l'usure de chaque batterie.
CREATE OR REPLACE VIEW battery_lifecycle_summary AS
SELECT
    battery_id,
    MAX(cycle_count)                          AS latest_cycle_count,
    MAX(requested_at)                         AS last_seen_at,
    COUNT(*)                                  AS total_sessions,
    AVG(current_percent_before)               AS avg_soc_at_session,
    SUM(CASE WHEN is_emergency_grid_charge = 1 THEN 1 ELSE 0 END) AS emergency_sessions
FROM battery_snapshots bs
JOIN distribution_sessions ds ON ds.id = bs.session_id
WHERE cycle_count > 0
GROUP BY battery_id;

-- ── Commentaires de colonne utilitaires ─────────────────────────────────────
-- (rappel des nouvelles colonnes config.yaml correspondantes)

-- config.yaml — sous chaque batterie :
--   entities:
--     cycle_count_entity: "sensor.delta3_salon_battery_cycles"   # ML-8 optionnel
--   max_recommended_cycles: 3000                                  # ML-8 optionnel
--   cycle_aging_factor: 0.0001                                    # ML-8, défaut 0.0001

-- config.yaml — sous solar :
--   grid_import_entity: "sensor.p1_grid_import_power"            # ML-7 optionnel
--   grid_import_entity_multiplier: 1.0                           # ML-7, défaut 1.0
--   grid_import_significant_threshold_w: 50.0                    # ML-7, défaut 50W
