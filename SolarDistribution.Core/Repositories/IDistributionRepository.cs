using SolarDistribution.Core.Data.Entities;

namespace SolarDistribution.Core.Repositories;

public interface IDistributionRepository
{
    Task SaveSessionAsync(DistributionSession session, CancellationToken ct = default);

    /// <summary>
    /// Sessions avec feedback valide — les seules utilisées pour l'entraînement ML.
    /// Labels = ObservedOptimalSoftMax + ObservedOptimalPreventive issus de l'observation réelle.
    /// Utilise un sampling stratifié par mois/heure pour garantir une couverture calendaire
    /// uniforme sur toute la fenêtre d'entraînement, indépendamment du volume en DB.
    /// </summary>
    Task<List<DistributionSession>> GetSessionsForTrainingAsync(int maxRecords = 5000, CancellationToken ct = default);

    /// <summary>
    /// Compresse les anciennes sessions pour réduire le stockage DB :
    ///   - Sessions > compressionAgeDays : on garde 1 par tranche de slotMinutes,
    ///     SAUF les sessions à fort poids qualité (surplusWasted, import réseau)
    ///     qui sont toujours conservées car elles portent un signal rare.
    ///   - Sessions > hardDeleteAgeDays : suppression définitive.
    /// Les DailySummaries ne sont jamais touchés (déjà agrégés, volume négligeable).
    /// Retourne le nombre de sessions supprimées.
    /// </summary>
    Task<int> PurgeOldSessionsAsync(
        int compressionAgeDays,
        int compressionSlotMinutes,
        int hardDeleteAgeDays,
        CancellationToken ct = default);

    /// <summary>
    /// Sessions dont le feedback est encore pending et dont le délai de collecte est dépassé.
    /// </summary>
    Task<List<DistributionSession>> GetSessionsPendingFeedbackAsync(double feedbackDelayHours, CancellationToken ct = default);

    Task<DistributionSession?> GetLastSessionAsync(CancellationToken ct = default);
    Task SaveFeedbackAsync(SessionFeedback feedback, CancellationToken ct = default);
    Task UpdateMLScoreAsync(long sessionId, double efficiencyScore, CancellationToken ct = default);
    Task<int> CountSessionsAsync(CancellationToken ct = default);
    Task<int> CountValidFeedbacksAsync(CancellationToken ct = default);

    /// <summary>
    /// Retourne la moyenne de consommation maison (W) sur les N derniers cycles persistés.
    /// Null si aucun cycle avec consommation mesurée n'existe encore.
    /// Utilisé pour projeter EstimatedConsumptionNextHoursWh.
    /// </summary>
    Task<double?> GetRecentConsumptionAvgWAsync(int lastNCycles, CancellationToken ct = default);

    // ── Feature 6 — Bilan énergétique journalier ──────────────────────────────

    /// <summary>
    /// Crée ou met à jour le bilan journalier pour une date donnée (upsert par Date).
    /// Calcule les agrégats à partir des sessions de la journée : Wh solar, grid,
    /// surplus non utilisé, économies estimées, taux d'autosuffisance.
    /// </summary>
    Task UpsertDailySummaryAsync(DateTime date, CancellationToken ct = default);

    /// <summary>
    /// Retourne les bilans journaliers sur une plage de dates (incluses).
    /// Utilisé par GET /api/summary/daily?from=&amp;to=
    /// </summary>
    Task<List<DailySummary>> GetDailySummariesAsync(
        DateTime from, DateTime to, CancellationToken ct = default);

    /// <summary>
    /// Taux d'autosuffisance de la journée précédente (%).
    /// Null si aucune donnée Solcast n'est disponible pour hier.
    /// Feature ML YesterdaySelfSufficiencyPct.
    /// </summary>
    Task<double?> GetYesterdaySelfSufficiencyAsync(CancellationToken ct = default);
}