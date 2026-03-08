using SolarDistribution.Core.Data.Entities;
using SolarDistribution.Core.Models;
using SolarDistribution.Core.Services;
using SolarDistribution.Core.Services.ML;

namespace SolarDistribution.Infrastructure.Mapping;

/// <summary>
/// Fix #6 : Implémentation concrète de IDistributionSessionFactory.
/// Délègue au mapper statique — l'infrastructure est seule responsable
/// de la sérialisation JSON et de la construction des entités EF.
/// </summary>
public class DistributionSessionFactory : IDistributionSessionFactory
{
    public DistributionSession Build(
        DistributionResult  result,
        WeatherData?        weather,
        MLRecommendation?   mlRecommendation,
        string              decisionEngine,
        IList<Battery>      originalBatteries,
        TariffContext       tariff)
        => DistributionSessionMapper.ToEntity(result, weather, mlRecommendation,
                                              decisionEngine, originalBatteries, tariff);
}
