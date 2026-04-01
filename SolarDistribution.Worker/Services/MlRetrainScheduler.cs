using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SolarDistribution.Core.Repositories;
using SolarDistribution.Core.Services;
using SolarDistribution.Core.Services.ML;
using SolarDistribution.Worker.Configuration;

namespace SolarDistribution.Worker.Services;

/// <summary>
/// Secondary autonomous BackgroundService managing two scheduled tasks:
///
///   1. FEEDBACK COLLECTION (frequent, e.g. hourly)
///      Re-reads the batteries' real SOC from HA for past sessions
///      and computes true ML labels (ObservedOptimalSoftMax, ObservedOptimalPreventive).
///      → Feeds the database with real training data
///
///   2. ML RETRAIN (infrequent, e.g. Sunday 03:00)
///      Trains the two FastTree models on sessions with valid feedback.
///      → Enables ML once MIN_SESSIONS sessions have valid feedback
///
/// Configuration in config.yaml :
///   ml:
///     feedback_delay_hours: 4        # wait 4h before reading the real SOC
///     feedback_check_interval_hours: 1  # check pending feedbacks every hour
///     retrain_cron: "0 3 * * 0"      # Sunday 03:00 (cron syntax)
///     min_feedback_for_retrain: 50   # minimum valid feedbacks to trigger retrain
/// </summary>
public class MlRetrainScheduler : BackgroundService
{
    private readonly FeedbackEvaluator _feedbackEvaluator;
    private readonly IDistributionMLService _mlService;
    private readonly IDistributionRepository _repo;
    private readonly MlConfig _mlConfig;
    private readonly HeatingConfig _heatingConfig;
    private readonly IHeatingPreheatMlService _heatingMlService;
    private readonly DailySummaryService _dailySummaryService;
    private readonly ILogger<MlRetrainScheduler> _logger;

    // Last retrain performed — avoids duplicates if scheduler restarts
    private DateTime _lastRetrainAt = DateTime.MinValue;
    private DateTime _lastHeatingRetrainAt = DateTime.MinValue;
    // Last purge — once per week is enough
    private DateTime _lastPurgeAt = DateTime.MinValue;

    public MlRetrainScheduler(
        FeedbackEvaluator feedbackEvaluator,
        IDistributionMLService mlService,
        IHeatingPreheatMlService heatingMlService,
        IDistributionRepository repo,
        MlConfig mlConfig,
        HeatingConfig heatingConfig,
        DailySummaryService dailySummaryService,
        ILogger<MlRetrainScheduler> logger)
    {
        _feedbackEvaluator = feedbackEvaluator;
        _mlService = mlService;
        _heatingMlService = heatingMlService;
        _repo = repo;
        _mlConfig = mlConfig;
        _heatingConfig = heatingConfig;
        _dailySummaryService = dailySummaryService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "MlRetrainScheduler started | feedback check every {FeedbackInterval}h | retrain cron: {Cron}",
            _mlConfig.FeedbackCheckIntervalHours,
            _mlConfig.RetrainCron);

        // Small initial delay to let SolarWorker stabilize first
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;

            // ── 0. Daily energy balance (Feature 6) ──────────────────────────
            // Computed first so YesterdaySelfSufficiencyPct is available
            // in BuildFeatures() from the first cycle of the new day.
            try
            {
                await _dailySummaryService.CheckAndComputeYesterdayAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Daily summary computation failed");
            }

            // ── 1. Feedback collection ───────────────────────────────────────
            try
            {
                int collected = await _feedbackEvaluator.CollectPendingFeedbacksAsync(stoppingToken);

                if (collected > 0)
                {
                    _logger.LogInformation(
                        "Feedback: {Count} new valid feedbacks collected", collected);

                    // ML-6: GetStatusAsync replaces synchronous GetStatus() (deadlock risk)
                    var status = await _mlService.GetStatusAsync(stoppingToken);
                    _logger.LogInformation(
                        "ML training readiness: {Valid}/{Min} valid feedbacks " +
                        "(need {Remaining} more before retrain)",
                        status.SessionsInDb, _mlConfig.MinFeedbackForRetrain,
                        Math.Max(0, _mlConfig.MinFeedbackForRetrain - status.SessionsInDb));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Feedback collection failed");
            }

            // ── 2. Drift detection (ML-5) ────────────────────────────────────
            try
            {
                bool driftDetected = await _mlService.CheckForDriftAsync(
                    _mlConfig.DriftDetectionWindowSize,
                    _mlConfig.DriftDetectionR2Threshold,
                    stoppingToken);

                if (driftDetected)
                {
                    _logger.LogWarning("Concept drift detected — forcing immediate ML retrain");
                    await RunRetrainAsync(stoppingToken);
                    _lastRetrainAt = now;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Drift detection failed");
            }

            // ── 3. Scheduled retrain check ───────────────────────────────────
            try
            {
                if (await ShouldRetrainAsync(now, stoppingToken))
                {
                    await RunRetrainAsync(stoppingToken);
                    _lastRetrainAt = now;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ML retrain failed");
            }

            // ── 4. Heating ML retrain (6.2) ─────────────────────────────────
            try
            {
                if (_heatingConfig.Enabled
                    && (now - _lastHeatingRetrainAt).TotalHours >= Math.Max(1, _heatingConfig.MlRetrainIntervalHours))
                {
                    await RunHeatingRetrainAsync(stoppingToken);
                    _lastHeatingRetrainAt = now;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Heating ML retrain failed");
            }

            // ── 5. DB purge and compression (weekly) ─────────────────────────
            // Triggered after retrain to avoid impacting normal-cycle performance.
            // Purge runs at most once per week.
            try
            {
                if ((now - _lastPurgeAt).TotalDays >= 7)
                {
                    await RunPurgeAsync(stoppingToken);
                    _lastPurgeAt = now;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DB purge failed");
            }

            // ── Wait before next check ────────────────────────────────────────
            var interval = TimeSpan.FromHours(_mlConfig.FeedbackCheckIntervalHours);
            _logger.LogDebug("MlRetrainScheduler sleeping {Interval}h", interval.TotalHours);

            await Task.Delay(interval, stoppingToken);
        }
    }

    private async Task RunPurgeAsync(CancellationToken ct)
    {
        _logger.LogInformation(
            "DB purge starting | sessions: compression after {C}d (slot={S}min) | hard delete after {H}d",
            _mlConfig.PurgeCompressionAgeDays,
            _mlConfig.PurgeCompressionSlotMinutes,
            _mlConfig.PurgeHardDeleteAgeDays);

        int deletedSessions = await _repo.PurgeOldSessionsAsync(
            _mlConfig.PurgeCompressionAgeDays,
            _mlConfig.PurgeCompressionSlotMinutes,
            _mlConfig.PurgeHardDeleteAgeDays,
            ct);

        int deletedHeating = 0;
        if (_heatingConfig.Enabled)
        {
            deletedHeating = await _repo.PurgeOldHeatingSamplesAsync(
                _heatingConfig.PurgeCompressionAgeDays,
                _heatingConfig.PurgeCompressionSlotMinutes,
                _heatingConfig.PurgeHardDeleteAgeDays,
                ct);
        }

        if (deletedSessions + deletedHeating > 0)
            _logger.LogInformation("DB purge complete: {S} sessions + {H} heating samples removed", deletedSessions, deletedHeating);
        else
            _logger.LogDebug("DB purge: nothing to remove");
    }

    private async Task RunHeatingRetrainAsync(CancellationToken ct)
    {
        _logger.LogInformation("Heating ML retrain starting (interval {Hours}h)", _heatingConfig.MlRetrainIntervalHours);

        var result = await _heatingMlService.RetrainAsync(ct);
        if (result.Success)
        {
            _logger.LogInformation(
                "Heating ML retrained: version={Version} samples={Samples} R2={R2:F3} MAE={MAE:F2}m",
                result.ModelVersion,
                result.TrainingSamples,
                result.RSquared,
                result.MeanAbsoluteErrorMinutes);
        }
        else
        {
            _logger.LogWarning("Heating ML retrain skipped/failed: {Error}", result.ErrorMessage ?? "unknown");
        }
    }

    // ── Retrain decision logic ───────────────────────────────────────────────

    /// <summary>
    /// ML-6: Async to avoid GetAwaiter().GetResult() in scheduler.
    ///
    /// Cron-timing bug fix: scheduler wakes every N hours but
    /// not exactly at minute :00. Therefore cron is evaluated over a rolling
    /// window [now - feedbackInterval, now] to avoid missing target time.
    /// Example: cron "0 3 * * 0" (Sun 03:00), wake-up at 03:52 → window
    /// [02:52, 03:52] contains 03:00 → retrain triggered.
    /// </summary>
    private async Task<bool> ShouldRetrainAsync(DateTime now, CancellationToken ct)
    {
        // Check minimum valid feedbacks in database
        var status = await _mlService.GetStatusAsync(ct);
        if (status.SessionsInDb < _mlConfig.MinFeedbackForRetrain)
        {
            _logger.LogDebug(
                "Retrain skipped: only {Count}/{Min} valid feedbacks",
                status.SessionsInDb, _mlConfig.MinFeedbackForRetrain);
            return false;
        }

        // Check not already trained today (anti-duplicate protection)
        if (_lastRetrainAt.Date == now.Date)
        {
            _logger.LogDebug("Retrain skipped: already trained today at {Time}", _lastRetrainAt);
            return false;
        }

        // Evaluate cron over rolling window [now - interval, now] minute by minute.
        // Fixes the case where scheduler wakes after exact cron minute.
        var windowStart = now.AddHours(-_mlConfig.FeedbackCheckIntervalHours);
        var totalMinutes = (int)Math.Ceiling((now - windowStart).TotalMinutes);

        for (int m = 0; m <= totalMinutes; m++)
        {
            var candidate = windowStart.AddMinutes(m);
            if (CronMatches(_mlConfig.RetrainCron, candidate))
            {
                _logger.LogDebug(
                    "Cron matched at {Candidate} (window [{Start}, {End}])",
                    candidate, windowStart, now);
                return true;
            }
        }

        return false;
    }

    private async Task RunRetrainAsync(CancellationToken ct)
    {
        _logger.LogInformation("╔══════════════════════════════════════╗");
        _logger.LogInformation("║   ML Retrain starting (scheduled)    ║");
        _logger.LogInformation("╚══════════════════════════════════════╝");

        var result = await _mlService.RetrainAsync(ct);

        if (result.Success)
        {
            _logger.LogInformation(
                "✅ ML Retrain complete | version={Ver} | samples={N} | " +
                "SoftMax R²={R1:F3} | Preventive R²={R2:F3} | active={Active}",
                result.ModelVersion,
                result.TrainingSamples,
                result.SoftMaxRSquared,
                result.PreventiveRSquared,
                result.SoftMaxRSquared >= 0.65 && result.PreventiveRSquared >= 0.65);
        }
        else
        {
            _logger.LogWarning(
                "⚠️  ML Retrain failed: {Error}", result.ErrorMessage);
        }
    }

    // ── Minimal cron evaluator ───────────────────────────────────────────────
    // Supports standard 5-field syntax: "minute hour dayOfMonth month dayOfWeek"
    // Examples:
    //   "0 3 * * 0"   → Sunday at 3:00
    //   "0 3 * * *"   → every day at 3:00
    //   "0 2 * * 1"   → Monday at 2:00
    //   "30 4 1 * *"  → 1st of each month at 4:30

    private static bool CronMatches(string cron, DateTime now)
    {
        try
        {
            var parts = cron.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 5) return false;

            return MatchesCronField(parts[0], now.Minute)
                && MatchesCronField(parts[1], now.Hour)
                && MatchesCronField(parts[2], now.Day)
                && MatchesCronField(parts[3], now.Month)
                && MatchesCronField(parts[4], (int)now.DayOfWeek);
        }
        catch
        {
            return false;
        }
    }

    private static bool MatchesCronField(string field, int value)
    {
        if (field == "*") return true;

        // Lists: "1,3,5"
        if (field.Contains(','))
            return field.Split(',').Any(f => MatchesCronField(f.Trim(), value));

        // Ranges: "1-5"
        if (field.Contains('-'))
        {
            var range = field.Split('-');
            if (range.Length == 2
                && int.TryParse(range[0], out int min)
                && int.TryParse(range[1], out int max))
                return value >= min && value <= max;
        }

        // Steps: "*/5" or "0/15"
        if (field.Contains('/'))
        {
            var step = field.Split('/');
            if (step.Length == 2 && int.TryParse(step[1], out int interval))
            {
                int start = step[0] == "*" ? 0 : int.Parse(step[0]);
                return value >= start && (value - start) % interval == 0;
            }
        }

        // Exact value
        return int.TryParse(field, out int exact) && exact == value;
    }
}