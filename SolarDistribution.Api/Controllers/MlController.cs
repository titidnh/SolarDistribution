using Microsoft.AspNetCore.Mvc;
using SolarDistribution.Core.Services.ML;

namespace SolarDistribution.Api.Controllers;

/// <summary>
/// Gestion du modèle ML — ré-entraînement manuel et consultation du statut.
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
    /// Statut actuel du modèle ML : disponibilité, version, métriques, nombre de sessions.
    /// </summary>
    /// <response code="200">Statut retourné</response>
    [HttpGet("status")]
    [ProducesResponseType(typeof(MLModelStatusDto), StatusCodes.Status200OK)]
    public ActionResult<MLModelStatusDto> GetStatus()
    {
        var status = _mlService.GetStatus();
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
    /// Lance le ré-entraînement du modèle ML sur toutes les sessions disponibles en base.
    /// Opération synchrone — peut prendre quelques secondes selon le volume de données.
    /// </summary>
    /// <remarks>
    /// Minimum 50 sessions requises pour démarrer l'entraînement.
    /// Le modèle est automatiquement activé si R² >= 0.65 sur les deux prédictions.
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
    /// <summary>Le modèle est-il disponible et suffisamment confiant pour être utilisé ?</summary>
    public bool IsAvailable { get; set; }
    public string? ModelVersion { get; set; }
    public int TrainingSamples { get; set; }
    /// <summary>R² du modèle SoftMax (0.0 à 1.0 — plus proche de 1 = meilleur)</summary>
    public double? SoftMaxRSquared { get; set; }
    /// <summary>R² du modèle seuil préventif</summary>
    public double? PreventiveRSquared { get; set; }
    public DateTime? TrainedAt { get; set; }
    public int SessionsInDb { get; set; }
    public int MinSessionsRequired { get; set; }
    /// <summary>Progression vers le seuil minimum (0-100%)</summary>
    public int ProgressPercent { get; set; }
}

public class MLTrainingResultDto
{
    public bool Success { get; set; }
    public int TrainingSamples { get; set; }
    public double SoftMaxRSquared { get; set; }
    public double PreventiveRSquared { get; set; }
    public string ModelVersion { get; set; } = string.Empty;
    /// <summary>Le modèle sera utilisé pour les prochaines distributions (R² >= 0.65)</summary>
    public bool IsModelActive { get; set; }
}
