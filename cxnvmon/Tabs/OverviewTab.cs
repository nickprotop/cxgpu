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

    public OverviewTab(ConsoleWindowSystem windowSystem, IGpuStatsProvider stats)
        : base(windowSystem, stats)
    {
    }

    protected override List<string> BuildTextContent(GpuSnapshot snapshot)
    {
        var lines = new List<string>();
        foreach (var gpu in snapshot.Gpus)
        {
            lines.Add($"[bold cyan]GPU {gpu.Index}[/]");
            lines.Add($"  Utilization: {gpu.UtilizationPercent:F0}%");
            lines.Add($"  Memory:      {gpu.MemoryUsedMb:F0}/{gpu.MemoryTotalMb:F0} MB ({gpu.MemoryUsedPercent:F0}%)");
            lines.Add($"  Temperature: {gpu.TemperatureC:F0}°C");
            lines.Add($"  Power:       {gpu.PowerDrawWatts:F0}/{gpu.PowerLimitWatts:F0} W");
            lines.Add($"  Fan Speed:   {gpu.FanSpeedPercent:F0}%");
            lines.Add($"  Clocks:      SM: {gpu.SmClockMhz:F0} MHz, Mem: {gpu.MemClockMhz:F0} MHz");
            lines.Add("");
        }

        if (snapshot.Gpus.Count == 0)
            lines.Add("[red]No GPUs detected.[/]");

        return lines;
    }

    protected override void BuildGraphsContent(ScrollablePanelControl panel, GpuSnapshot snapshot)
    {
        if (snapshot.Gpus.Count == 0) return;

        foreach (var gpu in snapshot.Gpus)
        {
            AddSectionHeader(panel, $"GPU {gpu.Index} Metrics");

            // Utilization
            panel.AddControl(new BarGraphBuilder()
                .WithName($"gpu{gpu.Index}_util_bar")
                .WithLabel("Util")
                .WithLabelWidth(8)
                .WithValue(gpu.UtilizationPercent)
                .WithMaxValue(100)
                .WithAlignment(HorizontalAlignment.Stretch)
                .WithUnfilledColor(UIConstants.BarUnfilledColor)
                .ShowLabel().ShowValue()
                .WithSmoothGradient(UIConstants.SparkCpuTotal)
                .Build());
            panel.AddControl(new SparklineBuilder()
                .WithName($"gpu{gpu.Index}_util_spark")
                .WithHeight(6)
                .WithMaxValue(100)
                .WithMode(SparklineMode.Braille)
                .WithAutoFitDataPoints()
                .WithGradient(UIConstants.SparkCpuTotal)
                .WithAlignment(HorizontalAlignment.Stretch)
                .WithMargin(0, 0, 1, 0)
                .WithData(_histories.Get($"{gpu.Index}_util"))
                .Build());

            AddSectionSeparator(panel);

            // Memory
            panel.AddControl(new BarGraphBuilder()
                .WithName($"gpu{gpu.Index}_mem_bar")
                .WithLabel("Mem")
                .WithLabelWidth(8)
                .WithValue(gpu.MemoryUsedPercent)
                .WithMaxValue(100)
                .WithAlignment(HorizontalAlignment.Stretch)
                .WithUnfilledColor(UIConstants.BarUnfilledColor)
                .ShowLabel().ShowValue()
                .WithSmoothGradient(UIConstants.SparkMemUsed)
                .Build());
            panel.AddControl(new SparklineBuilder()
                .WithName($"gpu{gpu.Index}_mem_spark")
                .WithHeight(6)
                .WithMaxValue(100)
                .WithMode(SparklineMode.Braille)
                .WithAutoFitDataPoints()
                .WithGradient(UIConstants.SparkMemUsed)
                .WithAlignment(HorizontalAlignment.Stretch)
                .WithMargin(0, 0, 1, 0)
                .WithData(_histories.Get($"{gpu.Index}_mem"))
                .Build());

            AddSectionSeparator(panel);

            // Temperature
            panel.AddControl(new BarGraphBuilder()
                .WithName($"gpu{gpu.Index}_temp_bar")
                .WithLabel("Temp")
                .WithLabelWidth(8)
                .WithValue(gpu.TemperatureC)
                .WithMaxValue(100)
                .WithAlignment(HorizontalAlignment.Stretch)
                .WithUnfilledColor(UIConstants.BarUnfilledColor)
                .ShowLabel().ShowValue()
                .WithSmoothGradient(UIConstants.SparkCpuTotal)
                .Build());
            panel.AddControl(new SparklineBuilder()
                .WithName($"gpu{gpu.Index}_temp_spark")
                .WithHeight(6)
                .WithMaxValue(100)
                .WithMode(SparklineMode.Braille)
                .WithAutoFitDataPoints()
                .WithGradient(UIConstants.SparkCpuTotal)
                .WithAlignment(HorizontalAlignment.Stretch)
                .WithMargin(0, 0, 1, 0)
                .WithData(_histories.Get($"{gpu.Index}_temp"))
                .Build());

            AddSectionSeparator(panel);

            // Power
            panel.AddControl(new BarGraphBuilder()
                .WithName($"gpu{gpu.Index}_power_bar")
                .WithLabel("Power")
                .WithLabelWidth(8)
                .WithValue(gpu.PowerDrawWatts)
                .WithMaxValue(gpu.PowerLimitWatts > 0 ? gpu.PowerLimitWatts : 100)
                .WithAlignment(HorizontalAlignment.Stretch)
                .WithUnfilledColor(UIConstants.BarUnfilledColor)
                .ShowLabel().ShowValue()
                .WithSmoothGradient(UIConstants.SparkCpuTotal)
                .Build());
            panel.AddControl(new SparklineBuilder()
                .WithName($"gpu{gpu.Index}_power_spark")
                .WithHeight(6)
                .WithMaxValue(gpu.PowerLimitWatts > 0 ? gpu.PowerLimitWatts : 100)
                .WithMode(SparklineMode.Braille)
                .WithAutoFitDataPoints()
                .WithGradient(UIConstants.SparkCpuTotal)
                .WithAlignment(HorizontalAlignment.Stretch)
                .WithMargin(0, 0, 1, 0)
                .WithData(_histories.Get($"{gpu.Index}_power"))
                .Build());

            AddSectionSeparator(panel);

            // Fan Speed
            panel.AddControl(new BarGraphBuilder()
                .WithName($"gpu{gpu.Index}_fan_bar")
                .WithLabel("Fan")
                .WithLabelWidth(8)
                .WithValue(gpu.FanSpeedPercent)
                .WithMaxValue(100)
                .WithAlignment(HorizontalAlignment.Stretch)
                .WithUnfilledColor(UIConstants.BarUnfilledColor)
                .ShowLabel().ShowValue()
                .WithSmoothGradient(UIConstants.SparkCpuTotal)
                .Build());
            panel.AddControl(new SparklineBuilder()
                .WithName($"gpu{gpu.Index}_fan_spark")
                .WithHeight(6)
                .WithMaxValue(100)
                .WithMode(SparklineMode.Braille)
                .WithAutoFitDataPoints()
                .WithGradient(UIConstants.SparkCpuTotal)
                .WithAlignment(HorizontalAlignment.Stretch)
                .WithMargin(0, 0, 1, 0)
                .WithData(_histories.Get($"{gpu.Index}_fan"))
                .Build());

            AddSectionSeparator(panel);
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

        foreach (var gpu in snapshot.Gpus)
        {
            // Update BarGraphs
            if (panel.Children.TryFind<BarGraphControl>(c => c.Name == $"gpu{gpu.Index}_util_bar", out var uBar) && uBar != null)
                uBar.Value = gpu.UtilizationPercent;

            if (panel.Children.TryFind<BarGraphControl>(c => c.Name == $"gpu{gpu.Index}_mem_bar", out var mBar) && mBar != null)
                mBar.Value = gpu.MemoryUsedPercent;

            if (panel.Children.TryFind<BarGraphControl>(c => c.Name == $"gpu{gpu.Index}_temp_bar", out var tBar) && tBar != null)
                tBar.Value = gpu.TemperatureC;

            if (panel.Children.TryFind<BarGraphControl>(c => c.Name == $"gpu{gpu.Index}_power_bar", out var pBar) && pBar != null)
                pBar.Value = gpu.PowerDrawWatts;

            if (panel.Children.TryFind<BarGraphControl>(c => c.Name == $"gpu{gpu.Index}_fan_bar", out var fBar) && fBar != null)
                fBar.Value = gpu.FanSpeedPercent;

            // Update Sparklines
            if (panel.Children.TryFind<SparklineControl>(c => c.Name == $"gpu{gpu.Index}_util_spark", out var uSpark) && uSpark != null)
                uSpark.SetDataPoints(_histories.Get($"{gpu.Index}_util"));

            if (panel.Children.TryFind<SparklineControl>(c => c.Name == $"gpu{gpu.Index}_mem_spark", out var mSpark) && mSpark != null)
                mSpark.SetDataPoints(_histories.Get($"{gpu.Index}_mem"));

            if (panel.Children.TryFind<SparklineControl>(c => c.Name == $"gpu{gpu.Index}_temp_spark", out var tSpark) && tSpark != null)
                tSpark.SetDataPoints(_histories.Get($"{gpu.Index}_temp"));

            if (panel.Children.TryFind<SparklineControl>(c => c.Name == $"gpu{gpu.Index}_power_spark", out var pSpark) && pSpark != null)
                pSpark.SetDataPoints(_histories.Get($"{gpu.Index}_power"));

            if (panel.Children.TryFind<SparklineControl>(c => c.Name == $"gpu{gpu.Index}_fan_spark", out var fSpark) && fSpark != null)
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
            Margin = new Margin(1, 0, 1, 1),
            BackgroundColor = UIConstants.BaseBg,
            ForegroundColor = UIConstants.PrimaryText
        };

        if (layoutMode == ResponsiveLayoutMode.Wide)
        {
            // 3 columns: Text (fixed, sized to content), Separator (fixed), Graphs (Star = rest).
            // The text readout has a bounded max width, so give it a FIXED column and let the
            // graphs take ALL the remaining space via Star — a proportional text column would
            // waste width on a 200-col terminal. (Do NOT use Auto here: the text panel is
            // Stretch-aligned, so Auto would report its desired width as the whole row and
            // starve the graphs column to zero width / invisible.)
            grid.ColumnDefinitions.Add(GridLength.Cells(UIConstants.FixedTextColumnWidth));
            grid.ColumnDefinitions.Add(GridLength.Cells(UIConstants.SeparatorColumnWidth));
            grid.ColumnDefinitions.Add(GridLength.Star(1.0));
            grid.RowDefinitions.Add(GridLength.Star(1.0));

            var leftPanel = BuildScrollablePanel();
            AddMarkupLines(leftPanel, BuildTextContent(initialSnapshot));

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
            // 1 column: Everything
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
}

// Helper to find control by name in a list of children
public static class ControlExtensions
{
    public static bool TryFind<T>(this IEnumerable<IWindowControl> children, Func<IWindowControl, bool> predicate, out T? result) where T : class
    {
        foreach (var child in children)
        {
            if (predicate(child) && child is T t)
            {
                result = t;
                return true;
            }
        }
        result = null;
        return false;
    }
}
