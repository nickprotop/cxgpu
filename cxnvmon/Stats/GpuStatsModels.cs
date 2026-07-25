namespace cxnvmon.Stats;

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
    bool ThrottleHwSlowdown = false);

/// <summary>
/// Information about a process running on a GPU
/// </summary>
internal record GpuProcessSample(
    int Pid,
    string Name,
    double MemoryUsedMb,
    int GpuIndex);

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
    double TemperatureLimitC);
