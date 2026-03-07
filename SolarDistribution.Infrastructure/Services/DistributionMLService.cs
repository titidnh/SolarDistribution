using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.Trainers.FastTree;
using SolarDistribution.Core.Data.Entities;
using SolarDistribution.Core.Repositories;
using SolarDistribution.Core.Services.ML;

namespace SolarDistribution.Infrastructure.Services;

/// <summary>
/// Service ML.NET qui apprend de l'historique de distributions pour recommander :
///   1. Le SoftMaxPercent optimal par session
///   2. Le seuil de recharge préventive optimal selon météo + heure
///
/// Architecture :
///   - Deux modèles de régression indépendants (FastTree) entraînés en parallèle
///   - Confiance calculée via les métriques R² sur le jeu de validation
///   - Modèles persistés sur disque (.zip) — rechargés au démarrage
///   - Minimum MIN_SESSIONS_REQUIRED sessions pour activer le ML
///
/// Progression :
///   < MIN_SESSIONS   → ML inactif, fallback algo déterministe
///   >= MIN_SESSIONS  → ML actif, confiance croissante avec les données
/// </summary>
public class DistributionMLService : IDistributionMLService
{
    private const int    MIN_SESSIONS_REQUIRED  = 50;
    private const double MIN_CONFIDENCE_TO_APPLY = 0.65;
    private const string SOFTMAX_MODEL_FILE     = "ml_softmax_model.zip";
    private const string PREVENTIVE_MODEL_FILE  = "ml_preventive_model.zip";

    private readonly MLContext              _mlContext;
    private readonly IDistributionRepository _repo;
    private readonly ILogger<DistributionMLService> _logger;
    private readonly string _modelDirectory;

    private ITransformer? _softMaxModel;
    private ITransformer? _preventiveModel;
    private MLModelMeta?  _meta;

    private record MLModelMeta(
        string Version,
        int Samples,
        double SoftMaxR2,
        double PreventiveR2,
        DateTime TrainedAt);

    public DistributionMLService(
        IDistributionRepository repo,
        ILogger<DistributionMLService> logger,
        string modelDirectory = "ml_models")
    {
        _mlContext       = new MLContext(seed: 42);
        _repo            = repo;
        _logger          = logger;
        _modelDirectory  = modelDirectory;

        Directory.CreateDirectory(_modelDirectory);
        TryLoadModelsFromDisk();
    }

    // ── Prédiction ────────────────────────────────────────────────────────────

    public Task<MLRecommendation?> PredictAsync(
        DistributionFeatures features, CancellationToken ct = default)
    {
        if (_softMaxModel is null || _preventiveModel is null || _meta is null)
        {
            _logger.LogDebug("ML models not available — falling back to deterministic algorithm");
            return Task.FromResult<MLRecommendation?>(null);
        }

        if (_meta.SoftMaxR2 < MIN_CONFIDENCE_TO_APPLY ||
            _meta.PreventiveR2 < MIN_CONFIDENCE_TO_APPLY)
        {
            _logger.LogDebug(
                "ML confidence too low (R²={R2:.2f}) — falling back",
                Math.Min(_meta.SoftMaxR2, _meta.PreventiveR2));
            return Task.FromResult<MLRecommendation?>(null);
        }

        try
        {
            // Prédiction SoftMax
            var softMaxEngine = _mlContext.Model.CreatePredictionEngine<DistributionFeatures, SoftMaxPrediction>(
                _softMaxModel);
            var softMaxPred = softMaxEngine.Predict(features);

            // Prédiction seuil préventif
            var preventiveEngine = _mlContext.Model.CreatePredictionEngine<DistributionFeatures, PreventivePrediction>(
                _preventiveModel);
            var preventivePred = preventiveEngine.Predict(features);

            // Clamp des valeurs dans des plages raisonnables
            double softMax    = Math.Clamp(softMaxPred.PredictedSoftMaxPercent, 50, 100);
            double preventive = Math.Clamp(preventivePred.PredictedPreventiveThreshold, 20, 60);

            // Score de confiance = moyenne des R² des deux modèles
            double confidence = (Math.Max(0, _meta.SoftMaxR2) + Math.Max(0, _meta.PreventiveR2)) / 2.0;

            string rationale = BuildRationale(features, softMax, preventive);

            _logger.LogInformation(
                "ML prediction: softMax={SoftMax:F1}%, preventive={Prev:F1}%, confidence={Conf:F2}",
                softMax, preventive, confidence);

            return Task.FromResult<MLRecommendation?>(new MLRecommendation(
                RecommendedSoftMaxPercent:      softMax,
                RecommendedPreventiveThreshold: preventive,
                ConfidenceScore:               confidence,
                ModelVersion:                  _meta.Version,
                Rationale:                     rationale
            ));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ML prediction failed — falling back to deterministic");
            return Task.FromResult<MLRecommendation?>(null);
        }
    }

    // ── Entraînement ──────────────────────────────────────────────────────────

    public async Task<MLTrainingResult> RetrainAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("ML retraining started...");

        var sessions = await _repo.GetSessionsForTrainingAsync(5000, ct);

        if (sessions.Count < MIN_SESSIONS_REQUIRED)
        {
            _logger.LogWarning(
                "Not enough training data: {Count}/{Min} sessions",
                sessions.Count, MIN_SESSIONS_REQUIRED);

            return new MLTrainingResult(
                Success:            false,
                TrainingSamples:    sessions.Count,
                SoftMaxRSquared:    0,
                PreventiveRSquared: 0,
                ModelVersion:       "N/A",
                ErrorMessage:       $"Minimum {MIN_SESSIONS_REQUIRED} sessions required, got {sessions.Count}"
            );
        }

        try
        {
            var features = sessions
                .Where(s => s.Weather is not null)
                .Select(BuildFeatures)
                .Where(f => f is not null)
                .Cast<DistributionFeatures>()
                .ToList();

            _logger.LogInformation("Building ML features from {Count} sessions", features.Count);

            var dataView = _mlContext.Data.LoadFromEnumerable(features);
            var split    = _mlContext.Data.TrainTestSplit(dataView, testFraction: 0.2);

            // ── Modèle 1 : SoftMax optimal ────────────────────────────────────
            var (softMaxModel, softMaxR2) = TrainRegressionModel(
                split.TrainSet, split.TestSet,
                labelColumn: nameof(DistributionFeatures.OptimalSoftMaxPercent));

            // ── Modèle 2 : Seuil préventif optimal ───────────────────────────
            var (preventiveModel, preventiveR2) = TrainRegressionModel(
                split.TrainSet, split.TestSet,
                labelColumn: nameof(DistributionFeatures.OptimalPreventiveThreshold));

            // Persister sur disque
            string version = $"v{DateTime.UtcNow:yyyyMMdd-HHmmss}";
            SaveModel(softMaxModel,    SOFTMAX_MODEL_FILE,    dataView.Schema);
            SaveModel(preventiveModel, PREVENTIVE_MODEL_FILE, dataView.Schema);

            // Mise en mémoire
            _softMaxModel    = softMaxModel;
            _preventiveModel = preventiveModel;
            _meta = new MLModelMeta(version, features.Count, softMaxR2, preventiveR2, DateTime.UtcNow);

            _logger.LogInformation(
                "ML training complete. Version={Ver}, Samples={N}, SoftMaxR²={R1:F3}, PreventiveR²={R2:F3}",
                version, features.Count, softMaxR2, preventiveR2);

            return new MLTrainingResult(
                Success:            true,
                TrainingSamples:    features.Count,
                SoftMaxRSquared:    softMaxR2,
                PreventiveRSquared: preventiveR2,
                ModelVersion:       version
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ML training failed");
            return new MLTrainingResult(
                Success:            false,
                TrainingSamples:    0,
                SoftMaxRSquared:    0,
                PreventiveRSquared: 0,
                ModelVersion:       "ERROR",
                ErrorMessage:       ex.Message
            );
        }
    }

    public MLModelStatus GetStatus()
    {
        int sessionsInDb       = _repo.CountSessionsAsync().GetAwaiter().GetResult();
        int validFeedbacksInDb = _repo.CountValidFeedbacksAsync().GetAwaiter().GetResult();

        return new MLModelStatus(
            IsAvailable:          _meta is not null,
            ModelVersion:         _meta?.Version,
            TrainingSamples:      _meta?.Samples ?? 0,
            SoftMaxRSquared:      _meta?.SoftMaxR2,
            PreventiveRSquared:   _meta?.PreventiveR2,
            TrainedAt:            _meta?.TrainedAt,
            SessionsInDb:         sessionsInDb,
            ValidFeedbacksInDb:   validFeedbacksInDb,
            MinSessionsRequired:  MIN_SESSIONS_REQUIRED
        );
    }

    // ── Helpers privés ────────────────────────────────────────────────────────

    private (ITransformer Model, double R2) TrainRegressionModel(
        IDataView trainSet, IDataView testSet, string labelColumn)
    {
        // Features numériques (toutes sauf les labels)
        var featureColumns = new[]
        {
            nameof(DistributionFeatures.HourOfDay),
            nameof(DistributionFeatures.DayOfWeek),
            nameof(DistributionFeatures.MonthOfYear),
            nameof(DistributionFeatures.HoursUntilSunset),
            nameof(DistributionFeatures.CloudCoverPercent),
            nameof(DistributionFeatures.DirectRadiationWm2),
            nameof(DistributionFeatures.DiffuseRadiationWm2),
            nameof(DistributionFeatures.PrecipitationMmH),
            nameof(DistributionFeatures.AvgForecastRadiation6h),
            nameof(DistributionFeatures.AvgBatteryPercent),
            nameof(DistributionFeatures.MinBatteryPercent),
            nameof(DistributionFeatures.MaxBatteryPercent),
            nameof(DistributionFeatures.TotalCapacityWh),
            nameof(DistributionFeatures.UrgentBatteryCount),
            nameof(DistributionFeatures.SurplusW),
        };

        var pipeline = _mlContext.Transforms
            .CopyColumns("Label", labelColumn)
            .Append(_mlContext.Transforms.Concatenate("Features", featureColumns))
            .Append(_mlContext.Transforms.NormalizeMinMax("Features"))
            .Append(_mlContext.Regression.Trainers.FastTree(new FastTreeRegressionTrainer.Options
            {
                NumberOfTrees        = 100,
                NumberOfLeaves       = 20,
                MinimumExampleCountPerLeaf = 5,
                LearningRate         = 0.1f,
                LabelColumnName      = "Label",
                FeatureColumnName    = "Features"
            }));

        var model   = pipeline.Fit(trainSet);
        var metrics = _mlContext.Regression.Evaluate(model.Transform(testSet));

        return (model, metrics.RSquared);
    }

    private void SaveModel(ITransformer model, string filename, DataViewSchema schema)
    {
        var path = Path.Combine(_modelDirectory, filename);
        _mlContext.Model.Save(model, schema, path);
        _logger.LogDebug("ML model saved to {Path}", path);
    }

    private void TryLoadModelsFromDisk()
    {
        var softMaxPath    = Path.Combine(_modelDirectory, SOFTMAX_MODEL_FILE);
        var preventivePath = Path.Combine(_modelDirectory, PREVENTIVE_MODEL_FILE);

        if (!File.Exists(softMaxPath) || !File.Exists(preventivePath))
        {
            _logger.LogInformation("No ML models found on disk — will train after enough data");
            return;
        }

        try
        {
            _softMaxModel    = _mlContext.Model.Load(softMaxPath,    out _);
            _preventiveModel = _mlContext.Model.Load(preventivePath, out _);

            // Méta minimale reconstituée depuis les fichiers
            var modifiedAt = File.GetLastWriteTimeUtc(softMaxPath);
            _meta = new MLModelMeta(
                Version:    $"v{modifiedAt:yyyyMMdd-HHmmss}",
                Samples:    0,
                SoftMaxR2:  0.7,   // valeur conservative — recalculée au prochain retrain
                PreventiveR2: 0.7,
                TrainedAt:  modifiedAt);

            _logger.LogInformation("ML models loaded from disk (version {Ver})", _meta.Version);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load ML models from disk");
        }
    }

    /// <summary>
    /// Transforme une session persistée en features ML.
    ///
    /// LABELS RÉELS : les labels (OptimalSoftMaxPercent, OptimalPreventiveThreshold)
    /// viennent maintenant du SessionFeedback — c'est-à-dire de l'observation
    /// réelle du comportement des batteries N heures après la décision.
    ///
    /// Seules les sessions avec un feedback valide sont traitées ici.
    /// Les anciennes heuristiques ComputeOptimalSoftMax/Preventive ont été supprimées.
    /// </summary>
    private static DistributionFeatures? BuildFeatures(DistributionSession session)
    {
        // Exige un feedback valide — sans ça, pas de label réel → on ignore la session
        if (session.Feedback?.Status != FeedbackStatus.Valid)
            return null;

        if (session.Weather is null || !session.BatterySnapshots.Any())
            return null;

        var w        = session.Weather;
        var bss      = session.BatterySnapshots.ToList();
        var feedback = session.Feedback;

        double[] radForecast = ParseDoubleArray(w.RadiationForecast12hJson);
        double avg6hRad      = radForecast.Take(6).DefaultIfEmpty(0).Average();

        return new DistributionFeatures
        {
            // ── Features contextuelles ────────────────────────────────────────
            HourOfDay               = session.RequestedAt.Hour,
            DayOfWeek               = (float)session.RequestedAt.DayOfWeek,
            MonthOfYear             = session.RequestedAt.Month,
            HoursUntilSunset        = (float)w.HoursUntilSunset,

            // ── Features météo ────────────────────────────────────────────────
            CloudCoverPercent       = (float)w.CloudCoverPercent,
            DirectRadiationWm2      = (float)w.DirectRadiationWm2,
            DiffuseRadiationWm2     = (float)w.DiffuseRadiationWm2,
            PrecipitationMmH        = (float)w.PrecipitationMmH,
            AvgForecastRadiation6h  = (float)avg6hRad,

            // ── Features état batteries ───────────────────────────────────────
            AvgBatteryPercent       = (float)bss.Average(b => b.CurrentPercentBefore),
            MinBatteryPercent       = (float)bss.Min(b => b.CurrentPercentBefore),
            MaxBatteryPercent       = (float)bss.Max(b => b.CurrentPercentBefore),
            TotalCapacityWh         = (float)bss.Sum(b => b.CapacityWh),
            UrgentBatteryCount      = bss.Count(b => b.WasUrgent),
            SurplusW                = (float)session.SurplusW,

            // ── LABELS RÉELS (depuis SessionFeedback) ─────────────────────────
            // Plus d'heuristique codée en dur : ce sont les valeurs observées
            // qui ont réellement produit les meilleurs résultats.
            OptimalSoftMaxPercent      = (float)feedback.ObservedOptimalSoftMax,
            OptimalPreventiveThreshold = (float)feedback.ObservedOptimalPreventive,
        };
    }

    private static double[] ParseDoubleArray(string json)
    {
        try { return JsonSerializer.Deserialize<double[]>(json) ?? []; }
        catch { return []; }
    }

    private static string BuildRationale(DistributionFeatures f, double softMax, double preventive)
    {
        var reasons = new List<string>();

        if (f.HoursUntilSunset < 3)
            reasons.Add($"coucher du soleil dans {f.HoursUntilSunset:F1}h");
        if (f.CloudCoverPercent > 60)
            reasons.Add($"couverture nuageuse {f.CloudCoverPercent:F0}%");
        if (f.AvgForecastRadiation6h < 100)
            reasons.Add($"faible rayonnement prévu ({f.AvgForecastRadiation6h:F0}W/m²)");
        if (f.UrgentBatteryCount > 0)
            reasons.Add($"{f.UrgentBatteryCount} batterie(s) urgente(s)");

        string context = reasons.Any()
            ? string.Join(", ", reasons)
            : "conditions solaires favorables";

        return $"SoftMax={softMax:F0}%, préventif={preventive:F0}% — {context}";
    }
}
