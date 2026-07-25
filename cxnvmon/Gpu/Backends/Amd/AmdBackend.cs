namespace cxnvmon.Stats;

/// <summary>
/// The AMD GPU backend — one plugin for the vendor, whichever mechanism ends up serving the data.
///
/// AMD is readable more than one way (Linux sysfs; the amd-smi/rocm-smi CLI), and which is available
/// depends on the platform and what is installed. That selection is made here at probe time and is
/// invisible above this class: the registry, the config and the UI all see a single "AMD" backend.
/// A future native Windows source becomes another <see cref="IAmdReader"/>, not another plugin.
/// </summary>
internal sealed class AmdBackend : GpuBackendPlugin
{
    /// <summary>
    /// Candidate mechanisms in preference order. sysfs first on merit, measured on the dev box: no
    /// subprocess (~0 ms per tick against ~80 ms for rocm-smi), nothing to install, no root, and the
    /// only source here that can attribute memory to processes. On Windows sysfs simply declines and
    /// the CLI takes over.
    /// </summary>
    private static readonly Func<IAmdReader>[] ReaderFactories =
    {
        () => new AmdSysfsReader(),
        () => new AmdCliReader()
    };

    private IAmdReader? _reader;

    public override GpuBackendInfo BackendInfo =>
        new("AMD", "AMD", _reader?.Mechanism ?? "none", _driverVersion);

    private string? _driverVersion;

    /// <summary>
    /// Capabilities come from the reader that actually won, so they always describe the live source.
    /// This is the concrete payoff of keeping mechanism selection inside the backend: the sysfs reader
    /// can attribute memory per process while the CLI reader cannot, and there is exactly one honest
    /// answer rather than two sets for a caller to reconcile.
    /// </summary>
    public override GpuCapabilities Capabilities => _reader?.Capabilities ?? new GpuCapabilities();

    public override bool Probe()
    {
        foreach (var factory in ReaderFactories)
        {
            try
            {
                var candidate = factory();
                if (!candidate.Probe()) continue;

                _reader = candidate;
                _driverVersion = candidate.ReadDeviceInfo().FirstOrDefault()?.DriverVersion;
                return true;
            }
            catch
            {
                // A mechanism that throws is simply unavailable; try the next.
            }
        }

        return false;
    }

    public override IReadOnlyList<GpuSample> ReadSamples() => Safe(r => r.ReadSamples(), Array.Empty<GpuSample>());

    public override IReadOnlyList<GpuDeviceInfo> ReadDeviceInfo() =>
        Safe(r => r.ReadDeviceInfo(), Array.Empty<GpuDeviceInfo>());

    public override IReadOnlyList<GpuProcessSample> ReadProcesses() =>
        Safe(r => r.ReadProcesses(), Array.Empty<GpuProcessSample>());

    // A transient read failure yields empty rather than propagating: the registry treats one vendor's
    // bad tick as "no data this tick" and must still render the other vendor's GPUs.
    private T Safe<T>(Func<IAmdReader, T> read, T fallback)
    {
        if (_reader == null) return fallback;

        try
        {
            return read(_reader);
        }
        catch
        {
            return fallback;
        }
    }
}
