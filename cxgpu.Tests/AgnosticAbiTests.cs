using cxgpu.Gpu;
using SharpConsoleUI.Plugins;

namespace cxgpu.Tests;

/// <summary>
/// Guards the framework's agnostic plugin ABI — <c>Execute(name, parameters)</c> — against drift from
/// the typed <see cref="IGpuBackend"/> contract it is derived from.
///
/// This matters because the two surfaces are kept in sync BY HAND in
/// <c>GpuBackendPlugin</c>: one <c>switch</c> for dispatch and one list for discovery. Add a typed
/// member, advertise it, forget the switch arm, and nothing at runtime notices until a foreign caller
/// hits it. These tests are that notice.
///
/// Everything here uses <see cref="DemoBackend"/> (invents its own data) or constructs
/// <see cref="AmdBackend"/> without probing, so the suite stays hardware-independent like the rest.
/// </summary>
public class AgnosticAbiTests
{
    private static DemoBackend Demo(int count = 3) => new(count);

    // --- Discovery matches dispatch ---

    /// <summary>
    /// THE DRIFT GUARD: every operation the backend advertises must actually dispatch. An advertised
    /// name with no switch arm throws "Unknown operation", which is precisely the silent-until-foreign
    /// failure this file exists to prevent.
    /// </summary>
    [Fact]
    public void EveryAdvertisedOperationIsDispatchable()
    {
        var backend = Demo();
        var operations = backend.GetAvailableOperations();

        Assert.NotEmpty(operations);

        foreach (var operation in operations)
        {
            // Supply required parameters generically so this keeps working as operations are added.
            var parameters = new Dictionary<string, object>();
            foreach (var parameter in operation.Parameters.Where(p => p.Required))
                parameters[parameter.Name] = SampleValueFor(parameter);

            var exception = Record.Exception(() => backend.Execute(operation.Name, parameters));

            Assert.False(
                exception is InvalidOperationException invalid &&
                invalid.Message.Contains("Unknown operation", StringComparison.Ordinal),
                $"Operation '{operation.Name}' is advertised but not dispatched by Execute.");
        }
    }

    /// <summary>A name that was never advertised must be rejected rather than silently returning null.</summary>
    [Fact]
    public void UnknownOperationThrows()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => Demo().Execute("NoSuchOperation"));

        Assert.Contains("Unknown operation", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Service names are the lookup keys used by name-based callers, so their shape is contractual.</summary>
    [Fact]
    public void ServiceNameIsVendorQualified()
    {
        Assert.Equal("Gpu.Demo", Demo().ServiceName);
        Assert.Equal("Gpu.AMD", new AmdBackend().ServiceName);
    }

    // --- Typed and agnostic surfaces agree ---

    [Fact]
    public void ExecuteReadDeviceInfoMatchesTypedCall()
    {
        var backend = Demo();

        var typed = backend.ReadDeviceInfo();
        var viaAbi = Assert.IsAssignableFrom<IReadOnlyList<GpuDeviceInfo>>(
            backend.Execute("ReadDeviceInfo"));

        Assert.Equal(typed.Count, viaAbi.Count);
        Assert.Equal(typed.Select(d => d.Index), viaAbi.Select(d => d.Index));
        Assert.Equal(typed.Select(d => d.Name), viaAbi.Select(d => d.Name));
    }

    /// <summary>
    /// AMD is the backend with settings worth comparing — the base class returns an empty list, which
    /// would make this test pass without proving anything. Construction does not probe, so this runs
    /// on any machine.
    /// </summary>
    [Fact]
    public void ExecuteGetSettingsMatchesTypedCall()
    {
        var backend = new AmdBackend();

        var typed = backend.GetSettings();
        var viaAbi = Assert.IsAssignableFrom<IReadOnlyList<PluginSetting>>(
            backend.Execute("GetSettings"));

        Assert.NotEmpty(typed);
        Assert.Equal(typed.Select(s => s.Key), viaAbi.Select(s => s.Key));
        Assert.Equal(typed.Select(s => s.Kind), viaAbi.Select(s => s.Kind));
        Assert.Equal(typed.Select(s => s.Default), viaAbi.Select(s => s.Default));
    }

    [Fact]
    public void ExecuteCapabilitiesMatchesTypedCall()
    {
        var backend = Demo();

        Assert.Equal(backend.Capabilities, backend.Execute("Capabilities"));
    }

    [Fact]
    public void ExecuteProbeMatchesTypedCall()
    {
        var backend = Demo();

        Assert.Equal(backend.Probe(), Assert.IsType<bool>(backend.Execute("Probe")));
    }

    // --- Parameter marshalling ---

    /// <summary>
    /// The demo backend cannot signal (synthetic PIDs), so NotSupported is the CORRECT answer here —
    /// what is under test is that the dictionary reaches the typed member and its verdict comes back
    /// intact, not that a signal lands.
    /// </summary>
    [Fact]
    public void ExecuteSignalProcessMarshalsParameters()
    {
        var backend = Demo();

        var typed = backend.SignalProcess(9_000_001, GpuSignal.Terminate);
        var viaAbi = backend.Execute("SignalProcess", new Dictionary<string, object>
        {
            ["pid"] = 9_000_001,
            ["signal"] = GpuSignal.Terminate,
        });

        Assert.Equal(GpuSignalResult.NotSupported, typed);
        Assert.Equal(typed, Assert.IsType<GpuSignalResult>(viaAbi));
    }

    [Theory]
    [InlineData("pid")]
    [InlineData("signal")]
    public void ExecuteSignalProcessRejectsMissingParameter(string omit)
    {
        var parameters = new Dictionary<string, object>
        {
            ["pid"] = 9_000_001,
            ["signal"] = GpuSignal.Kill,
        };
        parameters.Remove(omit);

        Assert.Throws<InvalidOperationException>(
            () => Demo().Execute("SignalProcess", parameters));
    }

    [Fact]
    public void ExecuteSignalProcessRejectsNoParameters()
    {
        Assert.Throws<InvalidOperationException>(() => Demo().Execute("SignalProcess"));
    }

    /// <summary>A signal argument the enum cannot represent must fail loudly, not be coerced to Terminate.</summary>
    [Fact]
    public void ExecuteSignalProcessRejectsNonEnumSignal()
    {
        Assert.ThrowsAny<Exception>(() => Demo().Execute("SignalProcess", new Dictionary<string, object>
        {
            ["pid"] = 9_000_001,
            ["signal"] = "Terminate",
        }));
    }

    [Fact]
    public void ExecuteApplySettingsMarshalsDictionary()
    {
        var backend = new AmdBackend();

        backend.Execute("ApplySettings", new Dictionary<string, object>
        {
            ["values"] = new Dictionary<string, string?> { ["Reader"] = "rocm-smi" },
        });

        // Observable through the setting the backend just took: the reader choice is echoed back as
        // the stored value, which is what the settings dialog reads.
        Assert.Contains("rocm-smi", backend.GetSettings().Single(s => s.Key == "Reader").Options!);
    }

    [Fact]
    public void ExecuteApplySettingsRejectsMissingValues()
    {
        Assert.Throws<InvalidOperationException>(() => new AmdBackend().Execute("ApplySettings"));
    }

    // --- The app reaches backends ONLY through the ABI ---

    [Fact]
    public void RegistryReadDeviceInfoGoesThroughTheAbi()
    {
        var backend = new AbiCountingBackend();
        var registry = new GpuBackendRegistry(new IGpuBackend[] { backend });
        backend.ResetCounts();

        var infos = registry.ReadDeviceInfo();

        Assert.Equal(2, infos.Count);

        // Execute was the ENTRY POINT. The typed member is still reached, because the agnostic
        // surface is derived from it — that pass-through is the design, not a leak.
        Assert.Contains("ReadDeviceInfo", backend.Operations);
        Assert.Equal(1, backend.TypedDeviceInfoCalls);
    }

    /// <summary>
    /// The TICK PATH routes too. An earlier revision carved ReadSamples out to avoid boxing; routing
    /// everything is the current design, because a half-travelled ABI is the half that rots. This
    /// pins the decision so reverting it has to argue with a test.
    /// </summary>
    [Fact]
    public void RegistryReadSnapshotGoesThroughTheAbi()
    {
        var backend = new AbiCountingBackend();
        var registry = new GpuBackendRegistry(new IGpuBackend[] { backend });
        backend.ResetCounts();

        var snapshot = registry.ReadSnapshot();

        Assert.NotEmpty(snapshot.Gpus);
        Assert.Contains("ReadSamples", backend.Operations);
        Assert.Contains("ReadProcesses", backend.Operations);
        Assert.Contains("Capabilities", backend.Operations);
    }

    /// <summary>Probing happens through the ABI as well, at construction.</summary>
    [Fact]
    public void RegistryProbesThroughTheAbi()
    {
        var backend = new AbiCountingBackend();

        _ = new GpuBackendRegistry(new IGpuBackend[] { backend });

        Assert.Contains("Probe", backend.Operations);
    }

    /// <summary>
    /// ReadSamples must still precede ReadProcesses. The demo backend shares one snapshot between the
    /// two so its animation tick does not double-step, so reordering them would change behaviour —
    /// routing through Execute must not disturb that.
    /// </summary>
    [Fact]
    public void SamplesAreReadBeforeProcesses()
    {
        var backend = new AbiCountingBackend();
        var registry = new GpuBackendRegistry(new IGpuBackend[] { backend });
        backend.ResetCounts();

        registry.ReadSnapshot();

        var samplesAt = backend.Operations.IndexOf("ReadSamples");
        var processesAt = backend.Operations.IndexOf("ReadProcesses");

        Assert.True(samplesAt >= 0 && processesAt > samplesAt,
            $"Expected ReadSamples before ReadProcesses, got: {string.Join(", ", backend.Operations)}");
    }

    /// <summary>Stored settings are applied through the ABI, dictionary marshalling and all.</summary>
    [Fact]
    public void ApplySettingsGoesThroughTheAbi()
    {
        var backend = new AbiCountingBackend();

        backend.ApplySettingsVia(new Dictionary<string, string?> { ["Reader"] = "sysfs" });

        Assert.Contains("ApplySettings", backend.Operations);
        Assert.Equal("sysfs", backend.LastAppliedReader);
    }

    /// <summary>Identity is an ABI operation too, so no caller needs the typed property.</summary>
    [Fact]
    public void BackendInfoGoesThroughTheAbi()
    {
        var backend = new AbiCountingBackend();

        Assert.Equal("Counting", backend.InfoVia().Name);
        Assert.Contains("BackendInfo", backend.Operations);
    }

    [Fact]
    public void ExecuteBackendInfoMatchesTypedCall()
    {
        var backend = Demo();

        Assert.Equal(backend.BackendInfo, backend.Execute("BackendInfo"));
    }

    /// <summary>
    /// A backend that is not a plugin still works. IGpuBackend does not require plugin-hood, and the
    /// registry's fallback is what keeps that true.
    /// </summary>
    [Fact]
    public void RegistryFallsBackForNonPluginBackend()
    {
        var registry = new GpuBackendRegistry(new IGpuBackend[] { new PlainBackend() });

        var infos = registry.ReadDeviceInfo();

        Assert.Equal("Plain GPU", Assert.Single(infos).Name);
    }

    /// <summary>
    /// With the ABI in the path, the registry's own contract must still hold: global indices are
    /// contiguous across vendors, and device info agrees with the snapshot.
    /// </summary>
    [Fact]
    public void RegistryReassignsIndicesAcrossBackendsOverTheAbi()
    {
        var registry = new GpuBackendRegistry(new IGpuBackend[]
        {
            new AbiCountingBackend(),
            new AbiCountingBackend(),
        });

        var infos = registry.ReadDeviceInfo();
        var snapshot = registry.ReadSnapshot();

        Assert.Equal(new[] { 0, 1, 2, 3 }, infos.Select(i => i.Index));
        Assert.Equal(infos.Select(i => i.Index), snapshot.Gpus.Select(g => g.Index));
    }

    /// <summary>A backend whose ABI call throws is skipped, not fatal — one vendor must not blank another.</summary>
    [Fact]
    public void RegistrySkipsBackendWhoseAbiCallThrows()
    {
        var registry = new GpuBackendRegistry(new IGpuBackend[]
        {
            new ThrowingAbiBackend(),
            new AbiCountingBackend(),
        });

        Assert.Equal(2, registry.ReadDeviceInfo().Count);
    }

    // --- Helpers ---

    private static GpuSample Sample(int index) => new(
        Index: index,
        UtilizationPercent: 10,
        MemoryUsedPercent: 12.5,
        MemoryUsedMb: 1024,
        MemoryTotalMb: 8192,
        TemperatureC: 50,
        PowerDrawWatts: 100,
        PowerLimitWatts: 300,
        FanSpeedPercent: 40,
        SmClockMhz: 1500,
        MemClockMhz: 7000);

    private static GpuDeviceInfo Device(int index, string name) => new(
        Index: index,
        Name: name,
        DriverVersion: "1.0",
        VBiosVersion: "1.0",
        PcieGenWidth: "4.0x16",
        CudaVersion: "",
        MemoryTotalMb: 8192,
        PowerLimitWatts: 300,
        TemperatureLimitC: 90);

    private static object SampleValueFor(ServiceParameter parameter)
    {
        if (parameter.Type == typeof(int)) return 9_000_001;
        if (parameter.Type == typeof(GpuSignal)) return GpuSignal.Terminate;
        if (parameter.Type == typeof(IReadOnlyDictionary<string, string?>))
            return new Dictionary<string, string?>();

        return parameter.DefaultValue ?? throw new InvalidOperationException(
            $"No sample value known for required parameter '{parameter.Name}' of type {parameter.Type}. " +
            "Add one here when the ABI grows a new parameter type.");
    }

    /// <summary>A real plugin backend that records which surface the registry actually called.</summary>
    private sealed class AbiCountingBackend : GpuBackendPlugin
    {
        /// <summary>Operation names seen by Execute, in call order.</summary>
        public List<string> Operations { get; } = new();

        public int TypedDeviceInfoCalls { get; private set; }
        public int TypedSampleCalls { get; private set; }
        public string? LastAppliedReader { get; private set; }

        /// <summary>Clears the record, so a test can ignore the probe done at registry construction.</summary>
        public void ResetCounts()
        {
            Operations.Clear();
            TypedDeviceInfoCalls = 0;
            TypedSampleCalls = 0;
        }

        public override GpuBackendInfo BackendInfo => new("Counting", "Test", "memory", "1.0.0");
        public override GpuCapabilities Capabilities => new(PerProcessMemory: true);
        public override bool Probe() => true;

        public override void ApplySettings(IReadOnlyDictionary<string, string?> values)
        {
            if (values.TryGetValue("Reader", out var reader)) LastAppliedReader = reader;
        }

        public override IReadOnlyList<GpuSample> ReadSamples()
        {
            TypedSampleCalls++;
            return new[] { Sample(0), Sample(1) };
        }

        public override IReadOnlyList<GpuDeviceInfo> ReadDeviceInfo()
        {
            TypedDeviceInfoCalls++;
            return new[] { Device(0, "Counting GPU 0"), Device(1, "Counting GPU 1") };
        }

        public override object? Execute(string operationName, Dictionary<string, object>? parameters = null)
        {
            Operations.Add(operationName);
            return base.Execute(operationName, parameters);
        }
    }

    private sealed class ThrowingAbiBackend : GpuBackendPlugin
    {
        public override GpuBackendInfo BackendInfo => new("Throwing", "Test", "memory", "1.0.0");
        public override GpuCapabilities Capabilities => new();
        public override bool Probe() => true;
        public override IReadOnlyList<GpuSample> ReadSamples() => Array.Empty<GpuSample>();

        public override IReadOnlyList<GpuDeviceInfo> ReadDeviceInfo() =>
            throw new InvalidOperationException("device info unavailable");
    }

    /// <summary>An IGpuBackend that is deliberately NOT a plugin, exercising the typed fallback.</summary>
    private sealed class PlainBackend : IGpuBackend
    {
        public GpuBackendInfo BackendInfo => new("Plain", "Test", "memory", "1.0.0");
        public GpuCapabilities Capabilities => new();
        public bool Probe() => true;
        public IReadOnlyList<GpuSample> ReadSamples() => new[] { Sample(0) };

        public IReadOnlyList<GpuDeviceInfo> ReadDeviceInfo() => new[] { Device(0, "Plain GPU") };

        public IReadOnlyList<GpuProcessSample> ReadProcesses() => Array.Empty<GpuProcessSample>();
        public GpuSignalResult SignalProcess(int pid, GpuSignal signal) => GpuSignalResult.NotSupported;
        public IReadOnlyList<PluginSetting> GetSettings() => Array.Empty<PluginSetting>();
        public void ApplySettings(IReadOnlyDictionary<string, string?> values) { }
    }
}
