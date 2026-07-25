using cxnvmon.Helpers;
using cxnvmon.Stats;
using SharpConsoleUI;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;

namespace cxnvmon.Tabs;

/// <summary>
/// One GPU's card on the dashboard: a bordered panel with utilization and memory bars, the
/// temperature/power/fan line, a utilization history gauge, a throttle chip when throttling, and the
/// process count.
///
/// Deliberately a SINGLE control whose content is markup, rather than a panel containing real bar and
/// sparkline controls. Two reasons: click and double-click land on one target instead of whichever
/// child the pointer happened to hit, and the panel's content can be swapped in place on refresh
/// without touching the control tree — which is what keeps selection and focus alive (rebuilding
/// controls per tick destroyed both in the Processes views earlier).
///
/// Selection is carried by border colour, following ServerHub's dashboard: the caller passes the colour
/// in, so this type holds no selection state of its own.
/// </summary>
internal static class GpuHeroPanel
{
    /// <summary>Panel width in columns, including its border.</summary>
    public const int Width = 30;

    /// <summary>Cells in the inline bars — the panel's interior width less label and value.</summary>
    private const int BarCells = 12;

    /// <summary>
    /// Content rows every panel occupies, whatever it can measure.
    ///
    /// Capability gating means panels naturally differ in height — a card with no media engines has one
    /// fewer line — and side by side that reads as a ragged, broken row rather than as a deliberate
    /// omission. Padding to a fixed count keeps the row square while still omitting the metrics that
    /// would be fabricated.
    ///
    /// Six: two bars, the vitals line, encode/decode, the history gauge, and the footer.
    /// </summary>
    private const int ContentRows = 6;

    /// <summary>Control name for a GPU's panel, so it can be found for in-place updates.</summary>
    public static string NameFor(int gpuIndex) => $"dash_gpu{gpuIndex}";

    /// <summary>
    /// Builds the panel. <paramref name="history"/> is that GPU's utilization history, used for the
    /// trailing gauge; pass an empty list before any has accumulated.
    /// </summary>
    public static PanelControl Build(GpuSample gpu, string gpuName, int processCount,
                                     IReadOnlyList<double> history, bool selected)
    {
        var panel = PanelControl.Create()
            .WithContent(Content(gpu, processCount, history))
            .Rounded()
            .WithBorderColor(BorderColor(selected))
            .WithHeader(Header(gpu, gpuName, selected))
            .HeaderLeft()
            .WithPadding(1, 0, 1, 0)
            // Bars and braille gauges must never wrap — a wrapped bar is unreadable and would also
            // change the panel's height, breaking the row grid.
            .WordWrap(false)
            .WithName(NameFor(gpu.Index))
            .Build();

        panel.BackgroundColor = selected ? UIConstants.TileSelectedBg : UIConstants.CardBg;
        return panel;
    }

    /// <summary>
    /// Refreshes an existing panel in place. Only content, header and colours change — never the
    /// control identity, so the caller's selection and any focus survive a refresh tick.
    /// </summary>
    public static void Update(PanelControl panel, GpuSample gpu, string gpuName, int processCount,
                              IReadOnlyList<double> history, bool selected)
    {
        panel.SetContent(Content(gpu, processCount, history));
        panel.Header = Header(gpu, gpuName, selected);
        panel.BorderColor = BorderColor(selected);
        panel.BackgroundColor = selected ? UIConstants.TileSelectedBg : UIConstants.CardBg;
    }

    private static Color BorderColor(bool selected) =>
        selected ? UIConstants.Accent : UIConstants.SeparatorColor;

    /// <summary>
    /// Header: "GPU n · name", truncated to fit. The index leads because it is what the user types to
    /// select a card, and it stays readable when the name is cut.
    /// </summary>
    private static string Header(GpuSample gpu, string gpuName, bool selected)
    {
        var color = selected ? UIConstants.Accent.ToMarkup() : UIConstants.CardTitle.ToMarkup();
        var label = $"GPU {gpu.Index}";

        // Width less the border, padding, and the header's own decoration.
        int room = Width - 8 - label.Length;
        var name = gpuName.Length > room && room > 1 ? gpuName[..(room - 1)] + "…" : gpuName;

        return $"[{color} bold]{label}[/] [{UIConstants.MutedText.ToMarkup()}]·[/] [{color}]{name}[/]";
    }

    /// <summary>
    /// The panel body. Every metric here is capability-gated: a backend that cannot measure something
    /// omits the line rather than showing a zero, because an absent sensor is not a reading of zero.
    /// </summary>
    private static string Content(GpuSample gpu, int processCount, IReadOnlyList<double> history)
    {
        var muted = UIConstants.MutedText.ToMarkup();
        var text = UIConstants.PrimaryText.ToMarkup();
        var lines = new List<string>();

        // Utilization and memory as labelled bars — the two numbers most worth comparing across cards,
        // so they get the full-width treatment.
        lines.Add(BarLine(GpuFormat.IconUtil, gpu.UtilizationPercent, $"{gpu.UtilizationPercent,3:F0}%"));
        lines.Add(BarLine(GpuFormat.IconMem, gpu.MemoryUsedPercent, $"{gpu.MemoryUsedPercent,3:F0}%"));

        // Temperature, power and (where measurable) fan on one line: individually small, and together
        // they describe the card's thermal state.
        var vitals = new List<string>
        {
            GpuFormat.Metric(GpuFormat.IconTemp, $"{gpu.TemperatureC:F0}°C", gpu.TemperatureC),
            GpuFormat.Metric(GpuFormat.IconPower, $"{gpu.PowerDrawWatts:F0}W", GpuFormat.PowerPercent(gpu))
        };
        if (gpu.Caps.FanSpeed)
            vitals.Add(GpuFormat.Metric(GpuFormat.IconFan, $"{gpu.FanSpeedPercent:F0}%", gpu.FanSpeedPercent));
        lines.Add(string.Join(" ", vitals));

        if (gpu.Caps.EncoderDecoder)
            lines.Add(GpuFormat.MediaEngines(gpu));

        // Utilization trend. A wide braille gauge rather than a real sparkline control, so the whole
        // panel stays one control; it reads as a coarse history, which is all a fleet card needs.
        lines.Add($"[{UIConstants.Normal.ToMarkup()}]{HistoryGauge(history)}[/]");

        // Footer rows, built first so padding can be inserted ABOVE them — the process count belongs on
        // the panel's bottom edge, not floated up by blank filler.
        var footer = new List<string>();
        var chip = GpuFormat.ThrottleChip(gpu);
        if (chip.Length > 0) footer.Add(chip);
        footer.Add($"[{muted}]{processCount} process{(processCount == 1 ? "" : "es")}[/]");

        // Pad to a uniform height so panels in a row close their borders on the same line. Without this,
        // a card with fewer measurable metrics ends early and the row reads as broken rather than as
        // deliberately sparse.
        while (lines.Count + footer.Count < ContentRows)
            lines.Add("");

        lines.AddRange(footer);
        return string.Join("\n", lines);
    }

    /// <summary>An icon, a block bar, and a right-aligned value.</summary>
    private static string BarLine(string icon, double percent, string value)
    {
        var color = UIConstants.ThresholdColor(percent).ToMarkup();
        var unfilled = UIConstants.BarUnfilledColor.ToMarkup();

        int filled = (int)Math.Round(Math.Clamp(percent, 0, 100) / 100.0 * BarCells);
        var bar = $"[{color}]{new string('█', filled)}[/][{unfilled}]{new string('░', BarCells - filled)}[/]";

        // Icon padded to a uniform cell so the bars and values in stacked lines start at the same
        // column — the icons themselves are a mix of one- and two-column glyphs.
        return $"{GpuFormat.IconCell(icon)} {bar} [{color} bold]{value}[/]";
    }

    /// <summary>
    /// The utilization history as braille columns, most recent on the right. Takes the last N samples
    /// so the gauge shows recent behaviour rather than compressing the entire run.
    /// </summary>
    private static string HistoryGauge(IReadOnlyList<double> history)
    {
        const int cells = Width - 6;
        if (history.Count == 0) return new string('⠀', cells);

        var recent = history.Count > cells
            ? history.Skip(history.Count - cells).ToList()
            : history.ToList();

        var sb = new System.Text.StringBuilder(cells);
        // Left-pad so the newest sample stays hard right, matching how the Overview's sparklines read.
        sb.Append('⠀', Math.Max(0, cells - recent.Count));
        foreach (var value in recent)
            sb.Append(GpuFormat.UtilBar(value, cells: 1));

        return sb.ToString();
    }
}
