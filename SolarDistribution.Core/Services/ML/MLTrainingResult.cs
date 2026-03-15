namespace SolarDistribution.Core.Services.ML;

public record MLTrainingResult(
    bool Success,
    int TrainingSamples,
    double SoftMaxRSquared,
    double PreventiveRSquared,
    string ModelVersion,
    string? ErrorMessage = null
);
