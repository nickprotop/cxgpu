namespace cxnvmon.Stats;

/// <summary>
/// Synthetic multi-GPU provider used to exercise the multi-GPU UI paths (summary strip, selector,
/// throttle chip) on single-GPU machines — no NVIDIA hardware or driver required. Activated by
/// <c>--demo[=n]</c> on the command line, or by setting <c>CXNVMON_FAKE_GPUS=n</c>; never used
/// otherwise.
/// </summary>
internal sealed class FakeMultiGpuStatsProvider : IGpuStatsProvider
{
    /// <summary>GPU count used by <c>--demo</c> when no explicit count is given.</summary>
    public const int DefaultDemoGpuCount = 4;

    /// <summary>Upper bound on demo GPUs — the '1'-'9' direct-select keys only reach nine.</summary>
    public const int MaxDemoGpuCount = 9;

    private readonly int _count;
    private int _tick;

    public FakeMultiGpuStatsProvider(int count) => _count = Math.Clamp(count, 1, MaxDemoGpuCount);

    // Resolved once from the startup args (see ConfiguredCount) and cached, so every later caller —
    // the factory, the platform label, the status bar — agrees on whether this is a demo run
    // without having to thread argv through the whole app.
    private static int? _resolvedCount;
    private static bool _resolved;

    /// <summary>
    /// The demo GPU count settled at startup, or null in a normal (real-hardware) run.
    /// </summary>
    public static int? ActiveCount => _resolved ? _resolvedCount : ConfiguredCount(null);

    /// <summary>
    /// Resolves the demo GPU count from the command line, falling back to the CXNVMON_FAKE_GPUS
    /// environment variable. Returns null when demo mode isn't requested (the normal case).
    /// Accepted forms: <c>--demo</c>, <c>--demo=6</c>, <c>--demo 6</c>.
    /// The first call with a non-null <paramref name="args"/> latches the result for
    /// <see cref="ActiveCount"/>.
    /// </summary>
    public static int? ConfiguredCount(string[]? args = null)
    {
        if (args != null)
        {
            var found = ParseArgs(args) ?? FromEnvironment();
            _resolvedCount = found;
            _resolved = true;
            return found;
        }

        return _resolved ? _resolvedCount : FromEnvironment();
    }

    private static int? FromEnvironment()
    {
        var raw = Environment.GetEnvironmentVariable("CXNVMON_FAKE_GPUS");
        return int.TryParse(raw, out var n) && n > 0 ? n : null;
    }

    private static int? ParseArgs(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--demo", StringComparison.OrdinalIgnoreCase)) continue;

            // --demo=n
            var eq = arg.IndexOf('=');
            if (eq > 0)
                return int.TryParse(arg[(eq + 1)..], out var inline) && inline > 0
                    ? inline
                    : DefaultDemoGpuCount;

            if (!arg.Equals("--demo", StringComparison.OrdinalIgnoreCase)) continue;

            // --demo n  (only when the next token is a bare count, so "--demo" alone works)
            if (i + 1 < args.Length && int.TryParse(args[i + 1], out var next) && next > 0)
                return next;

            return DefaultDemoGpuCount;
        }

        return null;
    }

    public GpuSnapshot ReadSnapshot()
    {
        _tick++;
        var gpus = new List<GpuSample>();
        var procs = new List<GpuProcessSample>();

        for (int i = 0; i < _count; i++)
        {
            // Deterministic-but-moving values, spread across the threshold bands so the strip shows
            // green/yellow/red tiles at once.
            double util = (i * 37 + _tick * 3) % 101;
            double memPct = (i * 23 + 40) % 101;
            double temp = 35 + (i * 17 + _tick) % 55;

            gpus.Add(new GpuSample(
                Index: i,
                UtilizationPercent: util,
                MemoryUsedPercent: memPct,
                MemoryUsedMb: 24576 * memPct / 100.0,
                MemoryTotalMb: 24576,
                TemperatureC: temp,
                PowerDrawWatts: 30 + util * 2.8,
                PowerLimitWatts: 310,
                FanSpeedPercent: util * 0.8,
                SmClockMhz: 210 + util * 15,
                MemClockMhz: 9751,
                EncoderPercent: i == 1 ? 42 : 0,
                DecoderPercent: i == 1 ? 17 : 0,
                // One GPU thermal-throttling, one power-capped, so both chip severities render.
                ThrottleThermal: i == 2,
                ThrottlePower: i == 1,
                ThrottleHwSlowdown: false));

            // Two processes on the first GPU so per-GPU filtering is visibly exercised. One process
            // per GPU carries null percentages, mimicking a pmon row that reports "-".
            procs.Add(new GpuProcessSample(
                Pid: 1000 + i,
                Name: $"/usr/bin/fake-worker-{i}",
                MemoryUsedMb: 512 * (i + 1),
                GpuIndex: i,
                SmPercent: (i * 29 + _tick * 2) % 101,
                MemPercent: (i * 13 + 20) % 101,
                EncPercent: i == 1 ? 42 : 0,
                DecPercent: i == 1 ? 17 : 0));

            if (i == 0)
                procs.Add(new GpuProcessSample(
                    Pid: 2000,
                    Name: "/usr/lib/xorg/Xorg",
                    MemoryUsedMb: 128,
                    GpuIndex: 0));
        }

        return new GpuSnapshot(gpus, procs);
    }

    public IReadOnlyList<GpuDeviceInfo> ReadDeviceInfo()
    {
        var infos = new List<GpuDeviceInfo>();
        for (int i = 0; i < _count; i++)
        {
            infos.Add(new GpuDeviceInfo(
                Index: i,
                Name: i % 2 == 0 ? "NVIDIA GeForce RTX 3090" : "NVIDIA RTX A4000",
                DriverVersion: "595.84",
                VBiosVersion: "94.02.42.40.B7",
                PcieGenWidth: "4x16",
                CudaVersion: "13.2",
                MemoryTotalMb: 24576,
                PowerLimitWatts: 310,
                TemperatureLimitC: 0));
        }
        return infos;
    }
}
