using Microsoft.AspNetCore.Mvc;
using SolarDistribution.Core.Services.ML;

namespace SolarDistribution.Api.Controllers;

/// <summary>
/// ML model management — manual retrain and status inspection.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class MlController : ControllerBase
{
    private readonly IDistributionMLService _mlService;
    private readonly ILogger<MlController>  _logger;

    public MlController(IDistributionMLService mlService, ILogger<MlController> logger)
    {
        _mlService = mlService;
        _logger    = logger;
    }

    /// <summary>
    /// Current ML model status: availability, version, metrics, and number of sessions.
    /// </summary>
    /// <response code="200">Statut retourné</response>
    [HttpGet("status")]
    [ProducesResponseType(typeof(MLModelStatusDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<MLModelStatusDto>> GetStatus(CancellationToken ct)
    {
        var status = await _mlService.GetStatusAsync(ct);
        return Ok(new MLModelStatusDto
        {
            IsAvailable          = status.IsAvailable,
            ModelVersion         = status.ModelVersion,
            TrainingSamples      = status.TrainingSamples,
            SoftMaxRSquared      = status.SoftMaxRSquared,
            PreventiveRSquared   = status.PreventiveRSquared,
            TrainedAt            = status.TrainedAt,
            SessionsInDb         = status.SessionsInDb,
            MinSessionsRequired  = status.MinSessionsRequired,
            ProgressPercent      = Math.Min(100, (int)(status.SessionsInDb * 100.0 / status.MinSessionsRequired))
        });
    }

    /// <summary>
    /// Starts ML model retraining on all available sessions in the database.
    /// Synchronous operation — may take several seconds depending on dataset size.
    /// </summary>
    /// <remarks>
    /// Minimum 50 sessions required to start training.
    /// The model is automatically activated if R² >= 0.65 for both predictions.
    /// </remarks>
    /// <response code="200">Entraînement réussi — métriques retournées</response>
    /// <response code="400">Pas assez de données d'entraînement</response>
    [HttpPost("retrain")]
    [ProducesResponseType(typeof(MLTrainingResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<MLTrainingResultDto>> Retrain(CancellationToken ct)
    {
        _logger.LogInformation("Manual ML retrain requested");

        var result = await _mlService.RetrainAsync(ct);

        if (!result.Success)
        {
            return BadRequest(new ProblemDetails
            {
                Title  = "ML training failed",
                Detail = result.ErrorMessage,
                Status = 400
            });
        }

        return Ok(new MLTrainingResultDto
        {
            Success            = result.Success,
            TrainingSamples    = result.TrainingSamples,
            SoftMaxRSquared    = result.SoftMaxRSquared,
            PreventiveRSquared = result.PreventiveRSquared,
            ModelVersion       = result.ModelVersion,
            IsModelActive      = result.SoftMaxRSquared >= 0.65 && result.PreventiveRSquared >= 0.65
        });
    }
}

// ── DTOs réponse ML ───────────────────────────────────────────────────────────

public class MLModelStatusDto
{
    /// <summary>Is the model available and confident enough to be used?</summary>
    public bool IsAvailable { get; set; }
    public string? ModelVersion { get; set; }
    public int TrainingSamples { get; set; }
    /// <summary>R² of the SoftMax model (0.0 to 1.0 — closer to 1 = better)</summary>
    public double? SoftMaxRSquared { get; set; }
    /// <summary>R² of the preventive threshold model</summary>
    public double? PreventiveRSquared { get; set; }
    public DateTime? TrainedAt { get; set; }
    public int SessionsInDb { get; set; }
    public int MinSessionsRequired { get; set; }
    /// <summary>Progress toward the minimum threshold (0-100%)</summary>
    public int ProgressPercent { get; set; }
}

public class MLTrainingResultDto
{
    public bool Success { get; set; }
    public int TrainingSamples { get; set; }
    public double SoftMaxRSquared { get; set; }
    public double PreventiveRSquared { get; set; }
    public string ModelVersion { get; set; } = string.Empty;
    /// <summary>The model will be used for future distributions (R² >= 0.65)</summary>
    public bool IsModelActive { get; set; }
}
