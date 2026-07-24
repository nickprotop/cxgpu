using cxnvmon.Helpers;
using cxnvmon.Stats;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;

namespace cxnvmon.Tabs;

internal class DetailsTab : BaseResponsiveTab
{
    public override string Name => "Details";
    public override string PanelControlName => "DetailsPanel";

    protected override int LayoutThresholdWidth => 80;

    public DetailsTab(ConsoleWindowSystem windowSystem, IGpuStatsProvider stats)
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
            .WithColumns("Property", "Value")
            .Rounded()
            .WithHorizontalAlignment(SharpConsoleUI.Layout.HorizontalAlignment.Stretch)
            .WithVerticalAlignment(SharpConsoleUI.Layout.VerticalAlignment.Fill);

        var deviceInfos = Stats.ReadDeviceInfo();
        if (deviceInfos.Count > 0)
        {
            var info = deviceInfos[0];
            tableBuilder.AddRow("Name", info.Name);
            tableBuilder.AddRow("Driver Version", info.DriverVersion);
            tableBuilder.AddRow("VBIOS Version", info.VBiosVersion);
            tableBuilder.AddRow("PCIe Gen/Width", $"{info.PcieGenWidth}");
            tableBuilder.AddRow("Memory Total", $"{info.MemoryTotalMb:F0} MB");
            tableBuilder.AddRow("Power Limit", $"{info.PowerLimitWatts:F0} W");
            tableBuilder.AddRow("Temp Limit", $"{info.TemperatureLimitC:F0}°C");
            tableBuilder.AddRow("CUDA Version", info.CudaVersion);
        }
        else
        {
            tableBuilder.AddRow("[red]No device information available[/]", "");
        }

        return tableBuilder.Build();
    }

    protected override void UpdateGraphControls(IWindowControl grid, GpuSnapshot snapshot)
    {
        // Details is static.
    }
}
