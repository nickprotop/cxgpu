using cxgpu.Gpu;
using cxgpu.Gpu.Alerts;
using cxgpu.Helpers;

namespace cxgpu.Tests;

/// <summary>
/// Temperature colouring must follow the ALERT thresholds, not the generic percentage bands.
///
/// The wart this fixes: UIConstants.ThresholdColor treats 60/85 as warning/critical, which is sensible
/// for a percentage but wrong for °C — it paints a 3090 doing ordinary work critical, and would paint
/// an AMD card running at 95°C by design permanently critical. Colour and alert reading the same
/// numbers is also what makes a per-card override move both together.
/// </summary>
public class TemperatureColorTests : IDisposable
{
    // GpuFormat.TemperatureThresholds is a static hook, so each test restores it rather than leaking
    // its override into whatever runs next.
    private readonly Func<GpuSample, ThresholdPair?> _original = GpuFormat.TemperatureThresholds;

    public void Dispose() => GpuFormat.TemperatureThresholds = _original;

    private static GpuSample Gpu(double temp) =>
        new(Index: 0, UtilizationPercent: 50, MemoryUsedPercent: 20, MemoryUsedMb: 1024,
            MemoryTotalMb: 8192, TemperatureC: temp, PowerDrawWatts: 100, PowerLimitWatts: 300,
            FanSpeedPercent: 40, SmClockMhz: 1500, MemClockMhz: 7000);

    [Fact]
    public void ANormalLoadedConsumerCardIsNotPaintedCritical()
    {
        // 78°C under load is healthy for a 3090. The old percentage bands painted anything >= 85
        // critical and 60-85 warning, so this read as a warning at rest.
        GpuFormat.TemperatureThresholds = _ => AlertThresholds.NvidiaConsumer.TemperatureC;

        Assert.Equal(UIConstants.Normal, GpuFormat.TemperatureColor(Gpu(78)));
    }

    [Fact]
    public void CrossesToWarningAndCriticalAtTheAlertThresholds()
    {
        GpuFormat.TemperatureThresholds = _ => AlertThresholds.NvidiaConsumer.TemperatureC;

        Assert.Equal(UIConstants.Normal, GpuFormat.TemperatureColor(Gpu(82)));
        Assert.Equal(UIConstants.Warning, GpuFormat.TemperatureColor(Gpu(83)));
        Assert.Equal(UIConstants.Critical, GpuFormat.TemperatureColor(Gpu(89)));
    }

    [Fact]
    public void AmdRunsHotterBeforeWarning()
    {
        // 88°C is a warning on a GeForce and normal on a Radeon — the same reading, coloured by what
        // the part is rated for.
        GpuFormat.TemperatureThresholds = _ => AlertThresholds.AmdDiscrete.TemperatureC;
        Assert.Equal(UIConstants.Normal, GpuFormat.TemperatureColor(Gpu(88)));

        GpuFormat.TemperatureThresholds = _ => AlertThresholds.NvidiaConsumer.TemperatureC;
        Assert.Equal(UIConstants.Warning, GpuFormat.TemperatureColor(Gpu(88)));
    }

    [Fact]
    public void ColourAgreesWithTheAlertThatWouldFire()
    {
        // The guarantee worth having: for every temperature, what the user SEES matches what the
        // engine would RAISE. A mismatch means a red reading with no alert, or an alert with a green
        // reading — either way the user cannot trust the colour.
        var pair = AlertThresholds.NvidiaConsumer.TemperatureC!;
        GpuFormat.TemperatureThresholds = _ => pair;

        for (double t = 40; t <= 100; t += 1)
        {
            var colour = GpuFormat.TemperatureColor(Gpu(t));
            var severity = pair.SeverityFor(t);

            var expected = severity switch
            {
                EventSeverity.Critical => UIConstants.Critical,
                EventSeverity.Warning => UIConstants.Warning,
                _ => UIConstants.Normal
            };

            Assert.Equal(expected, colour);
        }
    }

    [Fact]
    public void APerCardOverrideMovesTheColourWithIt()
    {
        var strict = new ThresholdPair(Warn: 60, Critical: 70, ClearMargin: 2);
        GpuFormat.TemperatureThresholds = _ => strict;

        // 65°C is Normal by default and a Warning under this override.
        Assert.Equal(UIConstants.Warning, GpuFormat.TemperatureColor(Gpu(65)));
    }

    [Fact]
    public void NoThresholdsMeansNoAlarmingColour()
    {
        // A metric with alerting switched off must not be painted as if it were being judged.
        GpuFormat.TemperatureThresholds = _ => null;

        Assert.Equal(UIConstants.Normal, GpuFormat.TemperatureColor(Gpu(105)));
    }
}
