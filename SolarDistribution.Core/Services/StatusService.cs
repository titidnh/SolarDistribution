using System;
using System.Threading;

namespace SolarDistribution.Core.Services
{
    public class StatusService : IStatusService
    {
        private readonly ReaderWriterLockSlim _lock = new();
        private string _lastDecision = string.Empty;
        private double _effectiveSurplusW = 0.0;
        private bool _gridChargeAllowed = false;
        private DateTime? _nextGridChargeStartUtc = null;

        public void Update(string lastDecision, double effectiveSurplusW, bool gridChargeAllowed, DateTime? nextGridChargeStartUtc)
        {
            _lock.EnterWriteLock();
            try
            {
                _lastDecision = lastDecision ?? string.Empty;
                _effectiveSurplusW = effectiveSurplusW;
                _gridChargeAllowed = gridChargeAllowed;
                _nextGridChargeStartUtc = nextGridChargeStartUtc;
            }
            finally { _lock.ExitWriteLock(); }
        }

        public string LastDecision { get { _lock.EnterReadLock(); try { return _lastDecision; } finally { _lock.ExitReadLock(); } } }
        public double EffectiveSurplusW { get { _lock.EnterReadLock(); try { return _effectiveSurplusW; } finally { _lock.ExitReadLock(); } } }
        public bool GridChargeAllowed { get { _lock.EnterReadLock(); try { return _gridChargeAllowed; } finally { _lock.ExitReadLock(); } } }
        public DateTime? NextGridChargeStartUtc { get { _lock.EnterReadLock(); try { return _nextGridChargeStartUtc; } finally { _lock.ExitReadLock(); } } }
    }
}
