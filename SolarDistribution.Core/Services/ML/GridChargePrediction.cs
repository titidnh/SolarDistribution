using Microsoft.ML.Data;

namespace SolarDistribution.Core.Services.ML;

/// <summary>
/// ML-7c: output of the binary ShouldChargeFromGrid classification model.
/// Probability that the session should have triggered grid charging.
/// </summary>
public class GridChargePrediction
{
    [ColumnName("PredictedLabel")] public bool PredictedShouldCharge { get; set; }
    [ColumnName("Probability")] public float Probability { get; set; }
    [ColumnName("Score")] public float Score { get; set; }
}
