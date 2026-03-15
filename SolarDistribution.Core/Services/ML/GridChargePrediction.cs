using Microsoft.ML.Data;

namespace SolarDistribution.Core.Services.ML;

/// <summary>
/// ML-7c : sortie du modèle de classification binaire ShouldChargeFromGrid.
/// Probabilité que la session aurait dû déclencher une charge réseau.
/// </summary>
public class GridChargePrediction
{
    [ColumnName("PredictedLabel")] public bool PredictedShouldCharge { get; set; }
    [ColumnName("Probability")] public float Probability { get; set; }
    [ColumnName("Score")] public float Score { get; set; }
}
