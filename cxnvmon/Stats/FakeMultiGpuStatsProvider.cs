namespace cxnvmon.Stats;

/// <summary>
/// Synthetic multi-GPU provider used to exercise the multi-GPU UI paths (summary strip, selector,
/// throttle chip) on single-GPU development machines. Activated by setting
/// <c>CXNVMON_FAKE_GPUS=&lt;n&gt;</c>; never used otherwise.
/// </summary>
internal sealed class FakeMultiGpuStatsProvider : IGpuStatsProvider
{
    private readonly int _count;
    private int _tick;

    public FakeMultiGpuStatsProvider(int count) => _count = Math.Clamp(count, 1, 9);

    /// <summary>
    /// Returns the configured fake GPU count from CXNVMON_FAKE_GPUS, or null when unset/invalid.
    /// </summary>
    public static int? ConfiguredCount()
    {
        var raw = Environment.GetEnvironmentVariable("CXNVMON_FAKE_GPUS");
        return int.TryParse(raw, out var n) && n > 0 ? n : null;
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

            procs.Add(new GpuProcessSample(
                Pid: 1000 + i,
                Name: $"/usr/bin/fake-worker-{i}",
                MemoryUsedMb: 512 * (i + 1),
                GpuIndex: i));
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
