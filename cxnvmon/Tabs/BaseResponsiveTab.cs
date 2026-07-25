using cxnvmon.Helpers;
using cxnvmon.Stats;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;

namespace cxnvmon.Tabs;

internal enum ResponsiveLayoutMode { Wide, Narrow }

internal abstract class BaseResponsiveTab : ITab
{
    protected readonly ConsoleWindowSystem WindowSystem;
    protected readonly IGpuStatsProvider Stats;
    protected ResponsiveLayoutMode _currentLayout = ResponsiveLayoutMode.Wide;

    protected BaseResponsiveTab(ConsoleWindowSystem windowSystem, IGpuStatsProvider stats)
    {
        WindowSystem = windowSystem;
        Stats = stats;
    }

    #region Abstract Members
    
    public abstract string Name { get; }
    public abstract string PanelControlName { get; }
    protected abstract int LayoutThresholdWidth { get; }
    protected abstract List<string> BuildTextContent(GpuSnapshot snapshot);
    protected abstract void BuildGraphsContent(ScrollablePanelControl panel, GpuSnapshot snapshot);
    protected abstract void UpdateHistory(GpuSnapshot snapshot);
    protected abstract void UpdateGraphControls(IWindowControl grid, GpuSnapshot snapshot);

    #endregion

    #region ITab Implementation
    
    public virtual IWindowControl BuildPanel(GpuSnapshot initialSnapshot, int windowWidth)
    {
        _currentLayout = windowWidth >= LayoutThresholdWidth
            ? ResponsiveLayoutMode.Wide
            : ResponsiveLayoutMode.Narrow;

        return _currentLayout == ResponsiveLayoutMode.Wide
            ? BuildWideGrid(initialSnapshot)
            : BuildNarrowGrid(initialSnapshot);
    }

    public void UpdatePanel(GpuSnapshot snapshot)
    {
        var grid = FindPanel();
        if (grid == null)
            return;

        UpdateHistory(snapshot);
        UpdateGraphControls(grid, snapshot);

        UpdateLeftColumnText(grid, snapshot);
    }

    public virtual void HandleResize(int newWidth, int newHeight)
    {
        var grid = FindPanel();
        if (grid == null || !grid.Visible)
            return;

        var desired = newWidth >= LayoutThresholdWidth
            ? ResponsiveLayoutMode.Wide
            : ResponsiveLayoutMode.Narrow;

        if (desired == _currentLayout)
            return;

        _currentLayout = desired;
        RebuildColumns(grid);
    }

    #endregion

    #region Grid Building
    
    private HorizontalGridControl BuildWideGrid(GpuSnapshot snapshot)
    {
        var lines = BuildTextContent(snapshot);

        var grid = Controls
            .HorizontalGrid()
            .WithName(PanelControlName)
            .WithVerticalAlignment(VerticalAlignment.Fill)
            .WithAlignment(HorizontalAlignment.Stretch)
            .WithMargin(1, 0, 1, 1)
            .Column(col =>
            {
                col.Width(UIConstants.FixedTextColumnWidth);
                var leftPanel = BuildScrollablePanel();
                AddMarkupLines(leftPanel, lines);
                col.Add(leftPanel);
            })
            .Column(col =>
            {
                col.Width(UIConstants.SeparatorColumnWidth);
                col.Add(new SeparatorControl
                {
                    ForegroundColor = UIConstants.SeparatorColor,
                    VerticalAlignment = VerticalAlignment.Fill
                });
            })
            .Column(col =>
            {
                var rightPanel = BuildRightPanel();
                BuildGraphsContent(rightPanel, snapshot);
                col.Add(rightPanel);
            })
            .Build();

        grid.BackgroundColor = UIConstants.BaseBg;
        grid.ForegroundColor = UIConstants.PrimaryText;
        return grid;
    }

    private HorizontalGridControl BuildNarrowGrid(GpuSnapshot snapshot)
    {
        var lines = BuildTextContent(snapshot);

        var grid = Controls
            .HorizontalGrid()
            .WithName(PanelControlName)
            .WithVerticalAlignment(VerticalAlignment.Fill)
            .WithAlignment(HorizontalAlignment.Stretch)
            .WithMargin(1, 0, 1, 1)
            .Column(col =>
            {
                var scrollPanel = BuildScrollablePanel();
                AddMarkupLines(scrollPanel, lines);
                AddNarrowSeparator(scrollPanel);
                BuildGraphsContent(scrollPanel, snapshot);
                col.Add(scrollPanel);
            })
            .Build();

        grid.BackgroundColor = UIConstants.BaseBg;
        grid.ForegroundColor = UIConstants.PrimaryText;
        return grid;
    }

    #endregion

    #region Column Rebuild on Resize
    
    private void RebuildColumns(IWindowControl grid)
    {
        if (grid is not HorizontalGridControl hgc) return;
        var snapshot = GetLatestSnapshot();

        for (int i = hgc.Columns.Count - 1; i >= 0; i--)
            hgc.RemoveColumn(hgc.Columns[i]);

        if (_currentLayout == ResponsiveLayoutMode.Wide)
            BuildWideColumns(hgc, snapshot);
        else
            BuildNarrowColumns(hgc, snapshot);

        FindMainWindow()?.ForceRebuildLayout();
    }

    private void BuildWideColumns(HorizontalGridControl grid, GpuSnapshot snapshot)
    {
        var lines = BuildTextContent(snapshot);

        var leftPanel = BuildScrollablePanel();
        AddMarkupLines(leftPanel, lines);

        var leftCol = new ColumnContainer(grid) { Width = UIConstants.FixedTextColumnWidth };
        leftCol.AddContent(leftPanel);
        grid.AddColumn(leftCol);

        var sepCol = new ColumnContainer(grid) { Width = UIConstants.SeparatorColumnWidth };
        sepCol.AddContent(new SeparatorControl
        {
            ForegroundColor = UIConstants.SeparatorColor,
            VerticalAlignment = VerticalAlignment.Fill
        });
        grid.AddColumn(sepCol);

        var rightPanel = BuildScrollablePanel();
        BuildGraphsContent(rightPanel, snapshot);

        var rightCol = new ColumnContainer(grid);
        rightCol.AddContent(rightPanel);
        grid.AddColumn(rightCol);
    }

    private void BuildNarrowColumns(HorizontalGridControl grid, GpuSnapshot snapshot)
    {
        var lines = BuildTextContent(snapshot);

        var scrollPanel = BuildScrollablePanel();
        AddMarkupLines(scrollPanel, lines);
        AddNarrowSeparator(scrollPanel);
        BuildGraphsContent(scrollPanel, snapshot);

        var col = new ColumnContainer(grid);
        col.AddContent(scrollPanel);
        grid.AddColumn(col);
    }

    #endregion

    #region Update Helpers
    
    protected virtual void UpdateLeftColumnText(IWindowControl grid, GpuSnapshot snapshot)
    {
        if (grid is not HorizontalGridControl hgc || hgc.Columns.Count == 0)
            return;

        var leftCol = hgc.Columns[0];
        var leftPanel = leftCol.Contents.FirstOrDefault() as ScrollablePanelControl;
        if (leftPanel == null || leftPanel.Children.Count == 0)
            return;

        var markup = leftPanel.Children[0] as MarkupControl;
        if (markup != null)
        {
            var lines = BuildTextContent(snapshot);
            markup.SetContent(lines);
        }
    }

    // The graphs live in a DEDICATED panel that carries this name, set at build time in both
    // the wide (right column) and narrow (single column) layouts. FindGraphPanel must match it
    // by name: the wide layout has TWO ScrollablePanels (left text + right graphs), and a
    // "first panel wins" search would return the text panel, so live bar/sparkline updates would
    // silently no-op against a panel that holds none of them.
    protected const string GraphPanelName = "GraphPanel";

    protected ScrollablePanelControl? FindGraphPanel(IWindowControl panel)
    {
        if (panel is ScrollablePanelControl spc && spc.Name == GraphPanelName) return spc;

        if (panel is GridControl container)
        {
            foreach (var child in container.Children)
            {
                var found = FindGraphPanel(child);
                if (found != null) return found;
            }
        }

        return null;
    }

    #endregion

    #region Shared Utilities
    
    internal static ScrollablePanelControl BuildScrollablePanel()
    {
        var panel = Controls
            .ScrollablePanel()
            .WithVerticalAlignment(VerticalAlignment.Fill)
            .WithAlignment(HorizontalAlignment.Stretch)
            .Build();
        panel.BackgroundColor = UIConstants.BaseBg;
        panel.ForegroundColor = UIConstants.PrimaryText;
        return panel;
    }

    internal static ScrollablePanelControl BuildRightPanel()
    {
        var panel = Controls
            .ScrollablePanel()
            .WithVerticalAlignment(VerticalAlignment.Fill)
            .WithAlignment(HorizontalAlignment.Stretch)
            .Build();
        panel.BackgroundColor = UIConstants.RightPanelBg;
        panel.ForegroundColor = UIConstants.PrimaryText;
        return panel;
    }

    internal static ScrollablePanelControl BuildCard(string? header = null)
    {
        var builder = Controls
            .ScrollablePanel()
            .Rounded()
            .WithBorderColor(UIConstants.SeparatorColor)
            .WithBackgroundColor(UIConstants.CardBg)
            .WithPadding(1, 0, 1, 0)
            .WithAlignment(HorizontalAlignment.Stretch);

        if (header != null)
            builder = builder.WithHeader(CardHeaderMarkup(header));

        var card = builder.Build();
        card.ForegroundColor = UIConstants.PrimaryText;
        return card;
    }

    // Wraps a card title in the CardTitle color markup. The card header is otherwise painted with
    // the (subtle) border color, so titles must carry their own color — and, crucially, live
    // header updates (card.Header = ...) MUST route through here too, or a refresh reverts the
    // title to the dim border color.
    internal static string CardHeaderMarkup(string title) =>
        $"[{UIConstants.CardTitle.ToMarkup()} bold]{title}[/]";

    internal static void AddMarkupLines(ScrollablePanelControl panel, List<string> lines)
    {
        var markup = Controls.Markup();
        foreach (var line in lines)
            markup.AddLine(line);
        panel.AddControl(markup.Build());
    }

    // As AddMarkupLines, but names the markup control so it can be found and refreshed later
    // (needed wherever the text content is live rather than static).
    internal static void AddNamedMarkupLines(ScrollablePanelControl panel, List<string> lines, string name)
    {
        var markup = Controls.Markup().WithName(name);
        foreach (var line in lines)
            markup.AddLine(line);
        panel.AddControl(markup.Build());
    }

    internal static void AddNarrowSeparator(ScrollablePanelControl panel)
    {
        panel.AddControl(
            Controls.RuleBuilder()
                .WithColor(UIConstants.SeparatorColor)
                .Build()
        );
    }

    protected static void AddSectionSeparator(ScrollablePanelControl panel)
    {
        panel.AddControl(
            Controls.RuleBuilder()
                .WithColor(UIConstants.SeparatorColor)
                .Build()
        );
    }

    protected static void AddSectionHeader(ScrollablePanelControl panel, string title, string? name = null)
    {
        var builder = Controls.Markup()
            .AddLine(SectionHeaderMarkup(title))
            .WithAlignment(HorizontalAlignment.Left)
            .WithMargin(2, 0, 2, 1);

        if (name != null)
            builder = builder.WithName(name);

        panel.AddControl(builder.Build());
    }

    // Section-header styling. Live updates (markup.SetContent) MUST route through here, or the
    // refreshed header loses the muted/bold treatment.
    internal static string SectionHeaderMarkup(string title) =>
        $"[{UIConstants.MutedText.ToMarkup()} bold]{title}[/]";

    protected static void AddFluentSectionLabel(ScrollablePanelControl panel, string title)
    {
        panel.AddControl(
            Controls.Markup()
                .AddLine($"[{UIConstants.Accent.ToMarkup()}]{title}[/]")
                .WithAlignment(HorizontalAlignment.Left)
                .WithMargin(1, 1, 1, 0)
                .Build()
        );
        panel.AddControl(
            Controls.RuleBuilder()
                .WithColor(UIConstants.SeparatorColor)
                .Build()
        );
    }

    #endregion

    #region Window Access
    
    protected void TriggerRebuild()
    {
        var grid = FindMainWindow()?.FindControl<IWindowControl>(PanelControlName);
        if (grid is HorizontalGridControl hgc) RebuildColumns(hgc);
    }

    private IWindowControl? FindPanel()
    {
        return FindMainWindow()?.FindControl<IWindowControl>(PanelControlName);
    }

    protected Window? FindMainWindow()
    {
        return WindowSystem.Windows.Values.FirstOrDefault();
    }

    private GpuSnapshot GetLatestSnapshot()
    {
        return Stats.ReadSnapshot();
    }

    #endregion
}
