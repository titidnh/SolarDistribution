using SolarDistribution.Core.Data.Entities;
using SolarDistribution.Core.Models;
using SolarDistribution.Core.Services;
using SolarDistribution.Core.Services.ML;

namespace SolarDistribution.Infrastructure.Mapping;

/// <summary>
/// Fix #6: Concrete implementation of IDistributionSessionFactory.
/// Delegates to static mapper — infrastructure is solely responsible
/// for JSON serialization and EF entity construction.
/// </summary>
public class DistributionSessionFactory : IDistributionSessionFactory
{
    public DistributionSession Build(
        DistributionResult  result,
        WeatherData?        weather,
        MLRecommendation?   mlRecommendation,
        string              decisionEngine,
        IList<Battery>      originalBatteries,
        TariffContext       tariff,
        double?             measuredConsumptionW = null,
        double?             forecastTodayWhAtStartOfDay = null)
        => DistributionSessionMapper.ToEntity(result, weather, mlRecommendation,
                                              decisionEngine, originalBatteries, tariff,
                                              measuredConsumptionW, forecastTodayWhAtStartOfDay);
}
