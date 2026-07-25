using cxgpu.Gpu;
using cxgpu.Gpu.Alerts;

namespace cxgpu.Tests;

/// <summary>
/// Tests for the alert engine. The three rules it exists to enforce — edge-triggering, hysteresis and
/// capability gating — each prevent a specific, concrete failure, and each is asserted here against
/// the failure rather than against the implementation.
/// </summary>
public class AlertEngineTests
{
    private static readonly DateTime T0 = new(2026, 7, 25, 12, 0, 0, DateTimeKind.Utc);

    private static GpuCapabilities Caps(bool throttleReasons = true, bool powerLimit = true) =>
        new(FanSpeed: true, PowerLimit: powerLimit, ThrottleReasons: throttleReasons,
            EncoderDecoder: true, PerProcessMemory: true, PerProcessSm: true,
            ProcessSignal: true, CudaVersion: true);

    private static GpuSample Gpu(
        int index = 0,
        double temp = 50,
        double memPercent = 20,
        double power = 100,
        double powerLimit = 300,
        bool thermal = false,
        bool powerThrottle = false,
        bool hwSlowdown = false,
        GpuCapabilities? caps = null) =>
        new(
            Index: index,
            UtilizationPercent: 50,
            MemoryUsedPercent: memPercent,
            MemoryUsedMb: 1024,
            MemoryTotalMb: 8192,
            TemperatureC: temp,
            PowerDrawWatts: power,
            PowerLimitWatts: powerLimit,
            FanSpeedPercent: 40,
            SmClockMhz: 1500,
            MemClockMhz: 7000,
            ThrottleThermal: thermal,
            ThrottlePower: powerThrottle,
            ThrottleHwSlowdown: hwSlowdown,
            Capabilities: caps ?? Caps());

    private static GpuSnapshot Snap(params GpuSample[] gpus) =>
        new(gpus, Array.Empty<GpuProcessSample>());

    private static GpuDeviceInfo Info(int index = 0, string name = "NVIDIA GeForce RTX 3090",
                                      string backend = "nvidia", string cardId = "0000:01:00.0") =>
        new(index, name, "595.84", "", "4x16", "13.2", 24576, 310, 0, backend, "nvidia-smi", cardId);

    private static IReadOnlyList<GpuDeviceInfo> Infos(params GpuDeviceInfo[] infos) => infos;

    // ---- Edge triggering -------------------------------------------------------------------------

    [Fact]
    public void RaisesOnceWhenCrossingUp()
    {
        var engine = new AlertEngine();

        var first = engine.Evaluate(Snap(Gpu(temp: 95)), Infos(Info()), T0);

        Assert.Single(first.Raised);
        Assert.Equal(EventMetric.Temperature, first.Raised[0].Metric);
    }

    [Fact]
    public void DoesNotReRaiseWhileConditionPersists()
    {
        // The failure this prevents: at the 2s default refresh, a level-triggered alert produces
        // ~1800 events an hour for one hot GPU.
        var engine = new AlertEngine();
        engine.Evaluate(Snap(Gpu(temp: 95)), Infos(Info()), T0);

        for (int i = 1; i <= 20; i++)
        {
            var changes = engine.Evaluate(Snap(Gpu(temp: 95)), Infos(Info()), T0.AddSeconds(i * 2));
            Assert.Empty(changes.Raised);
        }

        Assert.Single(engine.Active);
        Assert.Single(engine.History);
    }

    [Fact]
    public void TracksPeakWhileActive()
    {
        var engine = new AlertEngine();
        engine.Evaluate(Snap(Gpu(temp: 90)), Infos(Info()), T0);
        engine.Evaluate(Snap(Gpu(temp: 97)), Infos(Info()), T0.AddSeconds(2));
        engine.Evaluate(Snap(Gpu(temp: 91)), Infos(Info()), T0.AddSeconds(4));

        Assert.Equal(97, engine.Active[0].PeakValue);
    }

    // ---- Hysteresis ------------------------------------------------------------------------------

    [Fact]
    public void DoesNotResolveJustBelowTheThreshold()
    {
        // NvidiaConsumer warns at 83 with a clear margin of 3, so 82 is below warn but inside the
        // margin: resolving here is what makes a card resting on the line flap forever.
        var engine = new AlertEngine();
        engine.Evaluate(Snap(Gpu(temp: 85)), Infos(Info()), T0);

        var changes = engine.Evaluate(Snap(Gpu(temp: 82)), Infos(Info()), T0.AddSeconds(2));

        Assert.Empty(changes.Resolved);
        Assert.Single(engine.Active);
    }

    [Fact]
    public void ResolvesOncePastTheClearMargin()
    {
        var engine = new AlertEngine();
        engine.Evaluate(Snap(Gpu(temp: 85)), Infos(Info()), T0);

        var changes = engine.Evaluate(Snap(Gpu(temp: 79)), Infos(Info()), T0.AddSeconds(2));

        Assert.Single(changes.Resolved);
        Assert.Empty(engine.Active);
        Assert.False(engine.History[0].IsActive);
    }

    [Fact]
    public void OscillatingAtTheThresholdDoesNotFlap()
    {
        // The concrete failure: alternating 83/82 must not produce an event per tick.
        var engine = new AlertEngine();
        engine.Evaluate(Snap(Gpu(temp: 84)), Infos(Info()), T0);

        int churn = 0;
        for (int i = 1; i <= 20; i++)
        {
            var t = i % 2 == 0 ? 82 : 84;
            var changes = engine.Evaluate(Snap(Gpu(temp: t)), Infos(Info()), T0.AddSeconds(i * 2));
            churn += changes.Raised.Count + changes.Resolved.Count;
        }

        Assert.Equal(0, churn);
        Assert.Single(engine.History);
    }

    // ---- Escalation ------------------------------------------------------------------------------

    [Fact]
    public void EscalatingToCriticalRaisesANewEvent()
    {
        // Warning -> Critical is a different thing happening: its toast is sticky where the warning's
        // was not, and when the card first crossed the warning line is worth keeping.
        var engine = new AlertEngine();
        engine.Evaluate(Snap(Gpu(temp: 85)), Infos(Info()), T0);

        var changes = engine.Evaluate(Snap(Gpu(temp: 91)), Infos(Info()), T0.AddSeconds(2));

        Assert.Single(changes.Raised);
        Assert.Equal(EventSeverity.Critical, changes.Raised[0].Severity);
        Assert.Equal(2, engine.History.Count);
        Assert.Single(engine.Active);
    }

    [Fact]
    public void FallingBackToWarningDoesNotRaiseAgain()
    {
        var engine = new AlertEngine();
        engine.Evaluate(Snap(Gpu(temp: 91)), Infos(Info()), T0);

        var changes = engine.Evaluate(Snap(Gpu(temp: 85)), Infos(Info()), T0.AddSeconds(2));

        Assert.Empty(changes.Raised);
        Assert.Single(engine.Active);
    }

    // ---- Capability gating -----------------------------------------------------------------------

    [Fact]
    public void NoThrottleEventWhenTheBackendCannotReadThrottleReasons()
    {
        // "We cannot tell" must never render as an event. AMD sysfs/CLI both report false here.
        var engine = new AlertEngine();
        var gpu = Gpu(thermal: true, caps: Caps(throttleReasons: false));

        var changes = engine.Evaluate(Snap(gpu), Infos(Info(backend: "amd")), T0);

        Assert.DoesNotContain(changes.Raised, e => e.Metric == EventMetric.Throttle);
    }

    [Fact]
    public void ThrottleFlagsAreIgnoredEntirelyWhenTheCapabilityIsAbsent()
    {
        // Flags SET but capability false — the shape a backend takes when its reader cannot report
        // reasons. Nothing throttle-shaped may end up in any of the engine's state.
        //
        // Note this passes through GpuFormat.ThrottleReasons's gate as well as the engine's, so it
        // asserts the composed behaviour rather than the engine's gate alone (see the engine's
        // comment on why the second gate is kept anyway).
        var engine = new AlertEngine();
        var gpu = Gpu(thermal: true, hwSlowdown: true, powerThrottle: true,
                      caps: Caps(throttleReasons: false));

        engine.Evaluate(Snap(gpu), Infos(Info(backend: "amd")), T0);

        Assert.DoesNotContain(engine.Active, e => e.Metric == EventMetric.Throttle);
        Assert.DoesNotContain(engine.History, e => e.Metric == EventMetric.Throttle);
        Assert.Null(engine.WorstActive);
    }

    [Fact]
    public void NoPowerEventWithoutAReportedCap()
    {
        // Without a cap there is no ratio; inventing one would imply a limit never reported.
        var engine = new AlertEngine();
        var gpu = Gpu(power: 250, powerLimit: 0,
                      caps: Caps(throttleReasons: false, powerLimit: false));

        var changes = engine.Evaluate(Snap(gpu), Infos(Info(backend: "amd")), T0);

        Assert.DoesNotContain(changes.Raised, e => e.Metric == EventMetric.Power);
    }

    // ---- Reported vs Derived ---------------------------------------------------------------------

    [Fact]
    public void DriverThrottleIsReportedNotDerived()
    {
        var engine = new AlertEngine();

        var changes = engine.Evaluate(Snap(Gpu(thermal: true)), Infos(Info()), T0);

        var throttle = Assert.Single(changes.Raised, e => e.Metric == EventMetric.Throttle);
        Assert.Equal(EventSource.Reported, throttle.Source);
        Assert.Equal(EventSeverity.Critical, throttle.Severity);
    }

    [Fact]
    public void PowerCapThrottleIsWarningNotCritical()
    {
        // Matches GpuFormat.ThrottleChip: a software power cap is expected behaviour at the limit.
        var engine = new AlertEngine();

        var changes = engine.Evaluate(Snap(Gpu(powerThrottle: true)), Infos(Info()), T0);

        var throttle = Assert.Single(changes.Raised, e => e.Metric == EventMetric.Throttle);
        Assert.Equal(EventSeverity.Warning, throttle.Severity);
    }

    [Fact]
    public void DerivedPowerIsSuppressedWhenTheDriverReportsThrottleReasons()
    {
        // THE dedup rule: sw_power_cap already states the card is capped. A ">95% of cap" threshold is
        // a weaker inference of the same fact, so firing both would be two alerts for one event.
        var engine = new AlertEngine();
        var gpu = Gpu(power: 305, powerLimit: 310, caps: Caps(throttleReasons: true));

        var changes = engine.Evaluate(Snap(gpu), Infos(Info()), T0);

        Assert.DoesNotContain(changes.Raised, e => e.Metric == EventMetric.Power);
    }

    [Fact]
    public void DerivedPowerFiresForBackendsWithoutThrottleReasons()
    {
        // The inverse: where the driver cannot tell us, the threshold IS the best available signal.
        var engine = new AlertEngine();
        var gpu = Gpu(power: 305, powerLimit: 310, caps: Caps(throttleReasons: false));

        var changes = engine.Evaluate(Snap(gpu), Infos(Info(backend: "amd")), T0);

        Assert.Contains(changes.Raised, e => e.Metric == EventMetric.Power);
    }

    [Fact]
    public void ReportedEventsSkipHysteresisAndResolveImmediately()
    {
        // The driver already debounced these; adding our margin would delay reporting a fact.
        var engine = new AlertEngine();
        engine.Evaluate(Snap(Gpu(thermal: true)), Infos(Info()), T0);

        var changes = engine.Evaluate(Snap(Gpu(thermal: false)), Infos(Info()), T0.AddSeconds(2));

        Assert.Contains(changes.Resolved, e => e.Metric == EventMetric.Throttle);
    }

    // ---- Multiple GPUs and metrics ---------------------------------------------------------------

    [Fact]
    public void EventsAreIndependentPerGpuAndMetric()
    {
        var engine = new AlertEngine();

        engine.Evaluate(
            Snap(Gpu(index: 0, temp: 95), Gpu(index: 1, memPercent: 98)),
            Infos(Info(0), Info(1, cardId: "0000:02:00.0")),
            T0);

        Assert.Equal(2, engine.Active.Count);
        Assert.Contains(engine.Active, e => e.GpuIndex == 0 && e.Metric == EventMetric.Temperature);
        Assert.Contains(engine.Active, e => e.GpuIndex == 1 && e.Metric == EventMetric.Memory);
    }

    [Fact]
    public void OneGpuCanHoldSeveralMetricsAtOnce()
    {
        var engine = new AlertEngine();

        engine.Evaluate(Snap(Gpu(temp: 95, memPercent: 98)), Infos(Info()), T0);

        Assert.Equal(2, engine.Active.Count);
    }

    [Fact]
    public void ActiveIsOrderedWorstFirst()
    {
        var engine = new AlertEngine();

        engine.Evaluate(Snap(Gpu(temp: 85, memPercent: 98)), Infos(Info()), T0);

        Assert.Equal(EventSeverity.Critical, engine.Active[0].Severity);
    }

    [Fact]
    public void DisappearingGpuResolvesItsEvents()
    {
        // A backend failing mid-session leaves no sample to clear the condition, so it would otherwise
        // stay active forever and the badge would never clear.
        var engine = new AlertEngine();
        engine.Evaluate(Snap(Gpu(index: 0, temp: 95)), Infos(Info(0)), T0);

        var changes = engine.Evaluate(Snap(), Infos(), T0.AddSeconds(2));

        Assert.Single(changes.Resolved);
        Assert.Empty(engine.Active);
    }

    // ---- Bookkeeping -----------------------------------------------------------------------------

    [Fact]
    public void WorstActiveReflectsTheHighestSeverity()
    {
        var engine = new AlertEngine();
        Assert.Null(engine.WorstActive);

        engine.Evaluate(Snap(Gpu(temp: 85)), Infos(Info()), T0);
        Assert.Equal(EventSeverity.Warning, engine.WorstActive);

        engine.Evaluate(Snap(Gpu(temp: 85, memPercent: 98)), Infos(Info()), T0.AddSeconds(2));
        Assert.Equal(EventSeverity.Critical, engine.WorstActive);
    }

    [Fact]
    public void ResolvedEventsRecordTheirDuration()
    {
        var engine = new AlertEngine();
        engine.Evaluate(Snap(Gpu(temp: 95)), Infos(Info()), T0);
        engine.Evaluate(Snap(Gpu(temp: 60)), Infos(Info()), T0.AddMinutes(5));

        var resolved = engine.History[0];
        Assert.Equal(TimeSpan.FromMinutes(5), resolved.Duration(T0.AddMinutes(9)));
    }

    [Fact]
    public void EventsCarryTheCardIdForConfigLookup()
    {
        var engine = new AlertEngine();

        engine.Evaluate(Snap(Gpu(temp: 95)), Infos(Info(cardId: "0000:01:00.0")), T0);

        Assert.Equal("0000:01:00.0", engine.Active[0].CardId);
    }

    [Fact]
    public void HistoryIsBoundedButNeverDropsActiveEvents()
    {
        // Unbounded growth in a process designed to run for days is a leak; dropping an ACTIVE event
        // would make the badge count disagree with the list it opens.
        var engine = new AlertEngine();

        for (int i = 0; i < AlertEngine.MaxHistory + 50; i++)
        {
            engine.Evaluate(Snap(Gpu(temp: 95)), Infos(Info()), T0.AddSeconds(i * 10));
            engine.Evaluate(Snap(Gpu(temp: 50)), Infos(Info()), T0.AddSeconds(i * 10 + 5));
        }

        engine.Evaluate(Snap(Gpu(temp: 95)), Infos(Info()), T0.AddHours(9));

        Assert.True(engine.History.Count <= AlertEngine.MaxHistory,
            $"history grew to {engine.History.Count}");
        Assert.Single(engine.Active);
        Assert.Contains(engine.History, e => e.IsActive);
    }

    // ---- Thresholds ------------------------------------------------------------------------------

    [Fact]
    public void WorkstationCardsGetAHigherThermalEnvelopeThanConsumer()
    {
        var consumer = AlertThresholds.DefaultFor("nvidia", "NVIDIA GeForce RTX 3090");
        var workstation = AlertThresholds.DefaultFor("nvidia", "NVIDIA RTX A4000");

        Assert.True(workstation.TemperatureC!.Warn > consumer.TemperatureC!.Warn);
    }

    [Fact]
    public void AmdRunsHotterThanNvidiaByDesign()
    {
        var nvidia = AlertThresholds.DefaultFor("nvidia", "NVIDIA GeForce RTX 3090");
        var amd = AlertThresholds.DefaultFor("amd", "AMD Radeon RX 7900 XT");

        Assert.True(amd.TemperatureC!.Warn > nvidia.TemperatureC!.Warn);
    }

    [Fact]
    public void ApuIsTreatedAsCoolerThanDiscreteAmd()
    {
        var apu = AlertThresholds.DefaultFor("amd", "AMD GPU 0x1900");
        var discrete = AlertThresholds.DefaultFor("amd", "AMD Radeon RX 7900 XT");

        Assert.True(apu.TemperatureC!.Warn < discrete.TemperatureC!.Warn);
    }

    [Fact]
    public void DefaultsDoNotFireOnANormalLoadedConsumerCard()
    {
        // The wart this replaces: UIConstants.ThresholdColor's global 85 critical fires on a 3090
        // doing ordinary work. 78°C under load must be silent.
        var engine = new AlertEngine();

        var changes = engine.Evaluate(
            Snap(Gpu(temp: 78, memPercent: 85, power: 280, powerLimit: 310)),
            Infos(Info()), T0);

        Assert.Empty(changes.Raised);
    }

    [Fact]
    public void CustomThresholdsOverrideDefaults()
    {
        // Per-card override is the headline feature, since the built-in temperatures are judgement
        // calls that will be wrong for some card.
        var strict = new AlertThresholds(
            TemperatureC: new ThresholdPair(60, 70, ClearMargin: 2),
            MemoryPercent: null,
            PowerPercent: null);

        var engine = new AlertEngine(_ => strict);

        var changes = engine.Evaluate(Snap(Gpu(temp: 65)), Infos(Info()), T0);

        Assert.Single(changes.Raised);
        Assert.Equal(EventSeverity.Warning, changes.Raised[0].Severity);
    }

    [Fact]
    public void NullThresholdDisablesThatMetricEntirely()
    {
        var tempOnly = new AlertThresholds(
            TemperatureC: new ThresholdPair(83, 89, 3), MemoryPercent: null, PowerPercent: null);

        var engine = new AlertEngine(_ => tempOnly);

        var changes = engine.Evaluate(Snap(Gpu(temp: 50, memPercent: 99)), Infos(Info()), T0);

        Assert.Empty(changes.Raised);
    }
}
