namespace cxnvmon.Stats;

/// <summary>
/// Reads AMD GPU telemetry through the <c>amd-smi</c> / <c>rocm-smi</c> CLI.
///
/// This is the Windows path (sysfs is a Linux kernel interface and cannot exist there) and the Linux
/// fallback when sysfs is unavailable.
///
/// NOT YET IMPLEMENTED — <see cref="Probe"/> returns false so <see cref="AmdBackend"/> falls through
/// to the sysfs reader. Implemented in the next step against verified <c>rocm-smi --json</c> output.
/// </summary>
internal sealed class AmdCliReader : IAmdReader
{
    public string Mechanism => "rocm-smi";

    /// <summary>
    /// The CLI cannot attribute memory per process on this hardware — <c>rocm-smi --showpids</c>
    /// returns "No JSON data to report" — so that capability is false even once reading works.
    /// </summary>
    public GpuCapabilities Capabilities => new(
        FanSpeed: false,
        PowerLimit: false,
        ThrottleReasons: false,
        EncoderDecoder: false,
        PerProcessMemory: false,
        PerProcessSm: false,
        ProcessSignal: true,
        CudaVersion: false);

    public bool Probe() => false;

    public IReadOnlyList<GpuSample> ReadSamples() => Array.Empty<GpuSample>();

    public IReadOnlyList<GpuDeviceInfo> ReadDeviceInfo() => Array.Empty<GpuDeviceInfo>();

    public IReadOnlyList<GpuProcessSample> ReadProcesses() => Array.Empty<GpuProcessSample>();
}
