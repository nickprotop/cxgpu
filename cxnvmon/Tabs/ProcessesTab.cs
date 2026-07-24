using cxnvmon.Helpers;
using cxnvmon.Stats;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;

namespace cxnvmon.Tabs;

internal class ProcessesTab : BaseResponsiveTab
{
    public override string Name => "Processes";
    public override string PanelControlName => "ProcessesPanel";

    protected override int LayoutThresholdWidth => 80;

    public ProcessesTab(ConsoleWindowSystem windowSystem, IGpuStatsProvider stats)
        : base(windowSystem, stats)
    {
    }

    protected override List<string> BuildTextContent(GpuSnapshot snapshot) => new();
    protected override void BuildGraphsContent(ScrollablePanelControl panel, GpuSnapshot snapshot) { }
    protected override void UpdateHistory(GpuSnapshot snapshot) { }

    public override IWindowControl BuildPanel(GpuSnapshot initialSnapshot, int windowWidth)
    {
        var grid = new GridControl
        {
            Name = PanelControlName,
            VerticalAlignment = VerticalAlignment.Fill,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Margin(1, 0, 1, 1),
            BackgroundColor = UIConstants.BaseBg,
            ForegroundColor = UIConstants.PrimaryText
        };

        // Single cell: the table fills the whole panel.
        grid.ColumnDefinitions.Add(GridLength.Star(1.0));
        grid.RowDefinitions.Add(GridLength.Star(1.0));
        grid.Place(BuildTableControl(initialSnapshot), 0, 0, 1, 1);

        return grid;
    }

    private IWindowControl BuildTableControl(GpuSnapshot snapshot)
    {
        var tableBuilder = Controls.Table()
            .WithColumns("PID", "Name", "GPU Mem (MB)")
            .Rounded()
            .WithHorizontalAlignment(SharpConsoleUI.Layout.HorizontalAlignment.Stretch)
            .WithVerticalAlignment(SharpConsoleUI.Layout.VerticalAlignment.Fill);

        if (snapshot.Processes.Count == 0)
        {
            tableBuilder.AddRow("[dim]N/A[/]", "[dim]No compute processes running[/]", "[dim]0[/]");
        }
        else
        {
            foreach (var proc in snapshot.Processes.OrderByDescending(p => p.MemoryUsedMb))
            {
                tableBuilder.AddRow(
                    proc.Pid.ToString(),
                    proc.Name,
                    $"{proc.MemoryUsedMb:F0}"
                );
            }
        }

        return tableBuilder.Build();
    }

    protected override void UpdateGraphControls(IWindowControl grid, GpuSnapshot snapshot)
    {
        // The table fills the single grid cell; rebuild it from the fresh snapshot.
        if (grid is not GridControl gridControl)
            return;

        gridControl.ClearControls();
        gridControl.Place(BuildTableControl(snapshot), 0, 0, 1, 1);
    }
}
