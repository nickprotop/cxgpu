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

    /// <summary>
    /// Forces a specific mechanism by name ("sysfs", "rocm-smi", "amd-smi"), overriding the preference
    /// order. Set from <c>CXNVMON_AMD_READER</c>; the user-facing plugin setting will feed the same
    /// field. Its main value is TESTING: it is the only way to exercise the Windows CLI path on a Linux
    /// box, where sysfs would otherwise always win.
    /// </summary>
    private string? ForcedMechanism
    {
        get
        {
            // The stored setting wins; the environment variable remains as an override that needs no
            // config file, which is what makes it usable in scripted verification.
            var configured = _readerSetting ?? Environment.GetEnvironmentVariable("CXNVMON_AMD_READER");
            return configured is { Length: > 0 } value &&
                   !value.Equals(AutoReader, StringComparison.OrdinalIgnoreCase)
                ? value
                : null;
        }
    }

    private const string AutoReader = "auto";
    private const string ReaderSettingKey = "Reader";

    private string? _readerSetting;

    /// <summary>
    /// The one thing worth configuring about AMD: which mechanism reads it. On Linux with rocm-smi
    /// installed BOTH work, so this is a real choice the user can make and cxnvmon itself cannot —
    /// exactly the case plugin-declared settings exist for.
    ///
    /// Allowing rocm-smi on Linux is also a TESTING affordance: it is the only way to exercise the
    /// Windows code path on a Linux box, where sysfs would otherwise always win.
    /// </summary>
    public override IReadOnlyList<PluginSetting> GetSettings() => new[]
    {
        new PluginSetting(
            Key: ReaderSettingKey,
            Label: "Data source",
            Kind: PluginSettingKind.Choice,
            Default: AutoReader,
            Hint: "auto prefers sysfs on Linux (faster, and the only source with per-process data); " +
                  "the CLI is used on Windows",
            Options: new[] { AutoReader, "sysfs", "rocm-smi" },
            RequiresRestart: true)
    };

    public override void ApplySettings(IReadOnlyDictionary<string, string?> values)
    {
        if (values.TryGetValue(ReaderSettingKey, out var reader) && !string.IsNullOrWhiteSpace(reader))
            _readerSetting = reader.Trim();
    }

    public override bool Probe()
    {
        var forced = ForcedMechanism;

        // First pass honours a forced mechanism; the second ignores it. A forced reader that cannot
        // probe must NOT leave the vendor dark — falling back is better than showing no AMD GPU
        // because of a stale setting.
        if (forced != null && TrySelectReader(forced)) return true;

        return TrySelectReader(null);
    }

    private bool TrySelectReader(string? mechanismFilter)
    {
        foreach (var factory in ReaderFactories)
        {
            try
            {
                var candidate = factory();

                // Match against the ALIASES, not the resolved Mechanism: the CLI reader does not know
                // whether it will land on amd-smi or rocm-smi until it probes, so filtering on the
                // resolved name would reject it before it ever looked.
                if (mechanismFilter != null &&
                    !candidate.MechanismAliases.Any(a => a.Equals(mechanismFilter, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

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
