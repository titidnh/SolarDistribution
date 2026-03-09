using Microsoft.Extensions.Logging;
using SolarDistribution.Core.Models;
using SolarDistribution.Core.Services.ML;
using SolarDistribution.Core.Repositories;

namespace SolarDistribution.Core.Services;

/// <summary>
/// Façade principale de distribution intelligente.
///
/// Fix #6 : La construction de l'entité DistributionSession est maintenant
/// déléguée à IDistributionSessionFactory (implémentée dans Infrastructure).
/// SmartDistributionService ne dépend plus directement des entités EF ni de JsonSerializer.
///
/// Flux d'exécution par cycle :
///   1. Météo Open-Meteo (avec prévision 12h radiation + nuages)
///   2. Contexte tarifaire (TariffEngine : tarif actuel, favorable ?, soleil prévu ?)
///   3. Features ML construites (météo + état batteries + tarif)
///   4. Prédiction ML si modèle disponible (SoftMax + seuil préventif)
///   5. Batteries effectives : paramètres ML ou déterministes + GridChargeAllowedW
///   6. Distribution 3 passes (surplus→SoftMax, surplus→HardMax, réseau→SoftMax)
///   7. Persistance complète (session + snapshots + météo + ML + tarif)
/// </summary>
public class SmartDistributionService
{
    private readonly IBatteryDistributionService _algo;
    private readonly IDistributionMLService _ml;
    private readonly IWeatherService _weather;
    private readonly IDistributionRepository _repo;
    private readonly TariffEngine _tariff;
    private readonly IDistributionSessionFactory _sessionFactory;
    private readonly ILogger<SmartDistributionService> _logger;

    public SmartDistributionService(
        IBatteryDistributionService algo,
        IDistributionMLService ml,
        IWeatherService weather,
        IDistributionRepository repo,
        TariffEngine tariff,
        IDistributionSessionFactory sessionFactory,
        ILogger<SmartDistributionService> logger)
    {
        _algo = algo;
        _ml = ml;
        _weather = weather;
        _repo = repo;
        _tariff = tariff;
        _sessionFactory = sessionFactory;
        _logger = logger;
    }

    public async Task<SmartDistributionResult> DistributeAsync(
        double surplusW,
        IList<Battery> batteries,
        double latitude,
        double longitude,
        WeatherData? weatherSnapshot = null,
        CancellationToken ct = default)
    {
        // ── 1. Météo ──────────────────────────────────────────────────────────
        // weatherSnapshot est fourni par WeatherCacheService (rafraîchi indépendamment).
        // Fallback : appel direct si non fourni (compatibilité + tests).
        var wx = weatherSnapshot ?? await _weather.GetCurrentWeatherAsync(latitude, longitude, ct);
        if (wx is null)
            _logger.LogWarning("Weather unavailable — proceeding without weather context");

        // ── 2. Contexte tarifaire (heure locale pour les créneaux) ────────────
        var localNow = DateTime.Now;
        var radForecast = wx?.RadiationForecast12h ?? Array.Empty<double>();
        var tariffCtx = _tariff.EvaluateContext(localNow, radForecast);

        LogTariffContext(tariffCtx, surplusW);

        // ── 3. Features ML ────────────────────────────────────────────────────
        MLRecommendation? mlReco = null;
        string decisionEngine = "Deterministic";

        if (wx is not null)
        {
            var features = BuildFeatures(surplusW, batteries, wx, tariffCtx);
            mlReco = await _ml.PredictAsync(features, ct);
        }

        // ── 4. Batteries effectives ───────────────────────────────────────────
        IList<Battery> effective;

        if (mlReco is not null)
        {
            effective = Apply(batteries, mlReco, tariffCtx);
            decisionEngine = mlReco.ConfidenceScore >= 0.75 ? "ML" : "ML-Fallback";
            _logger.LogInformation(
                "ML: softMax={SoftMax:F1}%, preventive={Prev:F1}%, confidence={Conf:P0} [{Engine}]",
                mlReco.RecommendedSoftMaxPercent,
                mlReco.RecommendedPreventiveThreshold,
                mlReco.ConfidenceScore, decisionEngine);
        }
        else
        {
            effective = Apply(batteries, null, tariffCtx);
        }

        // ── 5. Log urgences ──────────────────────────────────────────────────
        foreach (var b in effective.Where(b => b.IsEmergencyGridCharge))
        {
            double target = b.EmergencyGridChargeTargetPercent ?? b.SoftMaxPercent;
            _logger.LogWarning(
                "⚡ EMERGENCY grid charge — Battery {Id}: SOC {Soc:F1}% < threshold {Thr:F0}% " +
                "— will charge to {Target:F0}% from grid (solar expected: {Solar})",
                b.Id, b.CurrentPercent, b.EmergencyGridChargeBelowPercent, target,
                tariffCtx.SolarExpectedSoon ? "yes (skipped)" : "no");
        }

        // Log la puissance de charge réseau adaptative pour chaque batterie éligible
        foreach (var b in effective.Where(b => b.GridChargeAllowedW > 0 && !b.IsEmergencyGridCharge))
        {
            _logger.LogInformation(
                "🔋 Smart grid charge — Battery {Id}: SOC {Soc:F1}% → {SoftMax:F0}%, " +
                "{W:F0}W/{Max:F0}W ({Pct:F0}% of max) over {H:F1}h remaining in slot [{Slot}]",
                b.Id, b.CurrentPercent, b.SoftMaxPercent,
                b.GridChargeAllowedW, b.MaxChargeRateW,
                b.MaxChargeRateW > 0 ? b.GridChargeAllowedW / b.MaxChargeRateW * 100 : 0,
                tariffCtx.HoursRemainingInSlot ?? 0,
                tariffCtx.ActiveSlotName);
        }

        // ── 6. Distribution ───────────────────────────────────────────────────
        var result = _algo.Distribute(surplusW, effective);

        if (result.GridChargedW > 0)
            _logger.LogInformation(
                "Grid charge: {W:F0}W [{Slot}] {Price:F3}€/kWh",
                result.GridChargedW, tariffCtx.ActiveSlotName, tariffCtx.CurrentPricePerKwh);

        // ── 6. Persistance — Fix #6 : délégué à IDistributionSessionFactory ───
        var session = _sessionFactory.Build(result, wx, mlReco, decisionEngine, batteries, tariffCtx);
        await _repo.SaveSessionAsync(session, ct);

        _logger.LogInformation(
            "Cycle done [{Engine}] solar={Solar:F0}W grid={Grid:F0}W unused={Unused:F0}W → session#{Id}",
            decisionEngine, result.TotalAllocatedW, result.GridChargedW, result.UnusedSurplusW, session.Id);

        return new SmartDistributionResult(result, decisionEngine, mlReco, wx, tariffCtx, session.Id);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Calcule GridChargeAllowedW pour chaque batterie selon la priorité suivante :
    ///
    ///   PRIORITÉ 1 — AUTOCONSOMMATION (toujours préférée)
    ///     Si le soleil est attendu avant la fin du créneau tarifaire favorable,
    ///     ET que les batteries peuvent tenir jusqu'au retour du soleil sans tomber
    ///     sous EmergencyGridChargeBelowPercent → on n'active PAS la charge réseau.
    ///     Le solaire prendra le relais à temps.
    ///
    ///   PRIORITÉ 2 — URGENCE SOC (indépendante du tarif)
    ///     Si SOC < EmergencyGridChargeBelowPercent ET soleil non attendu à temps
    ///     → recharge forcée pleine puissance depuis le réseau.
    ///
    ///   PRIORITÉ 3 — CHARGE INTELLIGENTE HEURES CREUSES
    ///     Tarif favorable + soleil insuffisant ou trop lointain.
    ///     La puissance est modulée selon le temps restant dans le créneau :
    ///
    ///     a) URGENCE TEMPORELLE : créneau se termine bientôt (< Minutes critiques)
    ///        OU soleil n'arrivera pas avant la fin du créneau
    ///        → charge au maximum (MaxChargeRateW)
    ///
    ///     b) CHARGE ÉTALÉE : beaucoup de temps restant
    ///        → puissance réduite = énergie nécessaire / temps restant
    ///        → minimum MinGridChargeW (100W) pour éviter charge trop faible
    ///        → objectif : remplir jusqu'à SoftMaxPercent juste avant la fin du créneau
    ///
    /// La puissance calculée garantit d'atteindre SoftMaxPercent avant la fin du slot
    /// sans gaspiller les heures creuses ni sur-solliciter inutilement le réseau.
    /// </summary>
    private static IList<Battery> Apply(
        IList<Battery> src,
        MLRecommendation? reco,
        TariffContext tariff,
        double minGridChargeW = 100.0,
        double urgencyThresholdHours = 1.0)
    {
        return src.Select(b =>
        {
            double softMax = reco?.RecommendedSoftMaxPercent ?? b.SoftMaxPercent;

            // ── URGENCE SOC : recharge forcée réseau ─────────────────────────
            // SOC < seuil d'urgence ET le soleil n'arrivera pas à temps pour sauver la batterie
            bool solarWillArrive = tariff.HoursUntilSolar.HasValue
                && tariff.HoursUntilSolar.Value < double.MaxValue;

            bool isEmergency = b.EmergencyGridChargeBelowPercent.HasValue
                && b.CurrentPercent < b.EmergencyGridChargeBelowPercent.Value
                && !solarWillArrive;

            // ── AUTOCONSOMMATION POSSIBLE ? ──────────────────────────────────
            // Le soleil arrive AVANT la fin du créneau tarifaire favorable
            // ET la batterie peut tenir jusqu'au retour du soleil (SOC au-dessus du seuil d'urgence)
            bool solarBeforeSlotEnd = false;
            if (!isEmergency
                && tariff.IsFavorableForGrid
                && tariff.HoursRemainingInSlot.HasValue
                && tariff.HoursUntilSolar.HasValue
                && tariff.HoursUntilSolar.Value < double.MaxValue)
            {
                bool solarArrivesBeforeSlotEnd =
                    tariff.HoursUntilSolar.Value <= tariff.HoursRemainingInSlot.Value;

                // La batterie tiendra-t-elle jusqu'au soleil sans tomber en urgence ?
                bool batteryCanWait = !b.EmergencyGridChargeBelowPercent.HasValue
                    || b.CurrentPercent > b.EmergencyGridChargeBelowPercent.Value;

                solarBeforeSlotEnd = solarArrivesBeforeSlotEnd && batteryCanWait;
            }

            // ── CALCUL DE LA PUISSANCE DE CHARGE RÉSEAU ──────────────────────
            double gridAllowedW = 0;

            if (isEmergency)
            {
                // Urgence SOC → pleine puissance, indépendant du tarif
                gridAllowedW = b.MaxChargeRateW;
            }
            else if (solarBeforeSlotEnd)
            {
                // L'autoconsommation va arriver à temps → pas de charge réseau nécessaire
                gridAllowedW = 0;
            }
            else if (tariff.GridChargeAllowed)
            {
                // Tarif favorable + soleil insuffisant/trop lointain → charge intelligente
                gridAllowedW = ComputeAdaptiveGridChargeW(
                    b, softMax, tariff, minGridChargeW, urgencyThresholdHours);
            }

            bool isEmergencyCharge = isEmergency;

            return new Battery
            {
                Id = b.Id,
                CapacityWh = b.CapacityWh,
                MaxChargeRateW = b.MaxChargeRateW,
                MinPercent = reco is null
                    ? b.MinPercent
                    : Math.Max(b.MinPercent, reco.RecommendedPreventiveThreshold),
                SoftMaxPercent = softMax,
                HardMaxPercent = b.HardMaxPercent,
                CurrentPercent = b.CurrentPercent,
                Priority = b.Priority,
                GridChargeAllowedW = gridAllowedW,
                EmergencyGridChargeBelowPercent = b.EmergencyGridChargeBelowPercent,
                EmergencyGridChargeTargetPercent = isEmergencyCharge ? b.EmergencyGridChargeTargetPercent : null,
                IsEmergencyGridCharge = isEmergencyCharge,
            };
        }).ToList();
    }

    /// <summary>
    /// <summary>
    /// Calcule la puissance de charge réseau optimale en heures creuses.
    ///
    /// Principe fondamental : on ne demande au réseau QUE l'énergie que le solaire
    /// ne pourra PAS apporter pendant le créneau restant. La puissance est ensuite
    /// étalée sur le temps disponible pour maximiser l'utilisation des heures creuses
    /// sans sur-solliciter le réseau.
    ///
    /// Étapes :
    ///   1. Énergie brute nécessaire pour atteindre SoftMax depuis SOC actuel
    ///   2. Soustraction de l'énergie solaire estimée pendant les heures restantes
    ///      (prévision W/m² × solarEfficiencyFactor × heures restantes dans le créneau)
    ///   3. Énergie nette ≤ 0 → le solaire suffira → 0W réseau
    ///   4. Puissance étalée = énergie nette / heures restantes
    ///   5. Clamp : [minGridChargeW .. MaxChargeRateW]
    ///
    /// Urgences temporelles (court-circuit, ignorent le calcul solaire) :
    ///   - Créneau se termine dans moins de urgencyThresholdHours → MaxChargeRateW
    ///   - Soleil absent ET temps court → MaxChargeRateW
    ///
    /// solarEfficiencyFactor (0.0–1.0) :
    ///   Conversion W/m² → W effectifs livrés à la batterie.
    ///   Inclut surface, rendement panneau, pertes onduleur.
    ///   Valeur conservatrice par défaut : 0.15.
    ///   Sous-estimer est PRÉFÉRABLE (sécuritaire) à sur-estimer (risque manque d'énergie).
    ///
    /// Exemple :
    ///   Batterie 30%→80%, 10kWh, MaxRate 2000W, 3h creuses restantes,
    ///   prévision solaire [200, 350, 400] W/m², factor 0.15
    ///   → énergie brute  = 5000 Wh
    ///   → énergie solaire = (200+350+400) × 0.15 = 142.5 Wh  (heures 0,1,2 dans le créneau)
    ///   → énergie nette  = 5000 - 142.5 = 4857.5 Wh
    ///   → puissance cible = 4857.5 / 3 = 1619W  (au lieu de 1667W sans la déduction solaire)
    ///   → clampé à 2000W max → 1619W envoyé
    /// </summary>
    private static double ComputeAdaptiveGridChargeW(
        Battery b,
        double softMaxPercent,
        TariffContext tariff,
        double minGridChargeW,
        double urgencyThresholdHours,
        double solarEfficiencyFactor = 0.15)
    {
        double hoursRemaining = tariff.HoursRemainingInSlot ?? 0;

        // ── Urgence temporelle : créneau presque terminé → pleine puissance ──
        if (hoursRemaining <= urgencyThresholdHours)
            return b.MaxChargeRateW;

        // ── Soleil absent ET peu de temps → pleine puissance ─────────────────
        bool solarAfterSlot = !tariff.HoursUntilSolar.HasValue
            || tariff.HoursUntilSolar.Value >= double.MaxValue
            || tariff.HoursUntilSolar.Value > hoursRemaining;
        if (solarAfterSlot && hoursRemaining <= urgencyThresholdHours * 2)
            return b.MaxChargeRateW;

        // ── Batterie déjà pleine ──────────────────────────────────────────────
        if (b.CurrentPercent >= softMaxPercent)
            return 0;

        // ── Énergie brute nécessaire pour atteindre SoftMax (Wh) ─────────────
        double energyNeededWh = (softMaxPercent - b.CurrentPercent) / 100.0 * b.CapacityWh;

        // ── Énergie solaire attendue pendant les heures creuses restantes ─────
        // Parcourt les prochaines `hoursRemaining` heures de la prévision horaire.
        // Chaque index h = 1 heure. On ne compte que depuis HoursUntilSolar.
        // La dernière heure peut être partielle (ex: 2.5h restantes → h=2 compte 0.5h).
        double solarExpectedWh = 0;
        double solarStartH = tariff.HoursUntilSolar.HasValue
                             && tariff.HoursUntilSolar.Value < double.MaxValue
            ? tariff.HoursUntilSolar.Value
            : double.MaxValue;

        var forecast = tariff.SolarForecastWm2;
        int forecastHours = (int)Math.Min(Math.Ceiling(hoursRemaining), forecast.Length);

        for (int h = 0; h < forecastHours; h++)
        {
            if (h < solarStartH) continue;                            // soleil pas encore levé
            double hourFraction = Math.Min(1.0, hoursRemaining - h); // heure partielle en fin de slot
            solarExpectedWh += forecast[h] * solarEfficiencyFactor * hourFraction;
        }

        // ── Énergie nette = ce que le réseau doit compléter ──────────────────
        double netEnergyNeededWh = Math.Max(0, energyNeededWh - solarExpectedWh);

        // Le solaire couvre tout → pas besoin de charger depuis le réseau
        if (netEnergyNeededWh <= 0)
            return 0;

        // ── Puissance étalée sur le temps restant ─────────────────────────────
        // On divise l'énergie nette par le temps disponible pour étaler la charge.
        // Le clamp final garantit le respect des limites physiques ET du minimum fonctionnel.
        double targetW = netEnergyNeededWh / hoursRemaining;

        return Math.Clamp(targetW, minGridChargeW, b.MaxChargeRateW);
    }

    private static DistributionFeatures BuildFeatures(
        double surplusW, IList<Battery> batteries,
        WeatherData wx, TariffContext tariff)
    {
        var now = DateTime.UtcNow;

        double[] rad = wx.RadiationForecast12h.ToArray();
        double avg6h = rad.Take(6).DefaultIfEmpty(0).Average();

        double hourRad = 2.0 * Math.PI * now.Hour / 24.0;
        double monthRad = 2.0 * Math.PI * (now.Month - 1) / 12.0;

        return new DistributionFeatures
        {
            HourOfDay = now.Hour,
            DayOfWeek = (float)now.DayOfWeek,
            MonthOfYear = now.Month,
            DayOfYear = now.DayOfYear,

            SinHour = (float)Math.Sin(hourRad),
            CosHour = (float)Math.Cos(hourRad),
            SinMonth = (float)Math.Sin(monthRad),
            CosMonth = (float)Math.Cos(monthRad),

            DaylightHours = (float)wx.DaylightHours,
            HoursUntilSunset = (float)wx.HoursUntilSunset,

            CloudCoverPercent = (float)wx.CloudCoverPercent,
            DirectRadiationWm2 = (float)wx.DirectRadiationWm2,
            DiffuseRadiationWm2 = (float)wx.DiffuseRadiationWm2,
            PrecipitationMmH = (float)wx.PrecipitationMmH,
            AvgForecastRadiation6h = (float)avg6h,

            AvgBatteryPercent = (float)batteries.Average(b => b.CurrentPercent),
            MinBatteryPercent = (float)batteries.Min(b => b.CurrentPercent),
            MaxBatteryPercent = (float)batteries.Max(b => b.CurrentPercent),
            TotalCapacityWh = (float)batteries.Sum(b => b.CapacityWh),
            UrgentBatteryCount = batteries.Count(b => b.IsUrgent),
            TotalMaxChargeRateW = (float)batteries.Sum(b => b.MaxChargeRateW),

            SocStdDev = (float)StdDev(batteries.Select(b => b.CurrentPercent)),
            CapacityRatio = batteries.Min(b => b.CapacityWh) > 0
                ? (float)(batteries.Max(b => b.CapacityWh) / batteries.Min(b => b.CapacityWh))
                : 1.0f,
            NonUrgentBatteryCount = batteries.Count(b => !b.IsUrgent),

            SurplusW = (float)surplusW,

            NormalizedTariff = (float)tariff.NormalizedPrice,
            IsOffPeakHour = tariff.IsFavorableForGrid ? 1f : 0f,
            HoursToNextFavorable = (float)(tariff.HoursToNextFavorable ?? 12.0),
            AvgSolarForecastGrid = (float)tariff.AvgSolarForecastWm2,
            SolarExpectedSoon = tariff.SolarExpectedSoon ? 1f : 0f,
            MaxSavingsPerKwh = (float)tariff.MaxSavingsPerKwh,

            OptimalSoftMaxPercent = 80,
            OptimalPreventiveThreshold = 20,
        };
    }

    private static float StdDev(IEnumerable<double> values)
    {
        var list = values.ToList();
        if (list.Count < 2) return 0f;
        double avg = list.Average();
        return (float)Math.Sqrt(list.Average(v => (v - avg) * (v - avg)));
    }

    private void LogTariffContext(TariffContext ctx, double surplusW)
    {
        if (!ctx.CurrentPricePerKwh.HasValue) return;

        string slotInfo = ctx.HoursRemainingInSlot.HasValue
            ? $" | slot ends in {ctx.HoursRemainingInSlot.Value:F1}h"
            : string.Empty;

        string solarInfo = ctx.HoursUntilSolar.HasValue && ctx.HoursUntilSolar.Value < double.MaxValue
            ? $" | solar in {ctx.HoursUntilSolar.Value:F1}h"
            : " | no solar forecast";

        if (ctx.GridChargeAllowed)
            _logger.LogInformation(
                "Tariff [{Slot}] {Price:F3}€/kWh — GRID CHARGE ALLOWED{SlotInfo}{SolarInfo} " +
                "(surplus={S:F0}W, forecast={F:F0}W/m², savings={Sav:F3}€/kWh)",
                ctx.ActiveSlotName, ctx.CurrentPricePerKwh, slotInfo, solarInfo,
                surplusW, ctx.AvgSolarForecastWm2, ctx.MaxSavingsPerKwh);
        else if (ctx.IsFavorableForGrid)
            _logger.LogInformation(
                "Tariff [{Slot}] {Price:F3}€/kWh — favorable but grid charge skipped{SolarInfo}{SlotInfo} " +
                "(autoconsumption will cover)",
                ctx.ActiveSlotName, ctx.CurrentPricePerKwh, solarInfo, slotInfo);
        else
            _logger.LogDebug(
                "Tariff [{Slot}] {Price:F3}€/kWh — grid charge blocked ({Reason}){SlotInfo}",
                ctx.ActiveSlotName, ctx.CurrentPricePerKwh,
                ctx.SolarExpectedSoon ? "solar expected soon" : "price above threshold",
                slotInfo);
    }
}

public record SmartDistributionResult(
    DistributionResult Distribution,
    string DecisionEngine,
    MLRecommendation? MLRecommendation,
    WeatherData? Weather,
    TariffContext? Tariff,
    long SessionId
);