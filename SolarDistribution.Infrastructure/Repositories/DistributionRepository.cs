using Microsoft.EntityFrameworkCore;
using SolarDistribution.Infrastructure.Data;
using SolarDistribution.Core.Data.Entities;
using SolarDistribution.Core.Repositories;

namespace SolarDistribution.Infrastructure.Repositories;

public class DistributionRepository : IDistributionRepository
{
    private readonly SolarDbContext _db;

    public DistributionRepository(SolarDbContext db)
    {
        _db = db;
    }

    public async Task SaveSessionAsync(DistributionSession session, CancellationToken ct = default)
    {
        _db.DistributionSessions.Add(session);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Charge les sessions pour l'entraînement ML via un sampling stratifié calendaire.
    ///
    /// STRATÉGIE :
    ///   Au lieu d'un simple Take(N) qui surreprésente les données récentes,
    ///   on découpe la fenêtre temporelle en strates (mois × heure_du_jour) et on
    ///   tire un quota proportionnel dans chaque strate.
    ///
    ///   Résultat : le modèle voit autant de données de janvier que de juillet,
    ///   autant de données nocturnes que diurnes — ce qui est crucial pour apprendre
    ///   les patterns météo/calendrier sur 2 ans sans biais de récence.
    ///
    ///   Les sessions à fort poids qualitatif (surplusWasted, import réseau) sont
    ///   toujours incluses en priorité dans leur strate, puis complétées par les
    ///   sessions normales jusqu'au quota.
    /// </summary>
    public async Task<List<DistributionSession>> GetSessionsForTrainingAsync(
        int maxRecords = 5000, CancellationToken ct = default)
    {
        // ── 1. Récupérer les IDs stratifiés — requête légère, pas d'Include ──
        // On charge d'abord uniquement les métadonnées nécessaires au sampling
        // pour éviter de ramener des centaines de milliers de rows en mémoire.
        var cutoff = DateTime.UtcNow.AddYears(-2); // fenêtre fixe 2 ans

        var candidates = await _db.DistributionSessions
            .Where(s => s.Feedback != null
                     && s.Feedback.Status == FeedbackStatus.Valid
                     && s.RequestedAt >= cutoff)
            .Select(s => new
            {
                s.Id,
                s.RequestedAt,
                // Signal qualitatif pour priorité intra-strate
                IsHighQuality = s.Feedback!.SurplusWasted || s.Feedback.DidImportFromGrid == true
            })
            .AsNoTracking()
            .ToListAsync(ct);

        if (candidates.Count == 0)
            return new List<DistributionSession>();

        // ── 2. Sampling stratifié par (mois × tranche_horaire) ───────────────
        // 12 mois × 4 tranches de 6h = 48 strates
        // Chaque strate reçoit un quota = maxRecords / 48, arrondi.
        // Les strates avec peu de données contribuent ce qu'elles ont.
        const int HourBuckets = 4;          // 0-5h, 6-11h, 12-17h, 18-23h
        const int TotalStrata = 12 * HourBuckets; // 48
        int quotaPerStratum = Math.Max(1, maxRecords / TotalStrata);

        var selectedIds = new HashSet<long>(maxRecords);

        var byStratum = candidates.GroupBy(s => (
            Month: s.RequestedAt.Month,
            HourBucket: s.RequestedAt.Hour / 6
        ));

        foreach (var stratum in byStratum)
        {
            // Priorité aux sessions à fort signal dans la strate
            var highQuality = stratum.Where(s => s.IsHighQuality).Select(s => s.Id).ToList();
            var normal = stratum.Where(s => !s.IsHighQuality).Select(s => s.Id).ToList();

            // Toujours inclure les high-quality (rares et précieux), plafonné à quota×2
            foreach (var id in highQuality.Take(quotaPerStratum * 2))
                selectedIds.Add(id);

            // Compléter avec les sessions normales jusqu'au quota
            int remaining = Math.Max(0, quotaPerStratum - highQuality.Count);
            // Shuffle déterministe pour diversifier sans biais chronologique
            var shuffled = normal.OrderBy(id => id % 97).Take(remaining);
            foreach (var id in shuffled)
                selectedIds.Add(id);
        }

        // ── 3. Charger uniquement les sessions sélectionnées avec leur contexte ─
        // Split en batches de 500 IDs pour éviter les IN() trop larges sur MySQL
        var idList = selectedIds.ToList();
        var result = new List<DistributionSession>(idList.Count);

        const int BatchSize = 500;
        for (int i = 0; i < idList.Count; i += BatchSize)
        {
            var batch = idList.Skip(i).Take(BatchSize).ToList();
            var loaded = await _db.DistributionSessions
                .Include(s => s.BatterySnapshots)
                .Include(s => s.Weather)
                .Include(s => s.MlPrediction)
                .Include(s => s.Feedback)
                .Where(s => batch.Contains(s.Id))
                .AsNoTracking()
                .ToListAsync(ct);
            result.AddRange(loaded);
        }

        return result;
    }

    /// <summary>
    /// Compresse et purge les anciennes sessions pour maîtriser le stockage DB.
    ///
    /// RÈGLES :
    ///   Phase 1 — Compression (compressionAgeDays → hardDeleteAgeDays) :
    ///     Pour chaque tranche de slotMinutes, on garde exactement 1 session,
    ///     en priorisant les sessions à fort poids qualitatif (surplusWasted ou import).
    ///     Les sessions "gagnantes" sont conservées, les autres supprimées.
    ///
    ///   Phase 2 — Hard delete (> hardDeleteAgeDays) :
    ///     Suppression définitive de toutes les sessions hors de la fenêtre utile.
    ///     Les DailySummaries ne sont jamais touchés.
    ///
    /// Retourne le nombre total de sessions supprimées.
    /// </summary>
    public async Task<int> PurgeOldSessionsAsync(
        int compressionAgeDays,
        int compressionSlotMinutes,
        int hardDeleteAgeDays,
        CancellationToken ct = default)
    {
        int totalDeleted = 0;
        var now = DateTime.UtcNow;

        // ── Phase 1 : Compression ─────────────────────────────────────────────
        var compressionEnd = now.AddDays(-compressionAgeDays);
        var compressionStart = now.AddDays(-hardDeleteAgeDays);

        // Charger les métadonnées légères des sessions éligibles à la compression
        var toCompress = await _db.DistributionSessions
            .Where(s => s.RequestedAt < compressionEnd
                     && s.RequestedAt >= compressionStart)
            .Select(s => new
            {
                s.Id,
                s.RequestedAt,
                // Les sessions sans feedback valide sont des candidates directes à la purge
                IsValid = s.Feedback != null && s.Feedback.Status == FeedbackStatus.Valid,
                IsHighQuality = s.Feedback != null
                    && (s.Feedback.SurplusWasted || s.Feedback.DidImportFromGrid == true)
            })
            .AsNoTracking()
            .ToListAsync(ct);

        if (toCompress.Any())
        {
            // Grouper par tranche temporelle de slotMinutes
            var slotMs = (long)TimeSpan.FromMinutes(compressionSlotMinutes).TotalMilliseconds;

            var grouped = toCompress.GroupBy(s =>
            {
                long ticks = ((DateTimeOffset)s.RequestedAt).ToUnixTimeMilliseconds();
                return ticks / slotMs; // clé = numéro de tranche
            });

            var idsToDelete = new List<long>();

            foreach (var slot in grouped)
            {
                var sessions = slot.ToList();
                if (sessions.Count <= 1) continue; // rien à compresser

                // Élire le représentant de la tranche :
                //   1. High-quality en priorité (surplus gaspillé ou import réseau)
                //   2. À défaut, session la plus récente de la tranche
                long keepId = sessions
                    .OrderByDescending(s => s.IsHighQuality)
                    .ThenByDescending(s => s.RequestedAt)
                    .First().Id;

                idsToDelete.AddRange(sessions
                    .Where(s => s.Id != keepId)
                    .Select(s => s.Id));
            }

            // Supprimer par batches pour éviter les transactions trop grandes
            const int DeleteBatch = 200;
            for (int i = 0; i < idsToDelete.Count; i += DeleteBatch)
            {
                var batch = idsToDelete.Skip(i).Take(DeleteBatch).ToList();
                // EF Core ExecuteDeleteAsync : DELETE direct sans chargement en mémoire
                int deleted = await _db.DistributionSessions
                    .Where(s => batch.Contains(s.Id))
                    .ExecuteDeleteAsync(ct);
                totalDeleted += deleted;
            }
        }

        // ── Phase 2 : Hard delete ─────────────────────────────────────────────
        var hardDeleteCutoff = now.AddDays(-hardDeleteAgeDays);

        const int HardDeleteBatch = 500;
        int hardDeleted;
        do
        {
            // Boucle pour vider progressivement sans verrouiller la table
            hardDeleted = await _db.DistributionSessions
                .Where(s => s.RequestedAt < hardDeleteCutoff)
                .Take(HardDeleteBatch)
                .ExecuteDeleteAsync(ct);
            totalDeleted += hardDeleted;
        }
        while (hardDeleted == HardDeleteBatch && !ct.IsCancellationRequested);

        return totalDeleted;
    }

    /// <summary>
    /// Sessions dont le feedback Pending est prêt à être collecté.
    /// On passe feedbackDelayHours depuis la config (ex: 4.0).
    /// </summary>
    public async Task<List<DistributionSession>> GetSessionsPendingFeedbackAsync(
        double feedbackDelayHours, CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow.AddHours(-feedbackDelayHours);

        return await _db.DistributionSessions
            .Include(s => s.BatterySnapshots)
            .Include(s => s.Feedback)
            .Where(s => s.Feedback == null                          // jamais traité
                     || s.Feedback.Status == FeedbackStatus.Pending) // en attente
            .Where(s => s.RequestedAt <= cutoff)                    // délai écoulé
            .OrderBy(s => s.RequestedAt)
            .Take(100)   // batch max pour ne pas surcharger HA
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public async Task<DistributionSession?> GetLastSessionAsync(CancellationToken ct = default)
    {
        return await _db.DistributionSessions
            .Include(s => s.BatterySnapshots)
            .Include(s => s.Weather)
            .OrderByDescending(s => s.RequestedAt)
            .FirstOrDefaultAsync(ct);
    }

    public async Task SaveFeedbackAsync(SessionFeedback feedback, CancellationToken ct = default)
    {
        // Upsert : si le feedback existe déjà (Pending), on le met à jour
        var existing = await _db.SessionFeedbacks
            .FirstOrDefaultAsync(f => f.SessionId == feedback.SessionId, ct);

        if (existing is null)
            _db.SessionFeedbacks.Add(feedback);
        else
        {
            existing.CollectedAt = feedback.CollectedAt;
            existing.ObservedSocJson = feedback.ObservedSocJson;
            existing.AvgSocAtFeedback = feedback.AvgSocAtFeedback;
            existing.MinSocAtFeedback = feedback.MinSocAtFeedback;
            existing.EnergyEfficiencyScore = feedback.EnergyEfficiencyScore;
            existing.AvailabilityScore = feedback.AvailabilityScore;
            existing.ObservedOptimalSoftMax = feedback.ObservedOptimalSoftMax;
            existing.ObservedOptimalPreventive = feedback.ObservedOptimalPreventive;
            existing.CompositeScore = feedback.CompositeScore;
            existing.Status = feedback.Status;
            existing.InvalidReason = feedback.InvalidReason;
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateMLScoreAsync(long sessionId, double efficiencyScore, CancellationToken ct = default)
    {
        await _db.MLPredictionLogs
            .Where(m => m.SessionId == sessionId)
            .ExecuteUpdateAsync(s =>
                s.SetProperty(m => m.EfficiencyScore, efficiencyScore), ct);
    }

    public async Task<int> CountSessionsAsync(CancellationToken ct = default)
        => await _db.DistributionSessions.CountAsync(ct);

    public async Task<int> CountValidFeedbacksAsync(CancellationToken ct = default)
        => await _db.SessionFeedbacks
            .CountAsync(f => f.Status == FeedbackStatus.Valid, ct);

    /// <summary>
    /// Moyenne roulante de consommation maison sur les N derniers cycles persistés
    /// qui ont une valeur MeasuredConsumptionW non nulle.
    /// Retourne null si aucune donnée de consommation n'est encore disponible.
    /// </summary>
    public async Task<double?> GetRecentConsumptionAvgWAsync(int lastNCycles, CancellationToken ct = default)
    {
        if (lastNCycles <= 0) return null;

        var sessions = await _db.DistributionSessions
            .Where(s => s.MeasuredConsumptionW.HasValue)
            .OrderByDescending(s => s.RequestedAt)
            .Take(lastNCycles)
            .ToListAsync(ct);

        var values = sessions.Select(s => s.MeasuredConsumptionW!.Value).ToList();

        if (values.Count == 0) return null;

        return values.Average();
    }

    // ── Feature 6 — Bilan énergétique journalier ──────────────────────────────

    /// <summary>
    /// Agrège toutes les sessions d'une journée calendaire UTC et crée ou met à jour
    /// l'enregistrement daily_summaries correspondant.
    ///
    /// Durée de cycle : estimée à partir de l'écart entre sessions consécutives,
    /// plafonnée à 10 min pour éviter les gaps (redémarrages, maintenance).
    /// </summary>
    public async Task UpsertDailySummaryAsync(DateTime date, CancellationToken ct = default)
    {
        // Plage UTC de la journée
        var dayStart = date.Date.ToUniversalTime();
        var dayEnd = dayStart.AddDays(1);

        // Charge toutes les sessions du jour avec leur contexte tarifaire
        var sessions = await _db.DistributionSessions
            .Where(s => s.RequestedAt >= dayStart && s.RequestedAt < dayEnd)
            .OrderBy(s => s.RequestedAt)
            .AsNoTracking()
            .ToListAsync(ct);

        if (sessions.Count == 0) return;

        // ── Calcul de la durée pondérée de chaque session (en heures) ─────────
        // Principe : la durée d'une session = écart jusqu'à la session suivante,
        // plafonné à MaxCycleGapHours pour absorber les trous (redémarrages, pannes).
        const double MaxCycleGapHours = 10.0 / 60.0;  // 10 min max
        const double DefaultCycleHours = 1.0 / 60.0;  // 1 min par défaut si seule session

        double solarAllocatedWh = 0;
        double gridChargedWh = 0;
        double unusedSurplusWh = 0;
        double savingsNumerator = 0;
        double savingsDenominator = 0;

        for (int i = 0; i < sessions.Count; i++)
        {
            double durationH;
            if (i < sessions.Count - 1)
            {
                double gapH = (sessions[i + 1].RequestedAt - sessions[i].RequestedAt).TotalHours;
                durationH = Math.Min(gapH, MaxCycleGapHours);
            }
            else
            {
                durationH = sessions.Count > 1
                    ? Math.Min(
                        (sessions[i].RequestedAt - sessions[i - 1].RequestedAt).TotalHours,
                        MaxCycleGapHours)
                    : DefaultCycleHours;
            }

            solarAllocatedWh += sessions[i].TotalAllocatedW * durationH;
            gridChargedWh += sessions[i].GridChargedW * durationH;
            unusedSurplusWh += sessions[i].UnusedSurplusW * durationH;

            // Économies : TariffMaxSavingsPerKwh × énergie réseau de la session
            if (sessions[i].GridChargedW > 0)
            {
                var tms = sessions[i].TariffMaxSavingsPerKwh;
                if (tms.HasValue)
                {
                    double sessionGridWh = sessions[i].GridChargedW * durationH;
                    savingsNumerator += tms.Value * sessionGridWh;
                    savingsDenominator += sessionGridWh;
                }
            }
        }

        // ── Énergie réseau totale consommée ───────────────────────────────────
        // GridConsumedWh = tout ce qui a été pris au réseau, y compris la maison
        // pendant les périodes sans surplus. On approxime en utilisant gridChargedWh
        // (charge batterie réseau) comme valeur minimale garantie.
        // Les sessions n'ont pas de MeasuredConsumptionW directement utilisable
        // ici sans over-counting → on stocke gridChargedWh dans les deux champs
        // pour rester conservateur. La distinction sera affinée si un capteur P1
        // total est ajouté en Feature 9.
        double gridConsumedWh = gridChargedWh;

        // ── Solaire autoconsommé ──────────────────────────────────────────────
        // Utilise DailySolarConsumedWh de la dernière session du jour qui l'a,
        // car c'est la valeur la plus à jour (calculée incrémentalement dans SolarWorker).
        double? solarConsumedWh = sessions
            .LastOrDefault(s => s.DailySolarConsumedWh.HasValue)
            ?.DailySolarConsumedWh;

        // ── Taux d'autosuffisance ─────────────────────────────────────────────
        double? selfSufficiencyPct = null;
        if (solarConsumedWh.HasValue && solarConsumedWh.Value >= 0)
        {
            double total = solarConsumedWh.Value + gridConsumedWh;
            selfSufficiencyPct = total > 0
                ? Math.Round(solarConsumedWh.Value / total * 100.0, 2)
                : 100.0; // journée 100% solaire (aucun import réseau)
        }

        // ── Économies estimées ────────────────────────────────────────────────
        double? estimatedSavingsEur = savingsDenominator > 0
            ? Math.Round(savingsNumerator / 1000.0, 4)  // Wh → kWh, déjà pondéré par savings/kWh
            : null;

        // ── Upsert ────────────────────────────────────────────────────────────
        var existing = await _db.DailySummaries
            .FirstOrDefaultAsync(d => d.Date == dayStart, ct);

        if (existing is null)
        {
            _db.DailySummaries.Add(new DailySummary
            {
                Date = dayStart,
                SolarConsumedWh = solarConsumedWh,
                GridConsumedWh = gridConsumedWh,
                GridChargedWh = gridChargedWh,
                SolarAllocatedWh = solarAllocatedWh,
                UnusedSurplusWh = unusedSurplusWh,
                EstimatedSavingsEur = estimatedSavingsEur,
                SelfSufficiencyPct = selfSufficiencyPct,
                SessionCount = sessions.Count,
                ComputedAt = DateTime.UtcNow,
            });
        }
        else
        {
            existing.SolarConsumedWh = solarConsumedWh;
            existing.GridConsumedWh = gridConsumedWh;
            existing.GridChargedWh = gridChargedWh;
            existing.SolarAllocatedWh = solarAllocatedWh;
            existing.UnusedSurplusWh = unusedSurplusWh;
            existing.EstimatedSavingsEur = estimatedSavingsEur;
            existing.SelfSufficiencyPct = selfSufficiencyPct;
            existing.SessionCount = sessions.Count;
            existing.ComputedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task<List<DailySummary>> GetDailySummariesAsync(
        DateTime from, DateTime to, CancellationToken ct = default)
    {
        var fromUtc = from.Date.ToUniversalTime();
        var toUtc = to.Date.ToUniversalTime().AddDays(1); // inclure la date de fin

        return await _db.DailySummaries
            .Where(d => d.Date >= fromUtc && d.Date < toUtc)
            .OrderBy(d => d.Date)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public async Task<double?> GetYesterdaySelfSufficiencyAsync(CancellationToken ct = default)
    {
        var yesterday = DateTime.UtcNow.Date.AddDays(-1).ToUniversalTime();

        return await _db.DailySummaries
            .Where(d => d.Date == yesterday)
            .Select(d => d.SelfSufficiencyPct)
            .FirstOrDefaultAsync(ct);
    }
}