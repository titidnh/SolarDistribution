using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SolarDistribution.Core.Repositories;

namespace SolarDistribution.Worker.Services;

/// <summary>
/// Service responsable du calcul et de la persistance du bilan énergétique journalier.
///
/// DÉCLENCHEMENT :
///   Appelé par MlRetrainScheduler toutes les heures (feedback check interval).
///   Le service détecte lui-même si une journée vient de se terminer (transition
///   de date UTC) pour éviter des agrégations redondantes au cours de la journée.
///   Il peut aussi être appelé en mode "force" pour calculer le bilan de n'importe
///   quelle journée (backfill ou re-calcul manuel).
///
/// CONCEPTION :
///   Utilise IServiceScopeFactory pour résoudre IDistributionRepository en scope
///   scoped (DbContext EF), tout en étant lui-même singleton dans le conteneur DI.
///   Le calcul effectif (agrégation SQL + upsert) est entièrement dans le repository
///   pour rester testable sans ce service.
/// </summary>
public class DailySummaryService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DailySummaryService> _logger;

    // Dernière journée UTC pour laquelle le bilan a été calculé.
    // Null au démarrage → le premier check calcule hier si nécessaire.
    private DateTime? _lastComputedDate;

    public DailySummaryService(
        IServiceScopeFactory scopeFactory,
        ILogger<DailySummaryService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// Vérifie si la journée UTC précédente a été calculée.
    /// Si non, déclenche l'agrégation et persiste le résultat.
    /// Idempotent : plusieurs appels pour la même date n'ont aucun effet.
    /// </summary>
    public async Task CheckAndComputeYesterdayAsync(CancellationToken ct = default)
    {
        var yesterday = DateTime.UtcNow.Date.AddDays(-1);

        // Déjà calculé pour cette date → rien à faire
        if (_lastComputedDate.HasValue && _lastComputedDate.Value.Date == yesterday.Date)
            return;

        await ComputeForDateAsync(yesterday, ct);
        _lastComputedDate = yesterday;
    }

    /// <summary>
    /// Force le calcul du bilan pour une date spécifique (backfill ou re-calcul).
    /// Met à jour _lastComputedDate seulement si la date est hier.
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

        // Relire pour logger les valeurs persistées
        var summaries = await repo.GetDailySummariesAsync(dateUtc, dateUtc, ct);
        var s = summaries.FirstOrDefault();

        if (s is null)
        {
            _logger.LogWarning(
                "Daily summary: no sessions found for {Date:yyyy-MM-dd} — nothing persisted.",
                dateUtc);
            return;
        }

        // Build a single boxed multi-line message so the layout stays aligned
        var contentLines = new List<string>();
        contentLines.Add($"Daily Summary {dateUtc:yyyy-MM-dd}");
        contentLines.Add($"Sessions      : {s.SessionCount}");
        contentLines.Add($"Solar alloc   : {s.SolarAllocatedWh:F0} Wh  |  Unused surplus: {s.UnusedSurplusWh:F0} Wh");
        contentLines.Add($"Grid charged  : {s.GridChargedWh:F0} Wh");

        if (s.SolarConsumedWh.HasValue)
            contentLines.Add($"Solar consumed: {s.SolarConsumedWh.Value:F0} Wh  |  Self-sufficiency: {s.SelfSufficiencyPct ?? 0:F1}%");
        else
            contentLines.Add("Solar consumed: n/a (Solcast not configured)");

        if (s.EstimatedSavingsEur.HasValue)
            contentLines.Add($"Est. savings  : {s.EstimatedSavingsEur.Value:F2} €");

        // Determine box width based on longest content line
        int maxContentWidth = contentLines.Max(l => l.Length);
        int innerWidth = Math.Max(maxContentWidth, 40); // minimum width
        string top = "╔" + new string('═', innerWidth + 2) + "╗";
        string bottom = "╚" + new string('═', innerWidth + 2) + "╝";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(top);
        foreach (var line in contentLines)
        {
            sb.Append("║ ");
            sb.Append(line.PadRight(innerWidth));
            sb.AppendLine(" ║");
        }
        sb.Append(bottom);

        _logger.LogInformation(sb.ToString());
    }
}