using System.Globalization;
using System.Text;
using cxgpu.Gpu;
using cxgpu.Helpers;

namespace cxgpu.Export;

/// <summary>
/// Renders a snapshot as Prometheus text-format metrics.
///
/// CAPABILITY GATING MATTERS MORE HERE THAN IN THE UI. A metric the backend cannot read is OMITTED,
/// never exported as 0 — Prometheus will happily average a fabricated zero into a dashboard forever,
/// and unlike a blank UI cell nobody ever sees it happen. Same null-versus-zero rule as the views,
/// with worse consequences for getting it wrong.
///
/// Series are labelled by the STABLE card id (PCI address) as well as the index, because an
/// index-labelled series silently re-points at different hardware when a backend fails to probe — a
/// bug that is invisible in the UI but permanent in a time-series database.
/// </summary>
internal static class PrometheusFormatter
{
    private const string Prefix = "cxgpu_";

    public static string Render(GpuSnapshot snapshot, IReadOnlyList<GpuDeviceInfo> deviceInfos)
    {
        var sb = new StringBuilder();

        Metric(sb, "up", "gauge", "1 when the backend is reporting this GPU.");
        foreach (var gpu in snapshot.Gpus)
            Sample(sb, "up", gpu, deviceInfos, 1);

        Gauge(sb, snapshot, deviceInfos, "utilization_percent",
            "GPU core utilization (%).", g => g.UtilizationPercent);

        Gauge(sb, snapshot, deviceInfos, "memory_used_bytes",
            "VRAM in use (bytes).", g => g.MemoryUsedMb * 1024 * 1024);

        Gauge(sb, snapshot, deviceInfos, "memory_total_bytes",
            "VRAM capacity (bytes).", g => g.MemoryTotalMb * 1024 * 1024);

        Gauge(sb, snapshot, deviceInfos, "temperature_celsius",
            "GPU temperature (°C).", g => g.TemperatureC);

        // Power, fan and the media engines are all capability-gated: a card with no sensor contributes
        // no sample rather than a zero.
        Gauge(sb, snapshot, deviceInfos, "power_watts",
            "Power draw (W). Absent when the card reports none.",
            g => g.PowerDrawWatts,
            include: g => g.Caps.PowerLimit && g.PowerDrawWatts > 0);

        Gauge(sb, snapshot, deviceInfos, "power_limit_watts",
            "Power cap (W). Absent when the card reports none.",
            g => g.PowerLimitWatts,
            include: g => g.Caps.PowerLimit && g.PowerLimitWatts > 0);

        Gauge(sb, snapshot, deviceInfos, "fan_percent",
            "Fan speed (%). Absent on fanless parts.",
            g => g.FanSpeedPercent,
            include: g => g.Caps.FanSpeed);

        Gauge(sb, snapshot, deviceInfos, "clock_sm_mhz", "SM clock (MHz).", g => g.SmClockMhz);
        Gauge(sb, snapshot, deviceInfos, "clock_mem_mhz", "Memory clock (MHz).", g => g.MemClockMhz);

        Gauge(sb, snapshot, deviceInfos, "encoder_percent",
            "Video encoder utilization (%). Absent without media engines.",
            g => g.EncoderPercent,
            include: g => g.Caps.EncoderDecoder);

        Gauge(sb, snapshot, deviceInfos, "decoder_percent",
            "Video decoder utilization (%). Absent without media engines.",
            g => g.DecoderPercent,
            include: g => g.Caps.EncoderDecoder);

        RenderThrottles(sb, snapshot, deviceInfos);
        RenderProcessCounts(sb, snapshot, deviceInfos);

        return sb.ToString();
    }

    /// <summary>
    /// Throttle state, split by reason so an alerting rule can distinguish a thermal trip from an
    /// expected power cap. Emitted ONLY for backends that can read the reasons — a card that cannot
    /// report them contributes nothing rather than a run of zeros meaning "not throttling".
    /// </summary>
    private static void RenderThrottles(StringBuilder sb, GpuSnapshot snapshot,
                                        IReadOnlyList<GpuDeviceInfo> deviceInfos)
    {
        var capable = snapshot.Gpus.Where(g => g.Caps.ThrottleReasons).ToList();
        if (capable.Count == 0) return;

        Metric(sb, "throttled", "gauge", "1 while the named throttle reason is active.");

        foreach (var gpu in capable)
        {
            Sample(sb, "throttled", gpu, deviceInfos, gpu.ThrottleThermal ? 1 : 0, ("reason", "thermal"));
            Sample(sb, "throttled", gpu, deviceInfos, gpu.ThrottleHwSlowdown ? 1 : 0, ("reason", "hw_slowdown"));
            Sample(sb, "throttled", gpu, deviceInfos, gpu.ThrottlePower ? 1 : 0, ("reason", "power_cap"));
        }
    }

    private static void RenderProcessCounts(StringBuilder sb, GpuSnapshot snapshot,
                                            IReadOnlyList<GpuDeviceInfo> deviceInfos)
    {
        Metric(sb, "process_count", "gauge", "Processes with memory allocated on this GPU.");
        foreach (var gpu in snapshot.Gpus)
            Sample(sb, "process_count", gpu, deviceInfos,
                   snapshot.Processes.Count(p => p.GpuIndex == gpu.Index));
    }

    private static void Gauge(StringBuilder sb, GpuSnapshot snapshot,
                              IReadOnlyList<GpuDeviceInfo> deviceInfos,
                              string name, string help, Func<GpuSample, double> value,
                              Func<GpuSample, bool>? include = null)
    {
        var included = snapshot.Gpus.Where(g => include?.Invoke(g) ?? true).ToList();

        // A metric with no samples is omitted entirely, HELP and TYPE included: an empty declaration
        // tells a scraper the series exists but has no data, which is a different claim.
        if (included.Count == 0) return;

        Metric(sb, name, "gauge", help);
        foreach (var gpu in included)
            Sample(sb, name, gpu, deviceInfos, value(gpu));
    }

    private static void Metric(StringBuilder sb, string name, string type, string help)
    {
        sb.Append("# HELP ").Append(Prefix).Append(name).Append(' ').Append(help).Append('\n');
        sb.Append("# TYPE ").Append(Prefix).Append(name).Append(' ').Append(type).Append('\n');
    }

    private static void Sample(StringBuilder sb, string name, GpuSample gpu,
                               IReadOnlyList<GpuDeviceInfo> deviceInfos, double value,
                               params (string Key, string Value)[] extraLabels)
    {
        var info = deviceInfos.FirstOrDefault(d => d.Index == gpu.Index);

        sb.Append(Prefix).Append(name).Append('{');
        sb.Append("gpu=\"").Append(gpu.Index).Append('"');
        sb.Append(",name=\"").Append(Escape(info?.Name ?? "")).Append('"');
        sb.Append(",backend=\"").Append(Escape(info?.Backend ?? "")).Append('"');

        // Omitted rather than empty when the backend reports no identity: an empty label value is a
        // real label in Prometheus, and every such card would share one series.
        if (!string.IsNullOrEmpty(info?.CardId))
            sb.Append(",card=\"").Append(Escape(info.CardId)).Append('"');

        foreach (var (key, val) in extraLabels)
            sb.Append(',').Append(key).Append("=\"").Append(Escape(val)).Append('"');

        sb.Append("} ");
        sb.Append(value.ToString("0.###", CultureInfo.InvariantCulture));
        sb.Append('\n');
    }

    /// <summary>
    /// Escapes a label value per the Prometheus text format: backslash, double quote and newline.
    /// GPU names come from vendor tools and are not guaranteed free of any of them.
    /// </summary>
    private static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
}
