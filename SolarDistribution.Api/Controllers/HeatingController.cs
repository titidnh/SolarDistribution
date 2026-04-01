using Microsoft.AspNetCore.Mvc;
using SolarDistribution.Api.Models;
using SolarDistribution.Core.Data.Entities;
using SolarDistribution.Core.Repositories;
using SolarDistribution.Core.Services;

namespace SolarDistribution.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class HeatingController : ControllerBase
{
    private readonly IHeatingStatusService _status;
    private readonly IHeatingOrchestratorService _orchestrator;
    private readonly IHeatingPreheatMlService _preheatMl;
    private readonly IDistributionRepository _repository;

    public HeatingController(
        IHeatingStatusService status,
        IHeatingOrchestratorService orchestrator,
        IHeatingPreheatMlService preheatMl,
        IDistributionRepository repository)
    {
        _status = status;
        _orchestrator = orchestrator;
        _preheatMl = preheatMl;
        _repository = repository;
    }

    [HttpPost("gas/meter-readings")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<object>> AddGasMeterReading(
        [FromBody] GasMeterReadingCreateDto request,
        CancellationToken ct = default)
    {
        if (request.ReadingM3 <= 0)
            return BadRequest(new { error = "ReadingM3 must be > 0." });

        var readAtUtc = request.ReadAtUtc ?? DateTime.UtcNow;

        var last = await _repository.GetLastGasMeterReadingBeforeAsync(readAtUtc, ct);
        if (last is not null && request.ReadingM3 < last.ReadingM3)
            return BadRequest(new { error = "ReadingM3 must be monotonic (>= previous reading)." });

        var reading = new GasMeterReading
        {
            ReadAtUtc = readAtUtc,
            ReadingM3 = request.ReadingM3,
            Source = "manual",
            Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim()
        };

        await _repository.SaveGasMeterReadingAsync(reading, ct);

        return Ok(new
        {
            message = "Gas meter reading saved",
            reading.Id,
            reading.ReadAtUtc,
            reading.ReadingM3
        });
    }

    [HttpGet("gas/meter-readings")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<List<GasMeterReadingResponseDto>>> GetGasMeterReadings(
        [FromQuery] int count = 100,
        CancellationToken ct = default)
    {
        var safeCount = Math.Clamp(count, 1, 1000);
        var list = await _repository.GetRecentGasMeterReadingsAsync(safeCount, ct);

        var dtos = new List<GasMeterReadingResponseDto>(list.Count);
        for (int i = 0; i < list.Count; i++)
        {
            var current = list[i];
            double? delta = null;

            if (i + 1 < list.Count)
            {
                var previous = list[i + 1];
                var rawDelta = current.ReadingM3 - previous.ReadingM3;
                if (rawDelta >= 0)
                    delta = rawDelta;
            }

            dtos.Add(new GasMeterReadingResponseDto
            {
                Id = current.Id,
                ReadAtUtc = current.ReadAtUtc,
                ReadingM3 = current.ReadingM3,
                Source = current.Source,
                Note = current.Note,
                DeltaM3FromPrevious = delta
            });
        }

        return Ok(dtos);
    }

    [HttpGet("status/live")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<HeatingStatusSnapshot> GetLiveStatus()
    {
        return Ok(_status.GetSnapshot());
    }

    [HttpPost("simulate")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<object>> Simulate(
        [FromBody] HeatingSimulateRequestDto request,
        CancellationToken ct)
    {
        if (request.TargetTempC < request.CurrentTempC)
            return BadRequest(new { error = "TargetTempC must be >= CurrentTempC." });

        var context = BuildContext(request, DateTime.Now);
        var estimate = await _preheatMl.EstimateAsync(context, ct);
        var decision = await _orchestrator.DecideAsync(context, ct);

        _status.Update(new HeatingStatusSnapshot(
            PresenceMode: context.PresenceMode,
            CurrentTempC: context.CurrentTempC,
            TargetTempC: context.TargetTempC,
            NextStartAtLocal: decision.StartAtLocal,
            EstimatedMinutesToTarget: estimate.EstimatedMinutes,
            LastDecision: decision.Reason,
            UpdatedAtUtc: DateTime.UtcNow));

        return Ok(new
        {
            Context = context,
            Estimate = estimate,
            Decision = decision
        });
    }

    [HttpGet("preheat-plan")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<object>> GetPreheatPlan(
        [FromQuery] DateTime arrivalLocal,
        [FromQuery] double currentTempC,
        [FromQuery] double targetTempC,
        [FromQuery] double outsideTempC,
        [FromQuery] string mode = "near_home",
        [FromQuery] bool isOffPeak = false,
        [FromQuery] double currentPricePerKwh = 0.0,
        [FromQuery] bool isWeatherWarmingSoon = false,
        CancellationToken ct = default)
    {
        if (arrivalLocal <= DateTime.Now)
            return BadRequest(new { error = "arrivalLocal must be in the future." });

        var context = new HeatingOrchestratorContext(
            CurrentTempC: currentTempC,
            TargetTempC: targetTempC,
            OutsideTempC: outsideTempC,
            NowLocal: DateTime.Now,
            DesiredReadyAtLocal: arrivalLocal,
            PresenceMode: ParseMode(mode),
            IsOffPeak: isOffPeak,
            CurrentPricePerKwh: currentPricePerKwh,
            IsWeatherWarmingSoon: isWeatherWarmingSoon);

        var estimate = await _preheatMl.EstimateAsync(context, ct);
        var recommendedStart = arrivalLocal.AddMinutes(-estimate.P90Minutes);

        return Ok(new
        {
            ArrivalLocal = arrivalLocal,
            RecommendedStartLocal = recommendedStart,
            EstimatedMinutes = estimate.EstimatedMinutes,
            P50Minutes = estimate.P50Minutes,
            P90Minutes = estimate.P90Minutes,
            estimate.ModelVersion,
            estimate.Reason
        });
    }

    private static HeatingOrchestratorContext BuildContext(HeatingSimulateRequestDto request, DateTime nowLocal)
    {
        return new HeatingOrchestratorContext(
            CurrentTempC: request.CurrentTempC,
            TargetTempC: request.TargetTempC,
            OutsideTempC: request.OutsideTempC,
            NowLocal: nowLocal,
            DesiredReadyAtLocal: request.DesiredReadyAtLocal,
            PresenceMode: ParseMode(request.PresenceMode),
            IsOffPeak: request.IsOffPeak,
            CurrentPricePerKwh: request.CurrentPricePerKwh,
            IsWeatherWarmingSoon: request.IsWeatherWarmingSoon);
    }

    private static HeatingPresenceMode ParseMode(string mode)
    {
        return mode.Trim().ToLowerInvariant() switch
        {
            "away" => HeatingPresenceMode.Away,
            "sleep" => HeatingPresenceMode.Sleep,
            "near_home" => HeatingPresenceMode.NearHome,
            "nearhome" => HeatingPresenceMode.NearHome,
            _ => HeatingPresenceMode.Home
        };
    }
}
