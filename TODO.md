# SolarDistribution - TODO 2026 (Roadmap actionnable)

Objectif principal:
- Maximiser l'autoconsommation solaire.
- Reduire le cout total EUR/mois (import reseau + charge reseau) sans degrader la batterie.
- Rendre les decisions explicables, testables, et robustes.

---

## 0) Definition de succes (KPIs a suivre chaque jour)

- Autoconsommation (%)
- Autosuffisance (%)
- kWh importes reseau (hors charge batterie)
- kWh charges depuis reseau
- kWh surplus perdu
- EUR economises vs scenario baseline
- Nombre de cycles batterie et taux de sessions d'urgence

Done criteria:
- Les 7 KPIs sont exposes via API + dashboard HA.
- Un recap quotidien est disponible en DB et consultable sur 30 jours glissants.

---

## 1) Priorite immediate (Semaine 1)

### 1.1 API de simulation (sans envoi HA)

But:
- Tester un reglage sans attendre un vrai cycle.

Taches:
- [ ] Ajouter `POST /api/simulate` avec payload: surplus, SOC batteries, contexte tarifaire, forecast court terme.
- [ ] Retourner: puissance cible par batterie, charge reseau autorisee/bloquee, raison de decision.
- [ ] Garantir zero effet de bord (pas de commande HA, pas d'ecriture DB session runtime).
- [ ] Ajouter tests unitaires et tests de contrat API.

Done criteria:
- 10 scenarios connus passent (normal, nuit, surplus nul, urgence, tarif cher, etc.).
- Le endpoint repond en < 150 ms localement.

Fichiers cibles:
- `SolarDistribution.Api/Controllers/DistributionController.cs`
- `SolarDistribution.Core/Services/SmartDistributionService.cs`
- `SolarDistribution.Tests/*Simulation*Tests.cs`

### 1.2 Simulation sur historique (scenario)

But:
- Comparer "config actuelle" vs "config candidate" sur N sessions passees.

Taches:
- [ ] Ajouter `POST /api/simulate/scenario`.
- [ ] Rejouer les N dernieres sessions depuis la DB.
- [ ] Retourner un diff: autoconsommation, import reseau, charge reseau, surplus perdu, cout estime.
- [ ] Ajouter mode "top 3 reglages recommandes" (variation de buffers/thresholds).

Done criteria:
- Rapport comparatif clair pour N=288 (24h a 5 min) sans timeout.

Fichiers cibles:
- `SolarDistribution.Api/Controllers/DistributionController.cs`
- `SolarDistribution.Core/Services/SmartDistributionService.cs`
- `SolarDistribution.Infrastructure/Repositories/DistributionRepository.cs`

---

## 2) Forte valeur economique (Semaine 2)

### 2.1 Prendre en compte le prix d'export

But:
- Eviter de charger "a tout prix" la batterie si l'export est remunere et plus rentable.

Taches:
- [ ] Integrer `export_price_per_kwh` dans le calcul de valeur marginale.
- [ ] Ajouter une regle: si surplus important et export rentable, reduire/stopper la charge forcee.
- [ ] Logger la comparaison economique (charge vs export).

Done criteria:
- Les logs expliquent clairement le choix economique.
- Au moins 3 tests de decision "export > charge" passent.

Fichiers cibles:
- `SolarDistribution.Core/Services/TariffEngine.cs`
- `SolarDistribution.Core/Services/SmartDistributionService.cs`

### 2.2 Strategie anti-pic reseau

But:
- Limiter les importations en pointe tarifaire.

Taches:
- [ ] Ajouter un plafond de puissance importable en periode chere.
- [ ] Prioriser couverture consommation maison avant remplissage batterie.
- [ ] Ajouter une logique "peak shaving" configurable.

Done criteria:
- Reduction mesurable des imports pendant heures cheres sur 7 jours.

Fichiers cibles:
- `SolarDistribution.Core/Services/SmartDistributionService.cs`
- `config/config.yaml`

---

## 3) Fiabilite et alerting (Semaine 3)

### 3.1 Alertes HA utiles (pas de spam)

Taches:
- [ ] Alerte si charge d'urgence >= 3 fois / 24h.
- [ ] Alerte watchdog si aucun ordre envoye > 2 x polling interval.
- [ ] Alerte si SOC moyen a 08:00 < min + 10.
- [ ] Ajouter `notify_service` en config.

Done criteria:
- Chaque alerte a un cooldown et une raison explicite.
- Tests unitaires sur les seuils et anti-spam.

Fichiers cibles:
- `SolarDistribution.Worker/Services/SolarWorker.cs`
- `SolarDistribution.Worker/Services/HomeAssistantCommandSender.cs`
- `SolarDistribution.Core/Models/SolarConfig.cs`

### 3.2 Mode degrade (HA/DB indisponible)

Taches:
- [ ] Si HA est indisponible: fallback decision locale conservative.
- [ ] Si DB indisponible: persister dernier etat en JSON local.
- [ ] Retry exponentiel + journal clair de la strategie active.

Done criteria:
- Le worker continue a prendre des decisions sures pendant 30 min d'indisponibilite.

Fichiers cibles:
- `SolarDistribution.Worker/Services/SolarWorker.cs`
- `SolarDistribution.Infrastructure/*`
- `README.md`

---

## 4) Qualite logicielle (Semaine 4)

### 4.1 Campagne de tests prioritaire

Taches:
- [ ] E2E 24h: verifier bilan energetique final.
- [ ] Regression: decisions stables sur jeu historique fige.
- [ ] Fuzz `BatteryDistributionService.Distribute()` (bornes extremes).
- [ ] Tests minuit/transition slots dans `TariffEngine`.

Done criteria:
- Couverture des services critiques >= 80%.
- Aucun test flaky sur 20 runs CI.

Fichiers cibles:
- `SolarDistribution.Tests/*.cs`

### 4.2 Documentation de configuration

Taches:
- [ ] Ajouter 3 profils config commentes: small, medium, large.
- [ ] Documenter "valeurs recommandees" par puissance installee.
- [ ] Ajouter `config.example.yaml` minimal (onboarding rapide).

Done criteria:
- Un nouvel utilisateur peut lancer le systeme en < 20 min.

Fichiers cibles:
- `README.md`
- `config/config.yaml`
- `config/config.example.yaml`

---

## 5) Backlog optimisation avancee (prochaine iteration)

- [ ] Optimisation multi-objectifs (cout + usure batterie + confort).
- [ ] Detection derive capteurs (drift) avec score de confiance par entite HA.
- [ ] Auto-tuning des seuils (`lazy_buffer_hours`, `surplus_buffer_w`) via Bayesian search offline.
- [ ] Pilotage charges flexibles (ECS, VE, PAC) selon surplus prevu et prix spot.
- [ ] Predicteur court terme consommation maison (15 min/1 h) par type de jour.

---

## 6) Nouveau bloc ML - Prechauffage thermique intelligent (inertie batiment)

Vision:
- Faire du chauffage "predictif" plutot que reactif.
- Atteindre la temperature voulue exactement au bon moment, au cout le plus bas.
- Exploiter les heures creuses, la meteo, l'occupation reelle et l'inertie de la maison.

### 6.1 Donnees a collecter (base ML chauffage)

Taches:
- [x] Ajouter les entites HA thermostat: temperature interieure, consigne, mode HVAC, etat chauffe ON/OFF.
- [x] Ajouter temperature exterieure, humidite, vent, ensoleillement et previsions meteo horaires.
- [x] Ajouter signaux presence: `home`, `away`, `sleep`, `near_home` (zone geofencing + capteurs presence).
- [x] Ajouter prix energie horaire (slot fixe ou spot) et indicateur heures creuses.
- [x] Persister un historique a pas fixe (5 min) pour entrainement et evaluation.

Done criteria:
- Dataset chauffage disponible sur au moins 21 jours sans trou majeur.

Fichiers cibles:
- `SolarDistribution.Core/Models/SolarConfig.cs`
- `SolarDistribution.Worker/Services/HomeAssistantDataReader.cs`
- `SolarDistribution.Infrastructure/Repositories/DistributionRepository.cs`
- `SolarDistribution.Infrastructure/Data/Entities/*`

### 6.2 Modele ML "Time-To-Target" (temps de prechauffe)

But:
- Predire "combien de minutes avant d'atteindre la temperature cible" selon contexte.

Taches:
- [x] Creer un modele de regression `MinutesToTargetTemperature`.
- [x] Features minimales: delta temperature, temperature exterieure, tendance meteo 3 h, mode HVAC, heure, jour, etat presence, historique recent ON/OFF.
- [x] Label: temps reel observe pour passer de `T_current` a `T_target`.
- [x] Ajouter intervalle de confiance (`p50`, `p90`) pour eviter un relancement trop tardif.
- [x] Re-entrainement periodique (quotidien ou hebdo) avec validation temporelle.

Done criteria:
- Erreur mediane <= 10 min sur semaine de validation.
- Erreur p90 <= 20 min en conditions normales.

Fichiers cibles:
- `SolarDistribution.Core/Services/ML/*`
- `SolarDistribution.Core/Services/HeatingPreheatMlService.cs` (nouveau)
- `SolarDistribution.Tests/*HeatingMl*Tests.cs`

### 6.3 Orchestrateur intelligent chauffage

Regles produit:
- Si mode `sleep` ou `away`, appliquer consigne reduite automatiquement.
- Si mode `near_home`, calculer l'heure optimale de relance pour arriver a la temperature de confort a l'arrivee.
- Si cout energie eleve et inertie suffisante, anticiper le prechauffage en heure creuse.
- Si forte hausse de temperature exterieure prevue, limiter le prechauffage inutile.

Taches:
- [ ] Ajouter un service `HeatingOrchestratorService` avec decision explicable.
- [ ] Integrer un score de cout previsionnel sur l'horizon 6-12 h.
- [ ] Integrer contraintes de confort (bornes min/max, anti-yo-yo, temps mini ON/OFF).
- [ ] Ajouter fallback heuristique si modele indisponible.

Done criteria:
- Le moteur retourne toujours une action explicable: `heat_now`, `delay_until`, `eco_hold`, `resume_comfort`.

Fichiers cibles:
- `SolarDistribution.Core/Services/HeatingOrchestratorService.cs` (nouveau)
- `SolarDistribution.Worker/Services/SolarWorker.cs`
- `SolarDistribution.Worker/Services/HomeAssistantCommandSender.cs`

### 6.4 API et observabilite chauffage

Taches:
- [ ] Ajouter `GET /api/heating/status/live` (mode actuel, T interieure, cible, ETA).
- [ ] Ajouter `POST /api/heating/simulate` (scenario sans commande HA).
- [ ] Ajouter `GET /api/heating/preheat-plan?arrival=` (heure de relance conseillee, cout estime).
- [ ] Ajouter logs metier lisibles: "Relance a 17:20 pour 20.5C a 18:00".

Done criteria:
- Dashboard HA avec ETA de chauffe et prochain evenement de relance.

Fichiers cibles:
- `SolarDistribution.Api/Controllers/DistributionController.cs`
- `SolarDistribution.Api/Controllers/HeatingController.cs` (nouveau)
- `README.md`

### 6.5 KPIs chauffage et mesure d'impact

KPIs:
- [ ] EUR/jour chauffage avant vs apres.
- [ ] Taux d'arrivee a l'heure a la temperature cible.
- [ ] Nb de surchauffes et sous-chauffes.
- [ ] Confort percu (proxy): temps passe hors plage de confort.
- [ ] Reduction de conso pendant `away` et `sleep`.

Done criteria:
- Baisse de 10-20% de la conso chauffage sur 4 semaines (a meteo comparable) sans perte de confort significative.

---

## Plan d'execution conseille

1. API simulation (`/simulate`, `/simulate/scenario`)
2. Export pricing + peak shaving
3. Alertes HA + mode degrade
4. Bloc ML chauffage (collecte + modele ETA + orchestrateur)
5. Tests E2E/regression
6. Documentation et config exemple

Pourquoi cet ordre:
- Tu reduis vite les risques de mauvais reglages.
- Tu captures du gain economique concret rapidement.
- Tu blindes ensuite la fiabilite et la maintenabilite.

---

## Notes de gouvernance

- Toute nouvelle regle de decision doit avoir:
  - [ ] une justification metier,
  - [ ] un log explicite,
  - [ ] au moins 2 tests (cas nominal + edge case).
- Toute option de config ajoutee doit etre documentee dans `README.md` et `config.example.yaml`.
