using Microsoft.AspNetCore.Mvc;
using SolarDistribution.Api.Mapping;
using SolarDistribution.Api.Models;
using SolarDistribution.Core.Data.Entities;
using SolarDistribution.Core.Models;
using SolarDistribution.Core.Repositories;
using SolarDistribution.Core.Services;

namespace SolarDistribution.Api.Controllers;

/// <summary>
/// Intelligent distribution of solar surplus.
/// Uses ML.NET if available, otherwise falls back to the deterministic algorithm.
/// Each call is persisted in MariaDB with associated weather data.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class DistributionController : ControllerBase
{
    private readonly SmartDistributionService? _smartService;
    private readonly IBatteryDistributionService _deterministicService;
    private readonly IDistributionRepository _repo;
    private readonly ILogger<DistributionController> _logger;

    // Coordonnées par défaut (Bruxelles) — utilisées si non fournies dans la requête
    private const double DefaultLatitude = 50.85;
    private const double DefaultLongitude = 4.35;

    public DistributionController(
        SmartDistributionService smartService,
        IBatteryDistributionService deterministicService,
        IDistributionRepository repo,
        ILogger<DistributionController>? logger = null)
    {
        _smartService = smartService;
        _deterministicService = deterministicService;
        _repo = repo;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<DistributionController>.Instance;
    }

    public DistributionController(
        IBatteryDistributionService deterministicService,
        IDistributionRepository repo,
        ILogger<DistributionController>? logger = null)
    {
        _smartService = null;
        _deterministicService = deterministicService;
        _repo = repo;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<DistributionController>.Instance;
    }

    /// <summary>
    /// Live status for HA dashboard and quick checks.
    /// </summary>
    [HttpGet("status/live")]
    [ProducesResponseType(typeof(SolarDistribution.Api.Models.LiveStatusDto), StatusCodes.Status200OK)]
    public ActionResult<SolarDistribution.Api.Models.LiveStatusDto> GetLiveStatus([FromServices] SolarDistribution.Core.Services.IStatusService status)
    {
        var dto = new SolarDistribution.Api.Models.LiveStatusDto
        {
            LastDecision = status.LastDecision,
            EffectiveSurplusW = status.EffectiveSurplusW,
            GridChargeAllowed = status.GridChargeAllowed,
            NextGridChargeStartUtc = status.NextGridChargeStartUtc
        };

        return Ok(dto);
    }

    /// <summary>
    /// Distributes solar surplus among batteries with ML + weather assistance.
    /// </summary>
    /// <remarks>
    /// **Decision engine:**
    /// - `Deterministic` — priority/proportional algorithm (fallback or when ML not ready)
    /// - `ML` — ML model with confidence >= 75%
    /// - `ML-Fallback` — ML available but confidence 65-75%, parameters adjusted
    ///
    /// **Weather:** if latitude/longitude are provided, Open-Meteo data is fetched
    /// and stored. Otherwise, Brussels is used as default.
    /// </remarks>
    /// <response code="200">Distribution calculée, session persistée</response>
    /// <response code="400">Paramètres invalides</response>
    [HttpPost("calculate")]
    [ProducesResponseType(typeof(DistributionResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<DistributionResponseDto>> Calculate(
        [FromBody] DistributionRequestDto request)
    {
        // Validation métier
        var ids = request.Batteries.Select(b => b.Id).ToList();
        if (ids.Distinct().Count() != ids.Count)
            return BadRequest(new { error = "Battery IDs must be unique." });

        foreach (var b in request.Batteries)
        {
            if (b.SoftMaxPercent > b.HardMaxPercent)
                return BadRequest(new { error = $"Battery {b.Id}: SoftMaxPercent > HardMaxPercent." });
            if (b.MinPercent >= b.SoftMaxPercent)
                return BadRequest(new { error = $"Battery {b.Id}: MinPercent must be < SoftMaxPercent." });
        }

        var batteries = request.Batteries.Select(b => b.ToDomain()).ToList();
        double lat = request.Latitude ?? DefaultLatitude;
        double lon = request.Longitude ?? DefaultLongitude;

        if (_smartService is null)
        {
            var deterministicResult = _deterministicService.Distribute(request.SurplusW, batteries);
            var smartWrapper = new SmartDistributionResult(
                Distribution: deterministicResult,
                DecisionEngine: "Deterministic",
                MLRecommendation: null,
                Weather: null,
                Tariff: null,
                SessionId: 0);

            return Ok(smartWrapper.ToDto());
        }

        // Fix #2 : méthode async — plus de GetAwaiter().GetResult() qui risquait un deadlock
        var result = await _smartService.DistributeAsync(request.SurplusW, batteries, lat, lon);

        return Ok(result.ToDto());
    }

    /// <summary>
    /// Distribution without persistence or weather — useful for tests or simulation.
    /// </summary>
    [HttpPost("simulate")]
    [ProducesResponseType(typeof(DistributionResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public ActionResult<DistributionResponseDto> Simulate([FromBody] DistributionRequestDto request)
    {
        var ids = request.Batteries.Select(b => b.Id).ToList();
        if (ids.Distinct().Count() != ids.Count)
            return BadRequest(new { error = "Battery IDs must be unique." });

        var batteries = request.Batteries.Select(b => b.ToDomain());
        var result = _deterministicService.Distribute(request.SurplusW, batteries);

        return Ok(new DistributionResponseDto
        {
            SessionId = 0,
            SurplusInputW = result.SurplusInputW,
            TotalAllocatedW = result.TotalAllocatedW,
            UnusedSurplusW = result.UnusedSurplusW,
            DecisionEngine = "Deterministic-Simulate",
            Allocations = result.Allocations.Select(a => a.ToDto()).ToList()
        });
    }

    /// <summary>Returns 5 reference use cases for Swagger testing.</summary>
    [HttpGet("examples")]
    [ProducesResponseType(typeof(IEnumerable<object>), StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<object>> GetExamples() => Ok(new[]
    {
        new { Label = "UC1 — 500W, all at 50%",      SurplusW = 500  },
        new { Label = "UC2 — 1500W, all at 50%",     SurplusW = 1500 },
        new { Label = "UC3 — 1200W, B1 at 60%",      SurplusW = 1200 },
        new { Label = "UC4 — 400W, B1 urgent (18%)", SurplusW = 400  },
        new { Label = "UC5 — 600W, B1 urgent (18%)", SurplusW = 600  },
    });

    /// <summary>
    /// Returns daily energy balance summaries for a date range.
    /// Each entry contains solar consumption, grid import, self-sufficiency % and estimated savings.
    /// </summary>
    /// <remarks>
    /// Summaries are computed once per day (at midnight UTC + 1h) by the background scheduler.
    /// A summary is only available if at least one distribution session occurred on that day.
    ///
    /// Self-sufficiency requires Solcast entities to be configured — it will be null otherwise.
    /// </remarks>
    /// <param name="from">Start date inclusive (ISO 8601 date, e.g. 2025-06-01). Defaults to 30 days ago.</param>
    /// <param name="to">End date inclusive (ISO 8601 date, e.g. 2025-06-30). Defaults to yesterday.</param>
        /// <param name="ct">Cancellation token for the request.</param>
    /// <response code="200">List of daily summaries (may be empty if no sessions in range)</response>
    /// <response code="400">Date range invalid (from &gt; to, or range exceeds 366 days)</response>
    [HttpGet("summary/daily")]
    [ProducesResponseType(typeof(List<DailySummaryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<List<DailySummaryDto>>> GetDailySummaries(
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        CancellationToken ct = default)
    {
        var fromDate = (from ?? DateTime.UtcNow.AddDays(-30)).Date;
        var toDate = (to ?? DateTime.UtcNow.AddDays(-1)).Date;

        if (fromDate > toDate)
            return BadRequest(new { error = "'from' must be before or equal to 'to'." });

        if ((toDate - fromDate).TotalDays > 366)
            return BadRequest(new { error = "Date range cannot exceed 366 days." });

        var summaries = await _repo.GetDailySummariesAsync(fromDate, toDate, ct);

        var dtos = summaries.Select(s => new DailySummaryDto
        {
            Date = s.Date,
            SolarConsumedWh = s.SolarConsumedWh,
            GridConsumedWh = s.GridConsumedWh,
            GridChargedWh = s.GridChargedWh,
            SolarAllocatedWh = s.SolarAllocatedWh,
            UnusedSurplusWh = s.UnusedSurplusWh,
            EstimatedSavingsEur = s.EstimatedSavingsEur,
            SelfSufficiencyPct = s.SelfSufficiencyPct,
            SessionCount = s.SessionCount,
            ComputedAt = s.ComputedAt,
        }).ToList();

        _logger.LogDebug(
            "GET /summary/daily [{From:yyyy-MM-dd} → {To:yyyy-MM-dd}] → {Count} entries",
            fromDate, toDate, dtos.Count);

        return Ok(dtos);
    }
}