using Microsoft.ML.Data;

namespace SolarDistribution.Core.Services.ML;

public class PreventivePrediction
{
    [ColumnName("Score")] public float PredictedPreventiveThreshold { get; set; }
}
