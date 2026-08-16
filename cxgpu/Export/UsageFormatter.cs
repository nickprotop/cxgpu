using System.Globalization;
using System.IO;
using System.Text.Json;
using cxgpu.Gpu;
using cxgpu.Gpu.Alerts;
using cxgpu.Helpers;

namespace cxgpu.Export;

/// <summary>
/// Renders a GPU usage snapshot as a human-readable table or JSON.
///
/// Unlike the Prometheus formatter (which exports to a monitoring system), this produces output
/// suitable for direct human consumption or a simple machine-parseable dump. Capability-gating is
/// enforced: unsupported metrics render as "N/A" rather than 0, matching the philosophy that a
/// missing measurement is not the same as a measured zero.
/// </summary>
internal static class UsageFormatter
{
    /// <summary>
    /// Render a GPU usage snapshot as a bordered grid table to the provided writer.
    /// </summary>
    /// <param name="snapshot">The snapshot containing per-GPU metrics.</param>
    /// <param name="deviceInfos">Static device information for display names.</param>
    /// <param name="writer">The text writer to output to.</param>
    /// <param name="useColor">Whether to use ANSI color codes for threshold-based coloring.</param>
    /// <param name="appendProcesses">Whether to also render a per-process table below the GPU grid.</param>
    /// <param name="view">
    /// How to render — see <see cref="UsageView"/>. One parameter rather than five: this signature
    /// reached eight arguments, of which three consecutive bools meant transposing any two compiled
    /// cleanly and rendered the wrong thing.
    /// </param>
    /// <param name="useColor">
    /// Resolved by the caller, because the decision needs <c>Console.IsOutputRedirected</c> and the
    /// NO_COLOR environment variable — neither of which a formatter should be reaching for.
    /// </param>
    /// <param name="watching">
    /// True when this frame is one of many. Kept OUT of the view: the view is what the user asked
    /// for, this is where the renderer happens to be.
    /// </param>
    public static void RenderTable(GpuSnapshot snapshot,
                                   IReadOnlyList<GpuDeviceInfo> deviceInfos,
                                   TextWriter writer,
                                   UsageView view,
                                   bool useColor,
                                   bool watching = false)
    {
        if (snapshot.Gpus.Count == 0)
        {
            writer.WriteLine("No GPUs detected.");
            return;
        }

        // Collect GPU rows with device info
        var rows = new List<(GpuSample Gpu, GpuDeviceInfo? Info)>();
        foreach (var gpu in snapshot.Gpus)
        {
            var info = deviceInfos.FirstOrDefault(d => d.Index == gpu.Index);
            rows.Add((gpu, info));
        }

        // Compute column widths from header + all rows
        int gpuName = "GPU".Length;
        int util = "Util".Length;
        int memUsed = "Mem Used".Length;
        int temp = "Temp".Length;
        int power = "Power".Length;
        int fan = "Fan".Length;
        int clock = "Clock".Length;
        int encDec = "Enc/Dec".Length;
        int throttle = "Throttle".Length;

        foreach (var (gpu, info) in rows)
        {
            gpuName = Math.Max(gpuName, (info?.Name ?? "N/A").Length);
            util = Math.Max(util, FormatPercent(gpu, gpu.Caps).Length);
            memUsed = Math.Max(memUsed, FormatMemory(gpu, gpu.Caps).Length);
            temp = Math.Max(temp, FormatTemperature(gpu, gpu.Caps).Length);
            power = Math.Max(power, FormatPower(gpu, gpu.Caps).Length);
            fan = Math.Max(fan, FormatFan(gpu, gpu.Caps).Length);
            clock = Math.Max(clock, FormatClock(gpu, gpu.Caps).Length);
            encDec = Math.Max(encDec, FormatEncDec(gpu, gpu.Caps).Length);
            throttle = Math.Max(throttle, FormatThrottle(gpu, gpu.Caps).Length);
            }

        // Pad to minimum sensible widths
        gpuName = Math.Max(gpuName, 4);
        util = Math.Max(util, 5);
        memUsed = Math.Max(memUsed, 11);  // "Mem Used"
        temp = Math.Max(temp, 5);
        power = Math.Max(power, 6);
        fan = Math.Max(fan, 4);
        clock = Math.Max(clock, 10);   // "4095/4095 MHz"
        encDec = Math.Max(encDec, 8);  // "  0.0% /   0.0%"
        throttle = Math.Max(throttle, 4);  // "N/A" or "TP"

        int[] gpuWidths = { gpuName, util, memUsed, temp, power, clock, encDec, throttle, fan };

        // Top border
        writer.WriteLine(BorderRow('┌', '┐', '┬', gpuWidths));

        // Header row
        var headerColor = useColor ? Ansi.Dim : (string?)null;
        var headerMeta = new List<(string? Color, Align Alignment)>
        {
            ((string?)null, Align.Left),
            (headerColor, Align.Center),
            (headerColor, Align.Center),
            (headerColor, Align.Center),
            (headerColor, Align.Center),
            (headerColor, Align.Center),
            (headerColor, Align.Center),
            (headerColor, Align.Center),
            (headerColor, Align.Center),
            };
        writer.WriteLine(DrawGridRow(
            new List<string> { "GPU", "Util", "Mem Used", "Temp", "Power", "Clock", "Enc/Dec", "Throttle", "Fan" },
            gpuWidths, headerMeta));

        // Separator row
        writer.WriteLine(BorderRow('├', '┤', '┼', gpuWidths));

        // Data rows
        foreach (var (gpu, info) in rows)
        {
            string? pctColor = useColor ? ColorForPercentile(gpu.UtilizationPercent) : null;
            string? tempColor = useColor ? ColorForTemperature(gpu) : null;
            double memPct = gpu.MemoryTotalMb > 0
                ? (gpu.MemoryUsedMb / gpu.MemoryTotalMb * 100)
                : 0;
            string? memColor = useColor ? ColorForPercentile(memPct) : null;
            string? fanColor = useColor && gpu.Caps.FanSpeed
                ? ColorForPercentile(gpu.FanSpeedPercent)
                : null;
            string? powerColor = useColor && gpu.Caps.PowerLimit
                ? ColorForPercentile(GpuFormat.PowerPercent(gpu))
                : null;

            // Clock — no color, just plain
            string? clockColor = null;

            // Enc/Dec — color based on utilization percentage
            double encPct = gpu.EncoderPercent;
            string? encDecColor = useColor && gpu.Caps.EncoderDecoder
                ? ColorForPercentile(encPct)
                : null;

            // Throttle — colored per character
            string? throttleColor = null;
            if (useColor && gpu.Caps.ThrottleReasons)
            {
                var hasThrottle = gpu.ThrottleThermal || gpu.ThrottlePower || gpu.ThrottleHwSlowdown;
                if (hasThrottle)
                    throttleColor = $"\u001b[38;2;{0xff};{0x6b};{0x6b}m";  // red if any active
            }

            var cellMeta = new List<(string? Color, Align Alignment)>
            {
                ((string?)null, Align.Left),
                (pctColor, Align.Right),
                (memColor, Align.Right),
                (tempColor, Align.Right),
                (powerColor, Align.Right),
                (clockColor, Align.Right),
                (encDecColor, Align.Right),
                (throttleColor, Align.Center),
                (fanColor, Align.Right),
            };
            writer.WriteLine(DrawGridRow(
                new List<string>
                {
                    info?.Name ?? "N/A",
                    FormatPercent(gpu, gpu.Caps),
                    FormatMemory(gpu, gpu.Caps),
                    FormatTemperature(gpu, gpu.Caps),
                    FormatPower(gpu, gpu.Caps),
                    FormatClock(gpu, gpu.Caps),
                    FormatEncDec(gpu, gpu.Caps),
                    FormatThrottle(gpu, gpu.Caps),
                    FormatFan(gpu, gpu.Caps),
                },
                gpuWidths, cellMeta));
        }

        // Bottom border
        writer.WriteLine(BorderRow('└', '┘', '┴', gpuWidths));

        // Watch-mode hint — shown below the table so it refreshes each frame.
        if (watching)
        {
            writer.Write("  Press q to stop.");
        }

        // Print processes if requested
        if (view.AppendProcesses && snapshot.Processes.Count > 0)
        {
            writer.WriteLine();
            RenderProcessTable(snapshot, writer, useColor, view.Top, view.Sort);
        }
    }

    /// <summary>
    /// Render per-GPU-process snapshot as a bordered grid.
    /// </summary>
    /// <param name="snapshot">The snapshot containing process data.</param>
    /// <param name="writer">The text writer to output to.</param>
    /// <param name="useColor">Whether to use ANSI color codes (reserved for future use).</param>
    /// <param name="top">Number of processes to show (null means show all).</param>
    /// <param name="sort">Sort criterion: "memory", "sm", "pid", or "name" (default "memory").</param>
    public static void RenderProcessTable(GpuSnapshot snapshot,
    TextWriter writer,
    bool useColor = false,
    int? top = null,
    string sort = "memory")
    {
        var procs = snapshot.Processes.ToList();

        // Sort processes according to the specified criterion
        procs = SortProcesses(procs, sort);

        // COUNTED BEFORE THE CUT, or the summary line below can never know what it dropped. The
        // widths that follow are measured on the SURVIVORS, so a 200-character path that --top
        // excluded does not stretch a table it no longer appears in.
        var dropped = top is { } n && n < procs.Count ? procs.Count - n : 0;
        if (dropped > 0) procs = procs.Take(top!.Value).ToList();

        int pid = "PID".Length;
        int name = "Name".Length;
        int memUsed = "Mem Used".Length;
        int gpu = "GPU".Length;

        foreach (var proc in procs)
        {
        string pidStr = proc.Pid.ToString(CultureInfo.InvariantCulture);
        pid = Math.Max(pid, pidStr.Length);
        name = Math.Max(name, proc.Name.Length);
        string memStr = $"{proc.MemoryUsedMb,6:F0} MB";
        memUsed = Math.Max(memUsed, memStr.Length);
        gpu = Math.Max(gpu, proc.GpuIndex.ToString(CultureInfo.InvariantCulture).Length);
        }

        pid = Math.Max(pid, 3);
        name = Math.Max(name, 4);
        memUsed = Math.Max(memUsed, 4);
        gpu = Math.Max(gpu, 3);

        int[] procWidths = { pid, name, memUsed, gpu };

        // Top border
        writer.WriteLine(BorderRow('┌', '┐', '┬', procWidths));

        // Header row
        var headerMeta = new List<(string? Color, Align Alignment)>
        {
            (null, Align.Center),
            (null, Align.Center),
            (null, Align.Center),
            (null, Align.Center),
        };
        writer.WriteLine(DrawGridRow(
            new List<string> { "PID", "Name", "Mem Used", "GPU" },
            procWidths, headerMeta));

        // Separator row
        writer.WriteLine(BorderRow('├', '┤', '┼', procWidths));

        // Data rows
        foreach (var proc in procs)
        {
            var cellMeta = new List<(string? Color, Align Alignment)>
            {
                (null, Align.Right),
                (null, Align.Left),
                (null, Align.Right),
                (null, Align.Right),
            };
            writer.WriteLine(DrawGridRow(
                new List<string>
                {
                    proc.Pid.ToString(CultureInfo.InvariantCulture),
                    proc.Name,
                    $"{proc.MemoryUsedMb,6:F0} MB",
                    proc.GpuIndex.ToString(CultureInfo.InvariantCulture),
                },
                procWidths, cellMeta));
        }

        // Bottom border
        writer.WriteLine(BorderRow('└', '┘', '┴', procWidths));

        // SAYS SO WHEN IT CUT. A truncated list that does not admit to being truncated reads as the
        // whole story, and the reader concludes those are all the processes on the card.
        if (dropped > 0)
        {
            writer.WriteLine();
            writer.WriteLine($"  ... and {dropped} more");
        }
    }

    // --- Process sorting and filtering ---

    /// <summary>
    /// Orders processes the way the TUI's Processes tab does.
    ///
    /// <para>PID BREAKS EVERY TIE, so a table refreshed on a timer does not reshuffle rows that
    /// compare equal — two idle processes at 0% would otherwise swap places between frames for no
    /// reason the reader can see.</para>
    ///
    /// <para><c>SmPercent</c> is nullable (a backend that cannot measure per-process utilisation
    /// reports null), and null sorts LAST under descending order rather than first, which is what
    /// -1 buys: "not measured" is not the same as "busiest".</para>
    /// </summary>
    private static List<GpuProcessSample> SortProcesses(
        IEnumerable<GpuProcessSample> processes, string sort) =>
        sort.ToLowerInvariant() switch
        {
            "sm" => processes.OrderByDescending(p => p.SmPercent ?? -1)
                             .ThenBy(p => p.Pid).ToList(),
            "pid" => processes.OrderBy(p => p.Pid).ToList(),
            "name" => processes.OrderBy(p => GpuFormat.ShortenPath(p.Name),
                                        StringComparer.OrdinalIgnoreCase)
                               .ThenBy(p => p.Pid).ToList(),
            _ => processes.OrderByDescending(p => p.MemoryUsedMb)
                          .ThenBy(p => p.Pid).ToList(),
        };

    // --- Border grid helpers ---

    private static string BorderRow(char left, char right, char join, int[] widths)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(left);
        for (int i = 0; i < widths.Length; i++)
        {
            // +2 FOR THE CELL'S OWN PADDING. DrawGridRow writes "│ value │ value │", one space
            // either side of every cell, so a segment spanning only widths[i] is two columns short
            // PER COLUMN — six columns drew a 68-wide rule under an 80-wide row and none of the
            // joins lined up with the verticals above them. The join itself stays one column, the
            // same width as the '│' it has to sit under.
            sb.Append(new string('─', widths[i] + 2));
            if (i < widths.Length - 1)
                sb.Append(join);
        }
        sb.Append(right);
        return sb.ToString();
    }

    /// <summary>Alignment options for grid cells.</summary>
    private enum Align { Left, Center, Right }

    private static string DrawCell(string value, int width, Align align, string? color = null)
    {
        string padded = align switch
        {
            Align.Right  => value.PadLeft(width),
            Align.Center => CenterIn(value, width),
            _            => value.PadRight(width),
        };
        return color != null ? $"{color}{padded}{Ansi.Reset}" : padded;
    }

    /// <summary>Center <paramref name="text"/> within a field of <paramref name="width"/> characters.</summary>
    private static string CenterIn(string text, int width)
    {
        int pad = Math.Max(0, width - text.Length);
        int left = pad / 2;
        int right = pad - left;
        return new string(' ', left) + text + new string(' ', right);
    }

    private static string DrawGridRow(List<string> cells, int[] widths, List<(string? Color, Align Alignment)> meta)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append('│').Append(' ');
        for (int i = 0; i < cells.Count; i++)
        {
            sb.Append(DrawCell(cells[i], widths[i], meta[i].Alignment, meta[i].Color));
            if (i < cells.Count - 1)
                sb.Append(" │ ");
        }
        sb.Append(" │");
        return sb.ToString();
    }

    /// <summary>
    /// Render a GPU usage snapshot as a JSON document.
    /// </summary>
    /// <param name="snapshot">The snapshot containing per-GPU metrics.</param>
    /// <param name="deviceInfos">Static device information for display names.</param>
    /// <returns>A JSON string with write-indented formatting and camelCase property names.</returns>
    public static string RenderJson(GpuSnapshot snapshot,
                                    IReadOnlyList<GpuDeviceInfo> deviceInfos)
    {
        var gpuList = new List<object>();
        foreach (var gpu in snapshot.Gpus)
        {
            var info = deviceInfos.FirstOrDefault(d => d.Index == gpu.Index);
            gpuList.Add(new
            {
                index = gpu.Index,
                name = info?.Name ?? "Unknown",
                utilization_percent = gpu.UtilizationPercent,
                memory_used_percent = gpu.MemoryUsedPercent,
                memory_used_mb = gpu.MemoryUsedMb,
                memory_total_mb = gpu.MemoryTotalMb,
                temperature_c = gpu.TemperatureC,
                power_draw_watts = gpu.PowerDrawWatts,
                power_limit_watts = gpu.PowerLimitWatts,
                fan_speed_percent = gpu.FanSpeedPercent,
                sm_clock_mhz = gpu.SmClockMhz,
                mem_clock_mhz = gpu.MemClockMhz,
                encoder_percent = gpu.EncoderPercent,
                decoder_percent = gpu.DecoderPercent,
                throttle_thermal = gpu.ThrottleThermal,
                throttle_power = gpu.ThrottlePower,
                throttle_hw_slowdown = gpu.ThrottleHwSlowdown,
                capabilities = new
                {
                    fan_speed = gpu.Caps.FanSpeed,
                    power_limit = gpu.Caps.PowerLimit,
                    throttle_reasons = gpu.Caps.ThrottleReasons,
                    encoder_decoder = gpu.Caps.EncoderDecoder,
                },
                device = new
                {
                    driver_version = info?.DriverVersion ?? "",
                    vbios_version = info?.VBiosVersion ?? "",
                    pcie_gen = info?.PcieGenWidth ?? "",
                    thermal_limit_c = info?.TemperatureLimitC ?? 0,
                    backend = info?.Backend ?? "",
                    mechanism = info?.Mechanism ?? "",
                    card_id = info?.CardId ?? "",
                },
            });
        }

        var processList = new List<object>();
        foreach (var proc in snapshot.Processes)
        {
            processList.Add(new
            {
                pid = proc.Pid,
                name = proc.Name,
                memory_used_mb = proc.MemoryUsedMb,
                gpu_index = proc.GpuIndex,
                sm_percent = proc.SmPercent,
                mem_percent = proc.MemPercent,
                });
        }

        var result = new
        {
            timestamp = DateTime.UtcNow.ToString("o"),
            gpu_count = snapshot.Gpus.Count,
            gpus = gpuList,
            processes = processList,
        };

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        return JsonSerializer.Serialize(result, options);
    }

    // --- Formatting helpers ---

    private static string FormatPercent(GpuSample gpu, GpuCapabilities caps) =>
        $"{gpu.UtilizationPercent,4:F1}%";

    private static string FormatMemory(GpuSample gpu, GpuCapabilities caps) =>
        $"{gpu.MemoryUsedMb,6:F0} / {gpu.MemoryTotalMb,6:F0} MB";

    private static string FormatTemperature(GpuSample gpu, GpuCapabilities caps) =>
        $"{gpu.TemperatureC,3:F0}°C";

    private static string FormatPower(GpuSample gpu, GpuCapabilities caps) =>
        caps.PowerLimit && gpu.PowerLimitWatts > 0 ? $"{gpu.PowerDrawWatts,5:F0}W" : "N/A";

    private static string FormatFan(GpuSample gpu, GpuCapabilities caps) =>
        caps.FanSpeed ? $"{gpu.FanSpeedPercent,3:F0}%" : "N/A";

    private static string FormatClock(GpuSample gpu, GpuCapabilities caps)
    {
        // 0 is ambiguous with "not measured", but 0 MHz is also a legitimate idle state.
        // We always render the values — a real GPU at idle runs ~135 MHz, not 0.
        return $"{gpu.SmClockMhz,4:F0}/{gpu.MemClockMhz,4:F0} MHz";
    }

    private static string FormatEncDec(GpuSample gpu, GpuCapabilities caps) =>
        caps.EncoderDecoder
            ? $"{gpu.EncoderPercent,4:F1}% / {gpu.DecoderPercent,4:F1}%"
            : "N/A";

    private static string FormatThrottle(GpuSample gpu, GpuCapabilities caps)
    {
        if (!caps.ThrottleReasons)
            return "N/A";

        var flags = new List<char>();
        if (gpu.ThrottleThermal) flags.Add('T');
        if (gpu.ThrottlePower) flags.Add('P');
        if (gpu.ThrottleHwSlowdown) flags.Add('H');

        return flags.Count > 0 ? new string(flags.ToArray()) : "—";
    }

    // --- Color helpers ---

    /// <summary>Wrap text in a color prefix (ANSI sequence) and reset.</summary>
    private static string Colorize(string text, string colorPrefix) =>
        colorPrefix.Length == 0 ? text : $"{colorPrefix}{text}{Ansi.Reset}";

    /// <summary>
    /// Returns an ANSI foreground code for a percentage metric
    /// (utilization, fan, memory ratio, power-of-limit ratio).
    /// Thresholds match <see cref="UIConstants.ThresholdColor"/>.
    /// </summary>
    private static string? ColorForPercentile(double value) =>
        value switch
        {
            < 60 => $"\u001b[38;2;{0x4e};{0xcd};{0xc4}m",  // Normal (teal/green)
            < 85 => $"\u001b[38;2;{0xff};{0xd9};{0x3d}m",  // Warning (yellow)
            _     => $"\u001b[38;2;{0xff};{0x6b};{0x6b}m", // Critical (red)
        };

    /// <summary>
    /// Returns an ANSI foreground code for temperature in °C.
    /// Uses vendor-specific thresholds when available, falling back
    /// to generic thresholds (&lt; 70 Normal, 70-85 Warning, &gt;= 85 Critical).
    /// </summary>
    private static string ColorForTemperature(GpuSample gpu)
    {
        // Try vendor-specific thresholds first (same source GpuFormat uses)
        var pair = GpuFormat.TemperatureThresholds(gpu);
        if (pair != null)
        {
            return pair.SeverityFor(gpu.TemperatureC) switch
            {
                EventSeverity.Critical => $"\u001b[38;2;{0xff};{0x6b};{0x6b}m",
                EventSeverity.Warning  => $"\u001b[38;2;{0xff};{0xd9};{0x3d}m",
                _                       => $"\u001b[38;2;{0x4e};{0xcd};{0xc4}m",
            };
        }

        // Fallback to generic thresholds
        return gpu.TemperatureC switch
        {
            < 70 => $"\u001b[38;2;{0x4e};{0xcd};{0xc4}m",
            < 85 => $"\u001b[38;2;{0xff};{0xd9};{0x3d}m",
            _     => $"\u001b[38;2;{0xff};{0x6b};{0x6b}m",
        };
    }

    /// <summary>
    /// Raw ANSI 24-bit color escape codes for terminal output.
    ///
    /// Unlike SharpConsoleUI.Color.ToMarkup() which produces [rgb]...[/] markup
    /// (TUI-internal), these produce raw \u001b[38;2;R;G;Bm sequences that any
    /// ANSI-capable terminal interprets.
    /// </summary>
    private static class Ansi
    {
        /// Foreground color: \u001b[38;2;R;G;Bm
        public static string Fg(byte r, byte g, byte b) =>
            $"\u001b[38;2;{r};{g};{b}m";

        /// Reset: \u001b[0m
        public const string Reset = "\u001b[0m";

        /// Dim/bright: \u001b[2m
        public const string Dim = "\u001b[2m";

        /// Wrap text in a colored foreground and reset.
        public static string Wrap(string text, byte r, byte g, byte b) =>
            $"{Fg(r, g, b)}{text}{Reset}";

        /// Wrap text in dim and reset.
        public static string DimWrap(string text) =>
            $"{Dim}{text}{Reset}";
    }
}
