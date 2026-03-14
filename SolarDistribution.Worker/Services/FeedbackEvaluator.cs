using System.Text.Json;
using Microsoft.Extensions.Logging;
using SolarDistribution.Core.Data.Entities;
using SolarDistribution.Core.Repositories;
using SolarDistribution.Worker.Configuration;
using SolarDistribution.Worker.HA;

namespace SolarDistribution.Worker.Services;

/// <summary>
/// Collecte le feedback réel sur les sessions passées en relisant le SOC des batteries dans HA.
///
/// POURQUOI C'EST IMPORTANT :
///   Sans cela, le ML apprenait à reproduire une règle codée en dur (heuristique).
///   Avec ce feedback, le ML apprend depuis CE QUI S'EST VRAIMENT PASSÉ.
///
/// QUAND :
///   Déclenché par MlRetrainScheduler selon config ml.feedback_delay_hours (défaut: 4h).
///   On attend N heures après une session pour voir l'effet réel de la décision.
///
/// CE QU'ON MESURE :
///   On relit le SOC actuel de chaque batterie dans HA.
///   On calcule deux labels réels :
///
///   1. ObservedOptimalSoftMax :
///      - Si les batteries sont tombées trop bas après la session → le SoftMax était trop bas,
///        on aurait dû charger davantage → label = SoftMax + correction
///      - Si les batteries sont restées inutilement hautes → label = SoftMax légèrement réduit
///
///   2. ObservedOptimalPreventive :
///      - Si une batterie est passée sous MinPercent → le seuil préventif était trop bas
///      - Sinon → seuil préventif était correct ou légèrement trop conservateur
///
///   3. EnergyEfficiencyScore (0→1) :
///      - Ratio énergie stockée / énergie disponible
///
///   4. AvailabilityScore (0→1) :
///      - Pénalise les batteries trop basses au moment du feedback
/// </summary>
public class FeedbackEvaluator
{
    private readonly IDistributionRepository _repo;
    private readonly IHomeAssistantClient _haClient;
    private readonly SolarConfig _config;
    private readonly ILogger<FeedbackEvaluator> _logger;

    public FeedbackEvaluator(
        IDistributionRepository repo,
        IHomeAssistantClient haClient,
        SolarConfig config,
        ILogger<FeedbackEvaluator> logger)
    {
        _repo = repo;
        _haClient = haClient;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Collecte le feedback pour toutes les sessions en attente.
    /// Appelé périodiquement par MlRetrainScheduler.
    /// </summary>
    public async Task<int> CollectPendingFeedbacksAsync(CancellationToken ct = default)
    {
        double delayHours = _config.Ml.FeedbackDelayHours;

        var pendingSessions = await _repo.GetSessionsPendingFeedbackAsync(delayHours, ct);

        if (!pendingSessions.Any())
        {
            _logger.LogDebug("No sessions pending feedback collection");
            return 0;
        }

        _logger.LogInformation(
            "Collecting feedback for {Count} sessions (delay={Delay}h)",
            pendingSessions.Count, delayHours);

        int collected = 0;

        foreach (var session in pendingSessions)
        {
            try
            {
                var feedback = await EvaluateSessionAsync(session, ct);
                await _repo.SaveFeedbackAsync(feedback, ct);

                if (feedback.Status == FeedbackStatus.Valid)
                    collected++;

                _logger.LogInformation(
                    "Feedback session#{Id}: status={Status}, " +
                    "efficiency={Eff:P0}, availability={Avail:P0}, " +
                    "optimalSoftMax={SoftMax:F1}%, optimalPreventive={Prev:F1}%",
                    session.Id, feedback.Status,
                    feedback.EnergyEfficiencyScore, feedback.AvailabilityScore,
                    feedback.ObservedOptimalSoftMax, feedback.ObservedOptimalPreventive);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to collect feedback for session#{Id}", session.Id);
            }
        }

        _logger.LogInformation("Feedback collection complete: {Collected}/{Total} valid",
            collected, pendingSessions.Count);

        return collected;
    }

    // ── Évaluation d'une session ──────────────────────────────────────────────

    private async Task<SessionFeedback> EvaluateSessionAsync(
        DistributionSession session, CancellationToken ct)
    {
        double hoursElapsed = (DateTime.UtcNow - session.RequestedAt).TotalHours;

        // ── Lire le SOC actuel de chaque batterie dans HA ─────────────────────
        var observedSocs = new Dictionary<int, double>();
        bool anyReadFailed = false;

        foreach (var battConfig in _config.Batteries)
        {
            double? soc = await _haClient.GetNumericStateAsync(battConfig.Entities.Soc, ct);

            if (soc is null)
            {
                _logger.LogWarning(
                    "Feedback session#{Id}: cannot read SOC for battery {BattId} ({Name})",
                    session.Id, battConfig.Id, battConfig.Name);
                anyReadFailed = true;
            }
            else
            {
                observedSocs[battConfig.Id] = soc.Value;
            }
        }

        // Si toutes les lectures ont échoué → feedback invalide
        if (!observedSocs.Any())
        {
            return new SessionFeedback
            {
                SessionId = session.Id,
                CollectedAt = DateTime.UtcNow,
                FeedbackDelayHours = hoursElapsed,
                Status = FeedbackStatus.Invalid,
                InvalidReason = "All battery SOC reads failed (HA unavailable?)"
            };
        }

        // ── Calcul des scores d'efficacité ────────────────────────────────────
        double energyEfficiency = ComputeEnergyEfficiency(session);
        double availability = ComputeAvailabilityScore(observedSocs, session);
        double composite = energyEfficiency * 0.6 + availability * 0.4;

        // ── Calcul des labels réels pour l'entraînement ML ────────────────────
        // IMPORTANT : les sessions urgence, HC et avec prévisions HA ont des
        // dynamiques différentes. On les traite séparément pour des labels précis.
        bool wasEmergency = session.HadEmergencyGridCharge;
        bool wasOffPeak = session.WasGridChargeFavorable;
        bool hasHaForecast = session.ForecastTodayWh.HasValue || session.ForecastTomorrowWh.HasValue;

        double optimalSoftMax = ComputeObservedOptimalSoftMax(
            session, observedSocs, availability, wasEmergency, wasOffPeak, hasHaForecast);
        double optimalPreventive = ComputeObservedOptimalPreventive(
            session, observedSocs, wasEmergency, hasHaForecast);

        // ── ML-7a : ActualSelfSufficiencyPct ─────────────────────────────────
        // Mesure le taux d'autosuffisance réel N heures après la session.
        // On relit ConsumptionEntity et GridImportEntity depuis HA pour calculer :
        //   selfSufficiency = 1 − (grid_import / total_consumption)
        // Si les entités ne sont pas configurées, le champ reste null.
        double? actualSelfSufficiency = await ComputeActualSelfSufficiencyAsync(session, ct);

        // ── ML-7b : DidImportFromGrid ─────────────────────────────────────────
        // Lit GridImportEntity pour savoir si un import significatif s'est produit.
        // Import = puissance mesurée > GridImportSignificantThresholdW.
        bool? didImport = await ReadGridImportAsync(ct);

        // ── ML-7c : ShouldChargeFromGrid (classification binaire) ────────────
        // Détermine si la session aurait dû forcer la charge réseau.
        // Règle : on aurait dû charger si :
        //   - du courant a été importé après (didImport = true) ET
        //   - la session n'était pas déjà une charge réseau, OU
        //   - l'autosuffisance observée est < seuil bas (70%)
        bool? shouldCharge = ComputeShouldChargeFromGrid(
            session, didImport, actualSelfSufficiency);

        // ── ML-7d : SurplusWasted + TrainingWeight ────────────────────────────
        // Un surplus est gaspillé si les batteries étaient pleines et qu'il y avait
        // du surplus non absorbé (UnusedSurplusW > 0).
        // Ces sessions portent un signal fort : le ML doit apprendre à réduire
        // SoftMaxPercent la nuit quand le lendemain est ensoleillé.
        // TrainingWeight est augmenté pour amplifier ces cas rares mais importants.
        bool surplusWasted = session.UnusedSurplusW > 50
                          && observedSocs.Values.Any(soc => soc >= 95.0);

        double trainingWeight = ComputeTrainingWeight(
            surplusWasted, didImport, actualSelfSufficiency, wasEmergency);

        // Avertissement si des lectures ont partiellement échoué
        string? invalidReason = anyReadFailed
            ? "Some battery reads failed — partial feedback"
            : null;

        var feedback = new SessionFeedback
        {
            SessionId = session.Id,
            CollectedAt = DateTime.UtcNow,
            FeedbackDelayHours = hoursElapsed,
            ObservedSocJson = JsonSerializer.Serialize(observedSocs),
            AvgSocAtFeedback = observedSocs.Values.Average(),
            MinSocAtFeedback = observedSocs.Values.Min(),
            EnergyEfficiencyScore = energyEfficiency,
            AvailabilityScore = availability,
            ObservedOptimalSoftMax = optimalSoftMax,
            ObservedOptimalPreventive = optimalPreventive,
            CompositeScore = composite,
            // ML-7 : labels enrichis
            ActualSelfSufficiencyPct = actualSelfSufficiency,
            DidImportFromGrid = didImport,
            ShouldChargeFromGrid = shouldCharge,
            SurplusWasted = surplusWasted,
            TrainingWeight = trainingWeight,
            Status = anyReadFailed ? FeedbackStatus.Invalid : FeedbackStatus.Valid,
            InvalidReason = invalidReason
        };

        _logger.LogDebug(
            "Feedback ML-7 session#{Id}: selfSufficiency={SS:P0}, didImport={DI}, " +
            "shouldCharge={SC}, surplusWasted={SW}, trainingWeight={TW:F2}",
            session.Id, actualSelfSufficiency, didImport, shouldCharge, surplusWasted, trainingWeight);

        return feedback;
    }

    // ── ML-7a : Autosuffisance réelle ─────────────────────────────────────────

    /// <summary>
    /// Tente de calculer le taux d'autosuffisance en relisant ConsumptionEntity
    /// et GridImportEntity dans HA au moment du feedback.
    ///
    /// selfSufficiency = 1 − (import_W / consumption_W)
    ///   → 1.0 = 100% solaire, 0.0 = 100% réseau
    ///
    /// Retourne null si les entités ne sont pas configurées ou si la lecture échoue.
    /// </summary>
    private async Task<double?> ComputeActualSelfSufficiencyAsync(
        DistributionSession session, CancellationToken ct)
    {
        var solar = _config.Solar;

        // On a besoin d'au moins ConsumptionEntity ou ProductionEntity + GridImportEntity
        if (solar.GridImportEntity is null) return null;

        double? importW = await _haClient.GetNumericStateAsync(solar.GridImportEntity, ct);
        if (importW is null) return null;

        importW = importW.Value * solar.GridImportEntityMultiplier;

        // Lecture optionnelle de la consommation totale pour normaliser
        double? consumptionW = null;
        if (solar.ConsumptionEntity is not null)
            consumptionW = await _haClient.GetNumericStateAsync(solar.ConsumptionEntity, ct);

        if (consumptionW is null || consumptionW.Value <= 0)
        {
            // Fallback : estimer la consommation depuis l'import et le surplus de session
            // consumption ≈ production − surplus_net + import
            double productionEstimate = session.BatterySnapshots.Sum(b => b.AllocatedW) + session.SurplusW;
            consumptionW = productionEstimate + Math.Max(0, importW.Value);
        }

        if (consumptionW.Value <= 0) return null;

        double selfSufficiency = 1.0 - (Math.Max(0, importW.Value) / consumptionW.Value);
        return Math.Clamp(selfSufficiency, 0.0, 1.0);
    }

    // ── ML-7b : Import réseau binaire ────────────────────────────────────────

    /// <summary>
    /// Lit GridImportEntity et retourne true si un import significatif est détecté.
    /// Filtre les micro-imports sous GridImportSignificantThresholdW (bruit P1).
    /// </summary>
    private async Task<bool?> ReadGridImportAsync(CancellationToken ct)
    {
        var solar = _config.Solar;
        if (solar.GridImportEntity is null) return null;

        double? importW = await _haClient.GetNumericStateAsync(solar.GridImportEntity, ct);
        if (importW is null) return null;

        double adjusted = importW.Value * solar.GridImportEntityMultiplier;
        return adjusted > solar.GridImportSignificantThresholdW;
    }

    // ── ML-7c : Label de classification ShouldChargeFromGrid ─────────────────

    /// <summary>
    /// Calcule le label binaire ShouldChargeFromGrid :
    ///   true  → la session aurait dû forcer la charge réseau (on a importé après)
    ///   false → la décision de ne pas charger était correcte
    ///   null  → pas assez de données pour trancher
    ///
    /// Règles :
    ///   1. Si didImport = true ET la session n'était pas une charge réseau → shouldCharge = true
    ///   2. Si actualSelfSufficiency &lt; 0.70 → shouldCharge = true (70% = seuil configurable)
    ///   3. Si selfSufficiency ≥ 0.90 ET pas d'import → shouldCharge = false
    ///   4. Sinon → null (signal ambigu)
    /// </summary>
    private static bool? ComputeShouldChargeFromGrid(
        DistributionSession session,
        bool? didImport,
        double? selfSufficiency)
    {
        bool wasGridCharge = session.BatterySnapshots.Any(b => b.IsGridCharge);

        if (didImport == true && !wasGridCharge)
            return true;

        if (selfSufficiency.HasValue && selfSufficiency.Value < 0.70)
            return true;

        if (didImport == false && selfSufficiency.HasValue && selfSufficiency.Value >= 0.90)
            return false;

        // Signal ambigu → pas de label de classification pour cette session
        return null;
    }

    // ── ML-7d : Poids d'entraînement ─────────────────────────────────────────

    /// <summary>
    /// Calcule le poids d'entraînement pour cette session.
    ///
    /// RATIONALE :
    ///   Le dataset ML est déséquilibré : les sessions "correctes" (solaire bien absorbé)
    ///   sont majoritaires. Les sessions problématiques (surplus gaspillé, import non voulu)
    ///   sont rares mais portent un signal fort.
    ///
    ///   En augmentant leur poids, on force le modèle à mieux apprendre ces cas
    ///   sans modifier l'algorithme de training (FastTree supporte les poids via ColumnName).
    ///
    /// Pondération :
    ///   · Surplus gaspillé        → ×2.0 (signal fort : trop chargé la nuit)
    ///   · Import réseau non voulu → ×1.8 (signal fort : pas assez chargé)
    ///   · Autosuffisance &lt; 50%   → ×1.5 (signal moyen : journée dégradée)
    ///   · Session urgence         → ×1.4 (signal : algo trop conservateur)
    ///   · Poids cumulables jusqu'à ×3.5 max (évite la sur-correction)
    /// </summary>
    private static double ComputeTrainingWeight(
        bool surplusWasted,
        bool? didImport,
        double? selfSufficiency,
        bool wasEmergency)
    {
        double weight = 1.0;

        if (surplusWasted) weight *= 2.0;
        if (didImport == true) weight *= 1.8;
        if (selfSufficiency.HasValue && selfSufficiency.Value < 0.50) weight *= 1.5;
        if (wasEmergency) weight *= 1.4;

        return Math.Min(weight, 3.5); // plafond pour éviter les outliers dominants
    }

    // ── Calcul EnergyEfficiency ───────────────────────────────────────────────

    /// <summary>
    /// Efficacité énergétique = énergie effectivement stockée / énergie théoriquement disponible.
    ///
    /// Si on avait 1000W de surplus et qu'on n'a pu en stocker que 600W (batteries pleines
    /// ou MaxChargeRate atteint) → score = 0.6
    /// Si tout a été absorbé → score = 1.0
    /// </summary>
    private static double ComputeEnergyEfficiency(DistributionSession session)
    {
        if (session.SurplusW <= 0) return 1.0;

        double ratio = session.TotalAllocatedW / session.SurplusW;
        return Math.Clamp(ratio, 0, 1);
    }

    // ── Calcul AvailabilityScore ──────────────────────────────────────────────

    /// <summary>
    /// Score de disponibilité : les batteries sont-elles à un niveau acceptable N heures après ?
    ///
    /// Pénalise proportionnellement si le SOC est tombé sous MinPercent.
    /// Score = 1.0 si toutes les batteries sont au-dessus de MinPercent
    /// Score = 0.0 si toutes les batteries sont tombées au minimum absolu
    /// </summary>
    private double ComputeAvailabilityScore(
        Dictionary<int, double> observedSocs, DistributionSession session)
    {
        if (!observedSocs.Any()) return 0.5; // valeur neutre si pas de lecture

        var scores = new List<double>();

        foreach (var (battId, soc) in observedSocs)
        {
            var battConfig = _config.Batteries.FirstOrDefault(b => b.Id == battId);
            if (battConfig is null) continue;

            // Score 1.0 si au-dessus de MinPercent
            // Pénalité linéaire jusqu'à 0 si SOC = 0%
            double score = soc >= battConfig.MinPercent
                ? 1.0
                : soc / battConfig.MinPercent;

            scores.Add(score);
        }

        return scores.Any() ? scores.Average() : 0.5;
    }

    // ── Calcul ObservedOptimalSoftMax ─────────────────────────────────────────

    /// <summary>
    /// SoftMax optimal déduit de l'observation réelle, selon le contexte de la session.
    ///
    /// SESSIONS URGENCE (wasEmergency = true) :
    ///   La batterie était en crise. Le vrai signal pour le ML est : quel SoftMax HC
    ///   aurait permis d'avoir assez de réserve pour éviter cette urgence ?
    ///   → Correction plus agressive (×1.5) pour forcer le ML à apprendre à viser
    ///     plus haut en HC afin d'avoir de la marge face aux situations critiques.
    ///   → Ces sessions enrichissent le ML sans polluer les labels HC normaux :
    ///     le ML apprend la corrélation "urgence passée → viser SoftMax plus haut".
    ///
    /// SESSIONS HC NORMALES (wasOffPeak = true) :
    ///   Ajustement standard selon le résultat observé N heures après.
    ///
    /// SESSIONS SANS CONTEXTE :
    ///   Correction atténuée (×0.5) — signal moins fiable.
    /// </summary>
    private double ComputeObservedOptimalSoftMax(
        DistributionSession session,
        Dictionary<int, double> observedSocs,
        double availabilityScore,
        bool wasEmergency,
        bool wasOffPeak,
        bool hasHaForecast)
    {
        double appliedSoftMax = session.BatterySnapshots.Any()
            ? session.BatterySnapshots.Average(b => b.SoftMaxPercent)
            : 80.0;

        double avgSocNow = observedSocs.Values.DefaultIfEmpty(50).Average();

        if (wasEmergency)
        {
            // Session urgence → signal fort : le ML doit viser plus haut en HC
            // pour constituer une réserve suffisante avant la prochaine crise.
            if (availabilityScore < 0.8)
            {
                double penalty = (0.8 - availabilityScore) / 0.8;
                double correction = penalty * _config.Ml.FeedbackSoftmaxCorrectionFactor * 1.5;
                return Math.Clamp(appliedSoftMax + correction, 65, 95);
            }
            // Urgence résolue, batterie remontée → le SoftMax HC était suffisant
            return Math.Clamp(appliedSoftMax, 65, 95);
        }

        if (wasOffPeak)
        {
            // HC normale : ajustement standard
            if (availabilityScore < 0.7)
            {
                double penalty = (0.7 - availabilityScore) / 0.7;
                double correction = penalty * _config.Ml.FeedbackSoftmaxCorrectionFactor;
                return Math.Clamp(appliedSoftMax + correction, 60, 95);
            }
            // Batteries trop pleines et surplus non absorbé → réduction légère
            if (avgSocNow > appliedSoftMax + 5 && session.UnusedSurplusW > 0)
            {
                double reduction = _config.Ml.FeedbackSoftmaxReduction;
                // Si on avait une prévision HA qui annonçait une forte production demain,
                // et que les batteries sont effectivement restées trop pleines → signal
                // plus fort : le ML doit vraiment apprendre à réduire le SoftMax nocturne
                // quand demain est ensoleillé (laisser de la place pour l'autoconsommation).
                if (hasHaForecast && session.ForecastTomorrowWh.HasValue
                    && session.ForecastTomorrowWh.Value > 0)
                {
                    double totalCap = session.BatterySnapshots.Sum(b => b.CapacityWh);
                    double tomorrowRatio = totalCap > 0
                        ? session.ForecastTomorrowWh.Value / totalCap : 0;
                    // Si demain > 80% capacité batteries : réduction doublée
                    if (tomorrowRatio > 0.8)
                        reduction *= 1.5;
                }
                return Math.Clamp(appliedSoftMax - reduction, 60, 95);
            }

            return Math.Clamp(appliedSoftMax, 60, 95);
        }

        // Session sans contexte tarifaire — signal atténué
        if (availabilityScore < 0.7)
        {
            double penalty = (0.7 - availabilityScore) / 0.7;
            double correction = penalty * _config.Ml.FeedbackSoftmaxCorrectionFactor * 0.5;
            return Math.Clamp(appliedSoftMax + correction, 60, 95);
        }

        // Batteries inutilement hautes même sans contexte HC → réduction atténuée
        if (avgSocNow > appliedSoftMax + 5 && session.UnusedSurplusW > 0)
        {
            double reduction = _config.Ml.FeedbackSoftmaxReduction * 0.5;
            return Math.Clamp(appliedSoftMax - reduction, 60, 95);
        }

        return Math.Clamp(appliedSoftMax, 60, 95);
    }

    // ── Calcul ObservedOptimalPreventive ──────────────────────────────────────

    /// <summary>
    /// Seuil préventif optimal déduit de l'observation, selon le contexte.
    ///
    /// SESSIONS URGENCE :
    ///   Le déclenchement d'urgence EST la preuve que le seuil préventif était insuffisant.
    ///   Le label idéal = SOC au moment du déclenchement + marge de sécurité.
    ///   C'est un signal très fort et direct — le ML apprend exactement où placer la garde.
    ///
    /// SESSIONS NORMALES :
    ///   Ajustement standard selon si une batterie est tombée sous MinPercent.
    /// </summary>
    private double ComputeObservedOptimalPreventive(
        DistributionSession session,
        Dictionary<int, double> observedSocs,
        bool wasEmergency,
        bool hasHaForecast)
    {
        double appliedMinPercent = session.BatterySnapshots.Any()
            ? session.BatterySnapshots.Average(b => b.MinPercent)
            : 20.0;

        double minObservedSoc = observedSocs.Values.DefaultIfEmpty(50).Min();

        if (wasEmergency)
        {
            // SOC de déclenchement = valeur la plus basse parmi les batteries urgentes
            double triggerSoc = session.BatterySnapshots
                .Where(b => b.IsEmergencyGridCharge)
                .Select(b => b.CurrentPercentBefore)
                .DefaultIfEmpty(appliedMinPercent)
                .Min();

            // Seuil préventif idéal = SOC de déclenchement + marge de sécurité configurable
            double safetyMargin = _config.Ml.FeedbackMaxPreventiveCorrection * 0.5;
            double idealThreshold = triggerSoc + safetyMargin;

            // Si la batterie est encore basse au feedback → renforcement supplémentaire
            if (minObservedSoc < appliedMinPercent)
            {
                double shortfall = appliedMinPercent - minObservedSoc;
                idealThreshold += shortfall * _config.Ml.FeedbackPreventiveFactor;
            }

            return Math.Clamp(idealThreshold, 15, 50);
        }

        // Session normale : batterie tombée trop bas → augmenter le seuil
        if (minObservedSoc < appliedMinPercent)
        {
            double shortfall = appliedMinPercent - minObservedSoc;
            double correction = Math.Min(
                shortfall * _config.Ml.FeedbackPreventiveFactor,
                _config.Ml.FeedbackMaxPreventiveCorrection);

            // Si on avait une prévision HA pour aujourd'hui ET que les batteries sont quand
            // même tombées bas → la prévision n'a pas suffi à compenser.
            // Signal : garder un seuil préventif plus élevé même quand la prévision est bonne.
            // Le ML apprend à ne pas baisser sa garde même avec une bonne météo prévue.
            if (hasHaForecast && session.ForecastTodayWh.HasValue)
            {
                double totalCap = session.BatterySnapshots.Sum(b => b.CapacityWh);
                double todayRatio = totalCap > 0 ? session.ForecastTodayWh.Value / totalCap : 0;
                // Journée bien ensoleillée prévue mais batteries quand même basses
                // → signal paradoxal → correction plus conservatrice
                if (todayRatio > 0.5)
                    correction = Math.Min(correction * 1.25, _config.Ml.FeedbackMaxPreventiveCorrection);
            }

            return Math.Clamp(appliedMinPercent + correction, 15, 50);
        }

        // Batterie restée très au-dessus → seuil trop conservateur, réduction légère
        if (minObservedSoc > appliedMinPercent + 20)
            return Math.Clamp(appliedMinPercent - _config.Ml.FeedbackPreventiveReduction, 15, 50);

        return Math.Clamp(appliedMinPercent, 15, 50);
    }
}