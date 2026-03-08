using System.Text.Json;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.Trainers.FastTree;
using SolarDistribution.Core.Data.Entities;
using SolarDistribution.Core.Repositories;
using SolarDistribution.Core.Services.ML;

namespace SolarDistribution.Infrastructure.Services;

/// <summary>
/// Deux modèles FastTree regression entraînés sur les sessions historiques avec feedback valide :
///   Modèle 1 : prédit OptimalSoftMaxPercent    (quand s'arrêter de charger vers SoftMax)
///   Modèle 2 : prédit OptimalPreventiveThreshold (seuil bas pour forcer la recharge)
///
/// LABELS RÉELS : issus de SessionFeedback.ObservedOptimal* — jamais d'heuristiques codées en dur.
/// </summary>
public class DistributionMLService : IDistributionMLService
{
    private const int    MIN_FEEDBACKS_REQUIRED   = 50;
    private const double MIN_CONFIDENCE_TO_APPLY  = 0.65;
    private const string SOFTMAX_MODEL_FILE        = "ml_softmax_model.zip";
    private const string PREVENTIVE_MODEL_FILE     = "ml_preventive_model.zip";

    private readonly MLContext               _ctx;
    private readonly IDistributionRepository _repo;
    private readonly ILogger<DistributionMLService> _log;
    private readonly string _modelDir;

    private ITransformer? _softMaxModel;
    private ITransformer? _preventiveModel;
    private MLModelMeta?  _meta;

    // ML-1 : Pool maison thread-safe — évite de recréer PredictionEngine à chaque cycle.
    // ConcurrentBag : chaque thread prend un moteur, l'utilise, le repose.
    // Sans pool : CreatePredictionEngine() coûte ~5-20ms (allocation + JIT) à chaque appel.
    private ConcurrentBag<PredictionEngine<DistributionFeatures, SoftMaxPrediction>>?    _smEngines;
    private ConcurrentBag<PredictionEngine<DistributionFeatures, PreventivePrediction>>? _pvEngines;

    private record MLModelMeta(string Version, int Samples,
        double SoftMaxR2, double PreventiveR2, DateTime TrainedAt);

    public DistributionMLService(
        IDistributionRepository repo,
        ILogger<DistributionMLService> log,
        string modelDirectory = "ml_models")
    {
        _ctx     = new MLContext(seed: 42);
        _repo    = repo;
        _log     = log;
        _modelDir = modelDirectory;
        Directory.CreateDirectory(_modelDir);
        TryLoadFromDisk();
    }

    // ── Prédiction ────────────────────────────────────────────────────────────

    public Task<MLRecommendation?> PredictAsync(DistributionFeatures f, CancellationToken ct = default)
    {
        if (_softMaxModel is null || _preventiveModel is null || _meta is null)
        {
            _log.LogDebug("ML not available — falling back to deterministic");
            return Task.FromResult<MLRecommendation?>(null);
        }

        if (_meta.SoftMaxR2 < MIN_CONFIDENCE_TO_APPLY || _meta.PreventiveR2 < MIN_CONFIDENCE_TO_APPLY)
        {
            _log.LogDebug("ML confidence too low (R²={R:.2f}) — fallback",
                Math.Min(_meta.SoftMaxR2, _meta.PreventiveR2));
            return Task.FromResult<MLRecommendation?>(null);
        }

        try
        {
            // ML-1 : emprunte un moteur du pool (ou en crée un si le pool est vide)
            if (_smEngines is null || _pvEngines is null)
            {
                _log.LogDebug("ML prediction engines not ready — fallback");
                return Task.FromResult<MLRecommendation?>(null);
            }

            if (!_smEngines.TryTake(out var smEng))
                smEng = _ctx.Model.CreatePredictionEngine<DistributionFeatures, SoftMaxPrediction>(_softMaxModel!);
            if (!_pvEngines.TryTake(out var pvEng))
                pvEng = _ctx.Model.CreatePredictionEngine<DistributionFeatures, PreventivePrediction>(_preventiveModel!);

            double rawSoftMax, rawPreventive;
            try
            {
                rawSoftMax    = smEng.Predict(f).PredictedSoftMaxPercent;
                rawPreventive = pvEng.Predict(f).PredictedPreventiveThreshold;
            }
            finally
            {
                // Repose les moteurs dans le pool pour la prochaine prédiction
                _smEngines.Add(smEng);
                _pvEngines.Add(pvEng);
            }

            double softMax    = Math.Clamp(rawSoftMax,    50, 100);
            double preventive = Math.Clamp(rawPreventive, 15,  60);

            // ML-2 : contrainte de cohérence — garantit une marge minimale entre
            // PreventiveThreshold et SoftMax pour éviter des états impossibles.
            const double MinMarginPercent = 10.0;
            if (softMax - preventive < MinMarginPercent)
            {
                // On préfère ajuster le moins coûteux des deux
                if (softMax < 80)
                    softMax    = Math.Clamp(preventive + MinMarginPercent, 50, 100);
                else
                    preventive = Math.Clamp(softMax    - MinMarginPercent, 15,  60);

                _log.LogDebug(
                    "ML-2 coherence correction applied: softMax={SM:F1}%, preventive={PV:F1}%",
                    softMax, preventive);
            }

            double conf = (Math.Max(0, _meta.SoftMaxR2) + Math.Max(0, _meta.PreventiveR2)) / 2.0;

            _log.LogInformation(
                "ML prediction: softMax={SM:F1}%, prev={PV:F1}%, conf={C:F2}",
                softMax, preventive, conf);

            return Task.FromResult<MLRecommendation?>(new MLRecommendation(
                softMax, preventive, conf, _meta.Version,
                BuildRationale(f, softMax, preventive)));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "ML prediction failed — fallback");
            return Task.FromResult<MLRecommendation?>(null);
        }
    }

    // ── Entraînement ──────────────────────────────────────────────────────────

    public async Task<MLTrainingResult> RetrainAsync(CancellationToken ct = default)
    {
        _log.LogInformation("ML retraining started...");

        var sessions = await _repo.GetSessionsForTrainingAsync(10000, ct);
        var features = sessions
            .Where(s => s.Feedback?.Status == FeedbackStatus.Valid)
            .Select(BuildFeatures)
            .OfType<DistributionFeatures>()
            .ToList();

        if (features.Count < MIN_FEEDBACKS_REQUIRED)
        {
            _log.LogWarning("Not enough training data: {N}/{Min}", features.Count, MIN_FEEDBACKS_REQUIRED);
            return new MLTrainingResult(false, features.Count, 0, 0, "N/A",
                $"Minimum {MIN_FEEDBACKS_REQUIRED} feedbacks required, got {features.Count}");
        }

        try
        {
            var data  = _ctx.Data.LoadFromEnumerable(features);
            var split = _ctx.Data.TrainTestSplit(data, testFraction: 0.2);

            var (smModel, smR2) = Train(split.TrainSet, split.TestSet,
                nameof(DistributionFeatures.OptimalSoftMaxPercent));
            var (pvModel, pvR2) = Train(split.TrainSet, split.TestSet,
                nameof(DistributionFeatures.OptimalPreventiveThreshold));

            string ver = $"v{DateTime.UtcNow:yyyyMMdd-HHmmss}";
            Save(smModel, SOFTMAX_MODEL_FILE,    data.Schema);
            Save(pvModel, PREVENTIVE_MODEL_FILE, data.Schema);

            _softMaxModel    = smModel;
            _preventiveModel = pvModel;
            _meta = new MLModelMeta(ver, features.Count, smR2, pvR2, DateTime.UtcNow);

            // ML-1 : (re)construire les pools après chaque entraînement
            RebuildPredictionPools(data.Schema);

            _log.LogInformation(
                "ML trained: v={V}, N={N}, SoftMaxR²={R1:F3}, PreventiveR²={R2:F3}",
                ver, features.Count, smR2, pvR2);

            return new MLTrainingResult(true, features.Count, smR2, pvR2, ver);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "ML training failed");
            return new MLTrainingResult(false, 0, 0, 0, "ERROR", ex.Message);
        }
    }

    /// <summary>ML-6 : async pour éviter GetAwaiter().GetResult() — risque deadlock.</summary>
    public async Task<MLModelStatus> GetStatusAsync(CancellationToken ct = default)
    {
        int sessions  = await _repo.CountSessionsAsync(ct);
        int feedbacks = await _repo.CountValidFeedbacksAsync(ct);
        return new MLModelStatus(
            _meta is not null, _meta?.Version, _meta?.Samples ?? 0,
            _meta?.SoftMaxR2, _meta?.PreventiveR2, _meta?.TrainedAt,
            sessions, feedbacks, MIN_FEEDBACKS_REQUIRED);
    }

    /// <summary>
    /// ML-5 : Détection de dérive (concept drift).
    /// Calcule le R² du modèle actif sur les <paramref name="windowSize"/> sessions les plus récentes
    /// avec feedback valide, et compare avec le R² de référence.
    /// Retourne true si la dégradation dépasse <paramref name="threshold"/> (ex: 0.15).
    /// </summary>
    public async Task<bool> CheckForDriftAsync(int windowSize, double threshold, CancellationToken ct = default)
    {
        if (_softMaxModel is null || _preventiveModel is null || _meta is null)
            return false;

        try
        {
            var sessions = await _repo.GetSessionsForTrainingAsync(windowSize, ct);
            var recent = sessions
                .Where(s => s.Feedback?.Status == FeedbackStatus.Valid)
                .TakeLast(windowSize)
                .Select(BuildFeatures)
                .OfType<DistributionFeatures>()
                .ToList();

            if (recent.Count < 20)
            {
                _log.LogDebug("Drift check skipped: only {N} recent samples (min 20)", recent.Count);
                return false;
            }

            var data = _ctx.Data.LoadFromEnumerable(recent);

            var smMetrics = _ctx.Regression.Evaluate(_softMaxModel.Transform(data),
                labelColumnName: nameof(DistributionFeatures.OptimalSoftMaxPercent));
            var pvMetrics = _ctx.Regression.Evaluate(_preventiveModel.Transform(data),
                labelColumnName: nameof(DistributionFeatures.OptimalPreventiveThreshold));

            double recentR2  = (smMetrics.RSquared + pvMetrics.RSquared) / 2.0;
            double baselineR2 = (_meta.SoftMaxR2 + _meta.PreventiveR2) / 2.0;
            double degradation = baselineR2 - recentR2;

            _log.LogInformation(
                "Drift check: baseline R²={B:F3}, recent R²={R:F3}, degradation={D:F3} (threshold={T:F3})",
                baselineR2, recentR2, degradation, threshold);

            if (degradation > threshold)
            {
                _log.LogWarning(
                    "Concept drift detected! R² degraded by {D:F3} over last {N} sessions — retrain advised",
                    degradation, recent.Count);
                return true;
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Drift check failed");
        }

        return false;
    }

    // ── Helpers privés ────────────────────────────────────────────────────────

    private (ITransformer Model, double R2) Train(IDataView train, IDataView test, string label)
    {
        var featureCols = new[]
        {
            // Temporel brut
            nameof(DistributionFeatures.HourOfDay),
            nameof(DistributionFeatures.DayOfWeek),
            nameof(DistributionFeatures.MonthOfYear),
            nameof(DistributionFeatures.DayOfYear),
            // Cyclique
            nameof(DistributionFeatures.SinHour),
            nameof(DistributionFeatures.CosHour),
            nameof(DistributionFeatures.SinMonth),
            nameof(DistributionFeatures.CosMonth),
            // Saisonnalité directe
            nameof(DistributionFeatures.DaylightHours),
            nameof(DistributionFeatures.HoursUntilSunset),
            // Météo
            nameof(DistributionFeatures.CloudCoverPercent),
            nameof(DistributionFeatures.DirectRadiationWm2),
            nameof(DistributionFeatures.DiffuseRadiationWm2),
            nameof(DistributionFeatures.PrecipitationMmH),
            nameof(DistributionFeatures.AvgForecastRadiation6h),
            // Batteries
            nameof(DistributionFeatures.AvgBatteryPercent),
            nameof(DistributionFeatures.MinBatteryPercent),
            nameof(DistributionFeatures.MaxBatteryPercent),
            nameof(DistributionFeatures.TotalCapacityWh),
            nameof(DistributionFeatures.UrgentBatteryCount),
            nameof(DistributionFeatures.TotalMaxChargeRateW),
            // ML-4 : dispersion batteries
            nameof(DistributionFeatures.SocStdDev),
            nameof(DistributionFeatures.CapacityRatio),
            nameof(DistributionFeatures.NonUrgentBatteryCount),
            // Surplus
            nameof(DistributionFeatures.SurplusW),
            // Tarif
            nameof(DistributionFeatures.NormalizedTariff),
            nameof(DistributionFeatures.IsOffPeakHour),
            nameof(DistributionFeatures.HoursToNextFavorable),
            nameof(DistributionFeatures.AvgSolarForecastGrid),
            nameof(DistributionFeatures.SolarExpectedSoon),
            nameof(DistributionFeatures.MaxSavingsPerKwh),
        };

        var pipeline = _ctx.Transforms
            .CopyColumns("Label", label)
            .Append(_ctx.Transforms.Concatenate("Features", featureCols))
            .Append(_ctx.Transforms.NormalizeMinMax("Features"))
            .Append(_ctx.Regression.Trainers.FastTree(new FastTreeRegressionTrainer.Options
            {
                NumberOfTrees             = 150,
                NumberOfLeaves            = 30,
                MinimumExampleCountPerLeaf = 5,
                LearningRate              = 0.08f,
                LabelColumnName           = "Label",
                FeatureColumnName         = "Features"
            }));

        var model   = pipeline.Fit(train);
        var metrics = _ctx.Regression.Evaluate(model.Transform(test));
        return (model, metrics.RSquared);
    }

    private void Save(ITransformer model, string filename, DataViewSchema schema)
    {
        var path = Path.Combine(_modelDir, filename);
        _ctx.Model.Save(model, schema, path);
        _log.LogDebug("ML model saved: {P}", path);
    }

    private void TryLoadFromDisk()
    {
        var sm = Path.Combine(_modelDir, SOFTMAX_MODEL_FILE);
        var pv = Path.Combine(_modelDir, PREVENTIVE_MODEL_FILE);
        if (!File.Exists(sm) || !File.Exists(pv)) return;
        try
        {
            _softMaxModel    = _ctx.Model.Load(sm, out var smSchema);
            _preventiveModel = _ctx.Model.Load(pv, out _);
            var ts = File.GetLastWriteTimeUtc(sm);
            _meta  = new MLModelMeta($"v{ts:yyyyMMdd-HHmmss}", 0, 0.7, 0.7, ts);
            // ML-1 : construire les pools dès le chargement depuis le disque
            RebuildPredictionPools(smSchema);
            _log.LogInformation("ML models loaded from disk (version {V})", _meta.Version);
        }
        catch (Exception ex) { _log.LogError(ex, "Failed to load ML models from disk"); }
    }

    /// <summary>
    /// ML-1 : (Re)construit les pools de PredictionEngine après entraînement ou chargement disque.
    /// Pré-chauffe 2 moteurs par modèle pour les premiers cycles concurrents.
    /// </summary>
    private void RebuildPredictionPools(DataViewSchema _)
    {
        // Vider les anciens pools avant de reconstruire (modèle remplacé)
        _smEngines = new ConcurrentBag<PredictionEngine<DistributionFeatures, SoftMaxPrediction>>();
        _pvEngines = new ConcurrentBag<PredictionEngine<DistributionFeatures, PreventivePrediction>>();

        // Pré-chauffe : 2 moteurs suffisent pour un worker à cycle unique
        for (int i = 0; i < 2; i++)
        {
            _smEngines.Add(_ctx.Model.CreatePredictionEngine<DistributionFeatures, SoftMaxPrediction>(_softMaxModel!));
            _pvEngines.Add(_ctx.Model.CreatePredictionEngine<DistributionFeatures, PreventivePrediction>(_preventiveModel!));
        }

        _log.LogDebug("ML-1: PredictionEngine pool rebuilt (2 engines pre-warmed per model)");
    }

    private static DistributionFeatures? BuildFeatures(DistributionSession session)
    {
        if (session.Feedback?.Status != FeedbackStatus.Valid) return null;
        if (session.Weather is null || !session.BatterySnapshots.Any()) return null;

        var w  = session.Weather;
        var bs = session.BatterySnapshots.ToList();
        var fb = session.Feedback;
        var dt = session.RequestedAt;

        double[] rad   = ParseArr(w.RadiationForecast12hJson);
        double avg6h   = rad.Take(6).DefaultIfEmpty(0).Average();

        double hourRad  = 2.0 * Math.PI * dt.Hour / 24.0;
        double monthRad = 2.0 * Math.PI * (dt.Month - 1) / 12.0;

        return new DistributionFeatures
        {
            HourOfDay   = dt.Hour,
            DayOfWeek   = (float)dt.DayOfWeek,
            MonthOfYear = dt.Month,
            DayOfYear   = dt.DayOfYear,

            SinHour   = (float)Math.Sin(hourRad),
            CosHour   = (float)Math.Cos(hourRad),
            SinMonth  = (float)Math.Sin(monthRad),
            CosMonth  = (float)Math.Cos(monthRad),

            DaylightHours    = (float)w.DaylightHours,
            HoursUntilSunset = (float)w.HoursUntilSunset,

            CloudCoverPercent      = (float)w.CloudCoverPercent,
            DirectRadiationWm2     = (float)w.DirectRadiationWm2,
            DiffuseRadiationWm2    = (float)w.DiffuseRadiationWm2,
            PrecipitationMmH       = (float)w.PrecipitationMmH,
            AvgForecastRadiation6h = (float)avg6h,

            AvgBatteryPercent   = (float)bs.Average(b => b.CurrentPercentBefore),
            MinBatteryPercent   = (float)bs.Min(b => b.CurrentPercentBefore),
            MaxBatteryPercent   = (float)bs.Max(b => b.CurrentPercentBefore),
            TotalCapacityWh     = (float)bs.Sum(b => b.CapacityWh),
            UrgentBatteryCount  = bs.Count(b => b.WasUrgent),
            TotalMaxChargeRateW = (float)bs.Sum(b => b.MaxChargeRateW),

            // ML-4 : features de dispersion calculées depuis les snapshots
            SocStdDev            = (float)StdDev(bs.Select(b => b.CurrentPercentBefore)),
            CapacityRatio        = bs.Min(b => b.CapacityWh) > 0
                ? (float)(bs.Max(b => b.CapacityWh) / bs.Min(b => b.CapacityWh))
                : 1.0f,
            NonUrgentBatteryCount = bs.Count(b => !b.WasUrgent),

            SurplusW = (float)session.SurplusW,

            // Tarif — depuis les champs persistés en session
            NormalizedTariff     = (float)(session.TariffPricePerKwh.HasValue
                ? Math.Min(1.0, session.TariffPricePerKwh.Value / 0.40) : 0.5),
            IsOffPeakHour        = session.WasGridChargeFavorable ? 1f : 0f,
            HoursToNextFavorable = (float)(session.HoursToNextFavorableTariff ?? 12.0),
            AvgSolarForecastGrid = (float)(session.AvgSolarForecastWm2 ?? 0),
            SolarExpectedSoon    = session.SolarExpectedSoon ? 1f : 0f,
            MaxSavingsPerKwh     = (float)(session.TariffMaxSavingsPerKwh ?? 0),

            // Labels réels — jamais d'heuristique
            OptimalSoftMaxPercent      = (float)fb.ObservedOptimalSoftMax,
            OptimalPreventiveThreshold = (float)fb.ObservedOptimalPreventive,
        };
    }

    private static double[] ParseArr(string json)
    {
        try { return JsonSerializer.Deserialize<double[]>(json) ?? Array.Empty<double>(); }
        catch { return Array.Empty<double>(); }
    }

    /// <summary>ML-4 : écart-type population (σ) — retourne 0 si collection vide ou singleton.</summary>
    private static double StdDev(IEnumerable<double> values)
    {
        var list = values.ToList();
        if (list.Count < 2) return 0.0;
        double avg = list.Average();
        return Math.Sqrt(list.Average(v => (v - avg) * (v - avg)));
    }

    private static string BuildRationale(DistributionFeatures f, double softMax, double preventive)
    {
        var reasons = new List<string>();
        if (f.HoursUntilSunset < 3)  reasons.Add($"coucher soleil dans {f.HoursUntilSunset:F1}h");
        if (f.CloudCoverPercent > 60) reasons.Add($"nuages {f.CloudCoverPercent:F0}%");
        if (f.AvgForecastRadiation6h < 100) reasons.Add($"faible rayonnement prévu ({f.AvgForecastRadiation6h:F0}W/m²)");
        if (f.UrgentBatteryCount > 0) reasons.Add($"{f.UrgentBatteryCount} batterie(s) urgente(s)");
        if (f.IsOffPeakHour > 0.5)    reasons.Add($"heure creuse {f.NormalizedTariff * 0.40:F2}€/kWh");
        if (f.DaylightHours < 10)     reasons.Add($"jour court ({f.DaylightHours:F1}h) — saison hivernale");

        string ctx = reasons.Any() ? string.Join(", ", reasons) : "conditions solaires favorables";
        return $"SoftMax={softMax:F0}%, préventif={preventive:F0}% — {ctx}";
    }
}
