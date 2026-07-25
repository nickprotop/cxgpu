using cxgpu.Gpu;

namespace cxgpu.Tests;

/// <summary>
/// Tests for fleet aggregation — the numbers shown on the dashboard chip and nowhere else, so a wrong
/// total here is invisible by comparison with any per-GPU view.
/// </summary>
public class FleetSummaryTests
{
    private static GpuSample Gpu(
        int index,
        double memUsed = 1024,
        double memTotal = 8192,
        double temp = 50,
        double power = 100,
        bool thermalThrottle = false,
        GpuCapabilities? caps = null) =>
        new(
            Index: index,
            UtilizationPercent: 10,
            MemoryUsedPercent: memTotal == 0 ? 0 : memUsed / memTotal * 100,
            MemoryUsedMb: memUsed,
            MemoryTotalMb: memTotal,
            TemperatureC: temp,
            PowerDrawWatts: power,
            PowerLimitWatts: 300,
            FanSpeedPercent: 40,
            SmClockMhz: 1500,
            MemClockMhz: 7000,
            ThrottleThermal: thermalThrottle,
            Capabilities: caps);

    private static GpuSnapshot Snapshot(params GpuSample[] gpus) =>
        new(gpus, Array.Empty<GpuProcessSample>());

    [Fact]
    public void EmptySnapshotYieldsEmptySummary()
    {
        var summary = FleetSummary.From(Snapshot());

        Assert.Equal(0, summary.GpuCount);
        Assert.Null(summary.HottestGpuIndex);
    }

    [Fact]
    public void SumsVramAcrossGpus()
    {
        var summary = FleetSummary.From(Snapshot(
            Gpu(0, memUsed: 2048, memTotal: 8192),
            Gpu(1, memUsed: 1024, memTotal: 16384)));

        Assert.Equal(3072, summary.VramUsedMb);
        Assert.Equal(24576, summary.VramTotalMb);
    }

    [Fact]
    public void SumsPowerAndReportsCompleteness()
    {
        var summary = FleetSummary.From(Snapshot(
            Gpu(0, power: 120),
            Gpu(1, power: 180)));

        Assert.Equal(300, summary.PowerDrawWatts);
        Assert.Equal(2, summary.PowerReportingGpus);
        Assert.True(summary.PowerIsComplete);
    }

    [Fact]
    public void PowerTotalExcludesNonReportingGpusAndIsMarkedIncomplete()
    {
        // The AMD APU reports no power. The total must not silently present a partial figure as the
        // whole fleet's draw — this flag is what lets the UI say "1 of 2 reporting".
        var summary = FleetSummary.From(Snapshot(
            Gpu(0, power: 150),
            Gpu(1, power: 0)));

        Assert.Equal(150, summary.PowerDrawWatts);
        Assert.Equal(1, summary.PowerReportingGpus);
        Assert.False(summary.PowerIsComplete);
    }

    [Fact]
    public void FindsHottestGpu()
    {
        var summary = FleetSummary.From(Snapshot(
            Gpu(0, temp: 48),
            Gpu(1, temp: 71),
            Gpu(2, temp: 55)));

        Assert.Equal(1, summary.HottestGpuIndex);
        Assert.Equal(71, summary.HottestTemperatureC);
    }

    [Fact]
    public void HottestUsesGlobalIndexNotPosition()
    {
        // The registry reassigns global indices across backends, so the hottest GPU's reported index
        // must come from the sample rather than its position in the list.
        var summary = FleetSummary.From(Snapshot(
            Gpu(5, temp: 40),
            Gpu(9, temp: 80)));

        Assert.Equal(9, summary.HottestGpuIndex);
    }

    [Fact]
    public void CollectsThrottlingGpus()
    {
        var summary = FleetSummary.From(Snapshot(
            Gpu(0),
            Gpu(1, thermalThrottle: true)));

        var (index, reason) = Assert.Single(summary.Throttling);
        Assert.Equal(1, index);
        Assert.NotEmpty(reason);
    }

    [Fact]
    public void BackendWithoutThrottleSupportContributesNoThrottleEntry()
    {
        // A backend that cannot read throttle bits must not appear as "not throttling" — that is a
        // claim it has no basis for. It simply contributes nothing.
        var noThrottleCaps = new GpuCapabilities(
            FanSpeed: true, PowerLimit: true, ThrottleReasons: false, EncoderDecoder: true,
            PerProcessMemory: true, PerProcessSm: true, ProcessSignal: true, CudaVersion: true);

        var summary = FleetSummary.From(Snapshot(
            Gpu(0, thermalThrottle: true, caps: noThrottleCaps)));

        Assert.Empty(summary.Throttling);
    }

    [Fact]
    public void CountsProcessesAcrossFleet()
    {
        var snapshot = new GpuSnapshot(
            [Gpu(0), Gpu(1)],
            [
                new GpuProcessSample(Pid: 100, Name: "a", MemoryUsedMb: 512, GpuIndex: 0),
                new GpuProcessSample(Pid: 200, Name: "b", MemoryUsedMb: 256, GpuIndex: 1),
                new GpuProcessSample(Pid: 300, Name: "c", MemoryUsedMb: 128, GpuIndex: 1),
            ]);

        Assert.Equal(3, FleetSummary.From(snapshot).ProcessCount);
    }
}
