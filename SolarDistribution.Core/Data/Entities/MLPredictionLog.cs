using System;

namespace SolarDistribution.Core.Data.Entities;

public class MLPredictionLog
{
    public long Id { get; set; }
    public long SessionId { get; set; }
    public string ModelVersion { get; set; } = string.Empty;
    public double ConfidenceScore { get; set; }
    public double EfficiencyScore { get; set; }
    public string PredictedSoftMaxJson { get; set; } = string.Empty;
    public double PredictedPreventiveThreshold { get; set; }
    public bool WasApplied { get; set; }
    public DateTime PredictedAt { get; set; }
    public DistributionSession Session { get; set; } = null!;
}
