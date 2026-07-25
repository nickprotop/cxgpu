using System.Diagnostics;

namespace cxnvmon.Stats;

/// <summary>
/// Short-lived cache in front of a GPU stats provider, so a single user action cannot re-run the
/// underlying tools several times.
///
/// The problem this solves is measured, not theoretical. One snapshot costs ~110 ms on the dev box
/// (four nvidia-smi invocations — gpu, uuid, compute-apps, pmon — plus the AMD reads), and switching
/// GPU in the Overview triggered THREE of them plus three ReadDeviceInfo calls: roughly 400 ms of
/// subprocess work to redisplay data the app already had. That was the entire perceived lag.
///
/// Two different lifetimes, because the data has two different natures:
/// - Snapshots are live, so they are cached for one refresh interval. Callers within the same tick
///   share one read; the next tick fetches fresh.
/// - Device info is STATIC (verified: name/driver/vbios/memory identical across repeated reads), so it
///   is cached for the process lifetime. A driver change requires a restart anyway.
/// </summary>
internal sealed class CachedGpuStats : IGpuStatsProvider
{
    private readonly IGpuStatsProvider _inner;
    private readonly long _snapshotTtlTicks;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _gate = new();

    private GpuSnapshot? _snapshot;
    private long _snapshotAt = long.MinValue;
    private IReadOnlyList<GpuDeviceInfo>? _deviceInfo;

    /// <param name="inner">The real provider.</param>
    /// <param name="snapshotTtl">
    /// How long a snapshot stays fresh — the app's refresh interval. Anything longer would show stale
    /// numbers; anything shorter would defeat the purpose, since a single repaint makes several calls.
    /// </param>
    public CachedGpuStats(IGpuStatsProvider inner, TimeSpan snapshotTtl)
    {
        _inner = inner;
        _snapshotTtlTicks = (long)snapshotTtl.TotalMilliseconds;
    }

    /// <summary>The wrapped provider, for callers that need the concrete registry (backends, routing).</summary>
    public IGpuStatsProvider Inner => _inner;

    public GpuSnapshot ReadSnapshot()
    {
        lock (_gate)
        {
            var now = _clock.ElapsedMilliseconds;
            if (_snapshot != null && now - _snapshotAt < _snapshotTtlTicks)
                return _snapshot;

            _snapshot = _inner.ReadSnapshot();
            _snapshotAt = now;
            return _snapshot;
        }
    }

    public IReadOnlyList<GpuDeviceInfo> ReadDeviceInfo()
    {
        lock (_gate)
        {
            return _deviceInfo ??= _inner.ReadDeviceInfo();
        }
    }

    /// <summary>
    /// Drops the cached snapshot so the next read fetches fresh. Called by the update loop, which is
    /// the one place that genuinely wants new data rather than whatever the last repaint fetched.
    /// </summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _snapshotAt = long.MinValue;
        }
    }
}
