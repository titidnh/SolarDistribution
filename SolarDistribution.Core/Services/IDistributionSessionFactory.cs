using SolarDistribution.Core.Data.Entities;
using SolarDistribution.Core.Models;
using SolarDistribution.Core.Services.ML;

namespace SolarDistribution.Core.Services;

/// <summary>
/// Fix #6: Factory interface for building persistence entities.
/// SmartDistributionService depends on this abstraction (Core),
/// the real implementation (which knows EF, JSON, etc.) lives in Infrastructure.
/// </summary>
public interface IDistributionSessionFactory
{
    DistributionSession Build(
        DistributionResult  result,
        WeatherData?        weather,
        MLRecommendation?   mlRecommendation,
        string              decisionEngine,
        IList<Battery>      originalBatteries,
        TariffContext       tariff,
        double?             measuredConsumptionW = null,
        double?             forecastTodayWhAtStartOfDay = null);
}
