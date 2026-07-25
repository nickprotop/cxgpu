using cxgpu.Gpu;
using cxgpu.Gpu.Alerts;
using cxgpu.Helpers;

namespace cxgpu.Tests;

/// <summary>
/// The throttle chips (Overview, hero panel, fleet list) and the alert engine must never disagree
/// about whether a card is throttling or how serious it is — two answers to one question is exactly
/// the failure the unified event stream exists to prevent.
///
/// They agree by CONSTRUCTION rather than by wiring: both read the driver's flags through
/// GpuFormat.ThrottleReasons, which owns the capability gate and the wording. These tests pin that
/// shared behaviour, so a future change that gives either side its own copy of the rule fails here
/// instead of silently drifting.
/// </summary>
public class ChipEngineAgreementTests
{
    private static readonly DateTime T0 = new(2026, 7, 25, 12, 0, 0, DateTimeKind.Utc);

    private static GpuCapabilities Caps(bool throttleReasons = true) =>
        new(FanSpeed: true, PowerLimit: true, ThrottleReasons: throttleReasons,
            EncoderDecoder: true, PerProcessMemory: true, PerProcessSm: true,
            ProcessSignal: true, CudaVersion: true);

    private static GpuSample Gpu(bool thermal = false, bool power = false, bool hw = false,
                                 bool capable = true) =>
        new(Index: 0, UtilizationPercent: 50, MemoryUsedPercent: 20, MemoryUsedMb: 1024,
            MemoryTotalMb: 8192, TemperatureC: 50, PowerDrawWatts: 100, PowerLimitWatts: 300,
            FanSpeedPercent: 40, SmClockMhz: 1500, MemClockMhz: 7000,
            ThrottleThermal: thermal, ThrottlePower: power, ThrottleHwSlowdown: hw,
            Capabilities: Caps(capable));

    private static GpuDeviceInfo Info() =>
        new(0, "NVIDIA GeForce RTX 3090", "595.84", "", "4x16", "13.2", 24576, 310, 0,
            "nvidia", "nvidia-smi", "0000:01:00.0");

    private static GpuEvent? ThrottleEventFor(GpuSample gpu)
    {
        var engine = new AlertEngine();
        engine.Evaluate(new GpuSnapshot([gpu], []), new[] { Info() }, T0);
        return engine.Active.FirstOrDefault(e => e.Metric == EventMetric.Throttle);
    }

    [Theory]
    [InlineData(true, false, false)]   // thermal
    [InlineData(false, true, false)]   // power cap
    [InlineData(false, false, true)]   // hw slowdown
    [InlineData(true, true, true)]     // all at once
    public void ChipAndEngineAgreeThatSomethingIsThrottling(bool thermal, bool power, bool hw)
    {
        var gpu = Gpu(thermal, power, hw);

        bool chipShows = GpuFormat.ThrottleChip(gpu).Length > 0;
        bool engineRaised = ThrottleEventFor(gpu) != null;

        Assert.True(chipShows);
        Assert.Equal(chipShows, engineRaised);
    }

    [Fact]
    public void NeitherFiresOnACleanCard()
    {
        var gpu = Gpu();

        Assert.Equal("", GpuFormat.ThrottleChip(gpu));
        Assert.Null(ThrottleEventFor(gpu));
    }

    [Fact]
    public void NeitherFiresWhenTheBackendCannotReadThrottleReasons()
    {
        // Flags set but capability false: "we cannot tell" must silence BOTH surfaces, or the chip
        // would claim a throttle the event list has no record of.
        var gpu = Gpu(thermal: true, power: true, hw: true, capable: false);

        Assert.Equal("", GpuFormat.ThrottleChip(gpu));
        Assert.Null(ThrottleEventFor(gpu));
    }

    [Theory]
    [InlineData(true, false, false, true)]    // thermal -> critical
    [InlineData(false, false, true, true)]    // hw slowdown -> critical
    [InlineData(false, true, false, false)]   // power cap -> warning
    public void SeverityMatchesTheChipsColour(bool thermal, bool power, bool hw,
                                              bool expectCritical)
    {
        // Severity is passed as a bool rather than the enum: xunit's InlineData sits on a public
        // method, and EventSeverity is internal.
        var expected = expectCritical ? EventSeverity.Critical : EventSeverity.Warning;
        var gpu = Gpu(thermal, power, hw);
        var chip = GpuFormat.ThrottleChip(gpu);
        var evt = ThrottleEventFor(gpu);

        Assert.NotNull(evt);
        Assert.Equal(expected, evt!.Severity);

        // The chip encodes severity as colour; the event as an enum. Assert they name the same thing.
        var expectedColour = (expected == EventSeverity.Critical
            ? UIConstants.Critical
            : UIConstants.Warning).ToMarkup();
        Assert.Contains(expectedColour, chip);
    }

    [Fact]
    public void WordingIsIdenticalOnBothSurfaces()
    {
        // The event description and the chip text must name the same reasons in the same order, so a
        // toast and the chip beside it never read differently.
        var gpu = Gpu(thermal: true, power: true, hw: true);

        var evt = ThrottleEventFor(gpu);
        var chip = GpuFormat.ThrottleChip(gpu);

        Assert.NotNull(evt);
        Assert.Equal("thermal · hw slowdown · power cap", evt!.Description);
        Assert.Contains(evt.Description, chip);
    }
}
