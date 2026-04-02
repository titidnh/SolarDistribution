using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SolarDistribution.Worker.Configuration;

namespace SolarDistribution.Worker.Services;

/// <summary>
/// Manages dual-threshold hysteresis for per-battery IdleChargeW start/stop.
///
/// Problem without hysteresis: if surplus oscillates around IdleChargeW (e.g., 90W / 110W),
/// each cycle toggles idle mode ON/OFF → ZeroWActions / NonZeroWActions are repeatedly triggered
/// in a loop → stress on EcoFlow BMS during self-powered transitions.
///
/// Dual-threshold solution:
///   · Start      : effectiveSurplus >= IdleChargeW
///   · Stop       : effectiveSurplus &lt;  IdleChargeW - IdleStopBufferW
///   · Dead-band  : previous state is kept (no transition)
///
/// Example (IdleChargeW=100W, IdleStopBufferW=30W):
///   surplus=110W → ON   (110 >= 100)
///   surplus= 90W → ON   (maintained - dead band [70, 100[)
///   surplus= 65W → OFF  (65 &lt; 70)
///   surplus= 80W → OFF  (maintained - dead band [70, 100[)
///   surplus=105W → ON
/// </summary>
public class IdleChargeHysteresis
{
    private readonly ILogger _logger;

    // Key = BatteryConfig.Id, value = currently active idle state
    private readonly ConcurrentDictionary<int, bool> _state = new();

    public IdleChargeHysteresis(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Returns effective IdleCharge power for this battery on this cycle.
    /// Updates internal state (ON/OFF transitions).
    /// </summary>
    public double Compute(BatteryConfig bc, double effectiveSurplus)
    {
        if (bc.IdleChargeW <= 0)
            return 0;

        double startThreshold = bc.IdleChargeW;
        double stopThreshold = bc.IdleStopBufferW > 0
            ? bc.IdleChargeW - bc.IdleStopBufferW
            : bc.IdleChargeW; // IdleStopBufferW=0 → single threshold (dead-band disabled)

        bool wasIdle = _state.GetValueOrDefault(bc.Id, false);
        bool nowIdle;

        if (!wasIdle)
        {
            nowIdle = effectiveSurplus >= startThreshold;
            if (nowIdle)
                _logger.LogInformation(
                    "⚡ IdleCharge STARTED — Battery {Id} ({Name}): surplus {S:F0}W >= start {T:F0}W",
                    bc.Id, bc.Name, effectiveSurplus, startThreshold);
        }
        else
        {
            if (effectiveSurplus < stopThreshold)
            {
                nowIdle = false;
                _logger.LogInformation(
                    "🔌 IdleCharge STOPPED — Battery {Id} ({Name}): surplus {S:F0}W < stop {T:F0}W",
                    bc.Id, bc.Name, effectiveSurplus, stopThreshold);
            }
            else
            {
                nowIdle = true; // dead-band → maintained
                if (effectiveSurplus < startThreshold)
                    _logger.LogDebug(
                        "Battery {Id} ({Name}): IdleCharge maintained in dead-band " +
                        "[{Stop:F0}W – {Start:F0}W], surplus={S:F0}W",
                        bc.Id, bc.Name, stopThreshold, startThreshold, effectiveSurplus);
            }
        }

        _state[bc.Id] = nowIdle;
        return nowIdle ? bc.IdleChargeW : 0;
    }

    /// <summary>Exposes current state (for tests and logs).</summary>
    public bool IsIdle(int batteryId) => _state.TryGetValue(batteryId, out var v) && v;
}