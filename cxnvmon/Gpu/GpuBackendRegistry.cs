namespace cxnvmon.Stats;

/// <summary>
/// Holds the GPU backends, probes them once, and presents their combined output as a single
/// <see cref="IGpuStatsProvider"/> — the seam the whole UI already talks to, so multi-vendor support
/// is invisible above this line.
///
/// Probing happens at construction and deliberately does NOT require a
/// <c>ConsoleWindowSystem</c>: the factory runs before the window system exists. Registering the
/// backends as framework plugins is a separate, later call
/// (<see cref="RegisterWithPluginSystem"/>) made once the window system is available.
/// </summary>
internal sealed class GpuBackendRegistry : IGpuStatsProvider
{
    private readonly List<IGpuBackend> _active = new();

    /// <summary>The backends that probed successfully, in stable presentation order.</summary>
    public IReadOnlyList<IGpuBackend> ActiveBackends => _active;

    /// <summary>
    /// Probes each candidate and keeps those that can serve data. Order is significant and stable:
    /// it fixes which vendor owns GPU index 0, so the selected GPU does not move between runs.
    /// A backend whose probe throws is treated as absent — a broken vendor tool must never stop the
    /// app or hide another vendor's GPUs.
    /// </summary>
    public GpuBackendRegistry(IEnumerable<IGpuBackend> candidates)
    {
        foreach (var backend in candidates)
        {
            try
            {
                if (backend.Probe())
                    _active.Add(backend);
            }
            catch
            {
                // Probe must never be fatal; treat failure as "this vendor is not here".
            }
        }
    }

    /// <summary>
    /// Registers each active backend with the framework's plugin system, so plugin state, events and
    /// service lookup (<c>Gpu.NVIDIA</c>, <c>Gpu.AMD</c>) work as documented. Split from the
    /// constructor because probing must not depend on the window system existing yet.
    /// </summary>
    public void RegisterWithPluginSystem(SharpConsoleUI.ConsoleWindowSystem windowSystem)
    {
        foreach (var backend in _active)
        {
            if (backend is SharpConsoleUI.Plugins.IPlugin plugin)
            {
                try
                {
                    windowSystem.PluginStateService.LoadPlugin(plugin);
                }
                catch (Exception ex)
                {
                    // Registration is for discoverability only — the app reads backends directly, so a
                    // failure here must not cost us the GPU data.
                    windowSystem.LogService.LogError(
                        $"Failed to register GPU backend '{backend.Info.Name}'", ex, "cxnvmon");
                }
            }
        }
    }

    /// <summary>
    /// Combined snapshot across every active backend.
    ///
    /// GPU indices are REASSIGNED to be globally unique and contiguous, in backend order: a backend's
    /// own indices are vendor-local (both NVIDIA and AMD call their first card 0), so using them
    /// directly would collide. Processes are remapped through the same translation so they stay
    /// attached to the right card.
    /// </summary>
    public GpuSnapshot ReadSnapshot()
    {
        var gpus = new List<GpuSample>();
        var procs = new List<GpuProcessSample>();
        int nextIndex = 0;

        foreach (var backend in _active)
        {
            IReadOnlyList<GpuSample> samples;
            IReadOnlyList<GpuProcessSample> backendProcs;
            try
            {
                samples = backend.ReadSamples();
                backendProcs = backend.ReadProcesses();
            }
            catch
            {
                // One vendor failing this tick must not blank the others' data.
                continue;
            }

            // Vendor-local index -> global index for this backend.
            var indexMap = new Dictionary<int, int>();
            foreach (var sample in samples)
            {
                indexMap[sample.Index] = nextIndex;
                gpus.Add(sample with { Index = nextIndex });
                nextIndex++;
            }

            foreach (var proc in backendProcs)
            {
                if (indexMap.TryGetValue(proc.GpuIndex, out var global))
                    procs.Add(proc with { GpuIndex = global });
            }
        }

        return new GpuSnapshot(gpus, procs);
    }

    /// <summary>
    /// Combined static device info, with indices reassigned by the same rule as
    /// <see cref="ReadSnapshot"/> so the two agree.
    /// </summary>
    public IReadOnlyList<GpuDeviceInfo> ReadDeviceInfo()
    {
        var infos = new List<GpuDeviceInfo>();
        int nextIndex = 0;

        foreach (var backend in _active)
        {
            IReadOnlyList<GpuDeviceInfo> backendInfos;
            try
            {
                backendInfos = backend.ReadDeviceInfo();
            }
            catch
            {
                continue;
            }

            foreach (var info in backendInfos)
            {
                infos.Add(info with { Index = nextIndex });
                nextIndex++;
            }
        }

        return infos;
    }

    /// <summary>
    /// Finds the backend owning a global GPU index, so an action (e.g. a signal) is routed to the
    /// vendor that can actually perform it. Null when the index is unknown.
    /// </summary>
    public IGpuBackend? BackendForGpu(int globalIndex)
    {
        int nextIndex = 0;
        foreach (var backend in _active)
        {
            int count;
            try
            {
                count = backend.ReadSamples().Count;
            }
            catch
            {
                continue;
            }

            if (globalIndex >= nextIndex && globalIndex < nextIndex + count)
                return backend;

            nextIndex += count;
        }

        return null;
    }
}
