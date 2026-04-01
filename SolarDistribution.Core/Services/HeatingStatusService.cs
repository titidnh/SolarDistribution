using System.Threading;

namespace SolarDistribution.Core.Services;

public class HeatingStatusService : IHeatingStatusService
{
    private readonly ReaderWriterLockSlim _lock = new();

    private HeatingStatusSnapshot _snapshot = new(
        PresenceMode: HeatingPresenceMode.Home,
        CurrentTempC: 0,
        TargetTempC: 0,
        NextStartAtLocal: null,
        EstimatedMinutesToTarget: 0,
        LastDecision: "initial",
        UpdatedAtUtc: DateTime.UtcNow);

    public void Update(HeatingStatusSnapshot snapshot)
    {
        _lock.EnterWriteLock();
        try
        {
            _snapshot = snapshot;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public HeatingStatusSnapshot GetSnapshot()
    {
        _lock.EnterReadLock();
        try
        {
            return _snapshot;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }
}
