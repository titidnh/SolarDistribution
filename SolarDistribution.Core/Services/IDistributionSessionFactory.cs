using SolarDistribution.Core.Data.Entities;
using SolarDistribution.Core.Models;
using SolarDistribution.Core.Services.ML;

namespace SolarDistribution.Core.Services;

/// <summary>
/// Fix #6 : Interface de fabrique pour la construction des entités de persistance.
/// SmartDistributionService dépend de cette abstraction (Core),
/// l'implémentation réelle (qui connaît EF, JSON, etc.) vit dans Infrastructure.
/// </summary>
public interface IDistributionSessionFactory
{
    DistributionSession Build(
        DistributionResult  result,
        WeatherData?        weather,
        MLRecommendation?   mlRecommendation,
        string              decisionEngine,
        IList<Battery>      originalBatteries,
        TariffContext       tariff);
}
