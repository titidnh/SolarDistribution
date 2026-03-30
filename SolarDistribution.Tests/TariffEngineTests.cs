using FluentAssertions;
using NUnit.Framework;
using SolarDistribution.Core.Services;

namespace SolarDistribution.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="TariffEngine"/>.
///
/// ISO 8601 day convention: 1=Monday 2=Tuesday 3=Wednesday 4=Thursday 5=Friday 6=Saturday 7=Sunday
/// </summary>
[TestFixture]
public class TariffEngineTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static TariffSlot Slot(
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
    /// Base config: HC 22:00-06:00 at 0.10 EUR, HP 06:00-22:00 at 0.25 EUR, charge threshold=0.15 EUR.
    /// No day filter -> active every day.
    /// </summary>
    private static TariffConfig BaseConfig() => new()
    {
        GridChargeThresholdPerKwh   = 0.15,
        ExportPricePerKwh           = 0.07,
        MinSolarForecastForGridBlock = 100.0,
        SolarForecastHorizonHours   = 4,
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
        engine.GetActiveSlot(new DateTime(2025, 6, 1, 12, 0, 0))!.Name.Should().Be("HP");
    }

    [Test]
    public void GetActiveSlot_AtMidnight_ReturnsHcSlot()
    {
        var engine = new TariffEngine(BaseConfig());
        engine.GetActiveSlot(new DateTime(2025, 6, 1, 0, 30, 0))!.Name.Should().Be("HC");
    }

    [Test]
    public void GetActiveSlot_NoSlots_ReturnsNull()
    {
        var engine = new TariffEngine(new TariffConfig());
        engine.GetActiveSlot(DateTime.Now).Should().BeNull();
    }

    [Test]
    public void GetActiveSlot_OverlapWithPriceDiff_LogsConflictAndReturnsCheapest()
    {
        var config = new TariffConfig
        {
            GridChargeThresholdPerKwh = 0.15,
            Slots = new()
            {
                Slot("Slot-A", 0.30, "10:00", "18:00"),
                Slot("Slot-B", 0.10, "12:00", "16:00"),
            }
        };

        var engine = new TariffEngine(config);
        var slot   = engine.GetActiveSlot(new DateTime(2025, 6, 1, 14, 0, 0));

        slot!.Name.Should().Be("Slot-B");
        engine.LastSlotConflict.Should().NotBeNullOrEmpty();
    }

    [Test]
    public void GetActiveSlot_OverlapWithNegligiblePriceDiff_NoConflictLogged()
    {
        var config = new TariffConfig
        {
            Slots = new()
            {
                Slot("A", 0.10000, "10:00", "18:00"),
                Slot("B", 0.10005, "12:00", "16:00"),
            }
        };

        var engine = new TariffEngine(config);
        engine.GetActiveSlot(new DateTime(2025, 6, 1, 14, 0, 0));
        engine.LastSlotConflict.Should().BeNull();
    }

    // ── ISO 8601 DaysOfWeek ───────────────────────────────────────────────────

    [Test]
    public void GetActiveSlot_IsoWeekend_Saturday_IsDay6()
    {
        // ISO Saturday = 6
        var config = new TariffConfig
        {
            Slots = new() { Slot("Week-end", 0.12, "00:00", "00:00", new[] { 6, 7 }) }
        };

        var engine   = new TariffEngine(config);
        // 2025-06-07 = Saturday
        var saturday = new DateTime(2025, 6, 7, 10, 0, 0);
        engine.GetActiveSlot(saturday)!.Name.Should().Be("Week-end");
    }

    [Test]
    public void GetActiveSlot_IsoWeekend_Sunday_IsDay7()
    {
        // ISO Sunday = 7
        var config = new TariffConfig
        {
            Slots = new() { Slot("Week-end", 0.12, "00:00", "00:00", new[] { 6, 7 }) }
        };

        var engine = new TariffEngine(config);
        // 2025-06-08 = Sunday
        var sunday = new DateTime(2025, 6, 8, 14, 0, 0);
        engine.GetActiveSlot(sunday)!.Name.Should().Be("Week-end");
    }

    [Test]
    public void GetActiveSlot_IsoWeekday_Monday_IsDay1()
    {
        var config = new TariffConfig
        {
            Slots = new()
            {
                Slot("HC Semaine", 0.10, "22:00", "06:00", new[] { 1, 2, 3, 4, 5 }),
                Slot("HP Semaine", 0.28, "06:00", "22:00", new[] { 1, 2, 3, 4, 5 }),
            }
        };

        var engine = new TariffEngine(config);
        // 2025-06-02 = Monday (ISO 1)
        var monday = new DateTime(2025, 6, 2, 14, 0, 0);
        engine.GetActiveSlot(monday)!.Name.Should().Be("HP Semaine");
    }

    [Test]
    public void GetActiveSlot_IsoWeekday_Friday_IsDay5()
    {
        var config = new TariffConfig
        {
            Slots = new()
            {
                Slot("HC Semaine", 0.10, "22:00", "06:00", new[] { 1, 2, 3, 4, 5 }),
                Slot("HP Semaine", 0.28, "06:00", "22:00", new[] { 1, 2, 3, 4, 5 }),
            }
        };

        var engine = new TariffEngine(config);
        // 2025-06-06 = Friday (ISO 5), 23:00 -> HC
        var fridayNight = new DateTime(2025, 6, 6, 23, 0, 0);
        engine.GetActiveSlot(fridayNight)!.Name.Should().Be("HC Semaine");
    }

    [Test]
    public void GetActiveSlot_WeekendSlot_ReturnsNullOnMonday()
    {
        var config = new TariffConfig
        {
            Slots = new() { Slot("Week-end", 0.05, "08:00", "20:00", new[] { 6, 7 }) }
        };

        var engine = new TariffEngine(config);
        // 2025-06-02 = Monday
        var monday = new DateTime(2025, 6, 2, 10, 0, 0);
        engine.GetActiveSlot(monday).Should().BeNull("the slot is only active on weekends");
    }

    [Test]
    public void GetActiveSlot_NoDaysFilter_ActiveAllWeek()
    {
        // Without days_of_week -> active every day
        var config = new TariffConfig
        {
            Slots = new() { Slot("Toujours", 0.20, "00:00", "00:00") }
        };

        var engine = new TariffEngine(config);

        // Test Monday(2), Wednesday(4), Saturday(7), Sunday(8) June 2025
        engine.GetActiveSlot(new DateTime(2025, 6, 2, 12, 0, 0))!.Name.Should().Be("Toujours");
        engine.GetActiveSlot(new DateTime(2025, 6, 4, 12, 0, 0))!.Name.Should().Be("Toujours");
        engine.GetActiveSlot(new DateTime(2025, 6, 7, 12, 0, 0))!.Name.Should().Be("Toujours");
        engine.GetActiveSlot(new DateTime(2025, 6, 8, 12, 0, 0))!.Name.Should().Be("Toujours");
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
        var config = new TariffConfig
        {
            GridChargeThresholdPerKwh = 0,
            Slots = new() { Slot("HC", 0.05, "22:00", "06:00") }
        };
        new TariffEngine(config)
            .IsGridChargeFavorable(new DateTime(2025, 6, 1, 23, 0, 0))
            .Should().BeFalse();
    }

    // ── HoursUntilNextFavorableTariff ─────────────────────────────────────────

    [Test]
    public void HoursUntilNextFavorable_AlreadyFavorable_ReturnsZero()
    {
        var engine = new TariffEngine(BaseConfig());
        engine.HoursUntilNextFavorableTariff(new DateTime(2025, 6, 1, 23, 0, 0))
              .Should().Be(0);
    }

    [Test]
    public void HoursUntilNextFavorable_DuringHP_ReturnsPositiveHours()
    {
        var engine = new TariffEngine(BaseConfig());
        // At 12:00, HC starts at 22:00 -> ~10h
        var result = engine.HoursUntilNextFavorableTariff(new DateTime(2025, 6, 1, 12, 0, 0));
        result.Should().NotBeNull();
        result!.Value.Should().BeApproximately(10.0, 0.5);
    }

    [Test]
    public void HoursUntilNextFavorable_NoFavorableSlot_ReturnsNull()
    {
        var config = new TariffConfig
        {
            GridChargeThresholdPerKwh = 0.05,
            Slots = new() { Slot("HP", 0.30, "00:00", "00:00") }
        };
        new TariffEngine(config)
            .HoursUntilNextFavorableTariff(DateTime.Now)
            .Should().BeNull();
    }

    // ── EvaluateContext ───────────────────────────────────────────────────────

    [Test]
    public void EvaluateContext_HC_NoSolar_GridChargeAllowed()
    {
        var engine = new TariffEngine(BaseConfig());
        var ctx    = engine.EvaluateContext(new DateTime(2025, 6, 1, 22, 30, 0), new double[12]);

        ctx.GridChargeAllowed.Should().BeTrue();
        ctx.IsFavorableForGrid.Should().BeTrue();
        ctx.SolarExpectedSoon.Should().BeFalse();
        ctx.ActiveSlotName.Should().Be("HC");
    }

    [Test]
    public void EvaluateContext_HC_WithSolarForecast_GridChargeBlocked()
    {
        var engine   = new TariffEngine(BaseConfig());
        var forecast = Enumerable.Repeat(200.0, 12).ToArray();
        var ctx      = engine.EvaluateContext(new DateTime(2025, 6, 1, 22, 30, 0), forecast);

        ctx.GridChargeAllowed.Should().BeFalse();
        ctx.SolarExpectedSoon.Should().BeTrue();
    }

    [Test]
    public void EvaluateContext_HP_GridChargeNotAllowed()
    {
        var engine = new TariffEngine(BaseConfig());
        var ctx    = engine.EvaluateContext(new DateTime(2025, 6, 1, 12, 0, 0), new double[12]);

        ctx.GridChargeAllowed.Should().BeFalse();
        ctx.IsFavorableForGrid.Should().BeFalse();
    }

    [Test]
    public void EvaluateContext_NoSlots_GridChargeNotAllowed()
    {
        var engine = new TariffEngine(new TariffConfig());
        var ctx    = engine.EvaluateContext(DateTime.Now, new double[12]);

        ctx.GridChargeAllowed.Should().BeFalse();
        ctx.CurrentPricePerKwh.Should().BeNull();
        ctx.NormalizedPrice.Should().Be(0.5);
    }

    [Test]
    public void EvaluateContext_MaxSavings_PositiveWhenOffPeakCheaperThanPeak()
    {
        var engine = new TariffEngine(BaseConfig());
        var ctx    = engine.EvaluateContext(new DateTime(2025, 6, 1, 23, 0, 0), new double[12]);
        ctx.MaxSavingsPerKwh.Should().BeApproximately(0.15, 0.01);
    }

    [Test]
    public void GetMinPriceNextHours_SpansHcAndHp_ReturnsHcPrice()
    {
        var engine = new TariffEngine(BaseConfig());
        var min    = engine.GetMinPriceNextHours(new DateTime(2025, 6, 1, 20, 0, 0), 12);
        min.Should().Be(0.10);
    }
}

// ── Weekend configuration tests (ISO) ───────────────────────────────────────

[TestFixture]
public class TariffEngineWeekendTests
{
    /// <summary>
    /// Realistic config with days_of_week in ISO convention.
    ///   Weekdays: HC 22:00->6:00 [1-5], HP 6:00->22:00 [1-5]
    ///   Weekend : fixed tariff all day [6,7]
    /// </summary>
    private static TariffConfig WeekendConfig() => new()
    {
        GridChargeThresholdPerKwh = 0.15,
        ExportPricePerKwh         = 0.07,
        Slots = new()
        {
            new TariffSlot { Name="HC Semaine", PricePerKwh=0.10, StartTime="22:00", EndTime="06:00", DaysOfWeek=new(){1,2,3,4,5} },
            new TariffSlot { Name="HP Semaine", PricePerKwh=0.28, StartTime="06:00", EndTime="22:00", DaysOfWeek=new(){1,2,3,4,5} },
            new TariffSlot { Name="Week-end",   PricePerKwh=0.12, StartTime="00:00", EndTime="00:00", DaysOfWeek=new(){6,7} },
        }
    };

    [Test]
    public void Saturday14h_ReturnsWeekendSlot()
    {
        // 2025-06-07 = Saturday (ISO 6)
        new TariffEngine(WeekendConfig())
            .GetActiveSlot(new DateTime(2025, 6, 7, 14, 0, 0))!
            .Name.Should().Be("Week-end");
    }

    [Test]
    public void Sunday03h_ReturnsWeekendSlot()
    {
        // 2025-06-08 = Sunday (ISO 7)
        new TariffEngine(WeekendConfig())
            .GetActiveSlot(new DateTime(2025, 6, 8, 3, 0, 0))!
            .Name.Should().Be("Week-end");
    }

    [Test]
    public void Monday23h_ReturnsHcSemaine()
    {
        // 2025-06-02 = Monday (ISO 1)
        new TariffEngine(WeekendConfig())
            .GetActiveSlot(new DateTime(2025, 6, 2, 23, 0, 0))!
            .Name.Should().Be("HC Semaine");
    }

    [Test]
    public void Friday14h_ReturnsHpSemaine()
    {
        // 2025-06-06 = Friday (ISO 5)
        new TariffEngine(WeekendConfig())
            .GetActiveSlot(new DateTime(2025, 6, 6, 14, 0, 0))!
            .Name.Should().Be("HP Semaine");
    }

    [Test]
    public void Saturday_GridChargeAllowed_WhenNoSolar()
    {
        // Weekend 0.12 < threshold 0.15 -> grid charging allowed
        var ctx = new TariffEngine(WeekendConfig())
            .EvaluateContext(new DateTime(2025, 6, 7, 10, 0, 0), new double[12]);

        ctx.GridChargeAllowed.Should().BeTrue();
        ctx.ActiveSlotName.Should().Be("Week-end");
    }

    [Test]
    public void NightWeekend_SeparateSlots_NoOverlap()
    {
        // Config with separate day/night weekend slots - no overlap
        var config = new TariffConfig
        {
            GridChargeThresholdPerKwh = 0.15,
            Slots = new()
            {
                new TariffSlot { Name="Week-end Jour", PricePerKwh=0.12, StartTime="06:00", EndTime="22:00", DaysOfWeek=new(){6,7} },
                new TariffSlot { Name="Week-end Nuit", PricePerKwh=0.07, StartTime="22:00", EndTime="06:00", DaysOfWeek=new(){5,6,7} },
            }
        };

        var engine = new TariffEngine(config);

        // Saturday 23:00 -> only the weekend night slot (day slot ends at 22:00)
        var slot = engine.GetActiveSlot(new DateTime(2025, 6, 7, 23, 0, 0));
        slot!.Name.Should().Be("Week-end Nuit");
        slot.PricePerKwh.Should().Be(0.07);
        engine.LastSlotConflict.Should().BeNull("only one active slot");
    }

    [Test]
    public void Sunday_NoWeekdaySlot_ReturnsWeekendOnly()
    {
        // ISO Sunday=7 must not match weekday slots [1-5]
        var ctx = new TariffEngine(WeekendConfig())
            .EvaluateContext(new DateTime(2025, 6, 8, 14, 0, 0), new double[12]);

        ctx.ActiveSlotName.Should().Be("Week-end");
    }
}
