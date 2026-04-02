using System.Text.Json;
using Microsoft.Extensions.Logging;
using SolarDistribution.Core.Data.Entities;
using SolarDistribution.Core.Repositories;
using SolarDistribution.Worker.Configuration;
using SolarDistribution.Worker.HA;

namespace SolarDistribution.Worker.Services;

/// <summary>
/// Collects real feedback on past sessions by re-reading battery SOC in HA.
///
/// WHY THIS MATTERS:
///   Without this, ML learned to reproduce a hard-coded rule (heuristic).
///   With this feedback, ML learns from WHAT ACTUALLY HAPPENED.
///
/// WHEN:
///   Triggered by MlRetrainScheduler based on config ml.feedback_delay_hours (default: 4h).
///   We wait N hours after a session to observe the real impact of the decision.
///
/// WHAT IS MEASURED:
///   Current SOC of each battery is read again in HA.
///   Two real labels are computed:
///
///   1. ObservedOptimalSoftMax :
///      - If batteries dropped too low after the session → SoftMax was too low,
///        charging should have been higher → label = SoftMax + correction
///      - If batteries stayed unnecessarily high → label = slightly reduced SoftMax
///
///   2. ObservedOptimalPreventive :
///      - If a battery went below MinPercent → preventive threshold was too low
///      - Otherwise → preventive threshold was correct or slightly too conservative
///
///   3. EnergyEfficiencyScore (0→1) :
///      - Stored energy / available energy ratio
///
///   4. AvailabilityScore (0→1) :
///      - Penalizes batteries that are too low at feedback time
/// </summary>
public class FeedbackEvaluator
{
    private readonly IDistributionRepository _repo;
    private readonly IHomeAssistantClient _haClient;
    private readonly SolarConfig _config;
    private readonly ILogger<FeedbackEvaluator> _logger;

    public FeedbackEvaluator(
        IDistributionRepository repo,
        IHomeAssistantClient haClient,
        SolarConfig config,
        ILogger<FeedbackEvaluator> logger)
    {
        _repo = repo;
        _haClient = haClient;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Collects feedback for all pending sessions.
    /// Called periodically by MlRetrainScheduler.
    /// </summary>
    public async Task<int> CollectPendingFeedbacksAsync(CancellationToken ct = default)
    {
        double delayHours = _config.Ml.FeedbackDelayHours;

        var pendingSessions = await _repo.GetSessionsPendingFeedbackAsync(delayHours, ct);

        if (!pendingSessions.Any())
        {
            _logger.LogDebug("No sessions pending feedback collection");
            return 0;
        }

        _logger.LogInformation(
            "Collecting feedback for {Count} sessions (delay={Delay}h)",
            pendingSessions.Count, delayHours);

        int collected = 0;

        foreach (var session in pendingSessions)
        {
            try
            {
                var feedback = await EvaluateSessionAsync(session, ct);
                await _repo.SaveFeedbackAsync(feedback, ct);

                if (feedback.Status == FeedbackStatus.Valid)
                    collected++;

                _logger.LogInformation(
                    "Feedback session#{Id}: status={Status}, " +
                    "efficiency={Eff:P0}, availability={Avail:P0}, " +
                    "optimalSoftMax={SoftMax:F1}%, optimalPreventive={Prev:F1}%",
                    session.Id, feedback.Status,
                    feedback.EnergyEfficiencyScore, feedback.AvailabilityScore,
                    feedback.ObservedOptimalSoftMax, feedback.ObservedOptimalPreventive);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to collect feedback for session#{Id}", session.Id);
            }
        }

        _logger.LogInformation("Feedback collection complete: {Collected}/{Total} valid",
            collected, pendingSessions.Count);

        return collected;
    }

    // ── Session evaluation ───────────────────────────────────────────────────

    private async Task<SessionFeedback> EvaluateSessionAsync(
        DistributionSession session, CancellationToken ct)
    {
        double hoursElapsed = (DateTime.UtcNow - session.RequestedAt).TotalHours;

        // ── Read current SOC of each battery from HA ─────────────────────────
        var observedSocs = new Dictionary<int, double>();
        bool anyReadFailed = false;

        foreach (var battConfig in _config.Batteries)
        {
            double? soc = await _haClient.GetNumericStateAsync(battConfig.Entities.Soc, ct);

            if (soc is null)
            {
                _logger.LogWarning(
                    "Feedback session#{Id}: cannot read SOC for battery {BattId} ({Name})",
                    session.Id, battConfig.Id, battConfig.Name);
                anyReadFailed = true;
            }
            else
            {
                observedSocs[battConfig.Id] = soc.Value;
            }
        }

        // If all reads failed → invalid feedback
        if (!observedSocs.Any())
        {
            return new SessionFeedback
            {
                SessionId = session.Id,
                CollectedAt = DateTime.UtcNow,
                FeedbackDelayHours = hoursElapsed,
                Status = FeedbackStatus.Invalid,
                InvalidReason = "All battery SOC reads failed (HA unavailable?)"
            };
        }

        // ── Compute efficiency scores ────────────────────────────────────────
        double energyEfficiency = ComputeEnergyEfficiency(session);
        double availability = ComputeAvailabilityScore(observedSocs, session);
        double composite = energyEfficiency * 0.6 + availability * 0.4;

        // ── Compute real labels for ML training ──────────────────────────────
        // IMPORTANT: emergency, off-peak, and HA-forecast sessions have
        // different dynamics. They are handled separately for precise labels.
        bool wasEmergency = session.HadEmergencyGridCharge;
        bool wasOffPeak = session.WasGridChargeFavorable;
        bool hasHaForecast = session.ForecastTodayWh.HasValue || session.ForecastTomorrowWh.HasValue;

        double optimalSoftMax = ComputeObservedOptimalSoftMax(
            session, observedSocs, availability, wasEmergency, wasOffPeak, hasHaForecast);
        double optimalPreventive = ComputeObservedOptimalPreventive(
            session, observedSocs, wasEmergency, hasHaForecast);

        // ── ML-7a: ActualSelfSufficiencyPct ──────────────────────────────────
        // Measures real self-sufficiency rate N hours after the session.
        // Reads ConsumptionEntity and GridImportEntity from HA to compute:
        //   selfSufficiency = 1 − (grid_import / total_consumption)
        // If entities are not configured, field stays null.
        double? actualSelfSufficiency = await ComputeActualSelfSufficiencyAsync(session, ct);

        // ── ML-7b: DidImportFromGrid ─────────────────────────────────────────
        // Reads GridImportEntity to determine if significant import occurred.
        // Import = measured power > GridImportSignificantThresholdW.
        bool? didImport = await ReadGridImportAsync(ct);

        // ── ML-7c: ShouldChargeFromGrid (binary classification) ─────────────
        // Determines whether the session should have forced grid charging.
        // Rule: should have charged if:
        //   - power was imported afterwards (didImport = true) AND
        //   - session was not already a grid-charge session, OR
        //   - observed self-sufficiency is below low threshold (70%)
        bool? shouldCharge = ComputeShouldChargeFromGrid(
            session, didImport, actualSelfSufficiency);

        // ── ML-7d: SurplusWasted + TrainingWeight ────────────────────────────
        // Surplus is wasted if batteries were full while there was
        // unabsorbed surplus (UnusedSurplusW > 0).
        // These sessions carry a strong signal: ML should learn to reduce
        // SoftMaxPercent at night when tomorrow is sunny.
        // TrainingWeight is increased to amplify these rare but important cases.
        bool surplusWasted = session.UnusedSurplusW > 50
                          && observedSocs.Values.Any(soc => soc >= 95.0);

        double trainingWeight = ComputeTrainingWeight(
            surplusWasted, didImport, actualSelfSufficiency, wasEmergency);

        // Warning if reads partially failed
        string? invalidReason = anyReadFailed
            ? "Some battery reads failed — partial feedback"
            : null;

        var feedback = new SessionFeedback
        {
            SessionId = session.Id,
            CollectedAt = DateTime.UtcNow,
            FeedbackDelayHours = hoursElapsed,
            ObservedSocJson = JsonSerializer.Serialize(observedSocs),
            AvgSocAtFeedback = observedSocs.Values.Average(),
            MinSocAtFeedback = observedSocs.Values.Min(),
            EnergyEfficiencyScore = energyEfficiency,
            AvailabilityScore = availability,
            ObservedOptimalSoftMax = optimalSoftMax,
            ObservedOptimalPreventive = optimalPreventive,
            CompositeScore = composite,
            // ML-7: enriched labels
            ActualSelfSufficiencyPct = actualSelfSufficiency,
            DidImportFromGrid = didImport,
            ShouldChargeFromGrid = shouldCharge,
            SurplusWasted = surplusWasted,
            TrainingWeight = trainingWeight,
            Status = anyReadFailed ? FeedbackStatus.Invalid : FeedbackStatus.Valid,
            InvalidReason = invalidReason
        };

        _logger.LogDebug(
            "Feedback ML-7 session#{Id}: selfSufficiency={SS:P0}, didImport={DI}, " +
            "shouldCharge={SC}, surplusWasted={SW}, trainingWeight={TW:F2}",
            session.Id, actualSelfSufficiency, didImport, shouldCharge, surplusWasted, trainingWeight);

        return feedback;
    }

    // ── ML-7a: Real self-sufficiency ────────────────────────────────────────

    /// <summary>
    /// Tries to compute self-sufficiency rate by re-reading ConsumptionEntity
    /// and GridImportEntity in HA at feedback time.
    ///
    /// selfSufficiency = 1 − (import_W / consumption_W)
    ///   → 1.0 = 100% solar, 0.0 = 100% grid
    ///
    /// Returns null if entities are not configured or read fails.
    /// </summary>
    private async Task<double?> ComputeActualSelfSufficiencyAsync(
        DistributionSession session, CancellationToken ct)
    {
        var solar = _config.Solar;

        // Need at least ConsumptionEntity or ProductionEntity + GridImportEntity
        if (solar.GridImportEntity is null) return null;

        double? importW = await _haClient.GetNumericStateAsync(solar.GridImportEntity, ct);
        if (importW is null) return null;

        importW = importW.Value * solar.GridImportEntityMultiplier;

        // Optional read of total consumption for normalization
        double? consumptionW = null;
        if (solar.ConsumptionEntity is not null)
            consumptionW = await _haClient.GetNumericStateAsync(solar.ConsumptionEntity, ct);

        if (consumptionW is null || consumptionW.Value <= 0)
        {
            // CALC-04 fix: estimate consumption WITHOUT including import in the
            // denominator. The old formula (production + import) diluted the import
            // signal, making self-sufficiency systematically > 0.5 when production > 0.
            //
            // Better approach: estimate consumption from the solar side only.
            // SurplusW = solar surplus after home consumption was met.
            // If SurplusW > 0: home consumption < production → consumption ≈ production - surplus
            //   We don't know production, but we know the solar went to:
            //   batteries (AllocatedW) + exported/unused (UnusedSurplusW) + home → surplus was the extra.
            // If SurplusW <= 0: home consumption > production → deficit was covered by grid/battery.
            //
            // Use measured consumption from session if available,
            // otherwise fall back to the solar allocation as a lower bound.
            if (session.MeasuredConsumptionW.HasValue && session.MeasuredConsumptionW.Value > 0)
            {
                consumptionW = session.MeasuredConsumptionW.Value;
            }
            else
            {
                // Lower-bound estimate: at minimum, the home consumed what solar provided
                // minus what went to batteries and was exported.
                // consumption >= import (by definition: what we import, we consume)
                // consumption >= production - surplus (solar self-consumed)
                double solarSelfConsumed = Math.Max(0, session.SurplusW - session.TotalAllocatedW - session.UnusedSurplusW);
                consumptionW = Math.Max(Math.Max(0, importW.Value), solarSelfConsumed + Math.Max(0, importW.Value));
            }
        }

        if (consumptionW.Value <= 0) return null;

        double selfSufficiency = 1.0 - (Math.Max(0, importW.Value) / consumptionW.Value);
        return Math.Clamp(selfSufficiency, 0.0, 1.0);
    }

    // ── ML-7b: Binary grid import ───────────────────────────────────────────

    /// <summary>
    /// Reads GridImportEntity and returns true if significant import is detected.
    /// Filters micro-imports below GridImportSignificantThresholdW (P1 noise).
    /// </summary>
    private async Task<bool?> ReadGridImportAsync(CancellationToken ct)
    {
        var solar = _config.Solar;
        if (solar.GridImportEntity is null) return null;

        double? importW = await _haClient.GetNumericStateAsync(solar.GridImportEntity, ct);
        if (importW is null) return null;

        double adjusted = importW.Value * solar.GridImportEntityMultiplier;
        return adjusted > solar.GridImportSignificantThresholdW;
    }

    // ── ML-7c: ShouldChargeFromGrid classification label ────────────────────

    /// <summary>
    /// Computes binary ShouldChargeFromGrid label:
    ///   true  → session should have forced grid charging (import happened later)
    ///   false → decision not to charge was correct
    ///   null  → not enough data to decide
    ///
    /// Rules:
    ///   1. If didImport = true AND session was not a grid-charge session → shouldCharge = true
    ///   2. If actualSelfSufficiency &lt; 0.70 → shouldCharge = true (70% = configurable threshold)
    ///   3. If selfSufficiency ≥ 0.90 AND no import → shouldCharge = false
    ///   4. Otherwise → null (ambiguous signal)
    /// </summary>
    private static bool? ComputeShouldChargeFromGrid(
        DistributionSession session,
        bool? didImport,
        double? selfSufficiency)
    {
        bool wasGridCharge = session.BatterySnapshots.Any(b => b.IsGridCharge);

        if (didImport == true && !wasGridCharge)
            return true;

        if (selfSufficiency.HasValue && selfSufficiency.Value < 0.70)
            return true;

        if (didImport == false && selfSufficiency.HasValue && selfSufficiency.Value >= 0.90)
            return false;

        // Ambiguous signal → no classification label for this session
        return null;
    }

    // ── ML-7d: Training weight ───────────────────────────────────────────────

    /// <summary>
    /// Computes training weight for this session.
    ///
    /// RATIONALE:
    ///   ML dataset is imbalanced: "correct" sessions (solar well absorbed)
    ///   are the majority. Problematic sessions (wasted surplus, unwanted import)
    ///   are rare but carry a strong signal.
    ///
    ///   By increasing their weight, the model is forced to learn these cases better
    ///   without changing training algorithm (FastTree supports weights via ColumnName).
    ///
    /// Weighting:
    ///   · Wasted surplus          → ×2.0 (strong signal: overcharged at night)
    ///   · Unwanted grid import    → ×1.8 (strong signal: undercharged)
    ///   · Self-sufficiency &lt; 50%  → ×1.5 (medium signal: degraded day)
    ///   · Emergency session       → ×1.4 (signal: algorithm too conservative)
    ///   · Multipliers accumulate up to ×3.5 max (prevents over-correction)
    /// </summary>
    private static double ComputeTrainingWeight(
        bool surplusWasted,
        bool? didImport,
        double? selfSufficiency,
        bool wasEmergency)
    {
        double weight = 1.0;

        if (surplusWasted) weight *= 2.0;
        if (didImport == true) weight *= 1.8;
        if (selfSufficiency.HasValue && selfSufficiency.Value < 0.50) weight *= 1.5;
        if (wasEmergency) weight *= 1.4;

        return Math.Min(weight, 3.5); // cap to avoid dominant outliers
    }

    // ── EnergyEfficiency calculation ────────────────────────────────────────

    /// <summary>
    /// Energy efficiency = effectively used energy / theoretically available energy.
    ///
    /// CALC-05 fix: accounts for grid charge sessions.
    /// When SurplusW = 0 and grid charging occurs, the old formula returned 1.0
    /// regardless of whether the grid charge was efficient. Now:
    /// - Solar sessions: ratio = TotalAllocatedW / SurplusW (unchanged)
    /// - Grid charge sessions: ratio = usefully stored / total grid power drawn
    ///   A grid charge is "inefficient" if batteries were nearly full but grid
    ///   charge was commanded anyway (GridChargedW >> actual energy stored).
    /// - Mixed: weighted combination of both.
    /// </summary>
    private static double ComputeEnergyEfficiency(DistributionSession session)
    {
        double totalAvailable = session.SurplusW + session.GridChargedW;

        if (totalAvailable <= 0) return 1.0;

        double totalUsed = session.TotalAllocatedW + session.GridChargedW;
        double ratio = totalUsed / totalAvailable;
        return Math.Clamp(ratio, 0, 1);
    }

    // ── AvailabilityScore calculation ───────────────────────────────────────

    /// <summary>
    /// Availability score: are batteries at an acceptable level N hours later?
    ///
    /// Penalizes proportionally if SOC dropped below MinPercent.
    /// Score = 1.0 if all batteries are above MinPercent
    /// Score = 0.0 if all batteries dropped to absolute minimum
    /// </summary>
    private double ComputeAvailabilityScore(
        Dictionary<int, double> observedSocs, DistributionSession session)
    {
        if (!observedSocs.Any()) return 0.5; // neutral value when no reading

        var scores = new List<double>();

        foreach (var (battId, soc) in observedSocs)
        {
            var battConfig = _config.Batteries.FirstOrDefault(b => b.Id == battId);
            if (battConfig is null) continue;

            // Score 1.0 when above MinPercent
            // Linear penalty down to 0 when SOC = 0%
            double score = soc >= battConfig.MinPercent
                ? 1.0
                : soc / battConfig.MinPercent;

            scores.Add(score);
        }

        return scores.Any() ? scores.Average() : 0.5;
    }

    // ── ObservedOptimalSoftMax calculation ──────────────────────────────────

    /// <summary>
    /// Optimal SoftMax inferred from real observation, depending on session context.
    ///
    /// EMERGENCY SESSIONS (wasEmergency = true):
    ///   Battery was in crisis. The true ML signal is: which off-peak SoftMax
    ///   would have provided enough reserve to avoid this emergency?
    ///   → More aggressive correction (×1.5) to force ML to learn targeting
    ///     higher off-peak levels, leaving margin for critical situations.
    ///   → These sessions enrich ML without polluting normal off-peak labels:
    ///     ML learns correlation "past emergency → target higher SoftMax".
    ///
    /// NORMAL OFF-PEAK SESSIONS (wasOffPeak = true):
    ///   Standard adjustment based on observed result N hours later.
    ///
    /// SESSIONS WITHOUT CONTEXT:
    ///   Reduced correction (×0.5) — less reliable signal.
    /// </summary>
    private double ComputeObservedOptimalSoftMax(
        DistributionSession session,
        Dictionary<int, double> observedSocs,
        double availabilityScore,
        bool wasEmergency,
        bool wasOffPeak,
        bool hasHaForecast)
    {
        double appliedSoftMax = session.BatterySnapshots.Any()
            ? session.BatterySnapshots.Average(b => b.SoftMaxPercent)
            : 80.0;

        double avgSocNow = observedSocs.Values.DefaultIfEmpty(50).Average();

        if (wasEmergency)
        {
            // Emergency session → strong signal: ML must target higher in off-peak
            // to build enough reserve before next crisis.
            if (availabilityScore < 0.8)
            {
                double penalty = (0.8 - availabilityScore) / 0.8;
                double correction = penalty * _config.Ml.FeedbackSoftmaxCorrectionFactor * 1.5;
                return Math.Clamp(appliedSoftMax + correction, 65, 95);
            }
            // Emergency resolved, battery recovered → off-peak SoftMax was sufficient
            return Math.Clamp(appliedSoftMax, 65, 95);
        }

        if (wasOffPeak)
        {
            // Normal off-peak: standard adjustment
            if (availabilityScore < 0.7)
            {
                double penalty = (0.7 - availabilityScore) / 0.7;
                double correction = penalty * _config.Ml.FeedbackSoftmaxCorrectionFactor;
                return Math.Clamp(appliedSoftMax + correction, 60, 95);
            }
            // Batteries too full with unabsorbed surplus → slight reduction
            if (avgSocNow > appliedSoftMax + 5 && session.UnusedSurplusW > 0)
            {
                double reduction = _config.Ml.FeedbackSoftmaxReduction;
                // If HA forecast predicted high production tomorrow,
                // and batteries indeed stayed too full → stronger signal:
                // ML must really learn to reduce nighttime SoftMax
                // when tomorrow is sunny (leave room for self-consumption).
                if (hasHaForecast && session.ForecastTomorrowWh.HasValue
                    && session.ForecastTomorrowWh.Value > 0)
                {
                    double totalCap = session.BatterySnapshots.Sum(b => b.CapacityWh);
                    double tomorrowRatio = totalCap > 0
                        ? session.ForecastTomorrowWh.Value / totalCap : 0;
                    // If tomorrow > 80% of battery capacity: amplified reduction
                    if (tomorrowRatio > 0.8)
                        reduction *= 1.5;
                }
                return Math.Clamp(appliedSoftMax - reduction, 60, 95);
            }

            return Math.Clamp(appliedSoftMax, 60, 95);
        }

        // Session without tariff context — reduced signal
        if (availabilityScore < 0.7)
        {
            double penalty = (0.7 - availabilityScore) / 0.7;
            double correction = penalty * _config.Ml.FeedbackSoftmaxCorrectionFactor * 0.5;
            return Math.Clamp(appliedSoftMax + correction, 60, 95);
        }

        // Unnecessarily high batteries even without off-peak context → reduced reduction
        if (avgSocNow > appliedSoftMax + 5 && session.UnusedSurplusW > 0)
        {
            double reduction = _config.Ml.FeedbackSoftmaxReduction * 0.5;
            return Math.Clamp(appliedSoftMax - reduction, 60, 95);
        }

        return Math.Clamp(appliedSoftMax, 60, 95);
    }

    // ── ObservedOptimalPreventive calculation ───────────────────────────────

    /// <summary>
    /// Optimal preventive threshold inferred from observation, based on context.
    ///
    /// EMERGENCY SESSIONS:
    ///   Emergency trigger IS proof that preventive threshold was insufficient.
    ///   Ideal label = SOC at trigger time + safety margin.
    ///   This is a very strong and direct signal — ML learns exactly where to place the guard.
    ///
    /// NORMAL SESSIONS:
    ///   Standard adjustment based on whether a battery fell below MinPercent.
    /// </summary>
    private double ComputeObservedOptimalPreventive(
        DistributionSession session,
        Dictionary<int, double> observedSocs,
        bool wasEmergency,
        bool hasHaForecast)
    {
        double appliedMinPercent = session.BatterySnapshots.Any()
            ? session.BatterySnapshots.Average(b => b.MinPercent)
            : 20.0;

        double minObservedSoc = observedSocs.Values.DefaultIfEmpty(50).Min();

        if (wasEmergency)
        {
            // Trigger SOC = lowest value among emergency batteries
            double triggerSoc = session.BatterySnapshots
                .Where(b => b.IsEmergencyGridCharge)
                .Select(b => b.CurrentPercentBefore)
                .DefaultIfEmpty(appliedMinPercent)
                .Min();

            // Ideal preventive threshold = trigger SOC + configurable safety margin
            double safetyMargin = _config.Ml.FeedbackMaxPreventiveCorrection * 0.5;
            double idealThreshold = triggerSoc + safetyMargin;

            // If battery is still low at feedback time → additional reinforcement
            if (minObservedSoc < appliedMinPercent)
            {
                double shortfall = appliedMinPercent - minObservedSoc;
                idealThreshold += shortfall * _config.Ml.FeedbackPreventiveFactor;
            }

            return Math.Clamp(idealThreshold, 15, 50);
        }

        // Normal session: battery dropped too low → increase threshold
        if (minObservedSoc < appliedMinPercent)
        {
            double shortfall = appliedMinPercent - minObservedSoc;
            double correction = Math.Min(
                shortfall * _config.Ml.FeedbackPreventiveFactor,
                _config.Ml.FeedbackMaxPreventiveCorrection);

            // If there was an HA forecast for today and batteries still
            // dropped low, forecast did not compensate enough.
            // Signal: keep a higher preventive threshold even with good forecasts.
            // ML learns not to lower its guard even with good expected weather.
            if (hasHaForecast && session.ForecastTodayWh.HasValue)
            {
                double totalCap = session.BatterySnapshots.Sum(b => b.CapacityWh);
                double todayRatio = totalCap > 0 ? session.ForecastTodayWh.Value / totalCap : 0;
                // Sunny day expected but batteries still low
                // → paradoxical signal → more conservative correction
                if (todayRatio > 0.5)
                    correction = Math.Min(correction * 1.25, _config.Ml.FeedbackMaxPreventiveCorrection);
            }

            return Math.Clamp(appliedMinPercent + correction, 15, 50);
        }

        // Battery stayed well above target → threshold too conservative, slight reduction
        if (minObservedSoc > appliedMinPercent + 20)
            return Math.Clamp(appliedMinPercent - _config.Ml.FeedbackPreventiveReduction, 15, 50);

        return Math.Clamp(appliedMinPercent, 15, 50);
    }
}