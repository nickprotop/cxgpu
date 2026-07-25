namespace cxnvmon.Stats;

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
