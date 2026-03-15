using Microsoft.ML.Data;

namespace SolarDistribution.Core.Services.ML;

public class SoftMaxPrediction
{
    [ColumnName("Score")] public float PredictedSoftMaxPercent { get; set; }
}
