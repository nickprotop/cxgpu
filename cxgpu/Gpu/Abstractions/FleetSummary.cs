using cxgpu.Helpers;

namespace cxgpu.Gpu;

/// <summary>
/// Aggregate view of every GPU in a snapshot — the numbers that exist nowhere else in the app, since
/// every other view shows one card at a time. Answering "how much VRAM is free across the box?" or
/// "what is this machine drawing?" currently means switching GPUs and adding up by hand.
///
/// Pure logic with no UI, so it can be reasoned about and checked against the vendor tools directly.
/// </summary>
/// <param name="GpuCount">How many GPUs the snapshot covers.</param>
/// <param name="VramUsedMb">Summed VRAM in use.</param>
/// <param name="VramTotalMb">Summed VRAM capacity.</param>
/// <param name="PowerDrawWatts">Summed power draw across GPUs that actually report it.</param>
/// <param name="PowerReportingGpus">
/// How many GPUs contributed to <paramref name="PowerDrawWatts"/>. Exposed so the UI can say when the
/// figure covers fewer cards than the fleet — a total that silently omits a card would read as
/// complete.
/// </param>
/// <param name="HottestGpuIndex">Index of the warmest GPU, or null when the snapshot is empty.</param>
/// <param name="HottestTemperatureC">That GPU's temperature.</param>
/// <param name="ProcessCount">Total processes across all GPUs.</param>
/// <param name="Throttling">Each currently throttled GPU and its reasons.</param>
internal sealed record FleetSummary(
    int GpuCount,
    double VramUsedMb,
    double VramTotalMb,
    double PowerDrawWatts,
    int PowerReportingGpus,
    int? HottestGpuIndex,
    double HottestTemperatureC,
    int ProcessCount,
    IReadOnlyList<(int GpuIndex, string Reason)> Throttling)
{
    public static readonly FleetSummary Empty =
        new(0, 0, 0, 0, 0, null, 0, 0, Array.Empty<(int, string)>());

    /// <summary>
    /// Whether every GPU contributed to the power total. False means the figure covers a subset,
    /// because at least one backend reports no power reading.
    /// </summary>
    public bool PowerIsComplete => PowerReportingGpus == GpuCount;

    public static FleetSummary From(GpuSnapshot snapshot)
    {
        if (snapshot.Gpus.Count == 0) return Empty;

        double vramUsed = 0, vramTotal = 0, power = 0;
        int powerReporting = 0;
        int? hottestIndex = null;
        double hottest = double.MinValue;
        var throttling = new List<(int, string)>();

        foreach (var gpu in snapshot.Gpus)
        {
            vramUsed += gpu.MemoryUsedMb;
            vramTotal += gpu.MemoryTotalMb;

            // Only count power for cards that actually measure it. Adding a zero for a card with no
            // sensor would understate the fleet total while looking like a measurement — the same
            // null-versus-zero rule the per-GPU views follow. A reading of exactly 0 W from a real
            // sensor is not distinguishable here, but an idle GPU still draws some watts in practice,
            // so treating 0 as "not reported" is the safer reading.
            if (gpu.PowerDrawWatts > 0)
            {
                power += gpu.PowerDrawWatts;
                powerReporting++;
            }

            if (gpu.TemperatureC > hottest)
            {
                hottest = gpu.TemperatureC;
                hottestIndex = gpu.Index;
            }

            // Reasons come from GpuFormat, so this shares the capability gate and the wording with the
            // hero card's chip. A backend without throttle-reason bits contributes nothing here, rather
            // than appearing as "not throttling" — which would be a claim it cannot support.
            var reasons = GpuFormat.ThrottleReasons(gpu);
            if (reasons.Count > 0)
                throttling.Add((gpu.Index, string.Join(" · ", reasons)));
        }

        return new FleetSummary(
            GpuCount: snapshot.Gpus.Count,
            VramUsedMb: vramUsed,
            VramTotalMb: vramTotal,
            PowerDrawWatts: power,
            PowerReportingGpus: powerReporting,
            HottestGpuIndex: hottestIndex,
            HottestTemperatureC: hottestIndex == null ? 0 : hottest,
            ProcessCount: snapshot.Processes.Count,
            Throttling: throttling);
    }
}
