using System;

namespace SolarDistribution.Core.Services
{
    public interface IStatusService
    {
        void Update(string lastDecision, double effectiveSurplusW, bool gridChargeAllowed, DateTime? nextGridChargeStartUtc);
        string LastDecision { get; }
        double EffectiveSurplusW { get; }
        bool GridChargeAllowed { get; }
        DateTime? NextGridChargeStartUtc { get; }
    }
}
