using cxgpu.Gpu;
using cxgpu.Gpu.Alerts;

namespace cxgpu.Tests;

/// <summary>
/// Session statistics — the peaks and throttle time behind the Overview's SESSION section and the exit
/// summary. These answer "what did I miss", so getting the accumulation wrong is invisible in the live
/// view and only shows up as a number nobody can check.
/// </summary>
public class SessionStatsTests
{
    private static readonly DateTime T0 = new(2026, 7, 25, 12, 0, 0, DateTimeKind.Utc);

    private static GpuCapabilities Caps(bool powerLimit = true, bool throttleReasons = true) =>
        new(FanSpeed: true, PowerLimit: powerLimit, ThrottleReasons: throttleReasons,
            EncoderDecoder: true, PerProcessMemory: true, PerProcessSm: true,
            ProcessSignal: true, CudaVersion: true);

    private static GpuSample Gpu(int index = 0, double temp = 50, double power = 100,
                                 double memPercent = 20, bool thermal = false,
                                 GpuCapabilities? caps = null) =>
        new(Index: index, UtilizationPercent: 50, MemoryUsedPercent: memPercent,
            MemoryUsedMb: 1024, MemoryTotalMb: 8192, TemperatureC: temp,
            PowerDrawWatts: power, PowerLimitWatts: 300, FanSpeedPercent: 40,
            SmClockMhz: 1500, MemClockMhz: 7000, ThrottleThermal: thermal,
            Capabilities: caps ?? Caps());

    private static GpuSnapshot Snap(params GpuSample[] gpus) =>
        new(gpus, Array.Empty<GpuProcessSample>());

    private static GpuDeviceInfo Info(int index = 0) =>
        new(index, "NVIDIA GeForce RTX 3090", "595.84", "", "4x16", "13.2", 24576, 310, 0,
            "nvidia", "nvidia-smi", "0000:01:00.0");

    [Fact]
    public void TracksPeaksNotLatestValues()
    {
        var session = new SessionStats(T0);

        session.Observe(Snap(Gpu(temp: 60, power: 150)));
        session.Observe(Snap(Gpu(temp: 92, power: 290)));
        session.Observe(Snap(Gpu(temp: 55, power: 120)));

        var stats = session.For(0)!;
        Assert.Equal(92, stats.PeakTemperatureC);
        Assert.Equal(290, stats.PeakPowerWatts);
    }

    [Fact]
    public void PowerIsNotRecordedForACardThatCannotReportIt()
    {
        // Null-versus-zero: a peak of 0 W would be a measurement that never happened, so the row must
        // be omitted rather than shown at zero.
        var session = new SessionStats(T0);

        session.Observe(Snap(Gpu(power: 0, caps: Caps(powerLimit: false))));

        Assert.False(session.For(0)!.HasPower);
    }

    [Fact]
    public void AccumulatesThrottleTimeAcrossEpisodes()
    {
        var engine = new AlertEngine();
        var session = new SessionStats(T0);

        // Episode 1: 30s
        session.Observe(engine.Evaluate(Snap(Gpu(thermal: true)), new[] { Info() }, T0), T0);
        session.Observe(engine.Evaluate(Snap(Gpu(thermal: false)), new[] { Info() }, T0.AddSeconds(30)),
                        T0.AddSeconds(30));

        // Episode 2: 20s
        session.Observe(engine.Evaluate(Snap(Gpu(thermal: true)), new[] { Info() }, T0.AddMinutes(5)),
                        T0.AddMinutes(5));
        session.Observe(engine.Evaluate(Snap(Gpu(thermal: false)), new[] { Info() },
                                        T0.AddMinutes(5).AddSeconds(20)),
                        T0.AddMinutes(5).AddSeconds(20));

        Assert.Equal(TimeSpan.FromSeconds(50), session.For(0)!.ThrottledFor);
    }

    [Fact]
    public void IncludesAnEpisodeStillRunning()
    {
        // A card throttling RIGHT NOW must not report 0s until it recovers — that is exactly when
        // someone is looking at the number.
        var engine = new AlertEngine();
        var session = new SessionStats(T0);

        session.Observe(engine.Evaluate(Snap(Gpu(thermal: true)), new[] { Info() }, T0), T0);

        Assert.Equal(TimeSpan.FromSeconds(45), session.ThrottledFor(0, T0.AddSeconds(45)));
    }

    [Fact]
    public void EscalationDoesNotRestartTheThrottleClock()
    {
        // An escalation raises a SECOND event for a condition already running. Restarting the clock
        // there would silently discard the time already accumulated.
        var engine = new AlertEngine();
        var session = new SessionStats(T0);

        // Power cap (warning) then thermal (critical) — one continuous throttle episode.
        var gpuWarn = Gpu(thermal: false) with { ThrottlePower = true };
        session.Observe(engine.Evaluate(Snap(gpuWarn), new[] { Info() }, T0), T0);
        session.Observe(engine.Evaluate(Snap(Gpu(thermal: true)), new[] { Info() }, T0.AddSeconds(20)),
                        T0.AddSeconds(20));

        Assert.Equal(TimeSpan.FromSeconds(60), session.ThrottledFor(0, T0.AddSeconds(60)));
    }

    [Fact]
    public void CountsCriticalEventsOnly()
    {
        var engine = new AlertEngine();
        var session = new SessionStats(T0);

        // Thermal throttle is critical; a bare power cap is a warning.
        session.Observe(engine.Evaluate(Snap(Gpu(thermal: true)), new[] { Info() }, T0), T0);
        session.Observe(engine.Evaluate(
            Snap(Gpu(thermal: false) with { ThrottlePower = true }), new[] { Info() },
            T0.AddSeconds(60)), T0.AddSeconds(60));

        Assert.Equal(1, session.For(0)!.CriticalEvents);
    }

    [Fact]
    public void TracksEachGpuSeparately()
    {
        var session = new SessionStats(T0);

        session.Observe(Snap(Gpu(index: 0, temp: 90), Gpu(index: 1, temp: 60)));

        Assert.Equal(90, session.For(0)!.PeakTemperatureC);
        Assert.Equal(60, session.For(1)!.PeakTemperatureC);
    }

    [Fact]
    public void AQuietSessionReportsNothingHappened()
    {
        // Drives whether the exit summary prints at all — a clean run should exit without ceremony.
        var session = new SessionStats(T0);
        session.Observe(Snap(Gpu(temp: 50)));

        Assert.False(session.AnythingHappened);
    }

    [Fact]
    public void ASessionWithAThrottleReportsSomethingHappened()
    {
        var engine = new AlertEngine();
        var session = new SessionStats(T0);

        session.Observe(engine.Evaluate(Snap(Gpu(thermal: true)), new[] { Info() }, T0), T0);
        session.Observe(engine.Evaluate(Snap(Gpu(thermal: false)), new[] { Info() }, T0.AddSeconds(5)),
                        T0.AddSeconds(5));

        Assert.True(session.AnythingHappened);
    }

    [Fact]
    public void UnseenGpuHasNoStats()
    {
        Assert.Null(new SessionStats(T0).For(7));
    }
}
