using Microsoft.EntityFrameworkCore;
using SolarDistribution.Infrastructure.Data;
using SolarDistribution.Core.Data.Entities;
using SolarDistribution.Core.Repositories;

namespace SolarDistribution.Infrastructure.Repositories;

public class DistributionRepository : IDistributionRepository
{
    private readonly SolarDbContext _db;

    public DistributionRepository(SolarDbContext db)
    {
        _db = db;
    }

    public async Task SaveSessionAsync(DistributionSession session, CancellationToken ct = default)
    {
        _db.DistributionSessions.Add(session);
        await _db.SaveChangesAsync(ct);
    }

    public async Task SaveHeatingSampleAsync(HeatingSample sample, CancellationToken ct = default)
    {
        _db.HeatingSamples.Add(sample);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<List<HeatingSample>> GetHeatingSamplesForTrainingAsync(
        int maxRecords = 20000,
        int windowDays = 180,
        CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, windowDays));

        return await _db.HeatingSamples
            .Where(x => x.SampledAtUtc >= cutoff)
            .OrderByDescending(x => x.SampledAtUtc)
            .Take(Math.Max(100, maxRecords))
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public async Task<int> CountHeatingSamplesAsync(CancellationToken ct = default)
        => await _db.HeatingSamples.CountAsync(ct);

    public async Task<int> PurgeOldHeatingSamplesAsync(
        int compressionAgeDays,
        int compressionSlotMinutes,
        int hardDeleteAgeDays,
        CancellationToken ct = default)
    {
        int totalDeleted = 0;
        var now = DateTime.UtcNow;

        var compressionEnd = now.AddDays(-compressionAgeDays);
        var compressionStart = now.AddDays(-hardDeleteAgeDays);

        var toCompress = await _db.HeatingSamples
            .Where(s => s.SampledAtUtc < compressionEnd
                     && s.SampledAtUtc >= compressionStart)
            .Select(s => new { s.Id, s.SampledAtUtc })
            .AsNoTracking()
            .ToListAsync(ct);

        if (toCompress.Any())
        {
            var slotMs = (long)TimeSpan.FromMinutes(Math.Max(1, compressionSlotMinutes)).TotalMilliseconds;
            var grouped = toCompress.GroupBy(s => ((DateTimeOffset)s.SampledAtUtc).ToUnixTimeMilliseconds() / slotMs);
            var idsToDelete = new List<long>();

            foreach (var group in grouped)
            {
                var keepId = group
                    .OrderByDescending(x => x.SampledAtUtc)
                    .Select(x => x.Id)
                    .First();

                idsToDelete.AddRange(group.Where(x => x.Id != keepId).Select(x => x.Id));
            }

            const int DeleteBatch = 500;
            for (int i = 0; i < idsToDelete.Count; i += DeleteBatch)
            {
                var batch = idsToDelete.Skip(i).Take(DeleteBatch).ToList();
                int deleted = await _db.HeatingSamples
                    .Where(s => batch.Contains(s.Id))
                    .ExecuteDeleteAsync(ct);
                totalDeleted += deleted;
            }
        }

        var hardDeleteCutoff = now.AddDays(-hardDeleteAgeDays);
        const int HardDeleteBatch = 1000;

        int hardDeleted;
        do
        {
            hardDeleted = await _db.HeatingSamples
                .Where(s => s.SampledAtUtc < hardDeleteCutoff)
                .OrderBy(s => s.SampledAtUtc)
                .ThenBy(s => s.Id)
                .Take(HardDeleteBatch)
                .ExecuteDeleteAsync(ct);
            totalDeleted += hardDeleted;
        }
        while (hardDeleted == HardDeleteBatch && !ct.IsCancellationRequested);

        return totalDeleted;
    }

    public async Task SaveGasMeterReadingAsync(GasMeterReading reading, CancellationToken ct = default)
    {
        _db.GasMeterReadings.Add(reading);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<List<GasMeterReading>> GetRecentGasMeterReadingsAsync(
        int count = 100, CancellationToken ct = default)
        => await _db.GasMeterReadings
            .OrderByDescending(r => r.ReadAtUtc)
            .Take(count)
            .ToListAsync(ct);

    public async Task<GasMeterReading?> GetLastGasMeterReadingBeforeAsync(
        DateTime utcBefore, CancellationToken ct = default)
        => await _db.GasMeterReadings
            .Where(r => r.ReadAtUtc <= utcBefore)
            .OrderByDescending(r => r.ReadAtUtc)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// Loads sessions for ML training using calendar-stratified sampling.
    ///
    /// STRATEGY:
    ///   Instead of a simple Take(N) that overrepresents recent data,
    ///   the time window is split into strata (month × hour_of_day) and a
    ///   proportional quota is drawn from each stratum.
    ///
    ///   Result: the model sees as much January data as July data,
    ///   and as much nighttime data as daytime data — which is crucial for learning
    ///   weather/calendar patterns over 2 years without recency bias.
    ///
    ///   Sessions with high qualitative weight (surplusWasted, grid import) are
    ///   always included first within their stratum, then filled by normal
    ///   sessions up to the quota.
    /// </summary>
    public async Task<List<DistributionSession>> GetSessionsForTrainingAsync(
        int maxRecords = 5000, CancellationToken ct = default)
    {
        // ── 1. Fetch stratified IDs — lightweight query, no Include ──────────
        // Only load the metadata needed for sampling first
        // to avoid pulling hundreds of thousands of rows into memory.
        var cutoff = DateTime.UtcNow.AddYears(-2); // fixed 2-year window

        var candidates = await _db.DistributionSessions
            .Where(s => s.Feedback != null
                     && s.Feedback.Status == FeedbackStatus.Valid
                     && s.RequestedAt >= cutoff)
            .Select(s => new
            {
                s.Id,
                s.RequestedAt,
                // Qualitative signal for intra-stratum priority
                IsHighQuality = s.Feedback!.SurplusWasted || s.Feedback.DidImportFromGrid == true
            })
            .AsNoTracking()
            .ToListAsync(ct);

        if (candidates.Count == 0)
            return new List<DistributionSession>();

        // ── 2. Stratified sampling by (month × hour_bucket) ──────────────────
        // 12 months × 4 buckets of 6h = 48 strata
        // Each stratum receives a quota = maxRecords / 48, rounded.
        // Strata with fewer data points contribute what they have.
        const int HourBuckets = 4;          // 0-5h, 6-11h, 12-17h, 18-23h
        const int TotalStrata = 12 * HourBuckets; // 48
        int quotaPerStratum = Math.Max(1, maxRecords / TotalStrata);

        var selectedIds = new HashSet<long>(maxRecords);

        var byStratum = candidates.GroupBy(s => (
            Month: s.RequestedAt.Month,
            HourBucket: s.RequestedAt.Hour / 6
        ));

        foreach (var stratum in byStratum)
        {
            // Prioritize high-signal sessions within the stratum
            var highQuality = stratum.Where(s => s.IsHighQuality).Select(s => s.Id).ToList();
            var normal = stratum.Where(s => !s.IsHighQuality).Select(s => s.Id).ToList();

            // Always include high-quality sessions (rare and valuable), capped at quota×2
            foreach (var id in highQuality.Take(quotaPerStratum * 2))
                selectedIds.Add(id);

            // Fill with normal sessions up to the quota
            int remaining = Math.Max(0, quotaPerStratum - highQuality.Count);
            // Deterministic shuffle to diversify without chronological bias
            var shuffled = normal.OrderBy(id => id % 97).Take(remaining);
            foreach (var id in shuffled)
                selectedIds.Add(id);
        }

        // ── 3. Load only the selected sessions with their context ─────────────
        // Split into batches of 500 IDs to avoid overly large IN() clauses on MySQL
        var idList = selectedIds.ToList();
        var result = new List<DistributionSession>(idList.Count);

        const int BatchSize = 500;
        for (int i = 0; i < idList.Count; i += BatchSize)
        {
            var batch = idList.Skip(i).Take(BatchSize).ToList();
            var loaded = await _db.DistributionSessions
                .Include(s => s.BatterySnapshots)
                .Include(s => s.Weather)
                .Include(s => s.MlPrediction)
                .Include(s => s.Feedback)
                .Where(s => batch.Contains(s.Id))
                .AsNoTracking()
                .ToListAsync(ct);
            result.AddRange(loaded);
        }

        return result;
    }

    /// <summary>
    /// Compresses and purges old sessions to control DB storage.
    ///
    /// RULES:
    ///   Phase 1 — Compression (compressionAgeDays → hardDeleteAgeDays):
    ///     For each slotMinutes window, keep exactly 1 session,
    ///     prioritizing sessions with high qualitative weight (surplusWasted or import).
    ///     "Winner" sessions are kept; the rest are deleted.
    ///
    ///   Phase 2 — Hard delete (> hardDeleteAgeDays):
    ///     Permanent deletion of all sessions outside the useful window.
    ///     DailySummaries are never touched.
    ///
    /// Returns the total number of sessions deleted.
    /// </summary>
    public async Task<int> PurgeOldSessionsAsync(
        int compressionAgeDays,
        int compressionSlotMinutes,
        int hardDeleteAgeDays,
        CancellationToken ct = default)
    {
        int totalDeleted = 0;
        var now = DateTime.UtcNow;

        // ── Phase 1: Compression ─────────────────────────────────────────────
        var compressionEnd = now.AddDays(-compressionAgeDays);
        var compressionStart = now.AddDays(-hardDeleteAgeDays);

        // Load lightweight metadata of sessions eligible for compression
        var toCompress = await _db.DistributionSessions
            .Where(s => s.RequestedAt < compressionEnd
                     && s.RequestedAt >= compressionStart)
            .Select(s => new
            {
                s.Id,
                s.RequestedAt,
                // Sessions without valid feedback are direct candidates for purge
                IsValid = s.Feedback != null && s.Feedback.Status == FeedbackStatus.Valid,
                IsHighQuality = s.Feedback != null
                    && (s.Feedback.SurplusWasted || s.Feedback.DidImportFromGrid == true)
            })
            .AsNoTracking()
            .ToListAsync(ct);

        if (toCompress.Any())
        {
            // Group by slotMinutes time window
            var slotMs = (long)TimeSpan.FromMinutes(compressionSlotMinutes).TotalMilliseconds;

            var grouped = toCompress.GroupBy(s =>
            {
                long ticks = ((DateTimeOffset)s.RequestedAt).ToUnixTimeMilliseconds();
                return ticks / slotMs; // key = slot index
            });

            var idsToDelete = new List<long>();

            foreach (var slot in grouped)
            {
                var sessions = slot.ToList();
                if (sessions.Count <= 1) continue; // nothing to compress

                // Elect the representative for the slot:
                //   1. High-quality first (wasted surplus or grid import)
                //   2. Otherwise, the most recent session in the slot
                long keepId = sessions
                    .OrderByDescending(s => s.IsHighQuality)
                    .ThenByDescending(s => s.RequestedAt)
                    .First().Id;

                idsToDelete.AddRange(sessions
                    .Where(s => s.Id != keepId)
                    .Select(s => s.Id));
            }

            // Delete in batches to avoid overly large transactions
            const int DeleteBatch = 200;
            for (int i = 0; i < idsToDelete.Count; i += DeleteBatch)
            {
                var batch = idsToDelete.Skip(i).Take(DeleteBatch).ToList();
                // EF Core ExecuteDeleteAsync: direct DELETE without loading into memory
                int deleted = await _db.DistributionSessions
                    .Where(s => batch.Contains(s.Id))
                    .ExecuteDeleteAsync(ct);
                totalDeleted += deleted;
            }
        }

        // ── Phase 2: Hard delete ─────────────────────────────────────────────
        var hardDeleteCutoff = now.AddDays(-hardDeleteAgeDays);

        const int HardDeleteBatch = 500;
        int hardDeleted;
        do
        {
            // Loop to drain progressively without locking the table
            // Add OrderBy to make the selection deterministic
            // and avoid EF Core warning on Take without OrderBy.
            hardDeleted = await _db.DistributionSessions
                .Where(s => s.RequestedAt < hardDeleteCutoff)
                .OrderBy(s => s.RequestedAt)
                .ThenBy(s => s.Id)
                .Take(HardDeleteBatch)
                .ExecuteDeleteAsync(ct);
            totalDeleted += hardDeleted;
        }
        while (hardDeleted == HardDeleteBatch && !ct.IsCancellationRequested);

        return totalDeleted;
    }

    /// <summary>
    /// Sessions whose Pending feedback is ready to be collected.
    /// feedbackDelayHours is passed from config (e.g. 4.0).
    /// </summary>
    public async Task<List<DistributionSession>> GetSessionsPendingFeedbackAsync(
        double feedbackDelayHours, CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow.AddHours(-feedbackDelayHours);

        return await _db.DistributionSessions
            .Include(s => s.BatterySnapshots)
            .Include(s => s.Feedback)
            .Where(s => s.Feedback == null                          // never processed
                     || s.Feedback.Status == FeedbackStatus.Pending) // awaiting feedback
            .Where(s => s.RequestedAt <= cutoff)                    // delay elapsed
            .OrderBy(s => s.RequestedAt)
            .Take(100)   // max batch to avoid overloading HA
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public async Task<DistributionSession?> GetLastSessionAsync(CancellationToken ct = default)
    {
        return await _db.DistributionSessions
            .Include(s => s.BatterySnapshots)
            .Include(s => s.Weather)
            .OrderByDescending(s => s.RequestedAt)
            .FirstOrDefaultAsync(ct);
    }

    public async Task SaveFeedbackAsync(SessionFeedback feedback, CancellationToken ct = default)
    {
        // Upsert: if feedback already exists (Pending), update it
        var existing = await _db.SessionFeedbacks
            .FirstOrDefaultAsync(f => f.SessionId == feedback.SessionId, ct);

        if (existing is null)
            _db.SessionFeedbacks.Add(feedback);
        else
        {
            existing.CollectedAt = feedback.CollectedAt;
            existing.ObservedSocJson = feedback.ObservedSocJson;
            existing.AvgSocAtFeedback = feedback.AvgSocAtFeedback;
            existing.MinSocAtFeedback = feedback.MinSocAtFeedback;
            existing.EnergyEfficiencyScore = feedback.EnergyEfficiencyScore;
            existing.AvailabilityScore = feedback.AvailabilityScore;
            existing.ObservedOptimalSoftMax = feedback.ObservedOptimalSoftMax;
            existing.ObservedOptimalPreventive = feedback.ObservedOptimalPreventive;
            existing.CompositeScore = feedback.CompositeScore;
            existing.Status = feedback.Status;
            existing.InvalidReason = feedback.InvalidReason;
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateMLScoreAsync(long sessionId, double efficiencyScore, CancellationToken ct = default)
    {
        await _db.MLPredictionLogs
            .Where(m => m.SessionId == sessionId)
            .ExecuteUpdateAsync(s =>
                s.SetProperty(m => m.EfficiencyScore, efficiencyScore), ct);
    }

    public async Task<int> CountSessionsAsync(CancellationToken ct = default)
        => await _db.DistributionSessions.CountAsync(ct);

    public async Task<int> CountValidFeedbacksAsync(CancellationToken ct = default)
        => await _db.SessionFeedbacks
            .CountAsync(f => f.Status == FeedbackStatus.Valid, ct);

    /// <summary>
    /// Rolling average of home consumption over the last N persisted cycles
    /// that have a non-null MeasuredConsumptionW value.
    /// Returns null if no consumption data is available yet.
    /// </summary>
    public async Task<double?> GetRecentConsumptionAvgWAsync(int lastNCycles, CancellationToken ct = default)
    {
        if (lastNCycles <= 0) return null;

        var sessions = await _db.DistributionSessions
            .Where(s => s.MeasuredConsumptionW.HasValue)
            .OrderByDescending(s => s.RequestedAt)
            .Take(lastNCycles)
            .ToListAsync(ct);

        var values = sessions.Select(s => s.MeasuredConsumptionW!.Value).ToList();

        if (values.Count == 0) return null;

        return values.Average();
    }

    // ── Feature 6 — Daily energy balance ─────────────────────────────────────

    /// <summary>
    /// Aggregates all sessions for a UTC calendar day and creates or updates
    /// the corresponding daily_summaries record.
    ///
    /// Cycle duration: estimated from the gap between consecutive sessions,
    /// capped at 10 min to absorb gaps (restarts, maintenance).
    /// </summary>
    public async Task UpsertDailySummaryAsync(DateTime date, CancellationToken ct = default)
    {
        // UTC range of the day
        var dayStart = date.Date.ToUniversalTime();
        var dayEnd = dayStart.AddDays(1);

        // Load all sessions of the day with their tariff context
        var sessions = await _db.DistributionSessions
            .Where(s => s.RequestedAt >= dayStart && s.RequestedAt < dayEnd)
            .OrderBy(s => s.RequestedAt)
            .AsNoTracking()
            .ToListAsync(ct);

        if (sessions.Count == 0) return;

        // ── Weighted duration calculation for each session (in hours) ─────────
        // Principle: session duration = gap to the next session,
        // capped at MaxCycleGapHours to absorb gaps (restarts, outages).
        const double MaxCycleGapHours = 10.0 / 60.0;  // 10 min max
        const double DefaultCycleHours = 1.0 / 60.0;  // 1 min default if sole session

        double solarAllocatedWh = 0;
        double gridChargedWh = 0;
        double unusedSurplusWh = 0;
        double savingsNumerator = 0;
        double savingsDenominator = 0;

        for (int i = 0; i < sessions.Count; i++)
        {
            double durationH;
            if (i < sessions.Count - 1)
            {
                double gapH = (sessions[i + 1].RequestedAt - sessions[i].RequestedAt).TotalHours;
                durationH = Math.Min(gapH, MaxCycleGapHours);
            }
            else
            {
                durationH = sessions.Count > 1
                    ? Math.Min(
                        (sessions[i].RequestedAt - sessions[i - 1].RequestedAt).TotalHours,
                        MaxCycleGapHours)
                    : DefaultCycleHours;
            }

            solarAllocatedWh += sessions[i].TotalAllocatedW * durationH;
            gridChargedWh += sessions[i].GridChargedW * durationH;
            unusedSurplusWh += sessions[i].UnusedSurplusW * durationH;

            // Savings: TariffMaxSavingsPerKwh × grid energy for the session
            if (sessions[i].GridChargedW > 0)
            {
                var tms = sessions[i].TariffMaxSavingsPerKwh;
                if (tms.HasValue)
                {
                    double sessionGridWh = sessions[i].GridChargedW * durationH;
                    savingsNumerator += tms.Value * sessionGridWh;
                    savingsDenominator += sessionGridWh;
                }
            }
        }

        // ── Total grid energy consumed ────────────────────────────────────────
        // GridConsumedWh = everything drawn from the grid, including home consumption
        // during periods without surplus. Approximated using gridChargedWh
        // (grid battery charge) as the guaranteed minimum.
        // Sessions do not have a directly usable MeasuredConsumptionW here
        // without over-counting → store gridChargedWh in both fields
        // to stay conservative. The distinction will be refined if a P1
        // total sensor is added in Feature 9.
        double gridConsumedWh = gridChargedWh;

        // ── Self-consumed solar ───────────────────────────────────────────────
        // Uses DailySolarConsumedWh from the last session of the day that has it,
        // as it is the most up-to-date value (computed incrementally in SolarWorker).
        double? solarConsumedWh = sessions
            .LastOrDefault(s => s.DailySolarConsumedWh.HasValue)
            ?.DailySolarConsumedWh;

        // ── Self-sufficiency rate ─────────────────────────────────────────────
        double? selfSufficiencyPct = null;
        if (solarConsumedWh.HasValue && solarConsumedWh.Value >= 0)
        {
            double total = solarConsumedWh.Value + gridConsumedWh;
            selfSufficiencyPct = total > 0
                ? Math.Round(solarConsumedWh.Value / total * 100.0, 2)
                : 100.0; // 100% solar day (no grid import)
        }

        // ── Estimated savings ────────────────────────────────────────────────
        double? estimatedSavingsEur = savingsDenominator > 0
            ? Math.Round(savingsNumerator / 1000.0, 4)  // Wh → kWh, already weighted by savings/kWh
            : null;

        // ── Upsert ────────────────────────────────────────────────────────────
        var existing = await _db.DailySummaries
            .FirstOrDefaultAsync(d => d.Date == dayStart, ct);

        if (existing is null)
        {
            _db.DailySummaries.Add(new DailySummary
            {
                Date = dayStart,
                SolarConsumedWh = solarConsumedWh,
                GridConsumedWh = gridConsumedWh,
                GridChargedWh = gridChargedWh,
                SolarAllocatedWh = solarAllocatedWh,
                UnusedSurplusWh = unusedSurplusWh,
                EstimatedSavingsEur = estimatedSavingsEur,
                SelfSufficiencyPct = selfSufficiencyPct,
                SessionCount = sessions.Count,
                ComputedAt = DateTime.UtcNow,
            });
        }
        else
        {
            existing.SolarConsumedWh = solarConsumedWh;
            existing.GridConsumedWh = gridConsumedWh;
            existing.GridChargedWh = gridChargedWh;
            existing.SolarAllocatedWh = solarAllocatedWh;
            existing.UnusedSurplusWh = unusedSurplusWh;
            existing.EstimatedSavingsEur = estimatedSavingsEur;
            existing.SelfSufficiencyPct = selfSufficiencyPct;
            existing.SessionCount = sessions.Count;
            existing.ComputedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task<List<DailySummary>> GetDailySummariesAsync(
        DateTime from, DateTime to, CancellationToken ct = default)
    {
        var fromUtc = from.Date.ToUniversalTime();
        var toUtc = to.Date.ToUniversalTime().AddDays(1); // include the end date

        return await _db.DailySummaries
            .Where(d => d.Date >= fromUtc && d.Date < toUtc)
            .OrderBy(d => d.Date)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public async Task<double?> GetYesterdaySelfSufficiencyAsync(CancellationToken ct = default)
    {
        var yesterday = DateTime.UtcNow.Date.AddDays(-1).ToUniversalTime();

        return await _db.DailySummaries
            .Where(d => d.Date == yesterday)
            .Select(d => d.SelfSufficiencyPct)
            .FirstOrDefaultAsync(ct);
    }
}