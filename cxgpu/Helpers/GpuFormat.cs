using cxgpu.Gpu;

namespace cxgpu.Helpers;

/// <summary>
/// Shared formatting for GPU metrics: the icons, threshold-coloured value fragments, the braille
/// utilization gauge, and the throttle chip.
///
/// These began private to <c>OverviewTab</c>. They are here because a second view (the multi-GPU
/// dashboard) renders the same metrics, and two copies of a threshold rule or a capability gate is
/// exactly how two views of the same data start disagreeing — one showing a fan reading the other
/// omits, or a throttle chip appearing in one place and not the other.
/// </summary>
internal static class GpuFormat
{
    // Metric icons — plain Unicode, NOT Nerd Font.
    //
    // Memory and fan are geometric rather than emoji: U+25A6 reads as a grid of memory cells, and
    // U+274B is literally named a propeller. Both are single-width and MONOCHROME, so they inherit the
    // threshold colour like the gear does, where a colour emoji stays stubbornly multicoloured. The
    // brain emoji it replaces also rendered without its wide-continuation cell here, so its bar started
    // a column left of the others — see IconCell for why widths have to be normalised regardless.
    public const string IconUtil = "⚙";
    public const string IconMem = "▦";
    public const string IconTemp = "🌡";
    public const string IconPower = "⚡";
    public const string IconFan = "❋";
    public const string IconMedia = "🎬";

    /// <summary>An "&lt;icon&gt; &lt;value&gt;" fragment: icon plus a threshold-coloured value.</summary>
    public static string Metric(string icon, string value, double thresholdValue)
    {
        var color = UIConstants.ThresholdColor(thresholdValue).ToMarkup();
        return $"{IconCell(icon)} [{color} bold]{value}[/]";
    }

    /// <summary>
    /// An icon padded to a uniform two-column cell.
    ///
    /// The icons are a MIX of widths — ⚙ and 🌡 are narrow (1 column) while 🧠 ⚡ 🌀 🎬 are wide (2) — so
    /// stacked lines that each begin with a different icon end up misaligned by a column, and no fixed
    /// amount of padding fixes it. Pad the narrow ones to match the wide ones and every line's content
    /// starts at the same column.
    ///
    /// Width comes from the framework's own measurement, so it agrees with what the renderer will do
    /// rather than with a table maintained here.
    /// </summary>
    public static string IconCell(string icon)
    {
        int width = SharpConsoleUI.Parsing.MarkupParser.StripLength(icon);
        return width >= IconCellWidth ? icon : icon + new string(' ', IconCellWidth - width);
    }

    /// <summary>Columns every icon occupies, set by the widest of them (the emoji are 2 cells).</summary>
    public const int IconCellWidth = 2;

    /// <summary>
    /// NVENC/NVDEC readouts. Always shows both (muted at 0%) rather than appearing and disappearing:
    /// a line whose fields come and go makes the whole block jump. Callers gate on
    /// <see cref="GpuCapabilities.EncoderDecoder"/> — a backend with no media engines shows nothing at
    /// all rather than a fabricated 0%.
    /// </summary>
    public static string MediaEngines(GpuSample gpu)
    {
        var muted = UIConstants.MutedText.ToMarkup();
        string Engine(string label, double pct)
        {
            var color = pct > 0 ? UIConstants.ThresholdColor(pct).ToMarkup() : muted;
            return $"[{muted}]{label}[/] [{color} bold]{pct:F0}%[/]";
        }

        return $"{IconCell(IconMedia)} {Engine("enc", gpu.EncoderPercent)} {Engine("dec", gpu.DecoderPercent)}";
    }

    /// <summary>
    /// Full-scale value for a power bar or sparkline. Uses the reported cap when there is one;
    /// otherwise a fixed reference, because a device that exposes no cap still needs a STABLE axis —
    /// one that rescaled with the current draw would make every load look identical.
    /// </summary>
    public const double PowerScaleFallbackWatts = 100;

    public static double PowerScale(GpuSample gpu) =>
        gpu.Caps.PowerLimit && gpu.PowerLimitWatts > 0 ? gpu.PowerLimitWatts : PowerScaleFallbackWatts;

    /// <summary>
    /// Power as a percentage of its cap, or 0 when no cap is known. Kept here so both views colour
    /// power the same way: without a cap there is no ratio to colour by, and inventing one would imply
    /// a limit the hardware never reported.
    /// </summary>
    public static double PowerPercent(GpuSample gpu) =>
        gpu.Caps.PowerLimit && gpu.PowerLimitWatts > 0
            ? gpu.PowerDrawWatts / gpu.PowerLimitWatts * 100.0
            : 0.0;

    /// <summary>
    /// A throttle chip, or an empty string when the GPU is running clean.
    ///
    /// Surfaced ONLY for real throttles: the provider already filters out the benign gpu_idle and
    /// applications-clocks bits, which read "Active" on any idle card and would cry wolf.
    ///
    /// Severity: a hardware slowdown or thermal cap is Critical (clocks are being lost to heat or a
    /// protection trip); a software power cap is Warning (expected behaviour at the limit).
    /// </summary>
    public static string ThrottleChip(GpuSample gpu)
    {
        // Backends without named throttle-reason bits (amdgpu) report all flags false, so no chip would
        // appear anyway — but gating explicitly keeps "no chip" meaning "not throttling" rather than
        // "we cannot tell".
        if (!gpu.Caps.ThrottleReasons) return "";

        var reasons = ThrottleReasons(gpu);
        if (reasons.Count == 0) return "";

        var color = (gpu.ThrottleThermal || gpu.ThrottleHwSlowdown
            ? UIConstants.Critical
            : UIConstants.Warning).ToMarkup();

        return $"[{color} bold]⚠ {string.Join(" · ", reasons)}[/]";
    }

    /// <summary>
    /// The active throttle reasons, as plain words. Separate from <see cref="ThrottleChip"/> so a
    /// caller that needs the facts without the markup (a fleet summary listing which GPUs are
    /// throttled) shares the same capability gate and the same wording.
    /// </summary>
    public static List<string> ThrottleReasons(GpuSample gpu)
    {
        var reasons = new List<string>();
        if (!gpu.Caps.ThrottleReasons) return reasons;

        if (gpu.ThrottleThermal) reasons.Add("thermal");
        if (gpu.ThrottleHwSlowdown) reasons.Add("hw slowdown");
        if (gpu.ThrottlePower) reasons.Add("power cap");
        return reasons;
    }

    // Braille dot-columns, empty through full. Matches the sparklines' braille idiom so an inline
    // gauge belongs to the same visual family as the graphs. Levels fill from the BOTTOM up, and the
    // empty cell is U+2800 BRAILLE PATTERN BLANK — a real blank, not a mid-height dot, which floated
    // oddly against the bottom-anchored fills.
    private static readonly char[] BrailleColumns = { '⠀', '⡀', '⡄', '⡆', '⡇' };

    /// <summary>
    /// Seconds -> compact delta: "45s", "2m", "1m30s", "1h". Shared by the sparkline time axis and the
    /// alert list so a duration reads the same wherever it appears.
    /// </summary>
    public static string Duration(double seconds)
    {
        int s = (int)Math.Round(seconds);
        if (s < 60) return $"{s}s";
        if (s < 3600) { int m = s / 60, r = s % 60; return r == 0 ? $"{m}m" : $"{m}m{r}s"; }
        int h = s / 3600, mm = (s % 3600) / 60; return mm == 0 ? $"{h}h" : $"{h}h{mm}m";
    }

    /// <inheritdoc cref="Duration(double)"/>
    public static string Duration(TimeSpan span) => Duration(span.TotalSeconds);

    /// <summary>
    /// An inline braille gauge: <paramref name="cells"/> cells x four dot-levels, so four cells give
    /// 16 steps over 0-100%. A pre-attentive height cue that can be scanned for "which one is hot"
    /// without reading any digits.
    /// </summary>
    public static string UtilBar(double percent, int cells = 4)
    {
        const int levels = 4;   // dot rows per braille cell
        int filled = (int)Math.Round(Math.Clamp(percent, 0, 100) / 100.0 * (cells * levels));

        var sb = new System.Text.StringBuilder(cells);
        for (int i = 0; i < cells; i++)
        {
            int cellFill = Math.Clamp(filled - i * levels, 0, levels);
            sb.Append(BrailleColumns[cellFill]);
        }
        return sb.ToString();
    }
}
