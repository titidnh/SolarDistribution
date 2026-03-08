using FluentAssertions;
using NUnit.Framework;
using SolarDistribution.Core.Services;

namespace SolarDistribution.Tests.Unit;

/// <summary>
/// Tests unitaires pour <see cref="TariffEngine"/>.
///
/// Couvre :
///   - Sélection du slot actif (heure simple, chevauchement, minuit, aucun slot)
///   - IsGridChargeFavorable (seuil, pas de slots)
///   - HoursUntilNextFavorableTariff (déjà favorable, dans X heures, aucun)
///   - EvaluateContext (grid charge allowed / bloqué, soleil prévu)
///   - LastSlotConflict — overlap avec écart de prix > 1ct
/// </summary>
[TestFixture]
public class TariffEngineTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static TariffEngine.TariffSlot Slot(
        string name, double price, string start, string end, int[]? days = null) =>
        new()
        {
            Name        = name,
            PricePerKwh = price,
            StartTime   = start,
            EndTime     = end,
            DaysOfWeek  = days?.ToList()
        };

    /// <summary>
    /// Config de base : HC 22h-06h à 0.10 €, HP 06h-22h à 0.25 €, seuil charge=0.15 €.
    /// </summary>
    private static TariffEngine.TariffConfig BaseConfig() => new()
    {
        GridChargeThresholdPerKwh  = 0.15,
        ExportPricePerKwh          = 0.07,
        MinSolarForecastForGridBlock = 100.0,
        SolarForecastHorizonHours  = 4,
        Slots = new()
        {
            Slot("HC", 0.10, "22:00", "06:00"),
            Slot("HP", 0.25, "06:00", "22:00"),
        }
    };

    // ── GetActiveSlot ─────────────────────────────────────────────────────────

    [Test]
    public void GetActiveSlot_DuringOffPeak_ReturnsHcSlot()
    {
        var engine = new TariffEngine(BaseConfig());
        var slot   = engine.GetActiveSlot(new DateTime(2025, 6, 1, 23, 0, 0));

        slot.Should().NotBeNull();
        slot!.Name.Should().Be("HC");
        slot.PricePerKwh.Should().Be(0.10);
    }

    [Test]
    public void GetActiveSlot_DuringPeak_ReturnsHpSlot()
    {
        var engine = new TariffEngine(BaseConfig());
        var slot   = engine.GetActiveSlot(new DateTime(2025, 6, 1, 12, 0, 0));

        slot!.Name.Should().Be("HP");
    }

    [Test]
    public void GetActiveSlot_AtMidnight_ReturnsHcSlot()
    {
        var engine = new TariffEngine(BaseConfig());
        var slot   = engine.GetActiveSlot(new DateTime(2025, 6, 1, 0, 30, 0));

        slot!.Name.Should().Be("HC");
    }

    [Test]
    public void GetActiveSlot_NoSlots_ReturnsNull()
    {
        var engine = new TariffEngine(new TariffEngine.TariffConfig());
        engine.GetActiveSlot(DateTime.Now).Should().BeNull();
    }

    [Test]
    public void GetActiveSlot_OverlapWithPriceDiff_LogsConflictAndReturnsCheapest()
    {
        // Deux slots qui se chevauchent à 14h avec des prix très différents
        var config = new TariffEngine.TariffConfig
        {
            GridChargeThresholdPerKwh = 0.15,
            Slots = new()
            {
                Slot("Slot-A", 0.30, "10:00", "18:00"),
                Slot("Slot-B", 0.10, "12:00", "16:00"),  // overlap 12h-16h
            }
        };

        var engine = new TariffEngine(config);
        var slot   = engine.GetActiveSlot(new DateTime(2025, 6, 1, 14, 0, 0));

        // Comportement conservateur : on retourne le slot le moins cher
        slot!.Name.Should().Be("Slot-B");
        engine.LastSlotConflict.Should().NotBeNullOrEmpty("un conflit devrait être enregistré");
    }

    [Test]
    public void GetActiveSlot_OverlapWithNegligiblePriceDiff_NoConflictLogged()
    {
        // Overlap avec prix identiques (diff < 1ct) — pas de conflit logué
        var config = new TariffEngine.TariffConfig
        {
            Slots = new()
            {
                Slot("A", 0.10000, "10:00", "18:00"),
                Slot("B", 0.10005, "12:00", "16:00"),
            }
        };

        var engine = new TariffEngine(config);
        engine.GetActiveSlot(new DateTime(2025, 6, 1, 14, 0, 0));

        engine.LastSlotConflict.Should().BeNull("écart < 1ct → pas de conflit");
    }

    [Test]
    public void GetActiveSlot_DayOfWeekFilter_ReturnsNullOnWrongDay()
    {
        // Slot uniquement le weekend (6=Samedi, 0=Dimanche)
        var config = new TariffEngine.TariffConfig
        {
            Slots = new() { Slot("Weekend", 0.05, "08:00", "20:00", new[] { 0, 6 }) }
        };

        var engine = new TariffEngine(config);

        // Lundi = DayOfWeek 1
        var monday = new DateTime(2025, 6, 2, 10, 0, 0); // lundi
        engine.GetActiveSlot(monday).Should().BeNull("le slot n'est actif que le weekend");

        // Samedi = DayOfWeek 6
        var saturday = new DateTime(2025, 6, 7, 10, 0, 0);
        engine.GetActiveSlot(saturday)!.Name.Should().Be("Weekend");
    }

    // ── IsGridChargeFavorable ─────────────────────────────────────────────────

    [Test]
    public void IsGridChargeFavorable_DuringHC_ReturnsTrue()
    {
        var engine = new TariffEngine(BaseConfig());
        engine.IsGridChargeFavorable(new DateTime(2025, 6, 1, 23, 0, 0)).Should().BeTrue();
    }

    [Test]
    public void IsGridChargeFavorable_DuringHP_ReturnsFalse()
    {
        var engine = new TariffEngine(BaseConfig());
        engine.IsGridChargeFavorable(new DateTime(2025, 6, 1, 12, 0, 0)).Should().BeFalse();
    }

    [Test]
    public void IsGridChargeFavorable_ZeroThreshold_AlwaysFalse()
    {
        var config = new TariffEngine.TariffConfig
        {
            GridChargeThresholdPerKwh = 0,
            Slots = new() { Slot("HC", 0.05, "22:00", "06:00") }
        };

        var engine = new TariffEngine(config);
        engine.IsGridChargeFavorable(new DateTime(2025, 6, 1, 23, 0, 0)).Should().BeFalse();
    }

    // ── HoursUntilNextFavorableTariff ─────────────────────────────────────────

    [Test]
    public void HoursUntilNextFavorable_AlreadyFavorable_ReturnsZero()
    {
        var engine = new TariffEngine(BaseConfig());
        var result = engine.HoursUntilNextFavorableTariff(new DateTime(2025, 6, 1, 23, 0, 0));

        result.Should().Be(0);
    }

    [Test]
    public void HoursUntilNextFavorable_DuringHP_ReturnPositiveHours()
    {
        var engine = new TariffEngine(BaseConfig());
        // À 12h, HC commence à 22h → environ 10h
        var result = engine.HoursUntilNextFavorableTariff(new DateTime(2025, 6, 1, 12, 0, 0));

        result.Should().NotBeNull();
        result!.Value.Should().BeApproximately(10.0, 0.5);
    }

    [Test]
    public void HoursUntilNextFavorable_NoFavorableSlot_ReturnsNull()
    {
        var config = new TariffEngine.TariffConfig
        {
            GridChargeThresholdPerKwh = 0.05,
            Slots = new() { Slot("HP", 0.30, "00:00", "00:00") }  // toujours 0.30 > seuil 0.05
        };

        var engine = new TariffEngine(config);
        engine.HoursUntilNextFavorableTariff(DateTime.Now).Should().BeNull();
    }

    // ── EvaluateContext ───────────────────────────────────────────────────────

    [Test]
    public void EvaluateContext_HC_NoSolar_GridChargeAllowed()
    {
        var engine  = new TariffEngine(BaseConfig());
        var at22h   = new DateTime(2025, 6, 1, 22, 30, 0);
        var ctx     = engine.EvaluateContext(at22h, new double[12]); // pas de soleil

        ctx.GridChargeAllowed.Should().BeTrue();
        ctx.IsFavorableForGrid.Should().BeTrue();
        ctx.SolarExpectedSoon.Should().BeFalse();
        ctx.ActiveSlotName.Should().Be("HC");
    }

    [Test]
    public void EvaluateContext_HC_WithSolarForecast_GridChargeBlocked()
    {
        var engine  = new TariffEngine(BaseConfig());
        var at22h   = new DateTime(2025, 6, 1, 22, 30, 0);
        // Prévision solaire > 100 W/m² → soleil attendu → charge réseau bloquée
        var forecast = Enumerable.Repeat(200.0, 12).ToArray();
        var ctx      = engine.EvaluateContext(at22h, forecast);

        ctx.GridChargeAllowed.Should().BeFalse("soleil prévu → pas besoin du réseau");
        ctx.SolarExpectedSoon.Should().BeTrue();
    }

    [Test]
    public void EvaluateContext_HP_GridChargeNotAllowed()
    {
        var engine = new TariffEngine(BaseConfig());
        var at12h  = new DateTime(2025, 6, 1, 12, 0, 0);
        var ctx    = engine.EvaluateContext(at12h, new double[12]);

        ctx.GridChargeAllowed.Should().BeFalse();
        ctx.IsFavorableForGrid.Should().BeFalse();
    }

    [Test]
    public void EvaluateContext_NoSlots_GridChargeNotAllowed()
    {
        var engine = new TariffEngine(new TariffEngine.TariffConfig());
        var ctx    = engine.EvaluateContext(DateTime.Now, new double[12]);

        ctx.GridChargeAllowed.Should().BeFalse();
        ctx.CurrentPricePerKwh.Should().BeNull();
        ctx.NormalizedPrice.Should().Be(0.5, "valeur neutre si inconnu");
    }

    [Test]
    public void EvaluateContext_MaxSavings_IsPositiveWhenOffPeakCheaperThanPeak()
    {
        var engine = new TariffEngine(BaseConfig());
        // En HC (0.10€) avec HP à venir (0.25€) → savings = 0.15
        var ctx = engine.EvaluateContext(new DateTime(2025, 6, 1, 23, 0, 0), new double[12]);

        ctx.MaxSavingsPerKwh.Should().BeApproximately(0.15, 0.01);
    }

    // ── GetMinPriceNextHours ──────────────────────────────────────────────────

    [Test]
    public void GetMinPriceNextHours_SpansHcAndHp_ReturnsHcPrice()
    {
        var engine = new TariffEngine(BaseConfig());
        // À 20h : les 12 prochaines heures couvrent HP (0.25) ET HC (0.10)
        var min = engine.GetMinPriceNextHours(new DateTime(2025, 6, 1, 20, 0, 0), 12);

        min.Should().Be(0.10);
    }
}
