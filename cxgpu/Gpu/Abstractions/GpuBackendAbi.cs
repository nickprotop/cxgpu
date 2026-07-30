using SharpConsoleUI.Plugins;

namespace cxgpu.Gpu;

/// <summary>
/// The app's single route to a GPU backend: the framework's agnostic plugin ABI
/// (<c>IPluginService.Execute(name, parameters)</c>).
///
/// WHY THESE EXIST. cxgpu calls backends EXCLUSIVELY through <c>Execute</c> — nothing above
/// <see cref="GpuBackendPlugin"/> touches an <see cref="IGpuBackend"/> member directly. Doing that
/// raw at every call site would mean repeating a cast, a null check and a not-a-plugin fallback a
/// dozen times, so the pattern is written once here and every caller reads as a plain method call.
///
/// WHY EVERY OPERATION, INCLUDING THE TICK PATH. A half-agnostic surface is worse than either
/// extreme: the untravelled half rots silently, which is exactly the drift this ABI's tests exist to
/// catch. The cost of routing the once-a-second read is a boxed list and an interface check, against
/// vendor backends that spawn a subprocess or walk sysfs on that same tick — orders of magnitude
/// more. Correctness of the abstraction beats an allocation nobody can measure.
///
/// THE TYPED FALLBACK IS NOT DEAD CODE. <see cref="IGpuBackend"/> deliberately does not require
/// plugin-hood — it is the contract vendor authors implement, and <see cref="GpuBackendPlugin"/>
/// derives the agnostic surface from it. A backend that is not a plugin (including a
/// directly-constructed test double) still has to work, so each helper falls back to the typed
/// member rather than failing.
/// </summary>
internal static class GpuBackendAbi
{
    /// <summary>
    /// Invokes an operation that returns a reference type, falling back to <paramref name="typed"/>
    /// for a non-plugin backend or a result that is not of the expected shape.
    ///
    /// A mistyped or null result falls back rather than throwing: the ABI is string-dispatched, so a
    /// mismatch is a programming error that the parity tests catch at build time — at RUNTIME the
    /// right behaviour is to still show the user their GPUs.
    /// </summary>
    private static T Invoke<T>(IGpuBackend backend, string operation, Func<T> typed) where T : class
    {
        if (backend is not IPluginService service)
            return typed();

        return service.Execute(operation) as T ?? typed();
    }

    /// <summary>
    /// Invokes an operation that returns a value type. Separate from <see cref="Invoke{T}"/> because
    /// <c>as</c> does not apply to value types and a boxed struct needs a pattern match.
    /// </summary>
    private static T InvokeValue<T>(IGpuBackend backend, string operation, Func<T> typed) where T : struct
    {
        if (backend is not IPluginService service)
            return typed();

        return service.Execute(operation) is T value ? value : typed();
    }

    /// <summary>Identity and the resolved mechanism. Only meaningful after a successful probe.</summary>
    public static GpuBackendInfo InfoVia(this IGpuBackend backend) =>
        Invoke(backend, "BackendInfo", () => backend.BackendInfo);

    /// <summary>What this backend can report, per metric.</summary>
    public static GpuCapabilities CapabilitiesVia(this IGpuBackend backend) =>
        Invoke(backend, "Capabilities", () => backend.Capabilities);

    /// <summary>Whether this backend can serve data here. Called once per candidate at startup.</summary>
    public static bool ProbeVia(this IGpuBackend backend) =>
        InvokeValue(backend, "Probe", backend.Probe);

    /// <summary>Live per-GPU metrics. The tick path — see the class remarks on why it routes.</summary>
    public static IReadOnlyList<GpuSample> ReadSamplesVia(this IGpuBackend backend) =>
        Invoke(backend, "ReadSamples", backend.ReadSamples);

    /// <summary>Static per-GPU device facts.</summary>
    public static IReadOnlyList<GpuDeviceInfo> ReadDeviceInfoVia(this IGpuBackend backend) =>
        Invoke(backend, "ReadDeviceInfo", backend.ReadDeviceInfo);

    /// <summary>
    /// Processes using this vendor's GPUs. Must be called immediately after
    /// <see cref="ReadSamplesVia"/> for a backend that pairs the two reads (the demo backend shares
    /// one snapshot between them so its animation tick does not double-step); <c>Execute</c>
    /// preserves call order, so routing changes nothing here.
    /// </summary>
    public static IReadOnlyList<GpuProcessSample> ReadProcessesVia(this IGpuBackend backend) =>
        Invoke(backend, "ReadProcesses", backend.ReadProcesses);

    /// <summary>Settings this backend declares for the user to change.</summary>
    public static IReadOnlyList<PluginSetting> GetSettingsVia(this IGpuBackend backend) =>
        Invoke(backend, "GetSettings", backend.GetSettings);

    /// <summary>
    /// Applies stored setting values. Parameterised, so it exercises the ABI's dictionary
    /// marshalling; a backend that cannot digest its stored settings must still run on defaults, so
    /// the typed fallback covers a non-plugin backend rather than swallowing errors.
    /// </summary>
    public static void ApplySettingsVia(this IGpuBackend backend, IReadOnlyDictionary<string, string?> values)
    {
        if (backend is not IPluginService service)
        {
            backend.ApplySettings(values);
            return;
        }

        service.Execute("ApplySettings", new Dictionary<string, object> { ["values"] = values });
    }
}
