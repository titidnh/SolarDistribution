using SolarDistribution.Core.Data.Entities;

namespace SolarDistribution.Core.Repositories;

public interface IDistributionRepository
{
    Task SaveSessionAsync(DistributionSession session, CancellationToken ct = default);
    Task SaveHeatingSampleAsync(HeatingSample sample, CancellationToken ct = default);
    Task<List<HeatingSample>> GetHeatingSamplesForTrainingAsync(int maxRecords = 20000, int windowDays = 180, CancellationToken ct = default);
    Task<int> CountHeatingSamplesAsync(CancellationToken ct = default);
    Task<int> PurgeOldHeatingSamplesAsync(int compressionAgeDays, int compressionSlotMinutes, int hardDeleteAgeDays, CancellationToken ct = default);

    /// <summary>
    /// Sessions with valid feedback — the only ones used for ML training.
    /// Labels = ObservedOptimalSoftMax + ObservedOptimalPreventive derived from real observations.
    /// Uses stratified sampling by month/hour to guarantee uniform calendar coverage
    /// across the entire training window, regardless of the volume in the DB.
    /// </summary>
    Task<List<DistributionSession>> GetSessionsForTrainingAsync(int maxRecords = 5000, CancellationToken ct = default);

    /// <summary>
    /// Compresses old sessions to reduce DB storage:
    ///   - Sessions older than compressionAgeDays: keeps 1 per slotMinutes slot,
    ///     EXCEPT sessions with high quality weight (surplusWasted, grid import)
    ///     which are always kept because they carry a rare signal.
    ///   - Sessions older than hardDeleteAgeDays: permanently deleted.
    /// DailySummaries are never touched (already aggregated, negligible volume).
    /// Returns the number of sessions deleted.
    /// </summary>
    Task<int> PurgeOldSessionsAsync(
        int compressionAgeDays,
        int compressionSlotMinutes,
        int hardDeleteAgeDays,
        CancellationToken ct = default);

    /// <summary>
    /// Sessions whose feedback is still pending and whose collection delay has elapsed.
    /// </summary>
    Task<List<DistributionSession>> GetSessionsPendingFeedbackAsync(double feedbackDelayHours, CancellationToken ct = default);

    Task<DistributionSession?> GetLastSessionAsync(CancellationToken ct = default);
    Task SaveFeedbackAsync(SessionFeedback feedback, CancellationToken ct = default);
    Task UpdateMLScoreAsync(long sessionId, double efficiencyScore, CancellationToken ct = default);
    Task<int> CountSessionsAsync(CancellationToken ct = default);
    Task<int> CountValidFeedbacksAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the average home consumption (W) over the last N persisted cycles.
    /// Null if no cycle with measured consumption exists yet.
    /// Used to project EstimatedConsumptionNextHoursWh.
    /// </summary>
    Task<double?> GetRecentConsumptionAvgWAsync(int lastNCycles, CancellationToken ct = default);

    // ── Feature 6 — Daily energy summary ────────────────────────────────────

    /// <summary>
    /// Creates or updates the daily summary for a given date (upsert by Date).
    /// Computes aggregates from the day's sessions: solar Wh, grid,
    /// unused surplus, estimated savings, self-sufficiency rate.
    /// </summary>
    Task UpsertDailySummaryAsync(DateTime date, CancellationToken ct = default);

    /// <summary>
    /// Returns daily summaries over a date range (inclusive).
    /// Used by GET /api/summary/daily?from=&amp;to=
    /// </summary>
    Task<List<DailySummary>> GetDailySummariesAsync(
        DateTime from, DateTime to, CancellationToken ct = default);

    /// <summary>
    /// Self-sufficiency rate for the previous day (%).
    /// Null if no Solcast data is available for yesterday.
    /// ML feature YesterdaySelfSufficiencyPct.
    /// </summary>
    Task<double?> GetYesterdaySelfSufficiencyAsync(CancellationToken ct = default);

    // ── Gas meter readings ────────────────────────────────────────────────────

    /// <summary>
    /// Persists a gas meter reading (either automatic from HA or manual from API).
    /// </summary>
    Task SaveGasMeterReadingAsync(GasMeterReading reading, CancellationToken ct = default);

    /// <summary>
    /// Returns the most recent N gas meter readings, ordered descending by ReadAtUtc.
    /// </summary>
    Task<List<GasMeterReading>> GetRecentGasMeterReadingsAsync(int count = 100, CancellationToken ct = default);

    /// <summary>
    /// Last recorded reading before or at a given UTC timestamp.
    /// Used to compute consumption between two readings.
    /// </summary>
    Task<GasMeterReading?> GetLastGasMeterReadingBeforeAsync(DateTime utcBefore, CancellationToken ct = default);
}