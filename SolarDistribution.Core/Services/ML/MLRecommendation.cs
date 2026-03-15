namespace SolarDistribution.Core.Services.ML;

public record MLRecommendation(
    double RecommendedSoftMaxPercent,
    double RecommendedPreventiveThreshold,
    double ConfidenceScore,
    string ModelVersion,
    string Rationale,
    bool? ShouldChargeFromGridPrediction = null,
    double? GridChargeClassificationConfidence = null
);
