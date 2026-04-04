namespace SolarDistribution.Core.Models;

/// <summary>
/// Represents a physical battery with its configuration and current state.
/// </summary>
public class Battery
{
    public int Id { get; set; }
    public double CapacityWh { get; set; }
    public double MaxChargeRateW { get; set; }
    public double MinPercent { get; set; }
    public double SoftMaxPercent { get; set; } = 80;
    public double HardMaxPercent { get; set; } = 100;
    public double CurrentPercent { get; set; }
    public int Priority { get; set; }

    /// <summary>
    /// Maximum power allowed from the grid for this battery (W).
    ///   0  → grid charging forbidden (solar surplus only).
    ///   >0 → grid charging allowed (off-peak hours or SOC emergency).
    /// Computed by SmartDistributionService based on the tariff context.
    /// </summary>
    public double GridChargeAllowedW { get; set; } = 0;

    /// <summary>
    /// Idle charge power sent to the battery once its target is reached (W).
    ///
    /// When the battery has absorbed its surplus (SoftMax or HardMax reached),
    /// instead of sending 0 W, IdleChargeW is sent to:
    ///   • Avoid on/off cycling of some BMS devices
    ///   • Signal to the inverter that charging is still authorised
    ///   • Absorb residual micro-surpluses (P1 meter noise)
    ///
    /// Default 0 W (standard behaviour: cut at target).
    /// Configured via BatteryConfig.IdleChargeW (config default = 100 W).
    /// </summary>
    public double IdleChargeW { get; set; } = 0;

    /// <summary>
    /// Estimated passive self-discharge (% SOC lost per hour) used when deciding
    /// whether preventive off-peak charging is needed before meaningful solar arrives.
    ///
    /// Default 0 → conservative behaviour: no decay is assumed while waiting.
    /// Configured via BatteryConfig.SelfDischargePercentPerHour.
    /// </summary>
    public double SelfDischargePercentPerHour { get; set; } = 0.0;

    /// <summary>
    /// Optional preventive-charge mode for batteries where only the remaining
    /// percentage matters before solar returns.
    ///
    /// False (default): preventive off-peak charge is allowed only if the projected
    /// SOC before meaningful solar drops below MinPercent.
    /// True: preventive off-peak charge is allowed only if the projected SOC before
    /// meaningful solar reaches 0%.
    /// </summary>
    public bool PreventiveChargeOnlyIfEmptyBeforeSolar { get; set; } = false;

    /// <summary>
    /// Minimum power below which the battery does not accept charging (W).
    ///
    /// Hardware constraint: some batteries (e.g. EcoFlow Delta) refuse or ignore
    /// any setpoint below this threshold. Sending 50 W to a battery whose minimum
    /// is 100 W produces no real charge — the command is silently ignored.
    ///
    /// Impact on distribution:
    ///   · PASS 1/2 (solar surplus): if surplusW &lt; HardwareMinChargeW, the battery
    ///     is skipped — the surplus is not enough to exceed the hardware threshold.
    ///   · IdleCharge (POST-DISTRIBUTION): same guard — replaces the old condition
    ///     surplusW >= IdleChargeW (Bug #5) which was an imperfect proxy.
    ///   · Emergency grid charge: ignores HardwareMinChargeW — the battery must
    ///     always charge regardless of the available power.
    ///   · Grid charge HC (PASS 3): GridChargeAllowedW is already computed ≥ MinChargeRateW
    ///     by ComputeAdaptiveGridChargeW — no additional guard needed.
    ///
    /// Default 0 → disabled (original behaviour, no minimum threshold).
    /// Configured via BatteryConfig.HardwareMinChargeW.
    /// </summary>
    public double HardwareMinChargeW { get; set; } = 0;
    ///
    /// Avoids micro-commands of 1-3 W generated when the battery oscillates just
    /// below its target due to EcoFlow self-powered self-discharge (~1-2%/h).
    ///
    /// With SocHysteresisPercent = 2:
    ///   · Target 90% → grid charging only allowed if SOC &lt; 88%
    ///   · Between 88% and 90%: no grid charging (self-discharge accepted)
    ///   · SOC drops to 87.9% → normal grid charging (≥ 100 W)
    ///
    /// Default 0 → disabled, original behaviour.
    /// Propagated from BatteryConfig.SocHysteresisPercent.
    /// </summary>
    public double SocHysteresisPercent { get; set; } = 0.0;

    public double? EmergencyGridChargeTargetPercent { get; set; }
    public double? EmergencyGridChargeBelowPercent { get; set; }
    public bool IsEmergencyGridCharge { get; set; } = false;
    public bool IsWaitingForMeaningfulSolar { get; set; } = false;
    public bool IsPreventiveGridChargeSkippedUntilSolar { get; set; } = false;
    public double? HoursUntilMeaningfulSolar { get; set; }
    public double? ProjectedPercentAtMeaningfulSolar { get; set; }
    public double? PreventiveChargeFloorPercent { get; set; }
    public double? FleetReserveAboveEmergencyWh { get; set; }
    public double? ExpectedLoadBeforeMeaningfulSolarWh { get; set; }

    // ── ML-8 : cycle de vie ───────────────────────────────────────────────────

    /// <summary>
    /// Number of full charge cycles read from HA via CycleCountEntity.
    /// 0 if the entity is not configured or if the read failed.
    /// </summary>
    public int CycleCount { get; set; } = 0;

    /// <summary>
    /// Priority reduction factor per cycle (from BatteryConfig.CycleAgingFactor).
    /// 0 = disabled (no cycle-based weighting).
    /// </summary>
    public double CycleAgingFactor { get; set; } = 0.0001;

    // ── Computed ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Effective priority taking into account both SOC urgency AND cycle aging.
    ///
    /// Urgency rule (unchanged):
    ///   SOC &lt; MinPercent → EffectivePriority = 0 (URGENT, always first)
    ///
    /// Cycle-based weighting (ML-8):
    ///   effectivePriority = basePriority × (1 − CycleAgingFactor × CycleCount)
    ///   clamped to [basePriority × 0.5, basePriority] to stay within reasonable bounds.
    ///
    /// Impact on distribution:
    ///   Less-cycled (newer) batteries have a lower numeric priority
    ///   (reminder: sorted ASC → priority 0 = first). Aged batteries, whose
    ///   EffectivePriority approaches Priority (with no reduction), are processed later.
    ///   Effect: solar surplus goes to the freshest batteries first.
    /// </summary>
    public double EffectivePriority
    {
        get
        {
            if (CurrentPercent < MinPercent) return 0; // emergency always first

            if (CycleAgingFactor <= 0 || CycleCount <= 0)
                return Priority;

            // Priority reduction proportional to cycles
            double reduction = CycleAgingFactor * CycleCount;
            double aged = Priority * (1.0 - reduction);
            // Clamp: max 50% reduction to avoid a too-abrupt inversion
            return Math.Clamp(aged, Priority * 0.5, Priority);
        }
    }

    public bool IsUrgent => CurrentPercent < MinPercent;

    public double SpaceToSoftMaxWh =>
        Math.Max(0, (SoftMaxPercent - CurrentPercent) / 100.0 * CapacityWh);

    public double SpaceToHardMaxWh =>
        Math.Max(0, (HardMaxPercent - CurrentPercent) / 100.0 * CapacityWh);
}