using cxgpu.Helpers;

namespace cxgpu.Gpu.Alerts;

/// <summary>
/// Turns a stream of snapshots into a list of events.
///
/// Pure with respect to the UI: it takes samples and returns state, holds no framework references, and
/// is driven entirely by <see cref="Evaluate"/>. Time is a parameter rather than DateTime.Now so the
/// behaviour that matters — edge-triggering, hysteresis, durations — is testable without sleeping.
///
/// TWO SOURCES, ONE STREAM. Driver-reported throttles and our own threshold crossings both become
/// <see cref="GpuEvent"/>s, so the throttle chips and this list cannot disagree. The engine never
/// recomputes what the driver already states: where a card reports its own power cap, the derived
/// power threshold is suppressed for that card rather than firing a second event for one condition.
/// </summary>
internal sealed class AlertEngine
{
    private readonly Dictionary<(int, EventMetric), GpuEvent> _active = new();
    private readonly List<GpuEvent> _history = new();
    private readonly Func<GpuDeviceInfo, AlertThresholds> _thresholdsFor;

    /// <summary>
    /// Bounded so a process left running for days cannot grow this without limit. Oldest resolved
    /// events are dropped first; active ones are never dropped, since dropping a live condition would
    /// make the badge count disagree with the list.
    /// </summary>
    public const int MaxHistory = 200;

    public AlertEngine(Func<GpuDeviceInfo, AlertThresholds>? thresholdsFor = null)
    {
        _thresholdsFor = thresholdsFor
            ?? (info => AlertThresholds.DefaultFor(info.Backend, info.Name));
    }

    /// <summary>Currently-active events, worst first.</summary>
    public IReadOnlyList<GpuEvent> Active =>
        _active.Values.OrderByDescending(e => e.Severity).ThenBy(e => e.RaisedAt).ToList();

    /// <summary>Everything raised this session, newest first — active and resolved.</summary>
    public IReadOnlyList<GpuEvent> History =>
        _history.OrderByDescending(e => e.RaisedAt).ToList();

    /// <summary>The worst active severity, or null when nothing is active.</summary>
    public EventSeverity? WorstActive =>
        _active.Count == 0 ? null : _active.Values.Max(e => e.Severity);

    /// <summary>
    /// Folds one snapshot into the event state and returns what CHANGED — the newly raised and newly
    /// resolved events. Returning only transitions is what makes this edge-triggered: a caller that
    /// raises a toast per returned event gets one toast per episode, not one per tick.
    /// </summary>
    public AlertChanges Evaluate(GpuSnapshot snapshot, IReadOnlyList<GpuDeviceInfo> deviceInfos, DateTime now)
    {
        var raised = new List<GpuEvent>();
        var resolved = new List<GpuEvent>();
        var seen = new HashSet<(int, EventMetric)>();

        foreach (var gpu in snapshot.Gpus)
        {
            var info = deviceInfos.FirstOrDefault(d => d.Index == gpu.Index);
            var thresholds = info != null ? _thresholdsFor(info) : AlertThresholds.NvidiaConsumer;
            string cardId = info?.CardId ?? "";

            EvaluateThrottle(gpu, cardId, now, seen, raised, resolved);
            EvaluateTemperature(gpu, cardId, thresholds, now, seen, raised, resolved);
            EvaluateMemory(gpu, cardId, thresholds, now, seen, raised, resolved);
            EvaluatePower(gpu, cardId, thresholds, now, seen, raised, resolved);
        }

        // A condition whose GPU vanished from the snapshot (backend failure, card removed) has no
        // sample to clear it, so resolve it here rather than leaving it active forever.
        foreach (var key in _active.Keys.Where(k => !seen.Contains(k)).ToList())
            resolved.Add(Resolve(key, now));

        TrimHistory();
        return new AlertChanges(raised, resolved);
    }

    // ---- Reported: the driver's own throttle bits -------------------------------------------------

    private void EvaluateThrottle(GpuSample gpu, string cardId, DateTime now,
                                  HashSet<(int, EventMetric)> seen,
                                  List<GpuEvent> raised, List<GpuEvent> resolved)
    {
        // A backend that cannot read throttle reasons contributes nothing — NOT "not throttling",
        // which would be a claim it has no basis for.
        //
        // This gate is REDUNDANT with the one inside GpuFormat.ThrottleReasons, and knowingly so:
        // mutation-testing confirmed that deleting it changes no observable behaviour, because the
        // helper returns an empty list and the no-reasons branch below reaches the same outcome.
        // Kept because it states the rule where the rule is enforced — a future change to the helper
        // (or a switch to reading the flags directly) would otherwise silently invent throttle events
        // for hardware that cannot report them.
        if (!gpu.Caps.ThrottleReasons) return;

        var key = (gpu.Index, EventMetric.Throttle);
        seen.Add(key);

        var reasons = GpuFormat.ThrottleReasons(gpu);
        if (reasons.Count == 0)
        {
            // Reported events skip hysteresis entirely: the driver has already debounced them, and
            // adding our own margin would delay reporting a fact.
            if (_active.ContainsKey(key)) resolved.Add(Resolve(key, now));
            return;
        }

        // Thermal and hardware slowdowns mean clocks are being lost to heat or a protection trip; a
        // software power cap is expected behaviour at the limit. Same rule as GpuFormat.ThrottleChip.
        var severity = gpu.ThrottleThermal || gpu.ThrottleHwSlowdown
            ? EventSeverity.Critical
            : EventSeverity.Warning;

        var description = string.Join(" · ", reasons);
        Raise(key, cardId, EventMetric.Throttle, EventSource.Reported, severity, description,
              value: 0, now, raised);
    }

    // ---- Derived: our own threshold crossings -----------------------------------------------------

    private void EvaluateTemperature(GpuSample gpu, string cardId, AlertThresholds thresholds,
                                     DateTime now, HashSet<(int, EventMetric)> seen,
                                     List<GpuEvent> raised, List<GpuEvent> resolved)
    {
        if (thresholds.TemperatureC is not { } pair) return;
        EvaluateThreshold(gpu.Index, cardId, EventMetric.Temperature, pair, gpu.TemperatureC,
                          v => $"{v:F0}°C", now, seen, raised, resolved);
    }

    private void EvaluateMemory(GpuSample gpu, string cardId, AlertThresholds thresholds,
                                DateTime now, HashSet<(int, EventMetric)> seen,
                                List<GpuEvent> raised, List<GpuEvent> resolved)
    {
        if (thresholds.MemoryPercent is not { } pair) return;
        EvaluateThreshold(gpu.Index, cardId, EventMetric.Memory, pair, gpu.MemoryUsedPercent,
                          v => $"VRAM {v:F0}%", now, seen, raised, resolved);
    }

    private void EvaluatePower(GpuSample gpu, string cardId, AlertThresholds thresholds,
                               DateTime now, HashSet<(int, EventMetric)> seen,
                               List<GpuEvent> raised, List<GpuEvent> resolved)
    {
        if (thresholds.PowerPercent is not { } pair) return;

        // No cap means no ratio to threshold against; inventing one would imply a limit the hardware
        // never reported.
        if (!gpu.Caps.PowerLimit || gpu.PowerLimitWatts <= 0) return;

        // THE DEDUPLICATION THAT MATTERS: where the driver reports its own throttle bits, sw_power_cap
        // already says authoritatively that the card is capped. A ">95% of cap" threshold is a weaker
        // inference of the same fact, and firing both would be two alerts for one event.
        if (gpu.Caps.ThrottleReasons) return;

        EvaluateThreshold(gpu.Index, cardId, EventMetric.Power, pair, GpuFormat.PowerPercent(gpu),
                          v => $"power {v:F0}% of cap", now, seen, raised, resolved);
    }

    /// <summary>
    /// The shared threshold state machine: raise on crossing up, escalate on crossing to critical,
    /// resolve only once the value falls past the clear margin.
    /// </summary>
    private void EvaluateThreshold(int gpuIndex, string cardId, EventMetric metric, ThresholdPair pair,
                                   double value, Func<double, string> describe, DateTime now,
                                   HashSet<(int, EventMetric)> seen,
                                   List<GpuEvent> raised, List<GpuEvent> resolved)
    {
        var key = (gpuIndex, metric);
        seen.Add(key);

        var severity = pair.SeverityFor(value);

        if (severity == null)
        {
            // Hysteresis: below warn is not enough — the value must clear the margin. Otherwise a card
            // resting on the threshold raises and resolves on alternate ticks forever.
            if (_active.TryGetValue(key, out var existing))
            {
                if (pair.ShouldClear(value))
                    resolved.Add(Resolve(key, now));
                else
                    _active[key] = existing with { PeakValue = Math.Max(existing.PeakValue, value) };
            }
            return;
        }

        Raise(key, cardId, metric, EventSource.Derived, severity.Value, describe(value), value, now, raised);
    }

    // ---- State transitions ------------------------------------------------------------------------

    private void Raise((int, EventMetric) key, string cardId, EventMetric metric, EventSource source,
                       EventSeverity severity, string description, double value, DateTime now,
                       List<GpuEvent> raised)
    {
        if (_active.TryGetValue(key, out var existing))
        {
            var peak = Math.Max(existing.PeakValue, value);

            // Escalation (warning → critical) is a NEW event: it is a different thing happening, the
            // toast for it should be sticky where the warning's was not, and the record of when the
            // card first crossed the warning line is worth keeping.
            if (severity > existing.Severity)
            {
                _history.Remove(existing);
                _history.Add(existing with { ResolvedAt = now, PeakValue = peak });

                var escalated = NewEvent(key, cardId, metric, source, severity, description, value, now)
                    with { PeakValue = peak };
                _active[key] = escalated;
                _history.Add(escalated);
                raised.Add(escalated);
                return;
            }

            // Same severity, still active: update the peak and the wording, but do NOT re-raise —
            // that is what makes this edge-triggered rather than one event per tick.
            var updated = existing with { PeakValue = peak, Description = description };
            _active[key] = updated;
            ReplaceInHistory(existing, updated);
            return;
        }

        var created = NewEvent(key, cardId, metric, source, severity, description, value, now);
        _active[key] = created;
        _history.Add(created);
        raised.Add(created);
    }

    private static GpuEvent NewEvent((int, EventMetric) key, string cardId, EventMetric metric,
                                     EventSource source, EventSeverity severity, string description,
                                     double value, DateTime now) =>
        new()
        {
            GpuIndex = key.Item1,
            CardId = cardId,
            Metric = metric,
            Source = source,
            Severity = severity,
            Description = description,
            RaisedAt = now,
            Value = value,
            PeakValue = value
        };

    private GpuEvent Resolve((int, EventMetric) key, DateTime now)
    {
        var active = _active[key];
        var done = active with { ResolvedAt = now };
        _active.Remove(key);
        ReplaceInHistory(active, done);
        return done;
    }

    private void ReplaceInHistory(GpuEvent from, GpuEvent to)
    {
        int i = _history.IndexOf(from);
        if (i >= 0) _history[i] = to;
    }

    private void TrimHistory()
    {
        if (_history.Count <= MaxHistory) return;

        // Drop oldest RESOLVED first. An active event dropped from history would still be counted by
        // the badge while being absent from the list the badge opens.
        var droppable = _history.Where(e => !e.IsActive).OrderBy(e => e.RaisedAt).ToList();
        foreach (var e in droppable)
        {
            if (_history.Count <= MaxHistory) break;
            _history.Remove(e);
        }
    }
}

/// <summary>What changed in one evaluation — the edges, not the levels.</summary>
internal sealed record AlertChanges(IReadOnlyList<GpuEvent> Raised, IReadOnlyList<GpuEvent> Resolved)
{
    public bool Any => Raised.Count > 0 || Resolved.Count > 0;
}
