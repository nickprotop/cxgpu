using cxgpu.Export;
using cxgpu.Gpu;

namespace cxgpu.Tests;

/// <summary>
/// Tests for the Prometheus output.
///
/// Capability gating matters more here than anywhere in the UI: a fabricated zero in a time-series
/// database is averaged into dashboards forever, and unlike a blank UI cell nobody sees it happen.
/// Most of these assert that an unreadable metric is ABSENT rather than zero.
/// </summary>
public class PrometheusFormatterTests
{
    private static GpuCapabilities Caps(bool fan = true, bool power = true, bool throttle = true,
                                        bool encDec = true) =>
        new(FanSpeed: fan, PowerLimit: power, ThrottleReasons: throttle, EncoderDecoder: encDec,
            PerProcessMemory: true, PerProcessSm: true, ProcessSignal: true, CudaVersion: true);

    private static GpuSample Gpu(int index = 0, double temp = 55, double power = 120,
                                 double powerLimit = 310, double fan = 40,
                                 bool thermal = false, GpuCapabilities? caps = null) =>
        new(Index: index, UtilizationPercent: 42, MemoryUsedPercent: 25,
            MemoryUsedMb: 2048, MemoryTotalMb: 8192, TemperatureC: temp,
            PowerDrawWatts: power, PowerLimitWatts: powerLimit, FanSpeedPercent: fan,
            SmClockMhz: 1500, MemClockMhz: 7000, ThrottleThermal: thermal,
            Capabilities: caps ?? Caps());

    private static GpuDeviceInfo Info(int index = 0, string name = "NVIDIA GeForce RTX 3090",
                                      string backend = "nvidia", string cardId = "0000:01:00.0") =>
        new(index, name, "595.84", "", "4x16", "13.2", 24576, 310, 0, backend, "nvidia-smi", cardId);

    private static string Render(GpuSample gpu, GpuDeviceInfo? info = null) =>
        PrometheusFormatter.Render(
            new GpuSnapshot([gpu], Array.Empty<GpuProcessSample>()),
            new[] { info ?? Info(gpu.Index) });

    // ---- Format validity -------------------------------------------------------------------------

    [Fact]
    public void EmitsHelpAndTypeForEveryMetric()
    {
        var output = Render(Gpu());

        foreach (var line in output.Split('\n').Where(l => l.StartsWith("cxgpu_")))
        {
            var metric = line[..line.IndexOf('{')];
            Assert.Contains($"# HELP {metric} ", output);
            Assert.Contains($"# TYPE {metric} ", output);
        }
    }

    [Fact]
    public void UsesInvariantDecimalSeparator()
    {
        // A comma decimal separator under a European locale would produce output Prometheus rejects.
        var output = Render(Gpu(temp: 55.5));

        Assert.Contains("55.5", output);
        Assert.DoesNotContain("55,5", output);
    }

    [Fact]
    public void EscapesLabelValues()
    {
        // GPU names come from vendor tools; a quote in one would otherwise break the line's syntax.
        var output = Render(Gpu(), Info(name: "GPU \"quoted\" \\ backslash"));

        Assert.Contains("\\\"quoted\\\"", output);
        Assert.Contains("\\\\", output);
    }

    [Fact]
    public void LabelsCarryTheStableCardId()
    {
        // Index-labelled series silently re-point at different hardware when a backend fails to
        // probe — invisible in the UI, permanent in a TSDB.
        var output = Render(Gpu(), Info(cardId: "0000:c6:00.0"));

        Assert.Contains("card=\"0000:c6:00.0\"", output);
    }

    [Fact]
    public void OmitsTheCardLabelWhenThereIsNoIdentity()
    {
        // An empty label value is a real label in Prometheus, so every identity-less card would
        // collapse into one shared series.
        var output = Render(Gpu(), Info(cardId: ""));

        Assert.DoesNotContain("card=\"\"", output);
    }

    // ---- Capability gating -----------------------------------------------------------------------

    [Fact]
    public void OmitsFanEntirelyOnAFanlessCard()
    {
        var output = Render(Gpu(fan: 0, caps: Caps(fan: false)));

        Assert.DoesNotContain("cxgpu_fan_percent", output);
    }

    [Fact]
    public void OmitsPowerWhenTheCardReportsNone()
    {
        var output = Render(Gpu(power: 0, powerLimit: 0, caps: Caps(power: false)));

        Assert.DoesNotContain("cxgpu_power_watts", output);
        Assert.DoesNotContain("cxgpu_power_limit_watts", output);
    }

    [Fact]
    public void OmitsMediaEnginesWhenUnsupported()
    {
        var output = Render(Gpu(caps: Caps(encDec: false)));

        Assert.DoesNotContain("cxgpu_encoder_percent", output);
        Assert.DoesNotContain("cxgpu_decoder_percent", output);
    }

    [Fact]
    public void OmitsThrottleSeriesForBackendsThatCannotReadReasons()
    {
        // Exporting zeros here would assert "not throttling" — a claim the backend cannot support,
        // and one that would make a throttle alert silently un-fireable.
        var output = Render(Gpu(thermal: true, caps: Caps(throttle: false)));

        Assert.DoesNotContain("cxgpu_throttled", output);
    }

    [Fact]
    public void OmitsHelpAndTypeForAMetricWithNoSamples()
    {
        // A declaration with no samples tells a scraper the series exists but has no data, which is a
        // different claim from "this card has no fan".
        var output = Render(Gpu(caps: Caps(fan: false)));

        Assert.DoesNotContain("# TYPE cxgpu_fan_percent", output);
        Assert.DoesNotContain("# HELP cxgpu_fan_percent", output);
    }

    // ---- Values ----------------------------------------------------------------------------------

    [Fact]
    public void ConvertsMegabytesToBytes()
    {
        var output = Render(Gpu());

        Assert.Contains($"cxgpu_memory_total_bytes", output);
        Assert.Contains((8192.0 * 1024 * 1024).ToString("0.###"), output);
    }

    [Fact]
    public void SplitsThrottleByReason()
    {
        var output = Render(Gpu(thermal: true));

        Assert.Contains("reason=\"thermal\"", output);
        Assert.Contains("reason=\"hw_slowdown\"", output);
        Assert.Contains("reason=\"power_cap\"", output);

        // The active one reads 1, the others 0 — all three are meaningful for a capable backend.
        var thermalLine = output.Split('\n').First(l => l.Contains("reason=\"thermal\""));
        Assert.EndsWith(" 1", thermalLine);
    }

    [Fact]
    public void CountsProcessesPerGpu()
    {
        var snapshot = new GpuSnapshot(
            [Gpu(0), Gpu(1)],
            [
                new GpuProcessSample(Pid: 1, Name: "a", MemoryUsedMb: 100, GpuIndex: 0),
                new GpuProcessSample(Pid: 2, Name: "b", MemoryUsedMb: 100, GpuIndex: 1),
                new GpuProcessSample(Pid: 3, Name: "c", MemoryUsedMb: 100, GpuIndex: 1),
            ]);

        var output = PrometheusFormatter.Render(snapshot, new[] { Info(0), Info(1, cardId: "0000:02:00.0") });

        var lines = output.Split('\n').Where(l => l.StartsWith("cxgpu_process_count")).ToList();
        Assert.Contains(lines, l => l.Contains("gpu=\"0\"") && l.EndsWith(" 1"));
        Assert.Contains(lines, l => l.Contains("gpu=\"1\"") && l.EndsWith(" 2"));
    }

    [Fact]
    public void EmitsOneSeriesPerGpu()
    {
        var snapshot = new GpuSnapshot([Gpu(0), Gpu(1)], Array.Empty<GpuProcessSample>());

        var output = PrometheusFormatter.Render(snapshot, new[] { Info(0), Info(1, cardId: "0000:02:00.0") });

        var temps = output.Split('\n').Count(l => l.StartsWith("cxgpu_temperature_celsius{"));
        Assert.Equal(2, temps);
    }

    [Fact]
    public void MixedFleetGatesPerCardNotPerFleet()
    {
        // The hybrid case this app exists for: one card reporting power and fan, one not. The capable
        // card must still export them.
        var nvidia = Gpu(0, caps: Caps());
        var amd = Gpu(1, power: 0, powerLimit: 0, fan: 0, caps: Caps(fan: false, power: false, throttle: false));

        var output = PrometheusFormatter.Render(
            new GpuSnapshot([nvidia, amd], Array.Empty<GpuProcessSample>()),
            new[] { Info(0), Info(1, name: "AMD GPU", backend: "amd", cardId: "0000:c6:00.0") });

        var fanLines = output.Split('\n').Where(l => l.StartsWith("cxgpu_fan_percent{")).ToList();
        Assert.Single(fanLines);
        Assert.Contains("gpu=\"0\"", fanLines[0]);
    }

    [Fact]
    public void EmptySnapshotProducesNoSeries()
    {
        var output = PrometheusFormatter.Render(
            new GpuSnapshot(Array.Empty<GpuSample>(), Array.Empty<GpuProcessSample>()),
            Array.Empty<GpuDeviceInfo>());

        Assert.DoesNotContain("cxgpu_temperature_celsius{", output);
    }
}
