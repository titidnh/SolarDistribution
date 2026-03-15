using System;

namespace SolarDistribution.Infrastructure.Data.Entities;

public class SessionFeedback
{
    public long     Id          { get; set; }
    public long     SessionId   { get; set; }
    public DateTime CollectedAt { get; set; } = DateTime.UtcNow;
    public double   FeedbackDelayHours    { get; set; }
    public string   ObservedSocJson       { get; set; } = "{}";
    public double   AvgSocAtFeedback      { get; set; }
    public double   MinSocAtFeedback      { get; set; }
    public double   EnergyEfficiencyScore { get; set; }
    public double   AvailabilityScore     { get; set; }
    public double   ObservedOptimalSoftMax    { get; set; }
    public double   ObservedOptimalPreventive { get; set; }
    public double   CompositeScore            { get; set; }
    public FeedbackStatus Status        { get; set; } = FeedbackStatus.Pending;
    public string?        InvalidReason { get; set; }
    public DistributionSession Session  { get; set; } = null!;
}
