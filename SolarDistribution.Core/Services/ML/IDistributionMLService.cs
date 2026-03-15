using System.Threading;
using System.Threading.Tasks;

namespace SolarDistribution.Core.Services.ML;

public interface IDistributionMLService
{
    Task<MLRecommendation?> PredictAsync(DistributionFeatures features, CancellationToken ct = default);
    Task<MLTrainingResult> RetrainAsync(CancellationToken ct = default);
    Task<MLModelStatus> GetStatusAsync(CancellationToken ct = default);
    Task<bool> CheckForDriftAsync(int windowSize, double threshold, CancellationToken ct = default);
}