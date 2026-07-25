namespace cxgpu.Stats;

/// <summary>The signal to deliver to a GPU process.</summary>
internal enum GpuSignal
{
    /// <summary>Ask the process to exit cleanly (POSIX SIGTERM).</summary>
    Terminate,

    /// <summary>Force termination; cannot be caught or ignored (POSIX SIGKILL).</summary>
    Kill
}

/// <summary>
/// Outcome of a signal attempt. Deliberately enumerates the ordinary failures rather than collapsing
/// them into "failed": on a shared GPU box, "belongs to another user" (EPERM) and "already exited"
/// (ESRCH) are both normal and mean different things to the operator.
/// </summary>
internal enum GpuSignalResult
{
    Ok,
    NotSupported,
    PermissionDenied,
    NoSuchProcess,
    Failed
}

/// <summary>
/// A GPU data source for one vendor.
///
/// This is the strongly-typed contract the app uses. Backends additionally expose the same operations
/// through the framework's agnostic <c>IPluginService.Execute</c> ABI (see
/// <see cref="GpuBackendPlugin"/>), so they remain valid plugins if runtime assembly loading is ever
/// added — but the app calls these typed members directly, keeping the once-per-second path free of
/// boxing and dictionary allocation.
/// </summary>
internal interface IGpuBackend
{
    /// <summary>
    /// Identity and the resolved mechanism. Only meaningful after a successful <see cref="Probe"/>.
    /// Named BackendInfo rather than Info because <c>PluginBase</c> already declares an abstract
    /// <c>Info</c> of a different type (<c>PluginInfo</c>), and a backend is both.
    /// </summary>
    GpuBackendInfo BackendInfo { get; }

    /// <summary>What this backend can report. May depend on which mechanism <see cref="Probe"/> selected.</summary>
    GpuCapabilities Capabilities { get; }

    /// <summary>
    /// Whether this backend can serve data on this machine, right now. Must be cheap, must not throw,
    /// and must actually EXERCISE the source rather than merely checking that a tool exists — a
    /// driver/library version mismatch leaves nvidia-smi installed but every invocation failing.
    /// </summary>
    bool Probe();

    /// <summary>Live per-GPU metrics. Returns empty (never throws) when the source fails transiently.</summary>
    IReadOnlyList<GpuSample> ReadSamples();

    /// <summary>Static device facts. Returns empty (never throws) on failure.</summary>
    IReadOnlyList<GpuDeviceInfo> ReadDeviceInfo();

    /// <summary>
    /// Processes using this vendor's GPUs. Empty when unsupported — which the caller distinguishes
    /// from "none running" via <see cref="GpuCapabilities.PerProcessMemory"/>.
    /// </summary>
    IReadOnlyList<GpuProcessSample> ReadProcesses();

    /// <summary>
    /// Delivers a signal to one of this backend's processes. Implementations must report honestly and
    /// never assume success. Backends without <see cref="GpuCapabilities.ProcessSignal"/> return
    /// <see cref="GpuSignalResult.NotSupported"/>.
    /// </summary>
    GpuSignalResult SignalProcess(int pid, GpuSignal signal);

    /// <summary>
    /// Settings this backend exposes for the user to change. Empty for backends with nothing to
    /// configure — most of them — so the settings dialog stays as short as the situation warrants.
    /// </summary>
    IReadOnlyList<PluginSetting> GetSettings();

    /// <summary>
    /// Applies stored values, keyed by <see cref="PluginSetting.Key"/>. Values arrive as strings
    /// (that is what the form produces and what is persisted); a value that fails to parse must leave
    /// the current setting untouched rather than throwing, matching the app's tolerant-config
    /// behaviour elsewhere. Unknown keys are ignored so a downgrade cannot break startup.
    /// </summary>
    void ApplySettings(IReadOnlyDictionary<string, string?> values);
}
