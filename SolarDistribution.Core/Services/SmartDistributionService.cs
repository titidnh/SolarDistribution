using Microsoft.Extensions.Logging;
using SolarDistribution.Core.Models;
using SolarDistribution.Core.Services.ML;
using SolarDistribution.Core.Repositories;

namespace SolarDistribution.Core.Services;

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
        _algo = algo; _ml = ml; _weather = weather;
        _repo = repo; _tariff = tariff;
        _sessionFactory = sessionFactory; _logger = logger;
    }

    public async Task<SmartDistributionResult> DistributeAsync(
        double surplusW,
        IList<Battery> batteries,
        double latitude,
        double longitude,
        WeatherData? weatherSnapshot = null,
        double? forecastTodayWh = null,
        double? forecastTomorrowWh = null,
        double? estimatedConsumptionNextHoursWh = null,
        double? measuredConsumptionW = null,
        double? forecastThisHourWh = null,
        double? forecastNextHourWh = null,
        double? forecastRemainingTodayWh = null,
        double? forecastTodayWhAtStartOfDay = null,
        CancellationToken ct = default)
    {
        // ── 1. Météo ──────────────────────────────────────────────────────────
        var wx = weatherSnapshot ?? await _weather.GetCurrentWeatherAsync(latitude, longitude, ct);
        if (wx is null)
            _logger.LogWarning("Weather unavailable — proceeding without weather context");

        // ── 2. Contexte tarifaire ─────────────────────────────────────────────
        var localNow = DateTime.Now;
        var radForecast = wx?.RadiationForecast12h ?? Array.Empty<double>();
        var tariffCtx = _tariff.EvaluateContext(
            localNow, radForecast,
            forecastTodayWh, forecastTomorrowWh,
            estimatedConsumptionNextHoursWh,
            forecastThisHourWh, forecastNextHourWh, forecastRemainingTodayWh,
            totalBatteryCapacityWh: batteries.Sum(b => b.CapacityWh),
            avgBatterySocPercent: batteries.Any() ? batteries.Average(b => b.CurrentPercent) : 0,
            avgBatterySoftMaxPercent: batteries.Any() ? batteries.Average(b => b.SoftMaxPercent) : 80);

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
            effective = Apply(batteries, mlReco, tariffCtx, surplusW);
            decisionEngine = mlReco.ConfidenceScore >= 0.75 ? "ML" : "ML-Fallback";
            _logger.LogInformation(
                "ML: softMax={SoftMax:F1}%, preventive={Prev:F1}%, confidence={Conf:P0} [{Engine}]",
                mlReco.RecommendedSoftMaxPercent,
                mlReco.RecommendedPreventiveThreshold,
                mlReco.ConfidenceScore, decisionEngine);
        }
        else
        {
            effective = Apply(batteries, null, tariffCtx, surplusW);
        }

        // ── 5. Log urgences + charge adaptative ──────────────────────────────
        foreach (var b in effective.Where(b => b.IsEmergencyGridCharge))
        {
            double target = b.EmergencyGridChargeTargetPercent ?? b.SoftMaxPercent;
            _logger.LogWarning(
                "⚡ EMERGENCY grid charge — Battery {Id}: SOC {Soc:F1}% < threshold {Thr:F0}% " +
                "— will charge to {Target:F0}% from grid (solar expected: {Solar})",
                b.Id, b.CurrentPercent, b.EmergencyGridChargeBelowPercent, target,
                tariffCtx.SolarExpectedSoon ? "yes (skipped)" : "no");
        }

        foreach (var b in effective.Where(b => b.GridChargeAllowedW > 0 && !b.IsEmergencyGridCharge))
        {
            _logger.LogInformation(
                "🔋 Smart grid charge — Battery {Id}: SOC {Soc:F1}%→{SoftMax:F0}%, " +
                "{W:F0}W/{Max:F0}W ({Pct:F0}% of max) over {H:F1}h [{Slot}]{FcInfo}",
                b.Id, b.CurrentPercent, b.SoftMaxPercent,
                b.GridChargeAllowedW, b.MaxChargeRateW,
                b.MaxChargeRateW > 0 ? b.GridChargeAllowedW / b.MaxChargeRateW * 100 : 0,
                tariffCtx.HoursRemainingInSlot ?? 0,
                tariffCtx.ActiveSlotName,
                tariffCtx.HasHaForecast ? " [HA forecast]" : " [Open-Meteo]");
        }

        // Log lazy charge : batteries éligibles mais en attente (GridChargeAllowedW == 0 en HC)
        if (tariffCtx.GridChargeAllowed)
        {
            foreach (var b in effective.Where(b => b.GridChargeAllowedW == 0
                && !b.IsEmergencyGridCharge
                && b.CurrentPercent < b.SoftMaxPercent - b.SocHysteresisPercent))
            {
                _logger.LogInformation(
                    "⏳ Lazy charge — Battery {Id}: SOC {Soc:F1}% (target {SoftMax:F0}%), " +
                    "waiting for end of [{Slot}] slot ({H:F1}h remaining) — will charge later",
                    b.Id, b.CurrentPercent, b.SoftMaxPercent,
                    tariffCtx.ActiveSlotName, tariffCtx.HoursRemainingInSlot ?? 0);
            }
        }

        // ── 6. Distribution ───────────────────────────────────────────────────
        var result = _algo.Distribute(surplusW, effective);

        if (result.GridChargedW > 0)
            _logger.LogInformation(
                "Grid charge: {W:F0}W [{Slot}] {Price:F3}€/kWh",
                result.GridChargedW, tariffCtx.ActiveSlotName, tariffCtx.CurrentPricePerKwh);

        // ── 7. Persistance ────────────────────────────────────────────────────
        var session = _sessionFactory.Build(result, wx, mlReco, decisionEngine, batteries, tariffCtx,
            measuredConsumptionW, forecastTodayWhAtStartOfDay);
        await _repo.SaveSessionAsync(session, ct);

        _logger.LogInformation(
            "Cycle done [{Engine}] solar={Solar:F0}W grid={Grid:F0}W unused={Unused:F0}W → session#{Id}",
            decisionEngine, result.TotalAllocatedW, result.GridChargedW, result.UnusedSurplusW, session.Id);

        return new SmartDistributionResult(result, decisionEngine, mlReco, wx, tariffCtx, session.Id);
    }

    // ── Apply: calcule GridChargeAllowedW par batterie ────────────────────────

    /// <summary>
    /// Priorité de décision :
    ///   1. AUTOCONSOMMATION : soleil arrive avant fin du slot ET batterie peut tenir → 0W réseau
    ///   2. URGENCE SOC : SOC critique + soleil absent → MaxChargeRateW (indépendant du tarif)
    ///   3. CHARGE INTELLIGENTE HC : puissance adaptative calculée par ComputeAdaptiveGridChargeW
    ///
    /// FIX Bug #4 — IdleChargeW sans surplus solaire :
    ///   IdleChargeW est mis à 0 dès que surplusW = 0, qu'on soit en HP ou HC.
    ///   Son rôle est d'absorber les micro-surplus résiduels du compteur P1 (bruit +-50W)
    ///   quand la batterie est déjà à sa cible — pas de tirer du réseau.
    ///   En HC sans surplus, c'est ComputeAdaptiveGridChargeW qui décide si une charge
    ///   réseau est justifiée (météo, forecast J+1, heures restantes dans le slot).
    /// </summary>
    private static IList<Battery> Apply(
        IList<Battery> src,
        MLRecommendation? reco,
        TariffContext tariff,
        double surplusW,
        double minGridChargeW = 100.0,
        double urgencyThresholdHours = 1.0)
    {
        var localNow = DateTime.Now;

        return src.Select(b =>
        {
            double softMax = reco?.RecommendedSoftMaxPercent ?? b.SoftMaxPercent;

            // ── Boost J+1 : demain mauvais + tarif favorable → charger plus fort maintenant ──
            // Logique : si ForecastTomorrow < seuil ET on est dans un créneau pas cher (HC ou
            // tout slot IsFavorableForGrid), on monte le SoftMax pour maximiser la réserve.
            // S'applique à n'importe quelle heure tant que le tarif est avantageux
            // (nuit HC à 2h, soirée creuse, week-end, etc.).
            // Le boost est plafonné à HardMaxPercent pour ne jamais dépasser la limite batterie.
            if (tariff.HasLowForecastTomorrow && tariff.IsFavorableForGrid)
            {
                double boosted = softMax + tariff.EveningBoostPercent;
                softMax = Math.Min(b.HardMaxPercent, boosted);
            }

            bool solarWillArrive = tariff.HoursUntilSolar.HasValue
                && tariff.HoursUntilSolar.Value < double.MaxValue;

            bool isEmergency = b.EmergencyGridChargeBelowPercent.HasValue
                && b.CurrentPercent < b.EmergencyGridChargeBelowPercent.Value
                && !solarWillArrive;

            // Autoconsommation possible avant fin du slot ?
            bool solarBeforeSlotEnd = false;
            if (!isEmergency
                && tariff.IsFavorableForGrid
                && tariff.HoursRemainingInSlot.HasValue
                && tariff.HoursUntilSolar.HasValue
                && tariff.HoursUntilSolar.Value < double.MaxValue)
            {
                bool solarArrivesBeforeSlotEnd =
                    tariff.HoursUntilSolar.Value <= tariff.HoursRemainingInSlot.Value;
                bool batteryCanWait = !b.EmergencyGridChargeBelowPercent.HasValue
                    || b.CurrentPercent > b.EmergencyGridChargeBelowPercent.Value;
                solarBeforeSlotEnd = solarArrivesBeforeSlotEnd && batteryCanWait;
            }

            double gridAllowedW = 0;

            if (isEmergency)
                gridAllowedW = b.MaxChargeRateW;
            else if (solarBeforeSlotEnd)
                gridAllowedW = 0;
            else if (tariff.GridChargeAllowed)
                gridAllowedW = ComputeAdaptiveGridChargeW(
                    b, softMax, tariff, minGridChargeW, urgencyThresholdHours, lazyBufferHours: tariff.LazyBufferHours);

            // ── FIX Bug #4 — IdleChargeW : surplus solaire uniquement ────────────
            // IdleChargeW a une seule vocation : absorber les micro-surplus résiduels
            // du compteur P1 quand la batterie est à sa cible (bruit ±50W, cycling BMS).
            // Il ne doit JAMAIS tirer du réseau, que ce soit en HP ou en HC.
            //
            // En HC sans surplus, c'est ComputeAdaptiveGridChargeW qui décide si on
            // charge (selon météo, forecast J+1, heures restantes dans le slot).
            // Court-circuiter cette logique avec IdleChargeW en HC serait incorrect :
            // on chargerait même quand le forecast prédit assez de solaire demain.
            //
            // Règle : IdleChargeW > 0 seulement si surplus solaire réel > 0.
            double effectiveIdleChargeW = surplusW > 0 ? b.IdleChargeW : 0;

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
                HardwareMinChargeW = b.HardwareMinChargeW,
                IdleChargeW = effectiveIdleChargeW,
                // Propagation nécessaire pour que ComputeAdaptiveGridChargeW accède à l'hystérésis
                SocHysteresisPercent = b.SocHysteresisPercent,
                GridChargeAllowedW = gridAllowedW,
                EmergencyGridChargeBelowPercent = b.EmergencyGridChargeBelowPercent,
                EmergencyGridChargeTargetPercent = isEmergency ? b.EmergencyGridChargeTargetPercent : null,
                IsEmergencyGridCharge = isEmergency,
            };
        }).ToList();
    }

    /// <summary>
    /// Puissance de charge réseau adaptative en HC — avec Lazy Charging.
    ///
    /// Principe du Lazy Charging :
    ///   Plutôt que de charger dès l'ouverture du créneau HC à faible puissance,
    ///   on calcule l'heure limite à partir de laquelle il FAUT démarrer pour
    ///   atteindre la cible avant la fin du slot, puis on attend jusque-là.
    ///
    ///   hoursNeeded = energyNeeded / MaxChargeRateW
    ///   hoursBeforeStart = hoursRemaining - hoursNeeded - lazyBuffer
    ///   → Si hoursBeforeStart > 0 : trop tôt, retourner 0 (attendre)
    ///   → Sinon : démarrer à pleine puissance adaptative
    ///
    /// Avantages :
    ///   - Maximise le temps de décharge sur batterie (self-powered) avant de charger
    ///   - La charge se fait en fin de nuit, juste avant le retour en HP (6h-7h)
    ///   - Puissance plus élevée sur une courte durée = moins de cycles partiels BMS
    ///
    /// Si des prévisions HA sont disponibles (ForecastTodayWh), elles remplacent
    /// le calcul générique Open-Meteo × 0.15 pour l'estimation de l'énergie solaire
    /// attendue pendant le créneau restant.
    ///
    /// Logique :
    ///   énergie brute = (SoftMax - SOC) × CapacityWh
    ///   énergie solaire attendue (créneau restant) :
    ///     - si HA forecast dispo → proratisation de ForecastTodayWh sur les heures restantes
    ///     - sinon → Σ forecast[h] × solarEfficiencyFactor
    ///   énergie nette = max(0, brute - solaire)
    ///   puissance = clamp(nette / hoursNeeded, minGridChargeW, MaxChargeRateW)
    /// </summary>
    private static double ComputeAdaptiveGridChargeW(
        Battery b,
        double softMaxPercent,
        TariffContext tariff,
        double minGridChargeW,
        double urgencyThresholdHours,
        double solarEfficiencyFactor = 0.15,
        double lazyBufferHours = 0.5)
    {
        double hoursRemaining = tariff.HoursRemainingInSlot ?? 0;

        if (hoursRemaining <= urgencyThresholdHours)
            return b.MaxChargeRateW;

        bool solarAfterSlot = !tariff.HoursUntilSolar.HasValue
            || tariff.HoursUntilSolar.Value >= double.MaxValue
            || tariff.HoursUntilSolar.Value > hoursRemaining;
        if (solarAfterSlot && hoursRemaining <= urgencyThresholdHours * 2)
            return b.MaxChargeRateW;

        // ── FIX Bug #1 : Hystérésis SOC ──────────────────────────────────────
        // Problème original : quand le SOC atteint 90% puis redescend à 89.9%
        // (auto-décharge EcoFlow self-powered), le calcul produisait energyNeeded=1Wh
        // → targetW=0.18W → clampé à minGridChargeW=100W, MAIS DistributeGridToGroup
        // fait Math.Min(spaceToTarget=1Wh, gridLeft=100W) → commande finale = 1W.
        // Résultat : 50+ micro-commandes ignorées par l'EcoFlow mais comptées comme
        // cycles BMS.
        //
        // Avec SocHysteresisPercent = 2% :
        //   · Seuil effectif = softMax - hysteresis = 90% - 2% = 88%
        //   · Entre 88% et 90% → return 0  (zone morte, auto-décharge acceptée)
        //   · SOC descend à 87.9% → energyNeeded = ~21Wh → commande ≥ 100W (efficace)
        //   · SocHysteresisPercent = 0 → comportement identique à l'original
        double rechargeThreshold = softMaxPercent - b.SocHysteresisPercent;
        if (b.CurrentPercent >= rechargeThreshold)
            return 0;
        // ─────────────────────────────────────────────────────────────────────

        double energyNeededWh = (softMaxPercent - b.CurrentPercent) / 100.0 * b.CapacityWh;

        // ── Énergie solaire attendue pendant les heures restantes ─────────────
        double solarExpectedWh;

        if (tariff.HasIntradayForecast && tariff.SolcastHourlyCurveWh is not null)
        {
            // ── Courbe Solcast horaire réelle (Feature 3) ─────────────────────
            // Remplace le profil sinusoïdal simplifié : on utilise les données Wh/h
            // réelles de Solcast pour calculer l'énergie attendue heure par heure.
            //
            // Avantages vs sinusoïde :
            //   · Tient compte des nuages prévus à des heures spécifiques
            //   · Intègre l'orientation/inclinaison réelle de l'installation
            //   · Précision horaire vs approximation journalière
            //
            // On intègre la courbe Solcast sur la fenêtre [now, now + hoursRemaining],
            // en pondérant la fraction d'heure pour la dernière tranche partielle.
            double solarStartH = tariff.HoursUntilSolar.HasValue
                                 && tariff.HoursUntilSolar.Value < double.MaxValue
                ? tariff.HoursUntilSolar.Value : 0.0;

            solarExpectedWh = 0;
            var curve = tariff.SolcastHourlyCurveWh;

            for (int h = 0; h < curve.Length && h < Math.Ceiling(hoursRemaining); h++)
            {
                if (h < solarStartH) continue;
                double hourFraction = Math.Min(1.0, hoursRemaining - h);
                solarExpectedWh += curve[h] * hourFraction;
            }

            // Si la courbe ne couvre pas tout l'horizon (ex: seulement 3h alors qu'il reste 6h),
            // on extrapole via le fallback sinusoïdal avec ForecastTodayWh pour les heures manquantes.
            if (curve.Length < hoursRemaining && tariff.ForecastTodayWh.HasValue)
            {
                double coveredH = curve.Length;
                double remainingUncoveredH = hoursRemaining - coveredH;
                double sunriseH = solarStartH;
                double sunsetH  = sunriseH + 12.0;

                double fallbackFraction = SolarFractionBetweenHours(
                    coveredH, coveredH + remainingUncoveredH, sunriseH, sunsetH);
                solarExpectedWh += tariff.ForecastTodayWh.Value * fallbackFraction;
            }
        }
        else if (tariff.HasHaForecast && tariff.ForecastTodayWh.HasValue)
        {
            // Profil sinusoïdal avec ForecastTodayWh (pas d'entités intraday configurées)
            double solarStartH = tariff.HoursUntilSolar.HasValue
                                 && tariff.HoursUntilSolar.Value < double.MaxValue
                ? tariff.HoursUntilSolar.Value : 24.0;

            double solarHoursInSlot = Math.Max(0, hoursRemaining - solarStartH);

            double sunriseH = solarStartH;
            double sunsetH = sunriseH + 12.0;

            double solarFraction = SolarFractionBetweenHours(
                sunriseH, sunriseH + solarHoursInSlot, sunriseH, sunsetH);

            solarExpectedWh = tariff.ForecastTodayWh.Value * solarFraction;

            // Si le slot traverse minuit et que demain est configuré, ajouter la part de J+1
            if (tariff.ForecastTomorrowWh.HasValue && solarStartH >= hoursRemaining && hoursRemaining > 0)
            {
                double tomorrowFraction = SolarFractionBetweenHours(0, solarHoursInSlot, 0, 12.0);
                solarExpectedWh += tariff.ForecastTomorrowWh.Value * tomorrowFraction;
            }
        }
        else
        {
            // Fallback Open-Meteo : W/m² × facteur d'efficacité
            solarExpectedWh = 0;
            double solarStartH = tariff.HoursUntilSolar.HasValue
                                 && tariff.HoursUntilSolar.Value < double.MaxValue
                ? tariff.HoursUntilSolar.Value : double.MaxValue;

            var forecast = tariff.SolarForecastWm2;
            int forecastHours = (int)Math.Min(Math.Ceiling(hoursRemaining), forecast.Length);

            for (int h = 0; h < forecastHours; h++)
            {
                if (h < solarStartH) continue;
                double hourFraction = Math.Min(1.0, hoursRemaining - h);
                solarExpectedWh += forecast[h] * solarEfficiencyFactor * hourFraction;
            }
        }

        double netEnergyNeededWh = Math.Max(0, energyNeededWh - solarExpectedWh);

        // ── Ajustement consommation maison estimée ────────────────────────────
        // Le surplus solaire estimé alimentera d'abord la consommation maison avant les batteries.
        // Si la consommation prévue dépasse le solaire attendu, les batteries ne seront pas
        // rechargées par le solaire → on augmente la charge réseau en conséquence.
        //
        // Exemple : solar attendu = 800Wh, conso estimée = 600Wh, déficit batterie = 500Wh
        //   → solar net pour batteries = max(0, 800 - 600) = 200Wh
        //   → netEnergyNeededWh = max(0, 500 - 200) = 300Wh (réseau nécessaire)
        //
        // Sans ce correctif : netEnergyNeededWh = max(0, 500 - 800) = 0Wh (trop optimiste)
        // → les batteries arrivent vides à la HP car le solaire a été absorbé par la conso maison.
        if (tariff.EstimatedConsumptionNextHoursWh.HasValue && solarExpectedWh > 0)
        {
            double consumptionLoad = tariff.EstimatedConsumptionNextHoursWh.Value;
            double solarForBatteries = Math.Max(0, solarExpectedWh - consumptionLoad);
            netEnergyNeededWh = Math.Max(0, energyNeededWh - solarForBatteries);
        }

        if (netEnergyNeededWh <= 0)
            return 0;

        // ── Réduction charge réseau si solaire arrive dans < 2h (Feature 3) ──
        // Si les prévisions Solcast intraday montrent une production significative
        // dans les prochaines heures, on réduit la charge réseau proportionnellement.
        // L'idée : ne pas charger 1000W depuis le réseau si 800Wh arrivent dans 1h30.
        //
        // Réduction proportionnelle : targetW × max(0, 1 - solarCoverage)
        //   où solarCoverage = ForecastNext3HoursWh / netEnergyNeededWh (clampé à 1)
        //
        // Exemple : netEnergyNeeded = 500Wh, next3h = 400Wh
        //   → solarCoverage = 0.80 → réduction = 80% → charge réseau = 20% de targetW
        //
        // La réduction ne s'applique QUE si les entités intraday sont configurées
        // ET si le solaire prévu dépasse le seuil MinSolarNext3HoursWhForGridReduction.
        // En urgence (hoursRemaining ≤ urgencyThresholdHours), pas de réduction.
        double intradaySolarReductionFactor = 1.0;

        if (tariff.HasIntradayForecast
            && tariff.ForecastNext3HoursWh.HasValue
            && tariff.ForecastNext3HoursWh.Value > 0
            && hoursRemaining > urgencyThresholdHours)
        {
            double next3hWh = tariff.ForecastNext3HoursWh.Value;

            if (next3hWh > 0 && netEnergyNeededWh > 0)
            {
                double solarCoverage = Math.Min(1.0, next3hWh / netEnergyNeededWh);
                // Réduction douce : on garde au minimum 30% de la charge pour l'urgence
                intradaySolarReductionFactor = Math.Max(0.30, 1.0 - solarCoverage * 0.7);
            }
        }
        // Calcule la durée minimale nécessaire pour charger à MaxChargeRateW,
        // puis vérifie si on a encore le temps d'attendre avant de démarrer.
        //
        // hoursNeeded   = énergie nette / puissance max
        // hoursBeforeStart = heures restantes - hoursNeeded - lazyBuffer
        //
        // Si hoursBeforeStart > 0 → on est encore trop tôt → retourner 0 (veille)
        // Si hoursBeforeStart ≤ 0 → il est temps de démarrer → puissance adaptative
        //
        // Exemple : slot HC 22h→7h (9h), batterie a besoin de 0.5h à 1000W.
        //   À 22h00 : hoursRemaining=9h, hoursNeeded=0.5h, lazyBuffer=0.5h
        //             → hoursBeforeStart = 9 - 0.5 - 0.5 = 8h → on attend
        //   À 06h00 : hoursRemaining=1h → ≤ urgencyThreshold → charge max (cas traité plus haut)
        //   À 05h30 : hoursRemaining=1.5h, hoursNeeded=0.5h, lazyBuffer=0.5h
        //             → hoursBeforeStart = 1.5 - 0.5 - 0.5 = 0.5h → encore positif → on attend
        //   À 06h00 : urgencyThreshold → charge max
        //
        // Le lazyBuffer est une marge de sécurité pour absorber les incertitudes
        // (SOC qui dérive, cycle BMS, légère sous-estimation de l'énergie nécessaire).
        double hoursNeeded = netEnergyNeededWh / b.MaxChargeRateW;
        double hoursBeforeStart = hoursRemaining - hoursNeeded - lazyBufferHours;

        if (hoursBeforeStart > 0)
            return 0; // Trop tôt — on attend, les batteries travaillent en self-powered

        // C'est l'heure de démarrer : puissance adaptative sur le temps qui reste
        double hoursToCharge = Math.Max(hoursNeeded, urgencyThresholdHours);
        double targetW = netEnergyNeededWh / hoursToCharge;

        // Appliquer la réduction intraday si le solaire arrive bientôt
        targetW *= intradaySolarReductionFactor;

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

        double totalCap = batteries.Sum(b => b.CapacityWh);

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
            TotalCapacityWh = (float)totalCap,
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

            // ML-7
            HoursRemainingInSlot = (float)(tariff.HoursRemainingInSlot ?? 0.0),
            HoursUntilSolarCapped = (float)Math.Min(tariff.HoursUntilSolar ?? 24.0, 24.0),
            WasEmergencySession = batteries.Any(b => b.IsEmergencyGridCharge) ? 1f : 0f,
            NormalizedGridChargeW = batteries.Any(b => b.GridChargeAllowedW > 0)
                ? (float)Math.Clamp(
                    batteries.Where(b => b.GridChargeAllowedW > 0).Average(b => b.GridChargeAllowedW)
                    / Math.Max(1, batteries.Average(b => b.MaxChargeRateW)), 0, 1)
                : 0f,

            // ML-8: HA forecasts — normalisés par capacité totale pour être sans dimension
            ForecastTodayNormalized = totalCap > 0 && tariff.ForecastTodayWh.HasValue
                ? (float)Math.Clamp(tariff.ForecastTodayWh.Value / totalCap, 0, 5) : 0f,
            ForecastTomorrowNormalized = totalCap > 0 && tariff.ForecastTomorrowWh.HasValue
                ? (float)Math.Clamp(tariff.ForecastTomorrowWh.Value / totalCap, 0, 5) : 0f,
            HasHaForecast = tariff.HasHaForecast ? 1f : 0f,

            // ML-9: ratio J+1 / J — encode la tendance solaire
            // > 1 : demain meilleur → moins urgent de charger maintenant
            // < 1 : demain pire    → préserver / charger plus fort ce soir
            // = 0 : données absentes (HasHaForecast = 0 dans ce cas)
            ForecastRatioTomorrowVsToday = tariff.ForecastTodayWh.HasValue
                && tariff.ForecastTodayWh.Value > 0
                && tariff.ForecastTomorrowWh.HasValue
                ? (float)Math.Clamp(tariff.ForecastTomorrowWh.Value / tariff.ForecastTodayWh.Value, 0, 3)
                : 1f,

            // ML-9: signal explicite "le blocage de la charge réseau vient du forecast HA"
            // Permet au ML de différencier "soleil Open-Meteo" vs "soleil Solcast précis"
            SolarBlockedByHaForecast = tariff.SolarExpectedFromHa ? 1f : 0f,

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

    /// <summary>
    /// Calcule la fraction d'énergie solaire produite entre [startH, endH]
    /// en supposant un profil sinusoïdal normalisé sur [sunriseH, sunsetH].
    ///
    /// Production solaire ≈ sin(π × (t - sunrise) / daylightDuration)
    /// → L'intégrale sur [a, b] normalisée vaut (cos(πa/D) - cos(πb/D)) / 2
    ///   avec D = daylightDuration, a/b = décalages par rapport au lever.
    ///
    /// Avantage vs fraction linéaire : correctement pondère le pic de midi
    /// (les 4h centrales représentent ~60% de l'énergie journalière).
    /// </summary>
    private static double SolarFractionBetweenHours(
        double startH, double endH, double sunriseH, double sunsetH)
    {
        double duration = sunsetH - sunriseH;
        if (duration <= 0 || endH <= startH) return 0;

        // Clamp dans la fenêtre solaire
        double a = Math.Max(0, startH - sunriseH);
        double b = Math.Min(duration, endH - sunriseH);
        if (b <= a) return 0;

        // Intégrale de sin(π×t/D) entre a et b, normalisée sur [0, D] (intégrale totale = 2D/π)
        double integralTotal = 2.0 * duration / Math.PI;
        double integralSlice = (duration / Math.PI)
            * (Math.Cos(Math.PI * a / duration) - Math.Cos(Math.PI * b / duration));

        return integralTotal > 0 ? Math.Max(0, integralSlice / integralTotal) : 0;
    }

    private void LogTariffContext(TariffContext ctx, double surplusW)
    {
        if (!ctx.CurrentPricePerKwh.HasValue) return;

        string slotInfo = ctx.HoursRemainingInSlot.HasValue
            ? $" | slot ends in {ctx.HoursRemainingInSlot.Value:F1}h" : string.Empty;
        string solarInfo = ctx.HoursUntilSolar.HasValue && ctx.HoursUntilSolar.Value < double.MaxValue
            ? $" | solar in {ctx.HoursUntilSolar.Value:F1}h" : " | no solar forecast";
        string fcInfo = ctx.HasHaForecast
            ? $" | HA fc today={ctx.ForecastTodayWh:F0}Wh tmrw={ctx.ForecastTomorrowWh:F0}Wh"
            : " | Open-Meteo only";
        string haBlockInfo = ctx.SolarExpectedFromHa ? " [blocked by HA forecast]" : string.Empty;
        string eveningBoostInfo = ctx.HasLowForecastTomorrow && ctx.IsFavorableForGrid
            ? $" | ⚡ softmax boost +{ctx.EveningBoostPercent:F0}% (low tmrw forecast + favorable tariff)"
            : string.Empty;
        string intradayInfo = ctx.HasIntradayForecast
            ? $" | Solcast next3h={ctx.ForecastNext3HoursWh:F0}Wh (rem={ctx.ForecastRemainingTodayWh:F0}Wh)"
            : string.Empty;
        string balanceInfo = ctx.EnergyDeficitTodayWh.HasValue
            ? $" | deficit={ctx.EnergyDeficitTodayWh:F0}Wh"
            : string.Empty;
        string balanceBlockInfo = ctx.GridChargeBlockedBySolarSufficiency
            ? " [BLOCKED: solar sufficient today]" : string.Empty;

        if (ctx.GridChargeAllowed)
            _logger.LogInformation(
                "Tariff [{Slot}] {Price:F3}€/kWh — GRID CHARGE ALLOWED{SlotInfo}{SolarInfo}{FcInfo}{EveningBoost}{Intraday}{Balance} " +
                "(surplus={S:F0}W, savings={Sav:F3}€/kWh)",
                ctx.ActiveSlotName, ctx.CurrentPricePerKwh, slotInfo, solarInfo, fcInfo, eveningBoostInfo,
                intradayInfo, balanceInfo, surplusW, ctx.MaxSavingsPerKwh);
        else if (ctx.IsFavorableForGrid)
            _logger.LogInformation(
                "Tariff [{Slot}] {Price:F3}€/kWh — GRID CHARGE BLOCKED{HaBlock}{BalanceBlock}{SlotInfo}{SolarInfo}{FcInfo}{Intraday}{Balance}",
                ctx.ActiveSlotName, ctx.CurrentPricePerKwh, haBlockInfo, balanceBlockInfo,
                slotInfo, solarInfo, fcInfo, intradayInfo, balanceInfo);
        else
            _logger.LogInformation(
                "Tariff [{Slot}] {Price:F3}€/kWh — HP grid charge not favorable{SlotInfo}",
                ctx.ActiveSlotName, ctx.CurrentPricePerKwh, slotInfo);
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