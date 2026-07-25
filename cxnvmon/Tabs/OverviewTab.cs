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

    public OverviewTab(ConsoleWindowSystem windowSystem, IGpuStatsProvider stats, Configuration.CxnvmonConfig config)
        : base(windowSystem, stats)
    {
        _sparklineHeight = config.SparklineHeight;
        _refreshSeconds = config.RefreshIntervalMs / 1000.0;
        _showTimeAxis = config.ShowTimeAxis;
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

        foreach (var gpu in snapshot.Gpus)
        {
            var d = deviceInfos.FirstOrDefault(di => di.Index == gpu.Index);

            // Device title.
            lines.Add($"[{accent} bold]{d?.Name ?? $"GPU {gpu.Index}"}[/]");

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

        if (snapshot.Gpus.Count == 0)
            lines.Add("[red]No GPUs detected.[/]");

        return lines;
    }

    protected override void BuildGraphsContent(ScrollablePanelControl panel, GpuSnapshot snapshot)
    {
        if (snapshot.Gpus.Count == 0) return;

        var deviceInfos = Stats.ReadDeviceInfo();

        foreach (var gpu in snapshot.Gpus)
        {
            var deviceInfo = deviceInfos.FirstOrDefault(d => d.Index == gpu.Index);
            var gpuName = deviceInfo?.Name ?? $"GPU {gpu.Index}";

            AddSectionHeader(panel, $"GPU {gpu.Index} Metrics");

            // Hero strip — untitled card (the GPU name is the first body line, so a card title
            // would just repeat it).
            var heroCard = BuildCard();
            heroCard.Name = $"gpu{gpu.Index}_hero_card";

            heroCard.AddControl(new MarkupBuilder()
                .WithName($"gpu{gpu.Index}_hero_markup")
                .AddLine($"[{UIConstants.Accent.ToMarkup()} bold]{gpuName}[/]")
                .AddLine(HeroVitals(gpu))
                .Build());
            panel.AddControl(heroCard);

            AddSectionSeparator(panel);

            // Utilization
            var utilCard = BuildCard($"Utilization — {gpu.UtilizationPercent:F0}%");
            utilCard.Name = $"gpu{gpu.Index}_util_card";
            utilCard.AddControl(new BarGraphBuilder()
                .WithName($"gpu{gpu.Index}_util_bar")
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
                .WithName($"gpu{gpu.Index}_util_spark")
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
            memCard.Name = $"gpu{gpu.Index}_mem_card";
            memCard.AddControl(new BarGraphBuilder()
                .WithName($"gpu{gpu.Index}_mem_bar")
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
                .WithName($"gpu{gpu.Index}_mem_spark")
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
            tempCard.Name = $"gpu{gpu.Index}_temp_card";
            tempCard.AddControl(new BarGraphBuilder()
                .WithName($"gpu{gpu.Index}_temp_bar")
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
                .WithName($"gpu{gpu.Index}_temp_spark")
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
            powerCard.Name = $"gpu{gpu.Index}_power_card";
            powerCard.AddControl(new BarGraphBuilder()
                .WithName($"gpu{gpu.Index}_power_bar")
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
                .WithName($"gpu{gpu.Index}_power_spark")
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
            fanCard.Name = $"gpu{gpu.Index}_fan_card";
            fanCard.AddControl(new BarGraphBuilder()
                .WithName($"gpu{gpu.Index}_fan_bar")
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
                .WithName($"gpu{gpu.Index}_fan_spark")
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

        foreach (var gpu in snapshot.Gpus)
        {
            var deviceInfo = deviceInfos.FirstOrDefault(d => d.Index == gpu.Index);
            var gpuName = deviceInfo?.Name ?? $"GPU {gpu.Index}";

            // Update Hero (untitled card; content in the inner markup only)
            if (FindControlRecursive<ScrollablePanelControl>(panel, $"gpu{gpu.Index}_hero_card", out var hCard) && hCard != null)
            {
                if (FindControlRecursive<MarkupControl>(hCard, $"gpu{gpu.Index}_hero_markup", out var hMarkup) && hMarkup != null)
                {
                    hMarkup.SetContent(new List<string> {
                        $"[{UIConstants.Accent.ToMarkup()} bold]{gpuName}[/]",
                        HeroVitals(gpu)
                    });
                }
            }

            // Update Cards (Header) and BarGraphs (Value + Color)
            if (FindControlRecursive<ScrollablePanelControl>(panel, $"gpu{gpu.Index}_util_card", out var uCard) && uCard != null)
            {
                uCard.Header = CardHeaderMarkup($"Utilization — {gpu.UtilizationPercent:F0}%");
                if (FindControlRecursive<BarGraphControl>(uCard, $"gpu{gpu.Index}_util_bar", out var uBar) && uBar != null)
                {
                    uBar.Value = gpu.UtilizationPercent;
                    uBar.FilledColor = UIConstants.ThresholdColor(gpu.UtilizationPercent);
                }
            }

            if (FindControlRecursive<ScrollablePanelControl>(panel, $"gpu{gpu.Index}_mem_card", out var mCard) && mCard != null)
            {
                mCard.Header = CardHeaderMarkup($"Memory — {gpu.MemoryUsedPercent:F0}%");
                if (FindControlRecursive<BarGraphControl>(mCard, $"gpu{gpu.Index}_mem_bar", out var mBar) && mBar != null)
                {
                    mBar.Value = gpu.MemoryUsedPercent;
                    mBar.FilledColor = UIConstants.ThresholdColor(gpu.MemoryUsedPercent);
                }
            }

            if (FindControlRecursive<ScrollablePanelControl>(panel, $"gpu{gpu.Index}_temp_card", out var tCard) && tCard != null)
            {
                tCard.Header = CardHeaderMarkup($"Temperature — {gpu.TemperatureC:F0}°C");
                if (FindControlRecursive<BarGraphControl>(tCard, $"gpu{gpu.Index}_temp_bar", out var tBar) && tBar != null)
                {
                    tBar.Value = gpu.TemperatureC;
                    tBar.FilledColor = UIConstants.ThresholdColor(gpu.TemperatureC);
                }
            }

            if (FindControlRecursive<ScrollablePanelControl>(panel, $"gpu{gpu.Index}_power_card", out var pCard) && pCard != null)
            {
                pCard.Header = CardHeaderMarkup($"Power — {gpu.PowerDrawWatts:F0}W");
                if (FindControlRecursive<BarGraphControl>(pCard, $"gpu{gpu.Index}_power_bar", out var pBar) && pBar != null)
                {
                    pBar.Value = gpu.PowerDrawWatts;
                    double powerPercent = gpu.PowerLimitWatts > 0 ? (gpu.PowerDrawWatts / gpu.PowerLimitWatts) * 100.0 : 0.0;
                    pBar.FilledColor = UIConstants.ThresholdColor(powerPercent);
                }
            }

            if (FindControlRecursive<ScrollablePanelControl>(panel, $"gpu{gpu.Index}_fan_card", out var fCard) && fCard != null)
            {
                fCard.Header = CardHeaderMarkup($"Fan — {gpu.FanSpeedPercent:F0}%");
                if (FindControlRecursive<BarGraphControl>(fCard, $"gpu{gpu.Index}_fan_bar", out var fBar) && fBar != null)
                {
                    fBar.Value = gpu.FanSpeedPercent;
                    fBar.FilledColor = UIConstants.ThresholdColor(gpu.FanSpeedPercent);
                }
            }

            // Update Sparklines (they are children of the cards)
            if (FindControlRecursive<SparklineControl>(panel, $"gpu{gpu.Index}_util_spark", out var uSpark) && uSpark != null)
                uSpark.SetDataPoints(_histories.Get($"{gpu.Index}_util"));

            if (FindControlRecursive<SparklineControl>(panel, $"gpu{gpu.Index}_mem_spark", out var mSpark) && mSpark != null)
                mSpark.SetDataPoints(_histories.Get($"{gpu.Index}_mem"));

            if (FindControlRecursive<SparklineControl>(panel, $"gpu{gpu.Index}_temp_spark", out var tSpark) && tSpark != null)
                tSpark.SetDataPoints(_histories.Get($"{gpu.Index}_temp"));

            if (FindControlRecursive<SparklineControl>(panel, $"gpu{gpu.Index}_power_spark", out var pSpark) && pSpark != null)
                pSpark.SetDataPoints(_histories.Get($"{gpu.Index}_power"));

            if (FindControlRecursive<SparklineControl>(panel, $"gpu{gpu.Index}_fan_spark", out var fSpark) && fSpark != null)
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

            var leftPanel = BuildScrollablePanel();
            leftPanel.BackgroundColor = UIConstants.LeftPanelBg;
            leftPanel.Padding = new Padding(1, 0, 0, 0);   // 1-col left gutter INSIDE the tinted bg
            AddMarkupLines(leftPanel, leftLines);

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

            var mainPanel = BuildScrollablePanel();
            mainPanel.Name = GraphPanelName;
            AddMarkupLines(mainPanel, BuildTextContent(initialSnapshot));
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
