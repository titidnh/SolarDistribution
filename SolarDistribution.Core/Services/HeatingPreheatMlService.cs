using Microsoft.Extensions.Logging;
using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.Trainers.FastTree;
using Microsoft.Extensions.DependencyInjection;
using SolarDistribution.Core.Data.Entities;
using SolarDistribution.Core.Repositories;
using System.Collections.Concurrent;
using System.Text.Json;

namespace SolarDistribution.Core.Services;

public class HeatingPreheatMlService : IHeatingPreheatMlService
{
    private const string ModelFile = "ml_heating_eta_model.zip";
    private const string MetaFile = "ml_heating_eta_meta.json";
    private const double MinR2ToEnable = 0.30;

    private readonly MLContext _ctx = new(seed: 42);
    private readonly ILogger<HeatingPreheatMlService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    private readonly string _modelDirectory;
    private readonly int _trainingWindowDays;
    private readonly int _targetSamples;
    private readonly int _minSamplesForRetrain;

    private ITransformer? _model;
    private HeatingMeta? _meta;
    private readonly ConcurrentBag<PredictionEngine<HeatingEtaFeatures, HeatingEtaPrediction>> _engines = new();

    private sealed record HeatingMeta(
        string ModelVersion,
        int TrainingSamples,
        double RSquared,
        double MeanAbsoluteErrorMinutes,
        DateTime TrainedAtUtc);

    private sealed class HeatingEtaFeatures
    {
        public float DeltaTempC { get; set; }
        public float OutdoorTempC { get; set; }
        public float OutdoorHumidityPct { get; set; }
        public float WindSpeedMs { get; set; }
        public float SolarIrradianceWm2 { get; set; }
        public float ForecastNextHourTempC { get; set; }
        public float ForecastTempTrend3hC { get; set; }
        public float CurrentPricePerKwh { get; set; }
        public float IsOffPeak { get; set; }
        public float IsHeatingOn { get; set; }
        public float PresenceModeEncoded { get; set; }
        public float SourceTypeEncoded { get; set; }
        public float EstimatedCostPerKwhThermal { get; set; }
        public float HourOfDay { get; set; }
        public float DayOfWeek { get; set; }
        public float LabelMinutesToTarget { get; set; }
    }

    private sealed class HeatingEtaPrediction
    {
        [ColumnName("Score")] public float PredictedMinutesToTarget { get; set; }
    }

    public HeatingPreheatMlService(
        ILogger<HeatingPreheatMlService> logger,
        IServiceScopeFactory scopeFactory,
        HeatingMlOptions? options = null)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        options ??= new HeatingMlOptions();
        _trainingWindowDays = Math.Max(30, options.TrainingWindowDays);
        _targetSamples = Math.Max(500, options.TargetSamples);
        _minSamplesForRetrain = Math.Max(50, options.MinSamplesForRetrain);

        // Allow explicit override for model directory (e.g. "/data/ml_models_heating").
        if (!string.IsNullOrWhiteSpace(options.ModelDirectory))
        {
            _modelDirectory = options.ModelDirectory!;
        }
        else
        {
            _modelDirectory = Path.Combine(AppContext.BaseDirectory, "ml_models_heating");
        }

        try
        {
            Directory.CreateDirectory(_modelDirectory);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to create heating ML model directory: {ModelDirectory}", _modelDirectory);
        }

        TryLoadFromDisk();
    }

    public async Task<HeatingPreheatEstimate> EstimateAsync(HeatingOrchestratorContext context, CancellationToken ct = default)
    {
        if (_model is null || _meta is null || _meta.RSquared < MinR2ToEnable)
            return EstimateWithFallback(context, "Fallback heuristic (ML not ready)");

        var features = BuildRuntimeFeatures(context);

        try
        {
            if (!_engines.TryTake(out var engine))
                engine = _ctx.Model.CreatePredictionEngine<HeatingEtaFeatures, HeatingEtaPrediction>(_model);

            float prediction;
            try
            {
                prediction = engine.Predict(features).PredictedMinutesToTarget;
            }
            finally
            {
                _engines.Add(engine);
            }

            var estimated = Math.Clamp((double)prediction, 1.0, 360.0);
            var p50 = estimated;
            var p90 = Math.Clamp(estimated + (1.5 * _meta.MeanAbsoluteErrorMinutes), 1.0, 420.0);

            return new HeatingPreheatEstimate(
                EstimatedMinutes: estimated,
                P50Minutes: p50,
                P90Minutes: p90,
                Confidence: Math.Clamp(_meta.RSquared, 0, 1),
                ModelVersion: _meta.ModelVersion,
                Reason: $"ML ETA (R2={_meta.RSquared:F2}, MAE={_meta.MeanAbsoluteErrorMinutes:F1}m)");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Heating ML predict failed, fallback used");
            return EstimateWithFallback(context, "Fallback heuristic (prediction error)");
        }
    }

    public async Task<HeatingMlTrainingResult> RetrainAsync(CancellationToken ct = default)
    {
        List<HeatingSample> samples;
        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IDistributionRepository>();
            samples = await repo.GetHeatingSamplesForTrainingAsync(_targetSamples, _trainingWindowDays, ct);
        }

        var trainingRows = BuildTrainingSet(samples);

        if (trainingRows.Count < _minSamplesForRetrain)
        {
            return new HeatingMlTrainingResult(
                Success: false,
                TrainingSamples: trainingRows.Count,
                RSquared: 0,
                MeanAbsoluteErrorMinutes: 0,
                ModelVersion: "N/A",
                ErrorMessage: $"Not enough labeled heating samples ({trainingRows.Count}/{_minSamplesForRetrain})");
        }

        try
        {
            var data = _ctx.Data.LoadFromEnumerable(trainingRows);
            var split = _ctx.Data.TrainTestSplit(data, testFraction: 0.2);

            var featureCols = new[]
            {
                nameof(HeatingEtaFeatures.DeltaTempC),
                nameof(HeatingEtaFeatures.OutdoorTempC),
                nameof(HeatingEtaFeatures.OutdoorHumidityPct),
                nameof(HeatingEtaFeatures.WindSpeedMs),
                nameof(HeatingEtaFeatures.SolarIrradianceWm2),
                nameof(HeatingEtaFeatures.ForecastNextHourTempC),
                nameof(HeatingEtaFeatures.ForecastTempTrend3hC),
                nameof(HeatingEtaFeatures.CurrentPricePerKwh),
                nameof(HeatingEtaFeatures.IsOffPeak),
                nameof(HeatingEtaFeatures.IsHeatingOn),
                nameof(HeatingEtaFeatures.PresenceModeEncoded),
                nameof(HeatingEtaFeatures.SourceTypeEncoded),
                nameof(HeatingEtaFeatures.EstimatedCostPerKwhThermal),
                nameof(HeatingEtaFeatures.HourOfDay),
                nameof(HeatingEtaFeatures.DayOfWeek),
            };

            var pipeline = _ctx.Transforms
                .CopyColumns("Label", nameof(HeatingEtaFeatures.LabelMinutesToTarget))
                .Append(_ctx.Transforms.Concatenate("Features", featureCols))
                .Append(_ctx.Transforms.NormalizeMinMax("Features"))
                .Append(_ctx.Regression.Trainers.FastTree(new FastTreeRegressionTrainer.Options
                {
                    LabelColumnName = "Label",
                    FeatureColumnName = "Features",
                    NumberOfTrees = 120,
                    NumberOfLeaves = 24,
                    MinimumExampleCountPerLeaf = 5,
                    LearningRate = 0.08f,
                }));

            var model = pipeline.Fit(split.TrainSet);
            var metrics = _ctx.Regression.Evaluate(model.Transform(split.TestSet));

            var version = $"heat-v{DateTime.UtcNow:yyyyMMdd-HHmmss}";
            var meta = new HeatingMeta(version, trainingRows.Count, metrics.RSquared, metrics.MeanAbsoluteError, DateTime.UtcNow);

            var modelPath = Path.Combine(_modelDirectory, ModelFile);
            var metaPath = Path.Combine(_modelDirectory, MetaFile);
            _ctx.Model.Save(model, data.Schema, modelPath);
            File.WriteAllText(metaPath, JsonSerializer.Serialize(meta));

            _model = model;
            _meta = meta;
            RebuildEnginePool();

            _logger.LogInformation(
                "Heating ML retrained: version={Version} samples={Samples} R2={R2:F3} MAE={MAE:F2}m",
                version, trainingRows.Count, metrics.RSquared, metrics.MeanAbsoluteError);

            return new HeatingMlTrainingResult(
                Success: true,
                TrainingSamples: trainingRows.Count,
                RSquared: metrics.RSquared,
                MeanAbsoluteErrorMinutes: metrics.MeanAbsoluteError,
                ModelVersion: version);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Heating ML retrain failed");
            return new HeatingMlTrainingResult(
                Success: false,
                TrainingSamples: trainingRows.Count,
                RSquared: 0,
                MeanAbsoluteErrorMinutes: 0,
                ModelVersion: "ERROR",
                ErrorMessage: ex.Message);
        }
    }

    public async Task<HeatingMlModelStatus> GetStatusAsync(CancellationToken ct = default)
    {
        int samplesInDb;
        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IDistributionRepository>();
            samplesInDb = await repo.CountHeatingSamplesAsync(ct);
        }

        return new HeatingMlModelStatus(
            IsAvailable: _meta is not null && _meta.RSquared >= MinR2ToEnable,
            ModelVersion: _meta?.ModelVersion,
            TrainingSamples: _meta?.TrainingSamples ?? 0,
            RSquared: _meta?.RSquared,
            MeanAbsoluteErrorMinutes: _meta?.MeanAbsoluteErrorMinutes,
            TrainedAt: _meta?.TrainedAtUtc,
            SamplesInDb: samplesInDb,
            MinSamplesRequired: _minSamplesForRetrain);
    }

    private List<HeatingEtaFeatures> BuildTrainingSet(List<HeatingSample> input)
    {
        var rows = new List<HeatingEtaFeatures>();
        var ordered = input
            .Where(s => s.IndoorTempC.HasValue && s.TargetTempC.HasValue)
            .OrderBy(s => s.SampledAtUtc)
            .ToList();

        for (int i = 0; i < ordered.Count; i++)
        {
            var start = ordered[i];
            var delta = start.TargetTempC!.Value - start.IndoorTempC!.Value;
            if (delta < 0.2 || delta > 8.0)
                continue;

            if (start.PresenceMode?.Equals("away", StringComparison.OrdinalIgnoreCase) == true
                || start.PresenceMode?.Equals("sleep", StringComparison.OrdinalIgnoreCase) == true)
                continue;

            var maxHorizonUtc = start.SampledAtUtc.AddHours(6);
            HeatingSample? reached = null;
            for (int j = i + 1; j < ordered.Count; j++)
            {
                var candidate = ordered[j];
                if (candidate.SampledAtUtc > maxHorizonUtc)
                    break;

                if (!candidate.IndoorTempC.HasValue)
                    continue;

                if (candidate.IndoorTempC.Value >= start.TargetTempC.Value - 0.1)
                {
                    reached = candidate;
                    break;
                }
            }

            if (reached is null)
                continue;

            var minutes = (reached.SampledAtUtc - start.SampledAtUtc).TotalMinutes;
            if (minutes < 1 || minutes > 360)
                continue;

            var (next1h, trend3h) = ParseForecast(start.ForecastOutdoorTempNextHoursJson);
            var dt = start.SampledAtUtc;

            rows.Add(new HeatingEtaFeatures
            {
                DeltaTempC = (float)delta,
                OutdoorTempC = (float)(start.OutdoorTempC ?? 10.0),
                OutdoorHumidityPct = (float)(start.OutdoorHumidityPct ?? 50.0),
                WindSpeedMs = (float)(start.WindSpeedMs ?? 2.0),
                SolarIrradianceWm2 = (float)(start.SolarIrradianceWm2 ?? 0.0),
                ForecastNextHourTempC = (float)next1h,
                ForecastTempTrend3hC = (float)trend3h,
                CurrentPricePerKwh = (float)(start.CurrentPricePerKwh ?? 0.2),
                IsOffPeak = start.IsOffPeak == true ? 1f : 0f,
                IsHeatingOn = start.IsHeatingOn == true ? 1f : 0f,
                PresenceModeEncoded = EncodePresence(start.PresenceMode),
                SourceTypeEncoded = EncodeSourceType(start.ActiveSourceType),
                EstimatedCostPerKwhThermal = (float)(start.EstimatedCostPerKwhThermal ?? start.CurrentPricePerKwh ?? 0.2),
                HourOfDay = dt.Hour,
                DayOfWeek = (float)dt.DayOfWeek,
                LabelMinutesToTarget = (float)minutes,
            });
        }

        return rows;
    }

    private static HeatingEtaFeatures BuildRuntimeFeatures(HeatingOrchestratorContext context)
    {
        return new HeatingEtaFeatures
        {
            DeltaTempC = (float)Math.Max(0.0, context.TargetTempC - context.CurrentTempC),
            OutdoorTempC = (float)context.OutsideTempC,
            OutdoorHumidityPct = 50f,
            WindSpeedMs = 2f,
            SolarIrradianceWm2 = 0f,
            ForecastNextHourTempC = (float)context.OutsideTempC,
            ForecastTempTrend3hC = context.IsWeatherWarmingSoon ? 1f : -0.5f,
            CurrentPricePerKwh = (float)Math.Max(0, context.CurrentPricePerKwh),
            IsOffPeak = context.IsOffPeak ? 1f : 0f,
            IsHeatingOn = 0f,
            PresenceModeEncoded = context.PresenceMode switch
            {
                HeatingPresenceMode.Away => 0f,
                HeatingPresenceMode.Sleep => 1f,
                HeatingPresenceMode.NearHome => 2f,
                _ => 3f
            },
            // Runtime simulation does not yet choose a source in API context,
            // so we use "unknown" defaults. Worker-side persisted samples do carry this signal.
            SourceTypeEncoded = 0f,
            EstimatedCostPerKwhThermal = (float)Math.Max(0, context.CurrentPricePerKwh),
            HourOfDay = context.NowLocal.Hour,
            DayOfWeek = (float)context.NowLocal.DayOfWeek,
            LabelMinutesToTarget = 0f,
        };
    }

    private static float EncodePresence(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode)) return 3f;
        return mode.Trim().ToLowerInvariant() switch
        {
            "away" => 0f,
            "sleep" => 1f,
            "near_home" => 2f,
            _ => 3f
        };
    }

    private static float EncodeSourceType(string? sourceType)
    {
        if (string.IsNullOrWhiteSpace(sourceType)) return 0f;
        return sourceType.Trim().ToLowerInvariant() switch
        {
            "gas" => 1f,
            "heat_pump" => 2f,
            "electric" => 3f,
            _ => 0f,
        };
    }

    private static (double Next1hTemp, double Trend3h) ParseForecast(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return (0, 0);

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            double h1 = root.TryGetProperty("next1h", out var a) && a.TryGetDouble(out var av) ? av : 0;
            double h2 = root.TryGetProperty("next2h", out var b) && b.TryGetDouble(out var bv) ? bv : h1;
            double h3 = root.TryGetProperty("next3h", out var c) && c.TryGetDouble(out var cv) ? cv : h2;
            double trend = h3 - h1;
            return (h1, trend);
        }
        catch
        {
            return (0, 0);
        }
    }

    private void TryLoadFromDisk()
    {
        var modelPath = Path.Combine(_modelDirectory, ModelFile);
        var metaPath = Path.Combine(_modelDirectory, MetaFile);
        if (!File.Exists(modelPath) || !File.Exists(metaPath))
            return;

        try
        {
            _model = _ctx.Model.Load(modelPath, out _);
            var metaJson = File.ReadAllText(metaPath);
            _meta = JsonSerializer.Deserialize<HeatingMeta>(metaJson);
            if (_meta is not null)
            {
                RebuildEnginePool();
                _logger.LogInformation(
                    "Heating ML model loaded: version={Version} R2={R2:F2} MAE={MAE:F1}m",
                    _meta.ModelVersion, _meta.RSquared, _meta.MeanAbsoluteErrorMinutes);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to load heating ML model from disk");
        }
    }

    private void RebuildEnginePool()
    {
        while (_engines.TryTake(out _)) { }
        if (_model is null) return;

        for (int i = 0; i < 2; i++)
            _engines.Add(_ctx.Model.CreatePredictionEngine<HeatingEtaFeatures, HeatingEtaPrediction>(_model));
    }

    private HeatingPreheatEstimate EstimateWithFallback(HeatingOrchestratorContext context, string reason)
    {
        var deltaTemp = Math.Max(0, context.TargetTempC - context.CurrentTempC);

        // Fallback model: linear warm-up estimate corrected by outdoor temperature.
        var baseMinutes = deltaTemp * 45.0;
        var coldPenaltyMinutes = Math.Max(0, 18.0 - context.OutsideTempC) * 2.0;
        var weatherBonusMinutes = context.IsWeatherWarmingSoon ? -8.0 : 0.0;

        var estimated = Math.Max(1.0, baseMinutes + coldPenaltyMinutes + weatherBonusMinutes);
        var p50 = estimated;
        var p90 = estimated * 1.35;

        _logger.LogDebug(
            "Heating ETA estimate: current={Current:F1}C target={Target:F1}C outside={Outside:F1}C -> {Eta:F1} min",
            context.CurrentTempC, context.TargetTempC, context.OutsideTempC, estimated);

        return new HeatingPreheatEstimate(
            EstimatedMinutes: estimated,
            P50Minutes: p50,
            P90Minutes: p90,
            Confidence: 0.35,
            ModelVersion: _meta?.ModelVersion ?? "heating-eta-bootstrap-v0",
            Reason: reason);
    }
}
