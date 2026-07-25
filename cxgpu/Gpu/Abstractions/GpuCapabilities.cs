namespace cxgpu.Gpu;

/// <summary>
/// What a backend can actually report, declared per metric rather than assumed.
///
/// This exists because vendors genuinely differ, and the differences are not hypothetical: the AMD
/// APU on the development box has no fan sensor at all (<c>fan1_input</c> is ENOENT), reports no
/// encoder/decoder utilization, and exposes no throttle-reason bits — while NVIDIA reports all three.
///
/// The rule these flags enforce: a metric that is UNSUPPORTED must never be rendered as a measured
/// zero. Unsupported metrics stay null in the sample and their UI is omitted; a genuine 0% is shown
/// as 0%. Conflating the two would quietly invent data.
/// </summary>
/// <param name="FanSpeed">Fan RPM/percentage is readable (false on fanless parts).</param>
/// <param name="PowerLimit">A configured power cap is readable, so power can be shown as a ratio.</param>
/// <param name="ThrottleReasons">Named throttle-reason flags are available.</param>
/// <param name="EncoderDecoder">NVENC/NVDEC-style engine utilization is available.</param>
/// <param name="PerProcessMemory">Per-process GPU memory can be attributed to PIDs.</param>
/// <param name="PerProcessSm">Per-process compute (SM) utilization is available.</param>
/// <param name="ProcessSignal">
/// This backend can signal its processes. False for the demo backend, whose PIDs are synthetic — which
/// is what makes signalling a fake process structurally impossible rather than merely discouraged.
/// </param>
/// <param name="CudaVersion">A CUDA runtime version is reportable.</param>
internal record GpuCapabilities(
    bool FanSpeed = false,
    bool PowerLimit = false,
    bool ThrottleReasons = false,
    bool EncoderDecoder = false,
    bool PerProcessMemory = false,
    bool PerProcessSm = false,
    bool ProcessSignal = false,
    bool CudaVersion = false);
