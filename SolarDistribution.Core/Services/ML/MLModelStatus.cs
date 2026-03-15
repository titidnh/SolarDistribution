using System;

namespace SolarDistribution.Core.Services.ML;

public record MLModelStatus(
    bool IsAvailable,
    string? ModelVersion,
    int TrainingSamples,
    double? SoftMaxRSquared,
    double? PreventiveRSquared,
    DateTime? TrainedAt,
    int SessionsInDb,
    int ValidFeedbacksInDb,
    int MinSessionsRequired
);
