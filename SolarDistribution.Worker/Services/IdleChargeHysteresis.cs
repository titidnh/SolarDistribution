using Microsoft.Extensions.Logging;
using SolarDistribution.Worker.Configuration;

namespace SolarDistribution.Worker.Services;

/// <summary>
/// Gère l'hystérésis double-seuil pour l'activation/arrêt de IdleChargeW par batterie.
///
/// Problème sans hystérésis : si le surplus oscille autour de IdleChargeW (ex: 90W / 110W),
/// chaque cycle toggle le mode idle ON/OFF → ZeroWActions / NonZeroWActions déclenchées
/// en boucle → stress BMS EcoFlow sur les transitions self-powered.
///
/// Solution double-seuil :
///   · Activation  : effectiveSurplus >= IdleChargeW
///   · Arrêt       : effectiveSurplus &lt;  IdleChargeW - IdleStopBufferW
///   · Zone morte  : état précédent maintenu (pas de transition)
///
/// Exemple (IdleChargeW=100W, IdleStopBufferW=30W) :
///   surplus=110W → ON   (110 >= 100)
///   surplus= 90W → ON   (maintenu — zone morte [70, 100[)
///   surplus= 65W → OFF  (65 &lt; 70)
///   surplus= 80W → OFF  (maintenu — zone morte [70, 100[)
///   surplus=105W → ON
/// </summary>
public class IdleChargeHysteresis
{
    private readonly ILogger _logger;

    // Clé = BatteryConfig.Id, valeur = idle actuellement actif
    private readonly Dictionary<int, bool> _state = new();

    public IdleChargeHysteresis(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Retourne la puissance IdleCharge effective pour cette batterie à ce cycle.
    /// Met à jour l'état interne (transitions ON/OFF).
    /// </summary>
    public double Compute(BatteryConfig bc, double effectiveSurplus)
    {
        if (bc.IdleChargeW <= 0)
            return 0;

        double startThreshold = bc.IdleChargeW;
        double stopThreshold = bc.IdleStopBufferW > 0
            ? bc.IdleChargeW - bc.IdleStopBufferW
            : bc.IdleChargeW; // IdleStopBufferW=0 → seuil unique (zone morte désactivée)

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
                nowIdle = true; // zone morte → maintenu
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

    /// <summary>Expose l'état courant (pour tests et logs).</summary>
    public bool IsIdle(int batteryId) => _state.GetValueOrDefault(batteryId, false);
}