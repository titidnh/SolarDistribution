using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SolarDistribution.Core.Repositories;
using SolarDistribution.Worker.Configuration;

namespace SolarDistribution.Worker.Services;

/// <summary>
/// Service responsible for computing and persisting the daily energy balance.
///
/// TRIGGERING:
///   Called by MlRetrainScheduler every hour (feedback check interval).
///   The service detects by itself when a day has just ended (local date transition)
///   to avoid redundant aggregations during the day.
///   It can also be called in "force" mode to compute the balance for any day
///   (backfill or manual recomputation).
///
/// DESIGN:
///   Uses IServiceScopeFactory to resolve IDistributionRepository in a scoped
///   lifetime (EF DbContext), while this service remains singleton in DI.
///   The effective computation (SQL aggregation + upsert) is entirely in the repository
///   to stay testable without this service.
/// </summary>
public class DailySummaryService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DailySummaryService> _logger;
    private readonly TimeZoneInfo _localTz;

    // Last local day for which the balance was computed.
    // Null at startup → first check computes yesterday if needed.
    private DateTime? _lastComputedDate;

    public DailySummaryService(
        IServiceScopeFactory scopeFactory,
        ILogger<DailySummaryService> logger,
        SolarConfig config)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _localTz = TimeZoneInfo.FindSystemTimeZoneById(config.Location.TimeZoneId);
    }

    /// <summary>
    /// Checks whether the previous local day has been computed.
    /// If not, triggers aggregation and persists the result.
    /// Idempotent: multiple calls for the same date have no effect.
    /// </summary>
    public async Task CheckAndComputeYesterdayAsync(CancellationToken ct = default)
    {
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _localTz);
        var yesterday = nowLocal.Date.AddDays(-1);

        // Already computed for this date → nothing to do
        if (_lastComputedDate.HasValue && _lastComputedDate.Value.Date == yesterday.Date)
            return;

        await ComputeForDateAsync(yesterday, ct);
        _lastComputedDate = yesterday;
    }

    /// <summary>
    /// Forces balance computation for a specific date (backfill or recomputation).
    /// Updates _lastComputedDate only if the date is yesterday.
    /// </summary>
    public async Task ComputeForDateAsync(DateTime date, CancellationToken ct = default)
    {
        var dateUtc = date.Date.ToUniversalTime();

        _logger.LogInformation(
            "Daily summary: computing energy balance for {Date:yyyy-MM-dd} (UTC)...",
            dateUtc);

        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDistributionRepository>();

        await repo.UpsertDailySummaryAsync(dateUtc, ct);

        // Read again to log persisted values
        var summaries = await repo.GetDailySummariesAsync(dateUtc, dateUtc, ct);
        var s = summaries.FirstOrDefault();

        if (s is null)
        {
            _logger.LogWarning(
                "Daily summary: no sessions found for {Date:yyyy-MM-dd} — nothing persisted.",
                dateUtc);
            return;
        }

        // Compact single-line summary
        var parts = new List<string>
        {
            $"DailySummary:{dateUtc:yyyy-MM-dd}",
            $"sessions={s.SessionCount}",
            $"solar_alloc={s.SolarAllocatedWh:F0}Wh",
            $"unused_surplus={s.UnusedSurplusWh:F0}Wh",
            $"grid_charged={s.GridChargedWh:F0}Wh"
        };

        if (s.SolarConsumedWh.HasValue)
            parts.Add($"solar_consumed={s.SolarConsumedWh.Value:F0}Wh");
        else
            parts.Add("solar_consumed=n/a");

        parts.Add($"self_sufficiency={s.SelfSufficiencyPct?.ToString("F1") ?? "0.0"}%");

        if (s.EstimatedSavingsEur.HasValue)
            parts.Add($"est_savings={s.EstimatedSavingsEur.Value:F2}€");

        _logger.LogInformation(string.Join(" | ", parts));
    }
}