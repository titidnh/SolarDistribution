using System;
using System.Collections.Generic;
using System.Linq;

namespace SolarDistribution.Core.Services;

/// <summary>
/// Selects the cheapest available heating source at a given outdoor temperature and energy prices.
///
/// Cost model:
///   - Heat pump  : elecPrice / COP(T_outdoor)
///                  COP(T) = CopAtRefTemp + CopSlopePerDegC × (T − CopRefTempC), floored at CopMinValue
///   - Gas boiler : gasPrice / BoilerEfficiency
///   - Electric   : elecPrice  (no COP gain)
///
/// When two sources have identical cost, Priority (lower = preferred) is used as tiebreaker.
/// </summary>
public sealed class HeatingSourceSelectorService : IHeatingSourceSelectorService
{
    public double ComputeCop(HeatingSourceDefinition source, double outdoorTempC)
    {
        if (!string.Equals(source.Type, "heat_pump", StringComparison.OrdinalIgnoreCase))
            return 1.0;

        var cop = source.CopAtRefTemp + source.CopSlopePerDegC * (outdoorTempC - source.CopRefTempC);
        return Math.Max(source.CopMinValue, cop);
    }

    public double ComputeCostPerKwhThermal(
        HeatingSourceDefinition source,
        double outdoorTempC,
        double elecPricePerKwh,
        double gasPricePerKwh)
    {
        return source.Type.ToLowerInvariant() switch
        {
            "heat_pump" => elecPricePerKwh / Math.Max(0.01, ComputeCop(source, outdoorTempC)),
            "gas"       => gasPricePerKwh  / Math.Max(0.01, source.BoilerEfficiency),
            "electric"  => elecPricePerKwh,
            _           => double.MaxValue
        };
    }

    public HeatingSourceCostResult SelectOptimalSource(
        IReadOnlyList<HeatingSourceDefinition> sources,
        double outdoorTempC,
        double elecPricePerKwh,
        double gasPricePerKwh)
    {
        if (sources is null || sources.Count == 0)
            return new HeatingSourceCostResult(null, null, 0, 0,
                Array.Empty<HeatingSourceBreakdown>(), "No sources configured");

        var candidates = sources
            .Where(s => s.Enabled)
            .Select(s =>
            {
                var cop  = ComputeCop(s, outdoorTempC);
                var cost = ComputeCostPerKwhThermal(s, outdoorTempC, elecPricePerKwh, gasPricePerKwh);
                return (Source: s, Cop: cop, Cost: cost);
            })
            .OrderBy(x => x.Cost)
            .ThenBy(x => x.Source.Priority)
            .ToList();

        if (candidates.Count == 0)
            return new HeatingSourceCostResult(null, null, 0, 0,
                Array.Empty<HeatingSourceBreakdown>(), "All sources disabled");

        var best = candidates[0];
        var all  = candidates
            .Select(c => new HeatingSourceBreakdown(c.Source.Name, c.Source.Type, c.Cop, c.Cost))
            .ToList();

        var reason = BuildReason(best.Source.Name, best.Source.Type, best.Cost, best.Cop, outdoorTempC);
        return new HeatingSourceCostResult(
            best.Source.Name,
            best.Source.Type,
            best.Cop,
            best.Cost,
            all,
            reason);
    }

    private static string BuildReason(
        string name, string type, double costPerKwh, double cop, double outdoorTempC)
    {
        return type.ToLowerInvariant() switch
        {
            "heat_pump" => $"{name} (PAC) selected: COP={cop:F2} @ {outdoorTempC:F1}°C → {costPerKwh * 100:F2} ct/kWh_th",
            "gas"       => $"{name} (gaz) selected: {costPerKwh * 100:F2} ct/kWh_th",
            "electric"  => $"{name} (élec) selected: {costPerKwh * 100:F2} ct/kWh_th",
            _           => $"{name} selected: {costPerKwh * 100:F2} ct/kWh_th"
        };
    }
}
