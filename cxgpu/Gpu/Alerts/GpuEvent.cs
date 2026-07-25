namespace cxgpu.Gpu.Alerts;

/// <summary>How serious an event is. Ordered so the worst of a set is <c>Max()</c>.</summary>
internal enum EventSeverity
{
    Warning = 1,
    Critical = 2
}

/// <summary>
/// Where an event's truth comes from — the distinction that keeps the app from having two competing
/// notions of "throttling".
/// </summary>
internal enum EventSource
{
    /// <summary>
    /// A driver-emitted fact: the hardware or driver states that clocks were cut. Authoritative, and
    /// already debounced by the driver.
    /// </summary>
    Reported,

    /// <summary>
    /// Our own threshold crossing. An inference about when a card is unhappy, not a statement that
    /// anything was actually throttled.
    /// </summary>
    Derived
}

/// <summary>
/// The metric an event is about. Used as part of the state key, so one card can be simultaneously
/// warning on temperature and critical on memory without the two interfering.
/// </summary>
internal enum EventMetric
{
    Temperature,
    Memory,
    Power,
    Throttle
}

/// <summary>
/// One thing that happened to one GPU.
///
/// Records the transition, not the sample: an event is raised when a condition BEGINS and resolved
/// when it ends, which is why <see cref="PeakValue"/> exists — the value at the moment of crossing is
/// rarely the interesting one.
/// </summary>
internal sealed record GpuEvent
{
    public required int GpuIndex { get; init; }

    /// <summary>Stable per-card id (PCI address) where the backend reports one; "" otherwise.</summary>
    public required string CardId { get; init; }

    public required EventMetric Metric { get; init; }
    public required EventSource Source { get; init; }
    public required EventSeverity Severity { get; init; }

    /// <summary>Human-readable cause, e.g. "thermal" or "VRAM 97%".</summary>
    public required string Description { get; init; }

    public required DateTime RaisedAt { get; init; }

    /// <summary>Null while the condition is still active.</summary>
    public DateTime? ResolvedAt { get; init; }

    /// <summary>Value at the moment the condition began.</summary>
    public double Value { get; init; }

    /// <summary>Worst value seen while the condition has been active.</summary>
    public double PeakValue { get; init; }

    public bool IsActive => ResolvedAt == null;

    /// <summary>How long the condition lasted, or has lasted so far.</summary>
    public TimeSpan Duration(DateTime now) => (ResolvedAt ?? now) - RaisedAt;

    /// <summary>
    /// Identity of the CONDITION, not of this record: one per (card, metric). Used to find the active
    /// event a new sample should update or resolve, and to keep at most one sticky toast per
    /// condition per card.
    /// </summary>
    public (int GpuIndex, EventMetric Metric) Key => (GpuIndex, Metric);
}
