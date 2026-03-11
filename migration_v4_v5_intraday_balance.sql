-- ═══════════════════════════════════════════════════════════════════════════════
-- Migration v4 + v5 — Load Forecasting + Intraday Solcast + Bilan journalier
-- ═══════════════════════════════════════════════════════════════════════════════
--
-- v4 : Load forecasting (consommation maison estimée — session précédente)
--        • measured_consumption_w
--        • estimated_consumption_next_hours_wh
--
-- v5 : Intraday Solcast + bilan énergétique journalier (cette session)
--        • forecast_remaining_today_wh
--        • energy_deficit_today_wh
--        • daily_solar_consumed_wh
--
-- ⚠  Les nouvelles installations utilisent directement init_db.sql — ne pas
--    appliquer cette migration sur une base vierge.
--
-- Compatible MariaDB ≥ 10.3 (ADD COLUMN IF NOT EXISTS).
-- ═══════════════════════════════════════════════════════════════════════════════

-- ─────────────────────────────────────────────────────────────────────────────
-- v4 : Load Forecasting
-- ─────────────────────────────────────────────────────────────────────────────
-- Problème résolu : l'algo ignorait la consommation maison prévue.
-- Un surplus de 300W semblait suffisant, mais si le four démarrait à 18h,
-- les batteries arrivaient vides en HP. Ces colonnes capturent la conso
-- mesurée (W) et la projection sur l'horizon configuré (Wh) pour chaque cycle.
-- La projection est persistée comme feature ML : l'algo apprend quelles heures
-- ont une forte conso et ajuste la charge réseau en anticipation.

ALTER TABLE distribution_sessions
    ADD COLUMN IF NOT EXISTS measured_consumption_w              DECIMAL(10,2) NULL
        COMMENT 'Consommation maison mesurée au cycle (W) — depuis ConsumptionEntity ou ZoneEntities',
    ADD COLUMN IF NOT EXISTS estimated_consumption_next_hours_wh DECIMAL(10,2) NULL
        COMMENT 'Consommation estimée sur les prochaines heures (Wh) — rolling avg × horizon';

-- ─────────────────────────────────────────────────────────────────────────────
-- v5 : Intraday Solcast + Bilan énergétique journalier
-- ─────────────────────────────────────────────────────────────────────────────
-- Problème résolu #1 (Intraday) :
--   L'algo connaissait la production totale J (ex: 8000 Wh) mais pas QUAND.
--   Il appliquait un profil sinusoïdal générique et pouvait charger 600W réseau
--   alors que 1000 Wh arrivent dès la prochaine heure (Solcast forecast_next_hour).
--
-- Problème résolu #2 (Bilan journalier) :
--   La décision "charger du réseau ce soir" ignorait ce qui a déjà été consommé
--   aujourd'hui et ce qui reste à produire. Résultat : on chargeait inutilement
--   depuis le réseau même quand le solaire restant suffisait largement.
--
-- forecast_remaining_today_wh : solaire restant depuis HA (Solcast remaining_today).
--   Utilisé pour calculer EnergyDeficitTodayWh = besoin batterie − solaire_restant.
--   Si déficit ≤ 0 → charge réseau bloquée même pendant les heures creuses.
--
-- energy_deficit_today_wh : capacité × (softMax − avgSOC) − forecast_remaining.
--   Positif → réseau justifié. Négatif/nul → solaire suffit → pas de charge réseau.
--   Feature ML importante : l'algo apprend la relation entre déficit et décision.
--
-- daily_solar_consumed_wh : autoconsommation solaire depuis minuit (Wh).
--   Calculé : ForecastTodayWh(début_journée) − ForecastRemainingToday(maintenant).
--   Feature ML clé : mesure l'écart entre le forecast et la réalité de consommation.
--   Alimente le modèle pour affiner les prédictions futures.

ALTER TABLE distribution_sessions
    ADD COLUMN IF NOT EXISTS forecast_remaining_today_wh DECIMAL(10,2) NULL
        COMMENT 'Production solaire Solcast restante aujourd hui (Wh) — remaining_today entity',
    ADD COLUMN IF NOT EXISTS energy_deficit_today_wh     DECIMAL(10,2) NULL
        COMMENT 'Déficit énergétique journalier (Wh) : besoin_batterie − solaire_restant. Négatif = solaire suffisant',
    ADD COLUMN IF NOT EXISTS daily_solar_consumed_wh     DECIMAL(10,2) NULL
        COMMENT 'Autoconsommation solaire depuis minuit (Wh) : forecast_today_début − forecast_remaining_now';

-- Index composite pour les requêtes ML sur le bilan journalier
-- Permet de filtrer efficacement les sessions avec données de bilan disponibles
-- et d'analyser la corrélation déficit/décision sur des fenêtres temporelles.
ALTER TABLE distribution_sessions
    ADD INDEX IF NOT EXISTS idx_session_energy_balance (
        energy_deficit_today_wh,
        forecast_remaining_today_wh,
        daily_solar_consumed_wh
    );

-- ─────────────────────────────────────────────────────────────────────────────
-- Mise à jour de init_db.sql : rappel manuel
-- ─────────────────────────────────────────────────────────────────────────────
-- Cette migration ne modifie pas init_db.sql automatiquement.
-- Pensez à ajouter ces colonnes dans la section distribution_sessions de init_db.sql
-- pour que les nouvelles installations les aient dès le départ :
--
--   -- v4 Load Forecasting
--   measured_consumption_w              DECIMAL(10,2) NULL,
--   estimated_consumption_next_hours_wh DECIMAL(10,2) NULL,
--
--   -- v5 Intraday + Balance
--   forecast_remaining_today_wh         DECIMAL(10,2) NULL,
--   energy_deficit_today_wh             DECIMAL(10,2) NULL,
--   daily_solar_consumed_wh             DECIMAL(10,2) NULL,
--
--   INDEX idx_session_energy_balance (energy_deficit_today_wh, forecast_remaining_today_wh, daily_solar_consumed_wh)
-- ─────────────────────────────────────────────────────────────────────────────
