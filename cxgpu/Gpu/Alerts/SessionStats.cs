namespace cxgpu.Gpu.Alerts;

/// <summary>
/// What one GPU did over the session: the peaks, and how long it spent throttled.
///
/// Answers the question the live view cannot — the chips show what is happening NOW and vanish when it
/// clears, so "did it get hot while I was away?" needs something that remembers.
/// </summary>
internal sealed class GpuSessionStats
{
    public double PeakTemperatureC { get; private set; }
    public double PeakPowerWatts { get; private set; }
    public double PeakMemoryPercent { get; private set; }

    /// <summary>Total time spent throttled, accumulated across episodes.</summary>
    public TimeSpan ThrottledFor { get; private set; }

    /// <summary>How many critical events this card raised.</summary>
    public int CriticalEvents { get; private set; }

    /// <summary>
    /// Whether power was ever reported. A card with no sensor must show no peak-power row rather than
    /// a peak of zero — the same null-versus-zero rule the live views follow.
    /// </summary>
    public bool HasPower { get; private set; }

    internal void Observe(GpuSample gpu)
    {
        PeakTemperatureC = Math.Max(PeakTemperatureC, gpu.TemperatureC);
        PeakMemoryPercent = Math.Max(PeakMemoryPercent, gpu.MemoryUsedPercent);

        if (gpu.Caps.PowerLimit && gpu.PowerDrawWatts > 0)
        {
            HasPower = true;
            PeakPowerWatts = Math.Max(PeakPowerWatts, gpu.PowerDrawWatts);
        }
    }

    internal void AddThrottleTime(TimeSpan span) => ThrottledFor += span;

    internal void CountCritical() => CriticalEvents++;
}

/// <summary>
/// Session statistics for every GPU seen, keyed by index.
/// </summary>
internal sealed class SessionStats
{
    private readonly Dictionary<int, GpuSessionStats> _perGpu = new();
    private readonly Dictionary<int, DateTime> _throttleStartedAt = new();

    /// <summary>When the session began — the denominator for "throttled 4m of 2h14m".</summary>
    public DateTime StartedAt { get; }

    public SessionStats(DateTime startedAt) => StartedAt = startedAt;

    public TimeSpan Elapsed(DateTime now) => now - StartedAt;

    /// <summary>Stats for one GPU, or null when it has never been seen.</summary>
    public GpuSessionStats? For(int gpuIndex) =>
        _perGpu.TryGetValue(gpuIndex, out var s) ? s : null;

    public IReadOnlyDictionary<int, GpuSessionStats> All => _perGpu;

    /// <summary>Whether anything worth reporting happened — drives whether a summary prints at all.</summary>
    public bool AnythingHappened =>
        _perGpu.Values.Any(s => s.CriticalEvents > 0 || s.ThrottledFor > TimeSpan.Zero);

    /// <summary>Folds one snapshot's samples into the peaks.</summary>
    public void Observe(GpuSnapshot snapshot)
    {
        foreach (var gpu in snapshot.Gpus)
            Stats(gpu.Index).Observe(gpu);
    }

    /// <summary>
    /// Accumulates throttle time and critical counts from the engine's transitions.
    ///
    /// Driven by events rather than by sampling, so the total is the sum of actual episodes: counting
    /// ticks would over- or under-report depending on the refresh interval.
    /// </summary>
    public void Observe(AlertChanges changes, DateTime now)
    {
        foreach (var e in changes.Raised)
        {
            if (e.Severity == EventSeverity.Critical)
                Stats(e.GpuIndex).CountCritical();

            // Only the first raise of an episode starts the clock; an escalation raises a second event
            // for a condition already running, and starting again would lose the elapsed time.
            if (e.Metric == EventMetric.Throttle && !_throttleStartedAt.ContainsKey(e.GpuIndex))
                _throttleStartedAt[e.GpuIndex] = e.RaisedAt;
        }

        foreach (var e in changes.Resolved)
        {
            if (e.Metric != EventMetric.Throttle) continue;
            if (!_throttleStartedAt.Remove(e.GpuIndex, out var startedAt)) continue;

            Stats(e.GpuIndex).AddThrottleTime((e.ResolvedAt ?? now) - startedAt);
        }
    }

    /// <summary>
    /// Throttle time including any episode still running, so a card throttling right now does not
    /// report 0s until it recovers.
    /// </summary>
    public TimeSpan ThrottledFor(int gpuIndex, DateTime now)
    {
        var total = For(gpuIndex)?.ThrottledFor ?? TimeSpan.Zero;
        if (_throttleStartedAt.TryGetValue(gpuIndex, out var startedAt))
            total += now - startedAt;
        return total;
    }

    private GpuSessionStats Stats(int gpuIndex)
    {
        if (!_perGpu.TryGetValue(gpuIndex, out var s))
            _perGpu[gpuIndex] = s = new GpuSessionStats();
        return s;
    }
}
