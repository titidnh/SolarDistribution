using SolarDistribution.Core.Data.Entities;

namespace SolarDistribution.Core.Repositories;

public interface IDistributionRepository
{
    Task SaveSessionAsync(DistributionSession session, CancellationToken ct = default);

    /// <summary>
    /// Sessions avec feedback valide — les seules utilisées pour l'entraînement ML.
    /// Labels = ObservedOptimalSoftMax + ObservedOptimalPreventive issus de l'observation réelle.
    /// </summary>
    Task<List<DistributionSession>> GetSessionsForTrainingAsync(int maxRecords = 5000, CancellationToken ct = default);

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
}
