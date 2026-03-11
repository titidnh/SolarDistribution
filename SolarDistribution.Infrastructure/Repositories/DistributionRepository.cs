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
            existing.CollectedAt              = feedback.CollectedAt;
            existing.ObservedSocJson          = feedback.ObservedSocJson;
            existing.AvgSocAtFeedback         = feedback.AvgSocAtFeedback;
            existing.MinSocAtFeedback         = feedback.MinSocAtFeedback;
            existing.EnergyEfficiencyScore    = feedback.EnergyEfficiencyScore;
            existing.AvailabilityScore        = feedback.AvailabilityScore;
            existing.ObservedOptimalSoftMax   = feedback.ObservedOptimalSoftMax;
            existing.ObservedOptimalPreventive = feedback.ObservedOptimalPreventive;
            existing.CompositeScore           = feedback.CompositeScore;
            existing.Status                   = feedback.Status;
            existing.InvalidReason            = feedback.InvalidReason;
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
}