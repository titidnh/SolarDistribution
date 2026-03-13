using System;

namespace SolarDistribution.Api.Models
{
    public class LiveStatusDto
    {
        public string LastDecision { get; set; } = string.Empty;
        public double EffectiveSurplusW { get; set; }
        public bool GridChargeAllowed { get; set; }
        public DateTime? NextGridChargeStartUtc { get; set; }
    }
}
