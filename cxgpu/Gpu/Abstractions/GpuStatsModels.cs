namespace cxgpu.Gpu;

/// <summary>
/// Per-GPU utilization and metrics snapshot
/// </summary>
internal record GpuSample(
    int Index,
    double UtilizationPercent,
    double MemoryUsedPercent,
    double MemoryUsedMb,
    double MemoryTotalMb,
    double TemperatureC,
    double PowerDrawWatts,
    double PowerLimitWatts,
    double FanSpeedPercent,
    double SmClockMhz,
    double MemClockMhz,
    // NVENC/NVDEC engine utilization (%). nvidia-smi reports these as "utilization.encoder"
    // / "utilization.decoder"; unsupported on some parts, where they come back as N/A -> 0.
    double EncoderPercent = 0,
    double DecoderPercent = 0,
    // Real throttle indicators, from the NAMED clocks_throttle_reasons.* booleans. Deliberately
    // excludes the benign bits (gpu_idle, applications_clocks_setting), which are "Active" on an
    // idle card and would cry wolf.
    bool ThrottleThermal = false,
    bool ThrottlePower = false,
    bool ThrottleHwSlowdown = false,
    // What the owning backend can actually report, stamped on by the registry.
    //
    // Several metrics above are non-nullable doubles, so a vendor with no such sensor can only put 0
    // there — and a rendered "0%" would claim a measurement that was never taken. This carries the
    // truth alongside the numbers: the AMD APU has no fan and no power cap, so its cards for those
    // metrics are omitted rather than drawn at zero. Defaults to everything supported, which keeps
    // the single-vendor NVIDIA path (and every existing construction site) behaving as before.
    GpuCapabilities? Capabilities = null)
{
    /// <summary>Capabilities of the owning backend, defaulting to "everything supported".</summary>
    public GpuCapabilities Caps => Capabilities ?? AllSupported;

    private static readonly GpuCapabilities AllSupported = new(
        FanSpeed: true, PowerLimit: true, ThrottleReasons: true, EncoderDecoder: true,
        PerProcessMemory: true, PerProcessSm: true, ProcessSignal: true, CudaVersion: true);
}

/// <summary>
/// Information about a process running on a GPU. The per-engine percentages come from
/// <c>nvidia-smi pmon</c>, which is a separate call from the compute-apps query that supplies
/// memory; they are null when pmon is unavailable or reports "-" (idle/unsupported), so the UI can
/// distinguish "no data" from a genuine 0%.
/// </summary>
internal record GpuProcessSample(
    int Pid,
    string Name,
    double MemoryUsedMb,
    int GpuIndex,
    double? SmPercent = null,
    double? MemPercent = null,
    double? EncPercent = null,
    double? DecPercent = null);

/// <summary>
/// Snapshot of all GPU statistics
/// </summary>
internal record GpuSnapshot(
    IReadOnlyList<GpuSample> Gpus,
    IReadOnlyList<GpuProcessSample> Processes);

/// <summary>
/// Static GPU device information
/// </summary>
internal record GpuDeviceInfo(
    int Index,
    string Name,
    string DriverVersion,
    string VBiosVersion,
    string PcieGenWidth,
    string CudaVersion,
    double MemoryTotalMb,
    double PowerLimitWatts,
    double TemperatureLimitC,
    // Which backend and data source produced this, stamped on by the registry. Surfaced in the
    // spec-sheet because a vendor can be read several ways (AMD via sysfs or the rocm-smi CLI) and
    // the numbers differ subtly between them — so "which source is live" has to be answerable
    // without attaching a debugger.
    string Backend = "",
    string Mechanism = "",
    // Stable per-card identity: the normalized PCI address ("0000:01:00.0").
    //
    // Index CANNOT serve this purpose — the registry reassigns indices globally, so a backend that
    // fails to probe on one boot shifts every later card's index. Config keyed on index would then
    // silently apply one card's settings to another.
    //
    // PCI over the vendor UUID because BOTH vendors expose it (UUID is NVIDIA-only, so a UUID key
    // would need a second scheme for AMD), because it is already the AMD sysfs directory name, and
    // because a human can check "0000:01:00.0" against lspci while a UUID is opaque. The tradeoff is
    // that moving a card to another slot changes its key; for per-card thresholds that is arguably
    // right, since slot-dependent behaviour is usually about airflow.
    //
    // "" when the backend cannot report one — a legitimate state that must fall through to vendor
    // defaults, NOT match a "" config key.
    string CardId = "",
    // The vendor UUID where one exists. RECORDED ONLY — nothing looks a card up by this today.
    //
    // Kept because capturing it is nearly free (NVIDIA already runs the query that returns it) and it
    // preserves a future option: recognising "same UUID, new PCI address" would let a later version
    // migrate a config entry when a card moves slots, instead of silently reverting it to defaults.
    // Adding a second lookup path now would require a rule for what happens when the two disagree;
    // that belongs with the feature that needs it.
    string CardUuid = "");
