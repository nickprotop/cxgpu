namespace cxgpu.Gpu;

/// <summary>
/// One way of reading AMD GPU telemetry. A vendor can be readable through several mechanisms — Linux
/// sysfs, or the <c>amd-smi</c>/<c>rocm-smi</c> CLI — and which one is available depends on the
/// platform and what is installed.
///
/// That choice is deliberately INTERNAL to <see cref="AmdBackend"/>: the plugin is the unit the user
/// enables and the unit that would ship as a DLL, so there is exactly one AMD backend. Exposing the
/// mechanisms as separate plugins would put entries in the config of which at most one could ever
/// apply, and would force the registry to arbitrate between two capability sets.
/// </summary>
internal interface IAmdReader
{
    /// <summary>Short name of the data source, surfaced so it is diagnosable which one is live.</summary>
    string Mechanism { get; }

    /// <summary>
    /// Every name this reader answers to, for matching a user-forced mechanism.
    ///
    /// Distinct from <see cref="Mechanism"/> because that reports the RESOLVED source and is only
    /// meaningful after probing — the CLI reader cannot know whether it will end up on amd-smi or
    /// rocm-smi until it looks. Matching a forced choice against the resolved name would therefore
    /// reject the reader before it ever got the chance to probe.
    /// </summary>
    IReadOnlyList<string> MechanismAliases { get; }

    /// <summary>Whether this mechanism can serve data here and now. Must be cheap and must not throw.</summary>
    bool Probe();

    /// <summary>
    /// What this mechanism can report. Differs BETWEEN mechanisms on the same hardware: the sysfs
    /// reader can attribute memory per process via fdinfo, while rocm-smi's --showpids returns nothing
    /// on this device. Reported from whichever reader won, so the capability is always the truth about
    /// the live source.
    /// </summary>
    GpuCapabilities Capabilities { get; }

    IReadOnlyList<GpuSample> ReadSamples();

    IReadOnlyList<GpuDeviceInfo> ReadDeviceInfo();

    IReadOnlyList<GpuProcessSample> ReadProcesses();
}
