using SharpConsoleUI.Plugins;

namespace cxgpu.Gpu;

/// <summary>
/// Base class for a GPU backend that is also a SharpConsoleUI plugin.
///
/// WHY THIS EXISTS. The framework's plugin service ABI is deliberately agnostic —
/// <c>Execute(string op, Dictionary&lt;string,object&gt;?) → object?</c> — so a plugin needs no shared
/// interface with its host. That generality is exactly what would let a GPU backend ship as a
/// standalone DLL, but writing string dispatch and boxing by hand in every backend would be verbose
/// and easy to get wrong.
///
/// So this class pays that cost ONCE. Vendor backends override strongly-typed members
/// (<see cref="IGpuBackend"/>) and never touch a dictionary; the agnostic surface is derived from them
/// here. The app calls the typed members directly, so the once-a-second path allocates nothing extra
/// — <see cref="Execute"/> exists for the foreign/future caller.
///
/// The payoff: SharpConsoleUI currently has no runtime assembly loading
/// (<c>LoadPlugin&lt;T&gt;</c> only instantiates types the host already references). When that lands,
/// these backends are already valid <see cref="IPluginService"/> implementations and become drop-in
/// DLLs with no refactor.
/// </summary>
internal abstract class GpuBackendPlugin : PluginBase, IPluginService, IGpuBackend
{
    // --- Typed contract: this is what vendor authors implement ---

    public abstract GpuBackendInfo BackendInfo { get; }
    public abstract GpuCapabilities Capabilities { get; }
    public abstract bool Probe();
    public abstract IReadOnlyList<GpuSample> ReadSamples();
    public abstract IReadOnlyList<GpuDeviceInfo> ReadDeviceInfo();

    public virtual IReadOnlyList<GpuProcessSample> ReadProcesses() => Array.Empty<GpuProcessSample>();

    public virtual GpuSignalResult SignalProcess(int pid, GpuSignal signal) => GpuSignalResult.NotSupported;

    /// <summary>No settings by default — a backend opts in only if it genuinely has a choice to offer.</summary>
    public virtual IReadOnlyList<PluginSetting> GetSettings() => Array.Empty<PluginSetting>();

    public virtual void ApplySettings(IReadOnlyDictionary<string, string?> values) { }

    // --- Plugin metadata ---

    /// <summary>Framework plugin metadata, derived from the backend's own identity.</summary>
    public override PluginInfo Info => new(
        Name: ServiceName,
        Version: BackendInfo.Version ?? "1.0.0",
        Author: "cxgpu",
        Description: Description);

    /// <summary>Service name used for lookup: <c>Gpu.NVIDIA</c>, <c>Gpu.AMD</c>, …</summary>
    public string ServiceName => $"Gpu.{BackendInfo.Name}";

    public string Description => $"{BackendInfo.Vendor} GPU telemetry via {BackendInfo.Mechanism}";

    public override IReadOnlyList<IPluginService> GetServicePlugins() => new IPluginService[] { this };

    // --- The agnostic ABI, derived once from the typed members above ---

    public IReadOnlyList<ServiceOperation> GetAvailableOperations() => new[]
    {
        new ServiceOperation("Probe", "Whether this backend can serve data on this machine",
            Array.Empty<ServiceParameter>(), typeof(bool)),
        new ServiceOperation("Capabilities", "Which metrics this backend can report",
            Array.Empty<ServiceParameter>(), typeof(GpuCapabilities)),
        new ServiceOperation("ReadSamples", "Live per-GPU metrics",
            Array.Empty<ServiceParameter>(), typeof(IReadOnlyList<GpuSample>)),
        new ServiceOperation("ReadDeviceInfo", "Static per-GPU device facts",
            Array.Empty<ServiceParameter>(), typeof(IReadOnlyList<GpuDeviceInfo>)),
        new ServiceOperation("ReadProcesses", "Processes using this vendor's GPUs",
            Array.Empty<ServiceParameter>(), typeof(IReadOnlyList<GpuProcessSample>)),
        new ServiceOperation("SignalProcess", "Deliver a signal to one of this backend's processes",
            new[]
            {
                new ServiceParameter("pid", typeof(int), Required: true, null, "Target process id"),
                new ServiceParameter("signal", typeof(GpuSignal), Required: true, null,
                    "Terminate (SIGTERM) or Kill (SIGKILL)")
            },
            typeof(GpuSignalResult)),
        new ServiceOperation("GetSettings", "Settings this backend exposes for the user to change",
            Array.Empty<ServiceParameter>(), typeof(IReadOnlyList<PluginSetting>)),
        new ServiceOperation("ApplySettings", "Apply stored setting values, keyed by setting key",
            new[]
            {
                new ServiceParameter("values", typeof(IReadOnlyDictionary<string, string?>),
                    Required: true, null, "Setting key -> stored value")
            },
            null)
    };

    /// <summary>
    /// Virtual so a subclass can observe or wrap dispatch — used by the ABI tests to prove the app
    /// really reaches a backend through this surface rather than the typed members.
    /// </summary>
    public virtual object? Execute(string operationName, Dictionary<string, object>? parameters = null)
    {
        switch (operationName)
        {
            case "Probe": return Probe();
            case "Capabilities": return Capabilities;
            case "ReadSamples": return ReadSamples();
            case "ReadDeviceInfo": return ReadDeviceInfo();
            case "ReadProcesses": return ReadProcesses();

            case "SignalProcess":
                if (parameters == null ||
                    !parameters.TryGetValue("pid", out var pidValue) ||
                    !parameters.TryGetValue("signal", out var signalValue))
                {
                    throw new InvalidOperationException("SignalProcess requires 'pid' and 'signal'.");
                }
                return SignalProcess(Convert.ToInt32(pidValue), (GpuSignal)signalValue);

            case "GetSettings": return GetSettings();

            case "ApplySettings":
                if (parameters?.GetValueOrDefault("values") is not IReadOnlyDictionary<string, string?> settings)
                    throw new InvalidOperationException("ApplySettings requires a 'values' dictionary.");
                ApplySettings(settings);
                return null;

            default:
                throw new InvalidOperationException($"Unknown operation: {operationName}");
        }
    }
}
