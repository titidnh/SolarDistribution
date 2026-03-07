using SolarDistribution.Core.Models;

namespace SolarDistribution.Core.Services;

/// <summary>
/// Contract for the solar surplus distribution algorithm.
/// Extracted as an interface to support dependency injection and unit test mocking.
/// </summary>
public interface IBatteryDistributionService
{
    /// <summary>
    /// Distributes <paramref name="surplusW"/> watts of solar surplus across the provided batteries.
    /// </summary>
    /// <param name="surplusW">Available solar surplus in watts (W).</param>
    /// <param name="batteries">Batteries eligible to receive charge.</param>
    /// <returns>Allocation result per battery plus summary totals.</returns>
    DistributionResult Distribute(double surplusW, IEnumerable<Battery> batteries);
}
