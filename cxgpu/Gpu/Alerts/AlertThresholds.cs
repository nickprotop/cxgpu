namespace cxgpu.Gpu.Alerts;

/// <summary>
/// A warn/critical pair with the hysteresis margin that clears it.
/// </summary>
/// <param name="Warn">Value at or above which a Warning is raised.</param>
/// <param name="Critical">Value at or above which the event escalates to Critical.</param>
/// <param name="ClearMargin">
/// How far BELOW <paramref name="Warn"/> the value must fall before the event resolves. Without this
/// a card sitting exactly on the threshold flaps between raised and resolved on every tick, which at
/// the 2 s default refresh means an unusable event list.
/// </param>
internal sealed record ThresholdPair(double Warn, double Critical, double ClearMargin)
{
    /// <summary>Severity for a value, or null when it is below the warning level.</summary>
    public EventSeverity? SeverityFor(double value)
    {
        if (value >= Critical) return EventSeverity.Critical;
        if (value >= Warn) return EventSeverity.Warning;
        return null;
    }

    /// <summary>
    /// Whether an ACTIVE event should now resolve. Deliberately not the negation of
    /// <see cref="SeverityFor"/>: the value must drop past the margin, not merely below the warn line.
    /// </summary>
    public bool ShouldClear(double value) => value < Warn - ClearMargin;
}

/// <summary>
/// Per-card alert thresholds, resolved card → vendor → built-in default.
///
/// The temperature numbers are JUDGEMENT CALLS, not hardware readings. GpuDeviceInfo.TemperatureLimitC
/// is hardcoded 0 in every backend, and nvidia-smi's temperature.gpu.tlimit reads [N/A] on the RTX
/// 3090 tested here — so there is no per-card limit to derive from, and these come from published
/// throttle behaviour instead. They will be wrong for some card, which is exactly why per-card
/// override is the headline feature rather than an afterthought.
///
/// Power needs no constants: it is a percentage of the cap the card actually reports.
/// </summary>
internal sealed record AlertThresholds(
    ThresholdPair? TemperatureC,
    ThresholdPair? MemoryPercent,
    ThresholdPair? PowerPercent)
{
    /// <summary>
    /// NVIDIA consumer parts (GeForce). GA102 begins backing off around 83°C; sustained 90°C+ is real
    /// boost loss. Deliberately ABOVE UIConstants.ThresholdColor's old global 85, which fired on a
    /// 3090 doing ordinary work.
    /// </summary>
    public static readonly AlertThresholds NvidiaConsumer = new(
        TemperatureC: new ThresholdPair(83, 89, ClearMargin: 3),
        MemoryPercent: new ThresholdPair(90, 97, ClearMargin: 5),
        PowerPercent: new ThresholdPair(95, 100, ClearMargin: 5));

    /// <summary>NVIDIA workstation/datacentre parts, rated for a higher sustained envelope.</summary>
    public static readonly AlertThresholds NvidiaWorkstation = NvidiaConsumer with
    {
        TemperatureC = new ThresholdPair(85, 92, ClearMargin: 3)
    };

    /// <summary>
    /// AMD discrete. RDNA junction temperatures run hotter by design and the hardware trip is ~110°C,
    /// so warning at 90 rather than 83 avoids crying wolf on normal operation.
    /// </summary>
    public static readonly AlertThresholds AmdDiscrete = NvidiaConsumer with
    {
        TemperatureC = new ThresholdPair(90, 100, ClearMargin: 4)
    };

    /// <summary>An APU shares the CPU package's thermal budget, so it runs cooler than a discrete part.</summary>
    public static readonly AlertThresholds AmdApu = NvidiaConsumer with
    {
        TemperatureC = new ThresholdPair(85, 95, ClearMargin: 4)
    };

    /// <summary>
    /// Picks defaults for a card. Vendor comes from the registry-stamped Backend; the name distinguishes
    /// consumer from workstation parts, which have materially different thermal envelopes.
    /// </summary>
    public static AlertThresholds DefaultFor(string backend, string name)
    {
        if (backend.Contains("amd", StringComparison.OrdinalIgnoreCase))
        {
            // An APU reports no fan and no power cap; its name carries neither "Radeon RX" nor a
            // discrete part number. Treated as the cooler-running case when in doubt, since an APU
            // alerting late is worse than a discrete card alerting early.
            bool discrete = name.Contains("Radeon RX", StringComparison.OrdinalIgnoreCase)
                         || name.Contains("Instinct", StringComparison.OrdinalIgnoreCase);
            return discrete ? AmdDiscrete : AmdApu;
        }

        bool workstation = name.Contains("RTX A", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("Quadro", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("Tesla", StringComparison.OrdinalIgnoreCase)
                        || name.Contains(" A100", StringComparison.OrdinalIgnoreCase)
                        || name.Contains(" H100", StringComparison.OrdinalIgnoreCase);

        return workstation ? NvidiaWorkstation : NvidiaConsumer;
    }
}
