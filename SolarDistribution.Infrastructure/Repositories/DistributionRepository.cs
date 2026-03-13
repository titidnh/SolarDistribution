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

    /// <summary>
    /// Charge uniquement les sessions avec un feedback VALIDE pour l'entraînement ML.
    /// Les labels utilisés (ObservedOptimalSoftMax, ObservedOptimalPreventive)
    /// sont issus de l'observation réelle, pas d'une heuristique codée.
    /// </summary>
    public async Task<List<DistributionSession>> GetSessionsForTrainingAsync(
        int maxRecords = 5000, CancellationToken ct = default)
    {
        return await _db.DistributionSessions
            .Include(s => s.BatterySnapshots)
            .Include(s => s.Weather)
            .Include(s => s.MlPrediction)
            .Include(s => s.Feedback)
            .Where(s => s.Feedback != null && s.Feedback.Status == FeedbackStatus.Valid)
            .OrderByDescending(s => s.RequestedAt)
            .Take(maxRecords)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    /// <summary>
    /// Sessions dont le feedback Pending est prêt à être collecté.
    /// On passe feedbackDelayHours depuis la config (ex: 4.0).
    /// </summary>
    public async Task<List<DistributionSession>> GetSessionsPendingFeedbackAsync(
        double feedbackDelayHours, CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow.AddHours(-feedbackDelayHours);

        return await _db.DistributionSessions
            .Include(s => s.BatterySnapshots)
            .Include(s => s.Feedback)
            .Where(s => s.Feedback == null                          // jamais traité
                     || s.Feedback.Status == FeedbackStatus.Pending) // en attente
            .Where(s => s.RequestedAt <= cutoff)                    // délai écoulé
            .OrderBy(s => s.RequestedAt)
            .Take(100)   // batch max pour ne pas surcharger HA
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
        // Upsert : si le feedback existe déjà (Pending), on le met à jour
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
    /// Moyenne roulante de consommation maison sur les N derniers cycles persistés
    /// qui ont une valeur MeasuredConsumptionW non nulle.
    /// Retourne null si aucune donnée de consommation n'est encore disponible.
    /// </summary>
    public async Task<double?> GetRecentConsumptionAvgWAsync(int lastNCycles, CancellationToken ct = default)
    {
        if (lastNCycles <= 0) return null;

        var values = await _db.DistributionSessions
            .Where(s => s.MeasuredConsumptionW != null)
            .OrderByDescending(s => s.RequestedAt)
            .Take(lastNCycles)
            .Select(s => s.MeasuredConsumptionW!.Value)
            .ToListAsync(ct);

        if (values.Count == 0) return null;

        return values.Average();
    }

    // ── Feature 6 — Bilan énergétique journalier ──────────────────────────────

    /// <summary>
    /// Agrège toutes les sessions d'une journée calendaire UTC et crée ou met à jour
    /// l'enregistrement daily_summaries correspondant.
    ///
    /// Durée de cycle : estimée à partir de l'écart entre sessions consécutives,
    /// plafonnée à 10 min pour éviter les gaps (redémarrages, maintenance).
    /// </summary>
    public async Task UpsertDailySummaryAsync(DateTime date, CancellationToken ct = default)
    {
        // Plage UTC de la journée
        var dayStart = date.Date.ToUniversalTime();
        var dayEnd = dayStart.AddDays(1);

        // Charge toutes les sessions du jour avec leur contexte tarifaire
        var sessions = await _db.DistributionSessions
            .Where(s => s.RequestedAt >= dayStart && s.RequestedAt < dayEnd)
            .OrderBy(s => s.RequestedAt)
            .AsNoTracking()
            .ToListAsync(ct);

        if (sessions.Count == 0) return;

        // ── Calcul de la durée pondérée de chaque session (en heures) ─────────
        // Principe : la durée d'une session = écart jusqu'à la session suivante,
        // plafonné à MaxCycleGapHours pour absorber les trous (redémarrages, pannes).
        const double MaxCycleGapHours = 10.0 / 60.0;  // 10 min max
        const double DefaultCycleHours = 1.0 / 60.0;  // 1 min par défaut si seule session

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

            // Économies : TariffMaxSavingsPerKwh × énergie réseau de la session
            if (sessions[i].TariffMaxSavingsPerKwh.HasValue && sessions[i].GridChargedW > 0)
            {
                double sessionGridWh = sessions[i].GridChargedW * durationH;
                savingsNumerator += sessions[i].TariffMaxSavingsPerKwh.Value * sessionGridWh;
                savingsDenominator += sessionGridWh;
            }
        }

        // ── Énergie réseau totale consommée ───────────────────────────────────
        // GridConsumedWh = tout ce qui a été pris au réseau, y compris la maison
        // pendant les périodes sans surplus. On approxime en utilisant gridChargedWh
        // (charge batterie réseau) comme valeur minimale garantie.
        // Les sessions n'ont pas de MeasuredConsumptionW directement utilisable
        // ici sans over-counting → on stocke gridChargedWh dans les deux champs
        // pour rester conservateur. La distinction sera affinée si un capteur P1
        // total est ajouté en Feature 9.
        double gridConsumedWh = gridChargedWh;

        // ── Solaire autoconsommé ──────────────────────────────────────────────
        // Utilise DailySolarConsumedWh de la dernière session du jour qui l'a,
        // car c'est la valeur la plus à jour (calculée incrémentalement dans SolarWorker).
        double? solarConsumedWh = sessions
            .LastOrDefault(s => s.DailySolarConsumedWh.HasValue)
            ?.DailySolarConsumedWh;

        // ── Taux d'autosuffisance ─────────────────────────────────────────────
        double? selfSufficiencyPct = null;
        if (solarConsumedWh.HasValue && solarConsumedWh.Value >= 0)
        {
            double total = solarConsumedWh.Value + gridConsumedWh;
            selfSufficiencyPct = total > 0
                ? Math.Round(solarConsumedWh.Value / total * 100.0, 2)
                : 100.0; // journée 100% solaire (aucun import réseau)
        }

        // ── Économies estimées ────────────────────────────────────────────────
        double? estimatedSavingsEur = savingsDenominator > 0
            ? Math.Round(savingsNumerator / 1000.0, 4)  // Wh → kWh, déjà pondéré par savings/kWh
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
        var toUtc = to.Date.ToUniversalTime().AddDays(1); // inclure la date de fin

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