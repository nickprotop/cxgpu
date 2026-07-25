namespace cxnvmon.Stats;

/// <summary>
/// Adapts an existing <see cref="IGpuStatsProvider"/> to <see cref="IGpuBackend"/>.
///
/// TRANSITIONAL. This exists so the move into <c>Gpu/</c> can be a pure relocation — the registry
/// becomes the real data path immediately, while the vendor providers are still converted into proper
/// backends one at a time in later steps. Each provider that gains a native
/// <see cref="GpuBackendPlugin"/> implementation drops out of here; this file is deleted once the last
/// one has.
/// </summary>
internal sealed class LegacyProviderBackend : IGpuBackend
{
    private readonly IGpuStatsProvider _provider;

    public LegacyProviderBackend(IGpuStatsProvider provider, GpuBackendInfo info, GpuCapabilities capabilities)
    {
        _provider = provider;
        Info = info;
        Capabilities = capabilities;
    }

    public GpuBackendInfo Info { get; }

    public GpuCapabilities Capabilities { get; }

    /// <summary>
    /// True when the wrapped provider actually returns a GPU. This exercises the source rather than
    /// merely checking a tool exists, which matters: a driver/library version mismatch leaves
    /// nvidia-smi installed while every invocation fails.
    /// </summary>
    public bool Probe()
    {
        try
        {
            return _provider.ReadSnapshot().Gpus.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    // The registry calls ReadSamples() and ReadProcesses() back-to-back, but the wrapped provider
    // produces both in ONE ReadSnapshot() (one set of nvidia-smi invocations). Calling it twice per
    // tick would double the subprocess cost, so the snapshot is taken once and reused for the pair.
    // Not a cache with a lifetime — just "these two calls share the read that produced them".
    private GpuSnapshot? _pending;

    public IReadOnlyList<GpuSample> ReadSamples()
    {
        _pending = _provider.ReadSnapshot();
        return _pending.Gpus;
    }

    public IReadOnlyList<GpuDeviceInfo> ReadDeviceInfo() => _provider.ReadDeviceInfo();

    public IReadOnlyList<GpuProcessSample> ReadProcesses()
    {
        var snapshot = _pending ?? _provider.ReadSnapshot();
        _pending = null;
        return snapshot.Processes;
    }

    /// <summary>
    /// Signalling stays with the UI until the dedicated step that moves it into the backends, so this
    /// adapter reports it as unsupported rather than pretending to route it.
    /// </summary>
    public GpuSignalResult SignalProcess(int pid, GpuSignal signal) => GpuSignalResult.NotSupported;
}
