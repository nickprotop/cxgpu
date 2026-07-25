using System.Linq;
using cxgpu.Helpers;
using cxgpu.Gpu;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;

namespace cxgpu.Tabs;

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

    /// <summary>
    /// Sentinel selection for the DASHBOARD chip — the first tile in the strip on a multi-GPU host.
    ///
    /// Modelled as a pseudo GPU index rather than a separate mode flag, so it flows through the machinery
    /// the strip already has: one selection, one set of click spans, one set of selector keys. A parallel
    /// "is dashboard showing" boolean would need every one of those paths to agree with it.
    ///
    /// Negative because real indices are non-negative, so it can never collide with a card.
    /// </summary>
    private const int DashboardIndex = -1;

    /// <summary>The nvidia-smi index of the GPU currently shown in the Overview detail.</summary>
    public int SelectedGpuIndex => _selectedGpuIndex;

    /// <summary>Whether the dashboard (fleet) view is showing rather than a single GPU's detail.</summary>
    private bool DashboardSelected => _selectedGpuIndex == DashboardIndex;

    // Utilization history per GPU for the dashboard panels' trailing gauges. Separate from _histories,
    // which is keyed by metric name for the single-GPU cards.
    private readonly KeyedHistoryTracker<int> _fleetHistories = new(120);

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

    // Wraps a GPU switch in a visible "working" notification. Optional so the tab still functions
    // (just without the indicator) if it is constructed without one.
    private readonly Action<string, Action>? _runBusy;

    public OverviewTab(ConsoleWindowSystem windowSystem, IGpuStatsProvider stats,
                       Configuration.CxgpuConfig config,
                       Action<string, Action>? runBusy = null)
        : base(windowSystem, stats)
    {
        _sparklineHeight = config.SparklineHeight;
        _refreshSeconds = config.RefreshIntervalMs / 1000.0;
        _showTimeAxis = config.ShowTimeAxis;
        _runBusy = runBusy;
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

        // The ring is [dashboard, gpu0, gpu1, ...] so '[' and ']' walk the strip exactly as it reads on
        // screen, dashboard chip included, rather than skipping the first tile.
        var ring = new List<int> { DashboardIndex };
        ring.AddRange(gpus.Select(g => g.Index));

        int pos = ring.IndexOf(_selectedGpuIndex);
        if (pos < 0) pos = 1;   // an unknown selection lands on the first GPU
        int next = ((pos + delta) % ring.Count + ring.Count) % ring.Count;
        return SelectGpu(ring[next]);
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
        // The dashboard sentinel is always selectable on a multi-GPU host; a real index must exist.
        if (gpuIndex == DashboardIndex)
        {
            if (gpus.Count <= 1) return false;
        }
        else if (gpus.All(g => g.Index != gpuIndex))
        {
            return false;
        }

        bool crossesDashboard = (_selectedGpuIndex == DashboardIndex) != (gpuIndex == DashboardIndex);
        var previousCaps = gpus.FirstOrDefault(g => g.Index == _selectedGpuIndex)?.Caps;
        _selectedGpuIndex = gpuIndex;
        var newCaps = gpus.FirstOrDefault(g => g.Index == gpuIndex)?.Caps;

        // Switching between GPUs whose backends support DIFFERENT metrics changes which cards exist,
        // and UpdatePanel only refreshes controls that are already there. Without a rebuild, moving
        // from an NVIDIA card to an AMD one would leave a stale Fan card frozen at NVIDIA's last
        // reading. Rebuild only when the capability set actually differs — the common case (same
        // vendor, or a single vendor) takes the cheap in-place path.
        // Crossing into or out of the dashboard swaps the entire right column — panel grid versus the
        // five metric cards — so the controls must be rebuilt regardless of capabilities.
        bool needsRebuild = crossesDashboard
                            || (previousCaps != null && newCaps != null && previousCaps != newCaps);

        void Apply()
        {
            if (needsRebuild)
                RebuildGraphsPanel();

            UpdatePanel(Stats.ReadSnapshot());
        }

        // Only announce the switch when it will actually be slow. The rebuild path re-reads every GPU
        // through the vendor tools — around 390 ms measured — but the in-place path is a few
        // milliseconds, and a dim-plus-toast that appears and vanishes within one or two frames reads
        // as a glitch rather than as feedback. Predicting on needsRebuild is exact, because the
        // rebuild is what costs.
        if (needsRebuild && _runBusy != null)
            _runBusy($"Loading GPU {gpuIndex}…", Apply);
        else
            Apply();

        return true;
    }

    // Discards and re-creates the cards in the graphs panel. Needed when the selected GPU's backend
    // supports a different set of metrics, since which cards EXIST is decided at build time.
    // (BaseResponsiveTab.TriggerRebuild only handles HorizontalGridControl, and this tab builds its
    // own GridControl, so it cannot be reused here.)
    private void RebuildGraphsPanel()
    {
        var root = FindMainWindow()?.FindControl<IWindowControl>(PanelControlName);
        if (root == null) return;

        var panel = FindGraphPanel(root);
        if (panel == null) return;

        var snapshot = Stats.ReadSnapshot();

        // In the narrow layout the graph panel also holds the spec-sheet and its separator, so clearing
        // everything would drop them. Rebuild the whole panel content for that case.
        bool narrow = _currentLayout == ResponsiveLayoutMode.Narrow;

        panel.ClearContents();
        if (narrow)
        {
            AddNamedMarkupLines(panel, BuildTextContent(snapshot), SpecSheetMarkupName);
            AddNarrowSeparator(panel);
        }
        BuildGraphsContent(panel, snapshot);

        FindMainWindow()?.ForceRebuildLayout();
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

    // One-line hero vitals — an at-a-glance summary with per-metric icons and threshold coloring
    // (green → yellow → red with load), so the whole GPU state reads instantly.
    // Metrics the owning backend cannot measure are OMITTED rather than shown at zero: an absent fan
    // sensor is not a fan spinning at 0%, and claiming otherwise would invent a measurement. The
    // single-vendor NVIDIA case is unaffected — it supports everything, so nothing is dropped.
    private static string HeroVitals(GpuSample gpu)
    {
        var muted = UIConstants.MutedText.ToMarkup();
        // Power is only meaningful as a ratio when a cap is known; without one, colour it by absolute
        // draw instead of dividing by a limit that doesn't exist.
        double powerPct = GpuFormat.PowerPercent(gpu);

        var parts = new List<string>
        {
            GpuFormat.Metric(GpuFormat.IconUtil, $"{gpu.UtilizationPercent:F0}%", gpu.UtilizationPercent),
            GpuFormat.Metric(GpuFormat.IconMem, $"{gpu.MemoryUsedMb / 1024.0:F1}/{gpu.MemoryTotalMb / 1024.0:F1} GB", gpu.MemoryUsedPercent),
            GpuFormat.Metric(GpuFormat.IconTemp, $"{gpu.TemperatureC:F0}°C", gpu.TemperatureC),
            GpuFormat.Metric(GpuFormat.IconPower, $"{gpu.PowerDrawWatts:F0} W", powerPct)
        };

        if (gpu.Caps.FanSpeed)
            parts.Add(GpuFormat.Metric(GpuFormat.IconFan, $"{gpu.FanSpeedPercent:F0}%", gpu.FanSpeedPercent));

        return string.Join($"[{muted}]   [/]", parts);
    }

    // The hero card's two lines: identity (+ throttle chip when throttling) and the vitals line
    // (+ media-engine readouts). Shared by build and live-update so the two can't drift.
    private static List<string> HeroLines(GpuSample gpu, string gpuName)
    {
        var titleLine = $"[{UIConstants.Accent.ToMarkup()} bold]{gpuName}[/]";
        var chip = GpuFormat.ThrottleChip(gpu);
        if (chip.Length > 0)
            titleLine += $"   {chip}";

        var muted = UIConstants.MutedText.ToMarkup();
        var vitals = HeroVitals(gpu);

        // Encoder/decoder readouts only appear for backends that expose those engines — amdgpu does
        // not, so "enc 0% dec 0%" would be fiction there.
        if (gpu.Caps.EncoderDecoder)
            vitals += $"[{muted}]   [/]" + GpuFormat.MediaEngines(gpu);

        return new List<string> { titleLine, vitals };
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

    // Tile enclosures: inward-pointing angle quotes (U+2039/203A) on EVERY tile, so each one reads as
    // the same enclosed control and the difference between them is state rather than shape. Selection
    // is carried by colour — full accent on the selected pair, a heavily muted accent on the rest —
    // together with the lifted background slab.
    //
    // These must not be "[" or "]": the width used for click hit-testing comes from
    // MarkupParser.StripLength, which parses those as the start of a markup tag and would measure the
    // tile short, drifting every click span. The angle quotes carry no markup meaning and are
    // single-width (verified), so they cannot shift the columns.
    private const string TileOpen = "‹";
    private const string TileClose = "›";

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
        // When the dashboard is selected, no GPU tile is — hence the sentinel rather than falling back
        // to a card.
        var selectedIndex = DashboardSelected ? DashboardIndex : (SelectedGpu(snapshot)?.Index ?? -2);

        var rows = new List<string>();
        var sb = new System.Text.StringBuilder();
        int column = 0;
        int row = 0;

        // The DASHBOARD chip leads the strip: it is the fleet view, so it reads before the individual
        // cards, and putting it first keeps its position stable however many GPUs come and go.
        {
            bool selected = DashboardSelected;
            string plain = $"{TileOpen}DASH {snapshot.Gpus.Count} GPUs{TileClose}";
            int width = SharpConsoleUI.Parsing.MarkupParser.StripLength(plain);

            var slab = (selected ? UIConstants.TileSelectedBg : UIConstants.TileBg).ToMarkup();
            var enclosure = selected ? $"{accent} bold" : UIConstants.TileBracket.ToMarkup();
            var labelColor = selected ? accent : UIConstants.PrimaryText.ToMarkup();

            sb.Append($"[{UIConstants.PrimaryText.ToMarkup()} on {slab}]");
            sb.Append($"[{enclosure}]{TileOpen}[/]");
            sb.Append($"[{labelColor} bold]DASH[/] ");
            sb.Append($"[{muted}]{snapshot.Gpus.Count} GPUs[/]");
            sb.Append($"[{enclosure}]{TileClose}[/]");
            sb.Append("[/]");

            _tileSpans.Add((column, column + width, DashboardIndex, row));
            column += width;
        }

        foreach (var gpu in snapshot.Gpus)
        {
            bool selected = gpu.Index == selectedIndex;

            // Tiles are FIXED-WIDTH with right-aligned numbers, so values line up column-wise across
            // tiles. Ragged tiles were a big part of why the strip read as one undifferentiated run
            // of text: with alignment, a hot GPU's digits sit directly under a cool one's.
            string plain =
                $"{TileOpen}GPU {gpu.Index}  " +
                $"{GpuFormat.UtilBar(gpu.UtilizationPercent)} {gpu.UtilizationPercent,3:F0}%  " +
                $"m{gpu.MemoryUsedPercent,3:F0}%  t{gpu.TemperatureC,3:F0}°" +
                $"{TileClose}";
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

            // The gap is plain card background — outside both slabs — so the eye reads two separate
            // objects with space between them rather than one continuous band.
            if (column > 0)
            {
                sb.Append(TileGap);
                column += TileGap.Length;
            }

            // Each tile sits on its own background SLAB, which is what turns it from text-in-a-stream
            // into a discrete object. Both slabs sit ABOVE the card background — a tile is clickable,
            // and a raised surface reads as a control where a recessed one reads as inert. The selected
            // slab is lifted further and its label accented.
            //
            // The tile is ENCLOSED, which closes it on both sides so its boundary is explicit rather
            // than inferred from spacing, and gives the slab a defined edge to fill.
            //
            // Square brackets were tried first and abandoned: the tile's width is measured with
            // MarkupParser.StripLength for click hit-testing, and that reads a literal "[" as the start
            // of a markup tag — so bracketed tiles measured short and every click span drifted. These
            // glyphs carry no markup meaning, so they measure as ordinary characters. All are
            // single-width (checked), so none can shift the columns.
            var slab = (selected ? UIConstants.TileSelectedBg : UIConstants.TileBg).ToMarkup();
            var labelColor = selected ? accent : UIConstants.PrimaryText.ToMarkup();

            // NOTE: the foreground is stated explicitly before "on" — a bare "[on <bg>]" tag takes a
            // different parser branch and did not paint the slab in practice.
            sb.Append($"[{UIConstants.PrimaryText.ToMarkup()} on {slab}]");
            var enclosure = selected ? $"{accent} bold" : UIConstants.TileBracket.ToMarkup();
            sb.Append($"[{enclosure}]{TileOpen}[/]");
            sb.Append($"[{labelColor} bold]GPU {gpu.Index}[/]  ");
            // Utilization gets a mini-bar: a pre-attentive height cue you can scan for "which one is
            // hot" without reading any digits — the actual point of a fleet strip.
            sb.Append($"[{UIConstants.ThresholdColor(gpu.UtilizationPercent).ToMarkup()} bold]" +
                      $"{GpuFormat.UtilBar(gpu.UtilizationPercent)} {gpu.UtilizationPercent,3:F0}%[/]  ");
            // Mem/temp keep single-letter prefixes instead of icons: eight repeated emoji carried no
            // distinguishing information and dominated the row visually.
            sb.Append($"[{muted}]m[/][{UIConstants.ThresholdColor(gpu.MemoryUsedPercent).ToMarkup()}]{gpu.MemoryUsedPercent,3:F0}%[/]  ");
            sb.Append($"[{muted}]t[/][{UIConstants.ThresholdColor(gpu.TemperatureC).ToMarkup()}]{gpu.TemperatureC,3:F0}°[/]");
            sb.Append($"[{enclosure}]{TileClose}[/]");
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

        // With the DASH chip selected the left column shows fleet totals rather than one card's
        // spec-sheet: combined VRAM, combined draw, the hottest card. Those numbers exist nowhere else
        // in the app, since every other view is per-GPU.
        if (DashboardSelected)
        {
            AppendFleetLines(lines, snapshot, Section, Row);
            return lines;
        }

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
                // The data source. Shown because a vendor can be read more than one way (AMD via
                // sysfs or the rocm-smi CLI) and the readings differ subtly, so which one is live has
                // to be answerable from the screen.
                if (!string.IsNullOrWhiteSpace(d.Mechanism)) Row("Source", d.Mechanism);
            }

            Section("CLOCKS");
            Row("SM", $"{gpu.SmClockMhz:F0}", "MHz");
            Row("Mem", $"{gpu.MemClockMhz:F0}", "MHz");

            Section("CAPACITY");
            Row("VRAM", $"{gpu.MemoryTotalMb / 1024.0:F1}", "GB");

            // The whole LIMITS section is conditional: with no power cap and no temperature limit
            // there is nothing to report, and an empty header reads as missing data rather than as
            // "this device exposes no limits".
            var powerLimit = d?.PowerLimitWatts ?? gpu.PowerLimitWatts;
            bool hasPowerLimit = gpu.Caps.PowerLimit && powerLimit > 0;
            bool hasTempLimit = d?.TemperatureLimitC is > 0;

            if (hasPowerLimit || hasTempLimit)
            {
                Section("LIMITS");
                if (hasPowerLimit) Row("Power", $"{powerLimit:F0}", "W");
                if (hasTempLimit) Row("Temp", $"{d!.TemperatureLimitC:F0}", "°C");
            }

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

    /// <summary>
    /// The fleet totals for the left column, reusing the spec-sheet's own Section/Row formatting so the
    /// two views of that column look like the same panel in two states.
    /// </summary>
    private static void AppendFleetLines(List<string> lines, GpuSnapshot snapshot,
                                         Action<string> section, Action<string, string, string> row)
    {
        var fleet = FleetSummary.From(snapshot);
        var accent = UIConstants.Accent.ToMarkup();
        var muted = UIConstants.MutedText.ToMarkup();
        var text = UIConstants.PrimaryText.ToMarkup();

        lines.Add($"[{accent} bold]FLEET · {fleet.GpuCount} GPUs[/]");

        section("MEMORY");
        row("Used", $"{fleet.VramUsedMb / 1024.0:F1}", "GB");
        row("Total", $"{fleet.VramTotalMb / 1024.0:F1}", "GB");

        section("POWER");
        row("Draw", $"{fleet.PowerDrawWatts:F0}", "W");
        // Say so when the total covers fewer cards than the fleet: a figure that silently omits a GPU
        // with no power sensor would read as complete.
        if (!fleet.PowerIsComplete)
            lines.Add($"[{muted}]  {fleet.PowerReportingGpus} of {fleet.GpuCount} reporting[/]");

        if (fleet.HottestGpuIndex is int hottest)
        {
            section("HOTTEST");
            row("GPU", hottest.ToString(), "");
            lines.Add($"[{muted}]{"Temp",-8}[/]" +
                      $"[{UIConstants.ThresholdColor(fleet.HottestTemperatureC).ToMarkup()} bold]" +
                      $"{fleet.HottestTemperatureC,6:F0}[/] [{muted}]°C[/]");
        }

        section("PROCESSES");
        row("Total", fleet.ProcessCount.ToString(), "");

        // Present only when something is actually throttling, so its presence is itself the signal.
        if (fleet.Throttling.Count > 0)
        {
            section("THROTTLING");
            foreach (var (gpuIndex, reason) in fleet.Throttling)
                lines.Add($"[{UIConstants.Critical.ToMarkup()} bold]GPU {gpuIndex}[/] [{text}]{reason}[/]");
        }
    }

    #region Fleet grid (the DASH chip's view)

    /// <summary>
    /// One hero panel per GPU, wrapped into rows. Wrapping is done by GROUPING the GPUs and emitting one
    /// horizontal grid per row — the grid itself has no wrapping, and this is how ServerHub's dashboard
    /// does the same job.
    /// </summary>
    private void BuildFleetGrid(ScrollablePanelControl panel, GpuSnapshot snapshot,
                                IReadOnlyList<GpuDeviceInfo> deviceInfos)
    {
        int perRow = Math.Max(1, _stripWidth / GpuHeroPanel.Width);

        for (int start = 0; start < snapshot.Gpus.Count; start += perRow)
        {
            var row = Controls.HorizontalGrid()
                .WithAlignment(HorizontalAlignment.Left)
                .WithVerticalAlignment(VerticalAlignment.Top);

            for (int i = start; i < Math.Min(start + perRow, snapshot.Gpus.Count); i++)
            {
                var gpu = snapshot.Gpus[i];
                var name = deviceInfos.FirstOrDefault(d => d.Index == gpu.Index)?.Name ?? $"GPU {gpu.Index}";

                // Panels are never "selected" here: the strip's chips carry selection, and a panel
                // highlighted independently of them would give two competing answers to "which GPU?".
                var heroPanel = GpuHeroPanel.Build(
                    gpu, name, ProcessCountFor(snapshot, gpu.Index),
                    _fleetHistories.Get(gpu.Index), selected: false);

                WireFleetPanel(heroPanel, gpu.Index);

                row.Column(col =>
                {
                    col.Width(GpuHeroPanel.Width);
                    col.Add(heroPanel);
                });
            }

            panel.AddControl(row.Build());
        }
    }

    private static int ProcessCountFor(GpuSnapshot snapshot, int gpuIndex) =>
        snapshot.Processes.Count(p => p.GpuIndex == gpuIndex);

    /// <summary>
    /// Double-click a panel to open that GPU's detail — which is just selecting its chip, so the strip
    /// and the view can never disagree about which GPU is current.
    ///
    /// No single-click handler by design: selection belongs to the strip's chips, and a panel that
    /// highlighted independently would be a second, competing notion of "current" for the user to
    /// reconcile.
    /// </summary>
    private void WireFleetPanel(PanelControl panel, int gpuIndex)
    {
        panel.MouseDoubleClick += (_, e) =>
        {
            SelectGpu(gpuIndex);
            e.Handled = true;
        };
    }

    #endregion

    protected override void BuildGraphsContent(ScrollablePanelControl panel, GpuSnapshot snapshot)
    {
        if (snapshot.Gpus.Count == 0) return;

        var deviceInfos = Stats.ReadDeviceInfo();

        // Multi-GPU only: the fleet-at-a-glance strip, which is also the selector.
        BuildSummaryStrip(panel, snapshot);

        // With the DASH chip selected, the right column becomes the fleet's hero-panel grid instead of
        // one GPU's cards. The strip stays above it either way — it is the selector for both.
        if (DashboardSelected)
        {
            BuildFleetGrid(panel, snapshot, deviceInfos);
            return;
        }

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
                .WithLabel(GpuFormat.IconUtil).WithLabelWidth(2).WithLabelSeparator(" ").ShowLabel()
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
                .WithLabel(GpuFormat.IconMem).WithLabelWidth(2).WithLabelSeparator(" ").ShowLabel()
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
                .WithLabel(GpuFormat.IconTemp).WithLabelWidth(2).WithLabelSeparator(" ").ShowLabel()
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
                .WithMaxValue(GpuFormat.PowerScale(gpu))
                .WithAlignment(HorizontalAlignment.Stretch)
                .WithUnfilledColor(UIConstants.BarUnfilledColor)
                .WithLabel(GpuFormat.IconPower).WithLabelWidth(2).WithLabelSeparator(" ").ShowLabel()
                .ShowValue()
                .WithAnimatedValue()
                .Build());
            powerCard.AddControl(Controls.RuleBuilder().WithColor(UIConstants.SeparatorColor).Build());
            powerCard.AddControl(new SparklineBuilder()
                .WithName("sel_power_spark")
                .WithHeight(_sparklineHeight)
                .WithMaxValue(GpuFormat.PowerScale(gpu))
                .WithMode(SparklineMode.Braille)
                .WithAutoFitDataPoints()
                .WithXAxis(_showTimeAxis ? TimeAxisTicks : null, _refreshSeconds)
                .WithGradient(UIConstants.SparkCpuTotal)
                .WithAlignment(HorizontalAlignment.Stretch)
                .WithMargin(0, 0, 1, 0)
                .WithData(_histories.Get($"{gpu.Index}_power"))
                .Build());
            panel.AddControl(powerCard);

            // Fan Speed — only for backends with a fan sensor. This APU has none (fan1_input is
            // ENOENT), and a card reading a flat "0%" would assert a measurement never taken. The
            // separator is inside the guard so omitting the card doesn't leave a stray trailing rule.
            if (!gpu.Caps.FanSpeed)
                return;

            AddSectionSeparator(panel);

            var fanCard = BuildCard($"Fan — {gpu.FanSpeedPercent:F0}%");
            fanCard.Name = "sel_fan_card";
            fanCard.AddControl(new BarGraphBuilder()
                .WithName("sel_fan_bar")
                .WithValue(gpu.FanSpeedPercent)
                .WithMaxValue(100)
                .WithAlignment(HorizontalAlignment.Stretch)
                .WithUnfilledColor(UIConstants.BarUnfilledColor)
                .WithLabel(GpuFormat.IconFan).WithLabelWidth(2).WithLabelSeparator(" ").ShowLabel()
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
            // Fleet gauge history for EVERY GPU, selected or not, so a panel already shows a trend the
            // first time you look at the dashboard.
            _fleetHistories.Add(gpu.Index, gpu.UtilizationPercent);

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

        // While the dashboard shows, the cards below the strip do not exist — refresh the hero panels
        // in place instead. Rebuilding them each tick would destroy the panel controls the mouse
        // handlers are attached to.
        if (DashboardSelected)
        {
            var fleetInfos = Stats.ReadDeviceInfo();
            foreach (var gpu in snapshot.Gpus)
            {
                if (!FindControlRecursive<PanelControl>(panel, GpuHeroPanel.NameFor(gpu.Index), out var hero)
                    || hero == null)
                {
                    continue;
                }

                var heroName = fleetInfos.FirstOrDefault(d => d.Index == gpu.Index)?.Name ?? $"GPU {gpu.Index}";
                GpuHeroPanel.Update(hero, gpu, heroName, ProcessCountFor(snapshot, gpu.Index),
                    _fleetHistories.Get(gpu.Index), selected: false);
            }
            return;
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
                    // Via GpuFormat: this site previously omitted the Caps.PowerLimit gate, so a card
                    // with no reported cap was coloured against a limit that does not exist.
                    double powerPercent = GpuFormat.PowerPercent(gpu);
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
