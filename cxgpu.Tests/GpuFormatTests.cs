using cxgpu.Gpu;
using cxgpu.Helpers;

namespace cxgpu.Tests;

/// <summary>
/// Tests for shared formatting — the capability gates and the gauge maths. These are the functions
/// where a wrong answer becomes a false claim on screen rather than a crash.
/// </summary>
public class GpuFormatTests
{
    private static GpuCapabilities Caps(bool powerLimit = true, bool throttleReasons = true) =>
        new(FanSpeed: true, PowerLimit: powerLimit, ThrottleReasons: throttleReasons,
            EncoderDecoder: true, PerProcessMemory: true, PerProcessSm: true,
            ProcessSignal: true, CudaVersion: true);

    private static GpuSample Gpu(
        double power = 100,
        double powerLimit = 300,
        bool thermal = false,
        bool hwSlowdown = false,
        bool powerThrottle = false,
        GpuCapabilities? caps = null) =>
        new(
            Index: 0,
            UtilizationPercent: 10,
            MemoryUsedPercent: 12,
            MemoryUsedMb: 1024,
            MemoryTotalMb: 8192,
            TemperatureC: 50,
            PowerDrawWatts: power,
            PowerLimitWatts: powerLimit,
            FanSpeedPercent: 40,
            SmClockMhz: 1500,
            MemClockMhz: 7000,
            ThrottleThermal: thermal,
            ThrottlePower: powerThrottle,
            ThrottleHwSlowdown: hwSlowdown,
            Capabilities: caps);

    // ---- Power scaling -------------------------------------------------------------------------

    [Fact]
    public void PowerScaleUsesReportedCap()
    {
        Assert.Equal(300, GpuFormat.PowerScale(Gpu(powerLimit: 300)));
    }

    [Fact]
    public void PowerScaleFallsBackToStableReferenceWithoutCap()
    {
        // A device with no cap still needs a FIXED axis; one that rescaled to the current draw would
        // make every load level look identical on the sparkline.
        var noCap = Gpu(powerLimit: 0, caps: Caps(powerLimit: false));

        Assert.Equal(GpuFormat.PowerScaleFallbackWatts, GpuFormat.PowerScale(noCap));
    }

    [Fact]
    public void PowerPercentIsRatioOfCap()
    {
        Assert.Equal(50, GpuFormat.PowerPercent(Gpu(power: 150, powerLimit: 300)));
    }

    [Fact]
    public void PowerPercentIsZeroWithoutCapRatherThanInvented()
    {
        // Without a cap there is no ratio to report. Returning a number derived from the fallback
        // would imply a limit the hardware never gave us.
        var noCap = Gpu(power: 80, powerLimit: 0, caps: Caps(powerLimit: false));

        Assert.Equal(0, GpuFormat.PowerPercent(noCap));
    }

    // ---- Throttle gating ----------------------------------------------------------------------

    [Fact]
    public void NoThrottleReasonsWhenRunningClean()
    {
        Assert.Empty(GpuFormat.ThrottleReasons(Gpu()));
        Assert.Equal("", GpuFormat.ThrottleChip(Gpu()));
    }

    [Fact]
    public void ReportsEachThrottleReason()
    {
        var reasons = GpuFormat.ThrottleReasons(
            Gpu(thermal: true, hwSlowdown: true, powerThrottle: true));

        Assert.Equal(["thermal", "hw slowdown", "power cap"], reasons);
    }

    [Fact]
    public void UnsupportedThrottleReadingYieldsNothingNotAllClear()
    {
        // The distinction that matters: "we cannot tell" must not render as "not throttling".
        var unsupported = Gpu(thermal: true, caps: Caps(throttleReasons: false));

        Assert.Empty(GpuFormat.ThrottleReasons(unsupported));
        Assert.Equal("", GpuFormat.ThrottleChip(unsupported));
    }

    [Fact]
    public void ThrottleChipIsCriticalForThermalAndWarningForPowerCap()
    {
        var thermal = GpuFormat.ThrottleChip(Gpu(thermal: true));
        var powerCap = GpuFormat.ThrottleChip(Gpu(powerThrottle: true));

        Assert.Contains(UIConstants.Critical.ToMarkup(), thermal);
        Assert.Contains(UIConstants.Warning.ToMarkup(), powerCap);
        Assert.NotEqual(thermal, powerCap);
    }

    // ---- Braille gauge ------------------------------------------------------------------------

    [Fact]
    public void UtilBarHasRequestedCellCount()
    {
        // Length in chars, since every braille cell is one char and one column.
        Assert.Equal(4, GpuFormat.UtilBar(50).Length);
        Assert.Equal(8, GpuFormat.UtilBar(50, cells: 8).Length);
    }

    [Fact]
    public void UtilBarIsBlankAtZeroAndFullAtHundred()
    {
        Assert.Equal("⠀⠀⠀⠀", GpuFormat.UtilBar(0));
        Assert.Equal("⡇⡇⡇⡇", GpuFormat.UtilBar(100));
    }

    [Theory]
    [InlineData(-50)]
    [InlineData(150)]
    [InlineData(double.NaN)]
    public void UtilBarClampsOutOfRangeInput(double percent)
    {
        // Out-of-range values must not index outside the braille table.
        var bar = GpuFormat.UtilBar(percent);

        Assert.Equal(4, bar.Length);
        Assert.All(bar, c => Assert.InRange(c, '⠀', '⣿'));
    }

    [Fact]
    public void UtilBarFillsMonotonically()
    {
        // Higher load must never render as less fill.
        int Filled(string bar) => bar.Sum(c => c - '⠀');

        for (double p = 0; p <= 95; p += 5)
            Assert.True(Filled(GpuFormat.UtilBar(p)) <= Filled(GpuFormat.UtilBar(p + 5)),
                $"fill decreased between {p}% and {p + 5}%");
    }

    // ---- Icon alignment -----------------------------------------------------------------------

    [Fact]
    public void IconCellPadsNarrowIconsToUniformWidth()
    {
        // The bug this prevents: mixed-width icons (⚙ is 1 column, ⚡ is 2) made stacked metric rows
        // misalign by a column, which no fixed padding could fix.
        foreach (var icon in new[] { GpuFormat.IconUtil, GpuFormat.IconMem, GpuFormat.IconTemp,
                                     GpuFormat.IconPower, GpuFormat.IconFan, GpuFormat.IconMedia })
        {
            var width = SharpConsoleUI.Parsing.MarkupParser.StripLength(GpuFormat.IconCell(icon));
            Assert.Equal(GpuFormat.IconCellWidth, width);
        }
    }
}
