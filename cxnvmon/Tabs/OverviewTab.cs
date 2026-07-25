using System.Linq;
using cxnvmon.Helpers;
using cxnvmon.Stats;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;

namespace cxnvmon.Tabs;

internal class OverviewTab : BaseResponsiveTab
{
    public override string Name => "Overview";
    public override string PanelControlName => "OverviewPanel";

    protected override int LayoutThresholdWidth => 100;

    // Track history for sparklines. Increased points for better visual trends.
    private readonly KeyedHistoryTracker<string> _histories = new(200);

    private readonly int _sparklineHeight;
    private readonly double _refreshSeconds;
    private readonly bool _showTimeAxis;

    // === Multi-GPU state (Architecture C: summary strip doubles as the selector) ===
    // The Overview renders the detail (spec-sheet + hero + 5 cards) for exactly ONE GPU: the
    // selected one. With a single GPU this is index 0 and the strip is not rendered at all, so the
    // single-GPU presentation is identical to before. Selection is by nvidia-smi GPU index (which
    // is what the user sees and types), NOT by position in the snapshot list.
    private int _selectedGpuIndex;

    /// <summary>The nvidia-smi index of the GPU currently shown in the Overview detail.</summary>
    public int SelectedGpuIndex => _selectedGpuIndex;

    // Click hit-test spans for the summary-strip tiles: display column range [Start, End) on tile
    // Row -> GPU index. Rebuilt every time the strip is composed, because tile widths depend on the
    // live values ("100%" is wider than "0%") and the row breaks depend on the current width.
    private readonly List<(int Start, int End, int GpuIndex, int Row)> _tileSpans = new();

    // Width the strip was last laid out for, so live updates re-wrap identically to the build.
    private int _stripWidth = 80;

    // Columns a card consumes around its content, subtracted when computing the strip's usable
    // width: 2 border + 2 padding (BuildCard uses Padding(1,0,1,0)) + 2 for the panel's scrollbar
    // gutter and a column of slack, so a tile never sits flush against the border.
    private const int CardChromeWidth = 6;

    public OverviewTab(ConsoleWindowSystem windowSystem, IGpuStatsProvider stats, Configuration.CxnvmonConfig config)
        : base(windowSystem, stats)
    {
        _sparklineHeight = config.SparklineHeight;
        _refreshSeconds = config.RefreshIntervalMs / 1000.0;
        _showTimeAxis = config.ShowTimeAxis;
    }

    // Resolves the GPU whose detail should be shown. Falls back to the first GPU when the selected
    // index isn't present (e.g. a GPU disappeared, or the app was started with a stale selection),
    // so the Overview never renders empty just because selection drifted.
    private GpuSample? SelectedGpu(GpuSnapshot snapshot)
    {
        if (snapshot.Gpus.Count == 0) return null;
        return snapshot.Gpus.FirstOrDefault(g => g.Index == _selectedGpuIndex) ?? snapshot.Gpus[0];
    }

    /// <summary>
    /// Moves the selection by <paramref name="delta"/> positions through the available GPUs
    /// (wrapping). Bound to <c>[</c> / <c>]</c>. Returns true when the selection actually changed.
    /// </summary>
    public bool CycleGpu(int delta)
    {
        var gpus = Stats.ReadSnapshot().Gpus;
        if (gpus.Count <= 1) return false;

        // Work in list positions (contiguous) and translate back to the GPU's real index, so
        // wrapping is correct even with non-contiguous indices.
        int pos = gpus.ToList().FindIndex(g => g.Index == _selectedGpuIndex);
        if (pos < 0) pos = 0;
        int next = ((pos + delta) % gpus.Count + gpus.Count) % gpus.Count;
        return SelectGpu(gpus[next].Index);
    }

    /// <summary>
    /// Selects a GPU by its nvidia-smi index. Bound to keys <c>1</c>–<c>9</c> (as index 0–8) and to
    /// clicks on the summary strip. Ignores indices that aren't present. Returns true when the
    /// selection changed.
    /// </summary>
    public bool SelectGpu(int gpuIndex)
    {
        if (gpuIndex == _selectedGpuIndex) return false;

        var gpus = Stats.ReadSnapshot().Gpus;
        if (gpus.All(g => g.Index != gpuIndex)) return false;

        _selectedGpuIndex = gpuIndex;
        // Repaint immediately with the new selection rather than waiting for the next refresh tick,
        // so switching feels instant.
        UpdatePanel(Stats.ReadSnapshot());
        return true;
    }

    // X-axis provider for the history sparklines: turns the graph geometry into time-delta ticks
    // (e.g. "-2m", "-1m", "now"), right-anchored at the newest sample. Picks a round tick interval
    // that fits the span so labels stay readable and don't overlap.
    private IEnumerable<SharpConsoleUI.Controls.SparklineAxisTick> TimeAxisTicks(
        SharpConsoleUI.Controls.SparklineAxisContext ctx)
    {
        var ticks = new List<SharpConsoleUI.Controls.SparklineAxisTick>();
        if (ctx.PointCount <= 1 || ctx.UnitsPerPoint <= 0) return ticks;

        double totalSeconds = (ctx.PointCount - 1) * ctx.UnitsPerPoint;
        // Candidate round intervals (seconds); pick the smallest that yields <= ~5 ticks.
        int[] steps = { 10, 15, 30, 60, 120, 300, 600, 1800, 3600 };
        double stepSec = steps[^1];
        foreach (var s in steps) { if (totalSeconds / s <= 5) { stepSec = s; break; } }

        var muted = UIConstants.MutedText;
        // Walk back from "now" (right edge) in stepSec increments.
        for (double ago = 0; ago <= totalSeconds + 0.001; ago += stepSec)
        {
            int idx = ctx.PointCount - 1 - (int)Math.Round(ago / ctx.UnitsPerPoint);
            if (idx < 0) break;
            string label = ago <= 0.001 ? "now" : "-" + FormatDelta(ago);
            ticks.Add(new SharpConsoleUI.Controls.SparklineAxisTick(idx, $"[{muted.ToMarkup()}]{label}[/]", muted));
        }
        return ticks;
    }

    // Seconds -> compact delta: "45s", "2m", "1m30s", "1h".
    private static string FormatDelta(double seconds)
    {
        int s = (int)Math.Round(seconds);
        if (s < 60) return $"{s}s";
        if (s < 3600) { int m = s / 60, r = s % 60; return r == 0 ? $"{m}m" : $"{m}m{r}s"; }
        int h = s / 3600, mm = (s % 3600) / 60; return mm == 0 ? $"{h}h" : $"{h}h{mm}m";
    }

    // Metric icons — plain Unicode/emoji (NOT Nerd Font). SharpConsoleUI handles wide glyphs
    // correctly (wide-continuation cells), so these render and align properly. Used in card titles.
    private const string IconUtil = "⚙";
    private const string IconMem = "🧠";
    private const string IconTemp = "🌡";
    private const string IconPower = "⚡";
    private const string IconFan = "🌀";

    // One-line hero vitals — an at-a-glance summary with per-metric icons and threshold coloring
    // (green → yellow → red with load), so the whole GPU state reads instantly.
    private static string HeroVitals(GpuSample gpu)
    {
        var muted = UIConstants.MutedText.ToMarkup();
        double powerPct = gpu.PowerLimitWatts > 0 ? gpu.PowerDrawWatts / gpu.PowerLimitWatts * 100.0 : 0.0;
        string sep = $"[{muted}]   [/]";

        return
            Metric(IconUtil, $"{gpu.UtilizationPercent:F0}%", gpu.UtilizationPercent) + sep +
            Metric(IconMem, $"{gpu.MemoryUsedMb / 1024.0:F1}/{gpu.MemoryTotalMb / 1024.0:F1} GB", gpu.MemoryUsedPercent) + sep +
            Metric(IconTemp, $"{gpu.TemperatureC:F0}°C", gpu.TemperatureC) + sep +
            Metric(IconPower, $"{gpu.PowerDrawWatts:F0} W", powerPct) + sep +
            Metric(IconFan, $"{gpu.FanSpeedPercent:F0}%", gpu.FanSpeedPercent);
    }

    // An "<icon> <value>" fragment: icon plus a threshold-colored value.
    private static string Metric(string icon, string value, double thresholdValue)
    {
        var color = UIConstants.ThresholdColor(thresholdValue).ToMarkup();
        return $"{icon} [{color} bold]{value}[/]";
    }

    private const string IconMedia = "🎬";

    // NVENC/NVDEC readouts, appended to the hero vitals line. Always shown (muted at 0%) rather
    // than appearing/disappearing: a line whose fields come and go makes the whole hero jump.
    private static string MediaEngines(GpuSample gpu)
    {
        var muted = UIConstants.MutedText.ToMarkup();
        string Engine(string label, double pct)
        {
            var color = pct > 0 ? UIConstants.ThresholdColor(pct).ToMarkup() : muted;
            return $"[{muted}]{label}[/] [{color} bold]{pct:F0}%[/]";
        }

        return $"{IconMedia} {Engine("enc", gpu.EncoderPercent)} {Engine("dec", gpu.DecoderPercent)}";
    }

    // Throttle chip for the hero card — surfaced ONLY for real throttles (the provider already
    // filters out the benign gpu_idle / applications-clocks bits, which are "Active" on any idle
    // card). Empty string when the GPU is running unthrottled, so the chip is simply absent.
    // Severity: a hardware slowdown or thermal cap is Critical (you are losing clocks to heat or a
    // protection trip); a software power cap is Warning (expected behaviour at the power limit).
    private static string ThrottleChip(GpuSample gpu)
    {
        var reasons = new List<string>();
        if (gpu.ThrottleThermal) reasons.Add("thermal");
        if (gpu.ThrottleHwSlowdown) reasons.Add("hw slowdown");
        if (gpu.ThrottlePower) reasons.Add("power cap");
        if (reasons.Count == 0) return "";

        var color = (gpu.ThrottleThermal || gpu.ThrottleHwSlowdown
            ? UIConstants.Critical
            : UIConstants.Warning).ToMarkup();

        return $"[{color} bold]⚠ {string.Join(" · ", reasons)}[/]";
    }

    // The hero card's two lines: identity (+ throttle chip when throttling) and the vitals line
    // (+ media-engine readouts). Shared by build and live-update so the two can't drift.
    private static List<string> HeroLines(GpuSample gpu, string gpuName)
    {
        var titleLine = $"[{UIConstants.Accent.ToMarkup()} bold]{gpuName}[/]";
        var chip = ThrottleChip(gpu);
        if (chip.Length > 0)
            titleLine += $"   {chip}";

        var muted = UIConstants.MutedText.ToMarkup();
        return new List<string>
        {
            titleLine,
            HeroVitals(gpu) + $"[{muted}]   [/]" + MediaEngines(gpu)
        };
    }

    // === Multi-GPU summary strip (Architecture C) ==========================================
    // A compact per-GPU tile row that IS the selector: the highlighted tile's GPU is the one shown
    // in the full Overview below. Rendered only when there is more than one GPU — with a single GPU
    // the strip would be pure noise, and its absence is what keeps the single-GPU layout unchanged.

    private const string StripMarkupName = "gpu_strip_markup";
    private const string StripCardName = "gpu_strip_card";

    // The "GPU N Metrics" section header above the detail cards. Named so it can be refreshed when
    // the selection changes — otherwise it would keep naming the GPU that was selected at build time.
    private const string MetricsHeaderName = "sel_metrics_header";

    // The left-column spec-sheet markup. Named because OverviewTab builds its own GridControl
    // layout, so the base class's HorizontalGridControl-based UpdateLeftColumnText can't reach it;
    // we find it by name instead (see the override below). Without this the sheet would keep
    // describing whichever GPU was selected when the panel was built.
    private const string SpecSheetMarkupName = "spec_sheet_markup";

    // OverviewTab lays out with a GridControl (not the base class's HorizontalGridControl), so the
    // base column-walking update finds nothing. Locate the spec-sheet markup by name and refresh it.
    protected override void UpdateLeftColumnText(IWindowControl grid, GpuSnapshot snapshot)
    {
        if (FindControlRecursive<MarkupControl>(grid, SpecSheetMarkupName, out var markup) && markup != null)
            markup.SetContent(BuildTextContent(snapshot));
    }

    // Gap between tiles, in display columns. Two columns of card background between slabs, so the
    // slabs read as separate objects rather than one continuous band.
    private const string TileGap = "  ";

    // Braille dot-columns, empty through full, matching the sparklines' braille idiom so the strip's
    // inline gauge belongs to the same visual family as the graphs below it. The levels fill from the
    // BOTTOM up (⡀ → ⡄ → ⡆ → ⡇), and the empty cell is U+2800 BRAILLE PATTERN BLANK — a real blank,
    // not a mid-height dot, which floated oddly against the bottom-anchored fills.
    private static readonly char[] BrailleColumns = { '⠀', '⡀', '⡄', '⡆', '⡇' };

    // Inline braille utilization gauge for a tile. Four cells x four dot-levels gives 16 steps over
    // 0-100% — a pre-attentive height cue you can scan for "which GPU is hot" without reading digits.
    private static string UtilBar(double percent)
    {
        const int cells = 4;
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

    // Builds the tile rows and (re)records the click hit-test spans. The selected tile is marked
    // with a leading "▌" bar and bright/bold text; unselected tiles are muted, so the selection
    // reads at a glance without relying on a background fill (which the tinted panel bg would
    // fight with).
    //
    // Tiles WRAP onto additional rows when they don't fit the available width: on a narrow terminal
    // (or with many GPUs) a single row would clip the last tiles, and a fleet view that hides part
    // of the fleet is worse than useless. Spans are recorded per row so click hit-testing stays
    // correct on wrapped rows.
    private List<string> BuildStripLines(GpuSnapshot snapshot, int availableWidth)
    {
        _tileSpans.Clear();

        var muted = UIConstants.MutedText.ToMarkup();
        var accent = UIConstants.Accent.ToMarkup();
        var selectedIndex = SelectedGpu(snapshot)?.Index ?? -1;

        var rows = new List<string>();
        var sb = new System.Text.StringBuilder();
        int column = 0;
        int row = 0;

        foreach (var gpu in snapshot.Gpus)
        {
            bool selected = gpu.Index == selectedIndex;

            // Tiles are FIXED-WIDTH with right-aligned numbers, so values line up column-wise across
            // tiles. Ragged tiles were a big part of why the strip read as one undifferentiated run
            // of text: with alignment, a hot GPU's digits sit directly under a cool one's.
            string plain =
                $"{(selected ? "▌" : "│")}GPU {gpu.Index}  " +
                $"{UtilBar(gpu.UtilizationPercent)} {gpu.UtilizationPercent,3:F0}%  " +
                $"m{gpu.MemoryUsedPercent,3:F0}%  t{gpu.TemperatureC,3:F0}° ";
            int width = SharpConsoleUI.Parsing.MarkupParser.StripLength(plain);

            // Wrap when this tile (plus its leading gap) would overflow. Never wrap the first tile
            // of a row — an over-wide tile has to clip rather than loop forever.
            if (column > 0 && column + TileGap.Length + width > availableWidth)
            {
                rows.Add(sb.ToString());
                sb.Clear();
                column = 0;
                row++;
            }

            if (column > 0)
            {
                sb.Append($"[{muted}]{TileGap}[/]");
                column += TileGap.Length;
            }

            // Each tile sits on its own background SLAB, which is what turns it from text-in-a-stream
            // into a discrete object. The selected slab is lifted (lighter) and its label accented;
            // unselected slabs are recessed below the card background. The parser keeps styles on a
            // stack, so inner [/] tags pop back to the slab background rather than clearing it.
            var slab = (selected ? UIConstants.TileSelectedBg : UIConstants.TileBg).ToMarkup();
            var labelColor = selected ? accent : UIConstants.PrimaryText.ToMarkup();

            // NOTE: the foreground is stated explicitly before "on" — a bare "[on <bg>]" tag takes a
            // different parser branch and did not paint the slab in practice.
            sb.Append($"[{UIConstants.PrimaryText.ToMarkup()} on {slab}]");
            sb.Append(selected ? $"[{accent} bold]▌[/]" : $"[{muted}]│[/]");
            sb.Append($"[{labelColor} bold]GPU {gpu.Index}[/]  ");
            // Utilization gets a mini-bar: a pre-attentive height cue you can scan for "which one is
            // hot" without reading any digits — the actual point of a fleet strip.
            sb.Append($"[{UIConstants.ThresholdColor(gpu.UtilizationPercent).ToMarkup()} bold]" +
                      $"{UtilBar(gpu.UtilizationPercent)} {gpu.UtilizationPercent,3:F0}%[/]  ");
            // Mem/temp keep single-letter prefixes instead of icons: eight repeated emoji carried no
            // distinguishing information and dominated the row visually.
            sb.Append($"[{muted}]m[/][{UIConstants.ThresholdColor(gpu.MemoryUsedPercent).ToMarkup()}]{gpu.MemoryUsedPercent,3:F0}%[/]  ");
            sb.Append($"[{muted}]t[/][{UIConstants.ThresholdColor(gpu.TemperatureC).ToMarkup()}]{gpu.TemperatureC,3:F0}°[/] ");
            sb.Append("[/]");

            _tileSpans.Add((column, column + width, gpu.Index, row));
            column += width;
        }

        if (sb.Length > 0 || rows.Count == 0)
            rows.Add(sb.ToString());

        return rows;
    }

    // The strip's second line: the key hints, so the selector documents itself.
    private static string StripHintLine()
    {
        var muted = UIConstants.MutedText.ToMarkup();
        var accent = UIConstants.Accent.ToMarkup();
        return $"[{muted}]select GPU:[/] [{accent}][[[/] [{accent}]]][/] [{muted}]prev/next  ·[/] " +
               $"[{accent}]1[/][{muted}]-[/][{accent}]9[/] [{muted}]direct  ·  click a tile[/]";
    }

    // The full strip content: the (possibly wrapped) tile rows followed by the hint line.
    private List<string> StripContent(GpuSnapshot snapshot)
    {
        var lines = BuildStripLines(snapshot, _stripWidth);
        lines.Add(StripHintLine());
        return lines;
    }

    // Adds the summary strip as the first element of the graphs panel. Clicking a tile selects that
    // GPU (hit-tested against the recorded tile spans).
    private void BuildSummaryStrip(ScrollablePanelControl panel, GpuSnapshot snapshot)
    {
        if (snapshot.Gpus.Count <= 1) return;

        var card = BuildCard();
        card.Name = StripCardName;

        var markup = new MarkupBuilder().WithName(StripMarkupName);
        foreach (var line in StripContent(snapshot))
            markup.AddLine(line);
        var stripMarkup = markup.Build();

        stripMarkup.MouseClick += (sender, e) =>
        {
            // Hit-test both the row and the column: tiles may wrap onto several rows, and the hint
            // line (the row after the last tile row) is decoration, so it matches nothing.
            foreach (var (start, end, gpuIndex, row) in _tileSpans)
            {
                if (e.Position.Y != row) continue;
                if (e.Position.X < start || e.Position.X >= end) continue;
                if (SelectGpu(gpuIndex)) e.Handled = true;
                return;
            }
        };

        card.AddControl(stripMarkup);
        panel.AddControl(card);
        AddSectionSeparator(panel);
    }

    // Padding added to the measured spec-sheet width for the left column (breathing room before
    // the separator).
    private const int LeftColumnPadding = 2;

    // Widest displayed line among markup lines, measured with the framework's StripLength so markup
    // tags are ignored and wide glyphs count correctly.
    private static int MeasureMaxWidth(List<string> lines)
    {
        int max = 0;
        foreach (var line in lines)
            max = Math.Max(max, SharpConsoleUI.Parsing.MarkupParser.StripLength(line));
        return max;
    }

    // "1x4" (gen x width, as the provider assembles it) -> "PCIe 1.0 ×4".
    private static string FormatPcie(string? genWidth)
    {
        if (string.IsNullOrWhiteSpace(genWidth)) return "";
        var parts = genWidth.Split('x', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return $"PCIe {genWidth}";
        return $"PCIe {parts[0].Trim()}.0 ×{parts[1].Trim()}";
    }

    // The left panel is the GPU's IDENTITY + hardware facts ("what it is") — deliberately the
    // static/spec-sheet counterpart to the live gauges on the right ("what it's doing"). Rendered
    // as colored, sectioned markup (AddMarkupLines parses markup, so colors apply here).
    protected override List<string> BuildTextContent(GpuSnapshot snapshot)
    {
        var lines = new List<string>();
        var deviceInfos = Stats.ReadDeviceInfo();

        var accent = UIConstants.Accent.ToMarkup();
        var muted = UIConstants.MutedText.ToMarkup();
        var text = UIConstants.PrimaryText.ToMarkup();

        // Spec-sheet inside the bordered left card: an aligned "label   value unit" grid grouped by
        // accent section headers (no inner rules — the card border frames it). Value bold/bright,
        // label + unit muted.
        const int labelW = 8;
        void Section(string title) { lines.Add(""); lines.Add($"[{accent} bold]{title}[/]"); }
        void Row(string label, string value, string unit = "") =>
            lines.Add($"[{muted}]{label,-labelW}[/][{text} bold]{value,6}[/]" +
                      (unit.Length > 0 ? $" [{muted}]{unit}[/]" : ""));

        // Top margin: start the content one row down from the panel's top edge.
        lines.Add("");

        // Spec-sheet describes the SELECTED GPU only (Architecture C) — with one GPU that's the
        // only GPU, so this is identical to the previous single-GPU rendering.
        var gpu = SelectedGpu(snapshot);
        if (gpu != null)
        {
            var d = deviceInfos.FirstOrDefault(di => di.Index == gpu.Index);

            // Device title. With multiple GPUs, prefix the index so it's unambiguous which card
            // the sheet describes.
            var title = d?.Name ?? $"GPU {gpu.Index}";
            if (snapshot.Gpus.Count > 1) title = $"GPU {gpu.Index} · {title}";
            lines.Add($"[{accent} bold]{title}[/]");

            if (d != null)
            {
                if (!string.IsNullOrWhiteSpace(d.DriverVersion)) Row("Driver", d.DriverVersion);
                var pcie = FormatPcie(d.PcieGenWidth);
                if (pcie.Length > 0) Row("PCIe", pcie.Replace("PCIe ", ""));
                if (!string.IsNullOrWhiteSpace(d.CudaVersion)) Row("CUDA", d.CudaVersion);
            }

            Section("CLOCKS");
            Row("SM", $"{gpu.SmClockMhz:F0}", "MHz");
            Row("Mem", $"{gpu.MemClockMhz:F0}", "MHz");

            Section("CAPACITY");
            Row("VRAM", $"{gpu.MemoryTotalMb / 1024.0:F1}", "GB");

            Section("LIMITS");
            Row("Power", $"{(d?.PowerLimitWatts ?? gpu.PowerLimitWatts):F0}", "W");
            if (d?.TemperatureLimitC is > 0)
                Row("Temp", $"{d.TemperatureLimitC:F0}", "°C");

            if (!string.IsNullOrWhiteSpace(d?.VBiosVersion))
            {
                Section("BIOS");
                lines.Add($"[{text}]{d.VBiosVersion}[/]");
            }
        }
        else
        {
            lines.Add("[red]No GPUs detected.[/]");
        }

        return lines;
    }

    protected override void BuildGraphsContent(ScrollablePanelControl panel, GpuSnapshot snapshot)
    {
        if (snapshot.Gpus.Count == 0) return;

        var deviceInfos = Stats.ReadDeviceInfo();

        // Multi-GPU only: the fleet-at-a-glance strip, which is also the selector.
        BuildSummaryStrip(panel, snapshot);

        // Detail for the SELECTED GPU. Control names are deliberately GPU-INDEPENDENT ("sel_*"):
        // only one GPU's detail exists at a time, so switching GPU updates these controls in place
        // instead of rebuilding the panel. (Histories stay keyed by real GPU index, so each GPU
        // keeps its own trend across switches.)
        {
            var gpu = SelectedGpu(snapshot)!;
            var deviceInfo = deviceInfos.FirstOrDefault(d => d.Index == gpu.Index);
            var gpuName = deviceInfo?.Name ?? $"GPU {gpu.Index}";

            AddSectionHeader(panel, $"GPU {gpu.Index} Metrics", MetricsHeaderName);

            // Hero strip — untitled card (the GPU name is the first body line, so a card title
            // would just repeat it).
            var heroCard = BuildCard();
            heroCard.Name = "sel_hero_card";

            var heroMarkup = new MarkupBuilder().WithName("sel_hero_markup");
            foreach (var line in HeroLines(gpu, gpuName))
                heroMarkup.AddLine(line);
            heroCard.AddControl(heroMarkup.Build());
            panel.AddControl(heroCard);

            AddSectionSeparator(panel);

            // Utilization
            var utilCard = BuildCard($"Utilization — {gpu.UtilizationPercent:F0}%");
            utilCard.Name = "sel_util_card";
            utilCard.AddControl(new BarGraphBuilder()
                .WithName("sel_util_bar")
                .WithValue(gpu.UtilizationPercent)
                .WithMaxValue(100)
                .WithAlignment(HorizontalAlignment.Stretch)
                .WithUnfilledColor(UIConstants.BarUnfilledColor)
                .WithLabel(IconUtil).WithLabelWidth(2).WithLabelSeparator(" ").ShowLabel()
                .ShowValue()
                .WithAnimatedValue()
                .Build());
            utilCard.AddControl(Controls.RuleBuilder().WithColor(UIConstants.SeparatorColor).Build());
            utilCard.AddControl(new SparklineBuilder()
                .WithName("sel_util_spark")
                .WithHeight(_sparklineHeight)
                .WithMaxValue(100)
                .WithMode(SparklineMode.Braille)
                .WithAutoFitDataPoints()
                .WithXAxis(_showTimeAxis ? TimeAxisTicks : null, _refreshSeconds)
                .WithGradient(UIConstants.SparkCpuTotal)
                .WithAlignment(HorizontalAlignment.Stretch)
                .WithMargin(0, 0, 1, 0)
                .WithData(_histories.Get($"{gpu.Index}_util"))
                .Build());
            panel.AddControl(utilCard);

            AddSectionSeparator(panel);

            // Memory
            var memCard = BuildCard($"Memory — {gpu.MemoryUsedPercent:F0}%");
            memCard.Name = "sel_mem_card";
            memCard.AddControl(new BarGraphBuilder()
                .WithName("sel_mem_bar")
                .WithValue(gpu.MemoryUsedPercent)
                .WithMaxValue(100)
                .WithAlignment(HorizontalAlignment.Stretch)
                .WithUnfilledColor(UIConstants.BarUnfilledColor)
                .WithLabel(IconMem).WithLabelWidth(2).WithLabelSeparator(" ").ShowLabel()
                .ShowValue()
                .WithAnimatedValue()
                .Build());
            memCard.AddControl(Controls.RuleBuilder().WithColor(UIConstants.SeparatorColor).Build());
            memCard.AddControl(new SparklineBuilder()
                .WithName("sel_mem_spark")
                .WithHeight(_sparklineHeight)
                .WithMaxValue(100)
                .WithMode(SparklineMode.Braille)
                .WithAutoFitDataPoints()
                .WithXAxis(_showTimeAxis ? TimeAxisTicks : null, _refreshSeconds)
                .WithGradient(UIConstants.SparkMemUsed)
                .WithAlignment(HorizontalAlignment.Stretch)
                .WithMargin(0, 0, 1, 0)
                .WithData(_histories.Get($"{gpu.Index}_mem"))
                .Build());
            panel.AddControl(memCard);

            AddSectionSeparator(panel);

            // Temperature
            var tempCard = BuildCard($"Temperature — {gpu.TemperatureC:F0}°C");
            tempCard.Name = "sel_temp_card";
            tempCard.AddControl(new BarGraphBuilder()
                .WithName("sel_temp_bar")
                .WithValue(gpu.TemperatureC)
                .WithMaxValue(100)
                .WithAlignment(HorizontalAlignment.Stretch)
                .WithUnfilledColor(UIConstants.BarUnfilledColor)
                .WithLabel(IconTemp).WithLabelWidth(2).WithLabelSeparator(" ").ShowLabel()
                .ShowValue()
                .WithAnimatedValue()
                .Build());
            tempCard.AddControl(Controls.RuleBuilder().WithColor(UIConstants.SeparatorColor).Build());
            tempCard.AddControl(new SparklineBuilder()
                .WithName("sel_temp_spark")
                .WithHeight(_sparklineHeight)
                .WithMaxValue(100)
                .WithMode(SparklineMode.Braille)
                .WithAutoFitDataPoints()
                .WithXAxis(_showTimeAxis ? TimeAxisTicks : null, _refreshSeconds)
                .WithGradient(UIConstants.SparkCpuTotal)
                .WithAlignment(HorizontalAlignment.Stretch)
                .WithMargin(0, 0, 1, 0)
                .WithData(_histories.Get($"{gpu.Index}_temp"))
                .Build());
            panel.AddControl(tempCard);

            AddSectionSeparator(panel);

            // Power
            var powerCard = BuildCard($"Power — {gpu.PowerDrawWatts:F0}W");
            powerCard.Name = "sel_power_card";
            powerCard.AddControl(new BarGraphBuilder()
                .WithName("sel_power_bar")
                .WithValue(gpu.PowerDrawWatts)
                .WithMaxValue(gpu.PowerLimitWatts > 0 ? gpu.PowerLimitWatts : 100)
                .WithAlignment(HorizontalAlignment.Stretch)
                .WithUnfilledColor(UIConstants.BarUnfilledColor)
                .WithLabel(IconPower).WithLabelWidth(2).WithLabelSeparator(" ").ShowLabel()
                .ShowValue()
                .WithAnimatedValue()
                .Build());
            powerCard.AddControl(Controls.RuleBuilder().WithColor(UIConstants.SeparatorColor).Build());
            powerCard.AddControl(new SparklineBuilder()
                .WithName("sel_power_spark")
                .WithHeight(_sparklineHeight)
                .WithMaxValue(gpu.PowerLimitWatts > 0 ? gpu.PowerLimitWatts : 100)
                .WithMode(SparklineMode.Braille)
                .WithAutoFitDataPoints()
                .WithXAxis(_showTimeAxis ? TimeAxisTicks : null, _refreshSeconds)
                .WithGradient(UIConstants.SparkCpuTotal)
                .WithAlignment(HorizontalAlignment.Stretch)
                .WithMargin(0, 0, 1, 0)
                .WithData(_histories.Get($"{gpu.Index}_power"))
                .Build());
            panel.AddControl(powerCard);

            AddSectionSeparator(panel);

            // Fan Speed
            var fanCard = BuildCard($"Fan — {gpu.FanSpeedPercent:F0}%");
            fanCard.Name = "sel_fan_card";
            fanCard.AddControl(new BarGraphBuilder()
                .WithName("sel_fan_bar")
                .WithValue(gpu.FanSpeedPercent)
                .WithMaxValue(100)
                .WithAlignment(HorizontalAlignment.Stretch)
                .WithUnfilledColor(UIConstants.BarUnfilledColor)
                .WithLabel(IconFan).WithLabelWidth(2).WithLabelSeparator(" ").ShowLabel()
                .ShowValue()
                .WithAnimatedValue()
                .Build());
            fanCard.AddControl(Controls.RuleBuilder().WithColor(UIConstants.SeparatorColor).Build());
            fanCard.AddControl(new SparklineBuilder()
                .WithName("sel_fan_spark")
                .WithHeight(_sparklineHeight)
                .WithMaxValue(100)
                .WithMode(SparklineMode.Braille)
                .WithAutoFitDataPoints()
                .WithXAxis(_showTimeAxis ? TimeAxisTicks : null, _refreshSeconds)
                .WithGradient(UIConstants.SparkCpuTotal)
                .WithAlignment(HorizontalAlignment.Stretch)
                .WithMargin(0, 0, 1, 0)
                .WithData(_histories.Get($"{gpu.Index}_fan"))
                .Build());
            panel.AddControl(fanCard);
            // No trailing separator after the last card — it would leave a stray line at the bottom.
        }
    }

    protected override void UpdateHistory(GpuSnapshot snapshot)
    {
        foreach (var gpu in snapshot.Gpus)
        {
            _histories.Add($"{gpu.Index}_util", gpu.UtilizationPercent);
            _histories.Add($"{gpu.Index}_mem", gpu.MemoryUsedPercent);
            _histories.Add($"{gpu.Index}_temp", gpu.TemperatureC);
            _histories.Add($"{gpu.Index}_power", gpu.PowerDrawWatts);
            _histories.Add($"{gpu.Index}_fan", gpu.FanSpeedPercent);
        }
    }

    protected override void UpdateGraphControls(IWindowControl grid, GpuSnapshot snapshot)
    {
        var panel = FindGraphPanel(grid);
        if (panel == null) return;

        var deviceInfos = Stats.ReadDeviceInfo();

        // The summary strip reflects ALL GPUs (it's the fleet view) and highlights the selected one.
        if (snapshot.Gpus.Count > 1 &&
            FindControlRecursive<MarkupControl>(panel, StripMarkupName, out var stripMarkup) && stripMarkup != null)
        {
            stripMarkup.SetContent(StripContent(snapshot));
        }

        // Everything below the strip is the SELECTED GPU's detail.
        var selected = SelectedGpu(snapshot);
        if (selected != null)
        {
            var gpu = selected;
            var deviceInfo = deviceInfos.FirstOrDefault(d => d.Index == gpu.Index);
            var gpuName = deviceInfo?.Name ?? $"GPU {gpu.Index}";

            // The section header names the selected GPU, so it must follow the selection.
            if (FindControlRecursive<MarkupControl>(panel, MetricsHeaderName, out var headerMarkup) && headerMarkup != null)
                headerMarkup.SetContent(new List<string> { SectionHeaderMarkup($"GPU {gpu.Index} Metrics") });

            // Update Hero (untitled card; content in the inner markup only)
            if (FindControlRecursive<ScrollablePanelControl>(panel, "sel_hero_card", out var hCard) && hCard != null)
            {
                if (FindControlRecursive<MarkupControl>(hCard, "sel_hero_markup", out var hMarkup) && hMarkup != null)
                {
                    hMarkup.SetContent(HeroLines(gpu, gpuName));
                }
            }

            // Update Cards (Header) and BarGraphs (Value + Color)
            if (FindControlRecursive<ScrollablePanelControl>(panel, "sel_util_card", out var uCard) && uCard != null)
            {
                uCard.Header = CardHeaderMarkup($"Utilization — {gpu.UtilizationPercent:F0}%");
                if (FindControlRecursive<BarGraphControl>(uCard, "sel_util_bar", out var uBar) && uBar != null)
                {
                    uBar.Value = gpu.UtilizationPercent;
                    uBar.FilledColor = UIConstants.ThresholdColor(gpu.UtilizationPercent);
                }
            }

            if (FindControlRecursive<ScrollablePanelControl>(panel, "sel_mem_card", out var mCard) && mCard != null)
            {
                mCard.Header = CardHeaderMarkup($"Memory — {gpu.MemoryUsedPercent:F0}%");
                if (FindControlRecursive<BarGraphControl>(mCard, "sel_mem_bar", out var mBar) && mBar != null)
                {
                    mBar.Value = gpu.MemoryUsedPercent;
                    mBar.FilledColor = UIConstants.ThresholdColor(gpu.MemoryUsedPercent);
                }
            }

            if (FindControlRecursive<ScrollablePanelControl>(panel, "sel_temp_card", out var tCard) && tCard != null)
            {
                tCard.Header = CardHeaderMarkup($"Temperature — {gpu.TemperatureC:F0}°C");
                if (FindControlRecursive<BarGraphControl>(tCard, "sel_temp_bar", out var tBar) && tBar != null)
                {
                    tBar.Value = gpu.TemperatureC;
                    tBar.FilledColor = UIConstants.ThresholdColor(gpu.TemperatureC);
                }
            }

            if (FindControlRecursive<ScrollablePanelControl>(panel, "sel_power_card", out var pCard) && pCard != null)
            {
                pCard.Header = CardHeaderMarkup($"Power — {gpu.PowerDrawWatts:F0}W");
                if (FindControlRecursive<BarGraphControl>(pCard, "sel_power_bar", out var pBar) && pBar != null)
                {
                    pBar.Value = gpu.PowerDrawWatts;
                    double powerPercent = gpu.PowerLimitWatts > 0 ? (gpu.PowerDrawWatts / gpu.PowerLimitWatts) * 100.0 : 0.0;
                    pBar.FilledColor = UIConstants.ThresholdColor(powerPercent);
                }
            }

            if (FindControlRecursive<ScrollablePanelControl>(panel, "sel_fan_card", out var fCard) && fCard != null)
            {
                fCard.Header = CardHeaderMarkup($"Fan — {gpu.FanSpeedPercent:F0}%");
                if (FindControlRecursive<BarGraphControl>(fCard, "sel_fan_bar", out var fBar) && fBar != null)
                {
                    fBar.Value = gpu.FanSpeedPercent;
                    fBar.FilledColor = UIConstants.ThresholdColor(gpu.FanSpeedPercent);
                }
            }

            // Update Sparklines (they are children of the cards)
            if (FindControlRecursive<SparklineControl>(panel, "sel_util_spark", out var uSpark) && uSpark != null)
                uSpark.SetDataPoints(_histories.Get($"{gpu.Index}_util"));

            if (FindControlRecursive<SparklineControl>(panel, "sel_mem_spark", out var mSpark) && mSpark != null)
                mSpark.SetDataPoints(_histories.Get($"{gpu.Index}_mem"));

            if (FindControlRecursive<SparklineControl>(panel, "sel_temp_spark", out var tSpark) && tSpark != null)
                tSpark.SetDataPoints(_histories.Get($"{gpu.Index}_temp"));

            if (FindControlRecursive<SparklineControl>(panel, "sel_power_spark", out var pSpark) && pSpark != null)
                pSpark.SetDataPoints(_histories.Get($"{gpu.Index}_power"));

            if (FindControlRecursive<SparklineControl>(panel, "sel_fan_spark", out var fSpark) && fSpark != null)
                fSpark.SetDataPoints(_histories.Get($"{gpu.Index}_fan"));
        }
    }

    public override IWindowControl BuildPanel(GpuSnapshot initialSnapshot, int windowWidth)
    {
        var layoutMode = windowWidth >= LayoutThresholdWidth
            ? ResponsiveLayoutMode.Wide
            : ResponsiveLayoutMode.Narrow;

        var grid = new GridControl
        {
            Name = PanelControlName,
            VerticalAlignment = VerticalAlignment.Fill,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Margin(0, 0, 0, 0),
            BackgroundColor = UIConstants.BaseBg,
            ForegroundColor = UIConstants.PrimaryText
        };

        if (layoutMode == ResponsiveLayoutMode.Wide)
        {
            // Left column sized to the spec-sheet's actual content. Auto() can't size to a
            // ScrollablePanel (it's a scrolling viewport that fills its space), so we MEASURE the
            // built lines with MarkupParser.StripLength (true display width, markup stripped) and
            // set a fixed width from that + padding. The graphs take the rest via Star.
            var leftLines = BuildTextContent(initialSnapshot);
            int leftWidth = MeasureMaxWidth(leftLines) + LeftColumnPadding + 1;  // +1 for the left gutter margin

            grid.ColumnDefinitions.Add(GridLength.Cells(leftWidth));
            grid.ColumnDefinitions.Add(GridLength.Cells(UIConstants.SeparatorColumnWidth));
            grid.ColumnDefinitions.Add(GridLength.Star(1.0));
            grid.RowDefinitions.Add(GridLength.Star(1.0));

            // Width available to the strip's tiles: the right column, less the card border+padding
            // and the panel's scrollbar gutter. Drives where the tile rows wrap.
            _stripWidth = Math.Max(20, windowWidth - leftWidth - UIConstants.SeparatorColumnWidth - CardChromeWidth);

            var leftPanel = BuildScrollablePanel();
            leftPanel.BackgroundColor = UIConstants.LeftPanelBg;
            leftPanel.Padding = new Padding(1, 0, 0, 0);   // 1-col left gutter INSIDE the tinted bg
            AddNamedMarkupLines(leftPanel, leftLines, SpecSheetMarkupName);

            var separator = new SeparatorControl { ForegroundColor = UIConstants.SeparatorColor, VerticalAlignment = VerticalAlignment.Fill };

            var rightPanel = BuildRightPanel();
            rightPanel.Name = GraphPanelName;
            BuildGraphsContent(rightPanel, initialSnapshot);

            grid.Place(leftPanel, 0, 0, 1, 1);
            grid.Place(separator, 0, 1, 1, 1);
            grid.Place(rightPanel, 0, 2, 1, 1);
        }
        else
        {
            grid.ColumnDefinitions.Add(GridLength.Star(1.0));
            grid.RowDefinitions.Add(GridLength.Star(1.0));

            _stripWidth = Math.Max(20, windowWidth - CardChromeWidth);

            var mainPanel = BuildScrollablePanel();
            mainPanel.Name = GraphPanelName;
            AddNamedMarkupLines(mainPanel, BuildTextContent(initialSnapshot), SpecSheetMarkupName);
            AddNarrowSeparator(mainPanel);
            BuildGraphsContent(mainPanel, initialSnapshot);

            grid.Place(mainPanel, 0, 0, 1, 1);
        }

        return grid;
    }

    private bool FindControlRecursive<T>(IWindowControl root, string name, out T? result) where T : class
    {
        if (root is T t && root.Name == name)
        {
            result = t;
            return true;
        }

        if (root is IContainerControl container)
        {
            foreach (var child in container.GetChildren())
            {
                if (FindControlRecursive<T>(child, name, out result))
                    return true;
            }
        }

        result = null;
        return false;
    }
}
