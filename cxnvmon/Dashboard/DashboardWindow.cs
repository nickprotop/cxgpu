using cxnvmon.Configuration;
using cxnvmon.Helpers;
using cxnvmon.Stats;
using cxnvmon.Tabs;
using SharpConsoleUI;
using SharpConsoleUI.Animation;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Configuration;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;
using SharpConsoleUI.Rendering;

namespace cxnvmon.Dashboard;

internal sealed class DashboardWindow
{
    private readonly ConsoleWindowSystem _windowSystem;
    private readonly IGpuStatsProvider _stats;
    private readonly CxnvmonConfig _config;

    private Window? _mainWindow;
    private readonly List<ITab> _tabs = new();
    private TabControl? _tabControl;
    private StatusBarControl? _statusBar;

    public DashboardWindow(
        ConsoleWindowSystem windowSystem,
        IGpuStatsProvider stats,
        CxnvmonConfig config)
    {
        _windowSystem = windowSystem;
        _stats = stats;
        _config = config;
    }

    public void Create()
    {
        _mainWindow = new WindowBuilder(_windowSystem)
            .WithTitle("cxnvmon - NVIDIA GPU Monitor")
            .WithColors(UIConstants.PrimaryText, UIConstants.BaseBg)
            .Borderless()
            .Maximized()
            .Resizable(false)
            .Movable(false)
            .Closable(false)
            .Minimizable(false)
            .Maximizable(false)
            .WithAsyncWindowThread(UpdateLoopAsync)
            .OnKeyPressed((sender, e) =>
            {
                if (e.KeyInfo.Key == ConsoleKey.F10 || e.KeyInfo.Key == ConsoleKey.Escape)
                {
                    _windowSystem.Shutdown();
                    e.Handled = true;
                    return;
                }
                if (e.KeyInfo.Key == ConsoleKey.F9)
                {
                    OpenSettings();
                    e.Handled = true;
                    return;
                }
                if (HandleTabShortcut(e.KeyInfo.Key))
                    e.Handled = true;
            })
            .Build();

        if (_mainWindow == null) return;
        var mainWindow = _mainWindow;

        BuildTopStatusBar(mainWindow);
        mainWindow.AddControl(Controls.RuleBuilder().StickyTop().WithColor(UIConstants.SeparatorColor).Build());

        CreateTabs();
        var initialSnapshot = _stats.ReadSnapshot();
        BuildTabSection(mainWindow, initialSnapshot);

        BuildBottomStatusBar(mainWindow);

        mainWindow.OnResize += (sender, e) =>
        {
            foreach (var tab in _tabs)
                tab.HandleResize(mainWindow.Width, mainWindow.Height);
        };

        _windowSystem.AddWindow(mainWindow);

        // Always start on the Overview tab. Set again here after the window is added, so the
        // initial selection survives any layout/activation that occurs on AddWindow.
        if (_tabControl != null)
            _tabControl.ActiveTabIndex = OverviewTabIndex;

        WindowAnimations.FadeIn(mainWindow,
            duration: TimeSpan.FromMilliseconds(UIConstants.FadeInMs),
            fadeColor: Color.Black,
            easing: EasingFunctions.EaseInOut);
    }

    #region Top Status Bar

    private void BuildTopStatusBar(Window mainWindow)
    {
        var systemInfo = GpuStatsFactory.GetPlatformName();

        var topStatusBar = Controls.HorizontalGrid()
            .StickyTop()
            .WithAlignment(HorizontalAlignment.Stretch)
            .Column(col =>
                col.Add(Controls.Markup($"[{UIConstants.Accent.ToMarkup()} bold]cxnvmon[/] [{UIConstants.MutedText.ToMarkup()}]• {systemInfo}[/]")
                    .WithAlignment(HorizontalAlignment.Left)
                    .WithMargin(1, 0, 0, 0)
                    .Build()))
            .Column(col =>
                col.Add(Controls.Markup($"[{UIConstants.MutedText.ToMarkup()}]--:--:--[/]")
                    .WithAlignment(HorizontalAlignment.Right)
                    .WithMargin(0, 0, 1, 0)
                    .WithName("topStatusClock")
                    .Build()))
            .Build();

        topStatusBar.BackgroundColor = UIConstants.HeaderBg;
        topStatusBar.ForegroundColor = UIConstants.PrimaryText;
        mainWindow.AddControl(topStatusBar);
    }

    #endregion

    #region Tabs

    private void CreateTabs()
    {
        if (_config.ShowOverviewTab)
            _tabs.Add(new OverviewTab(_windowSystem, _stats));
        if (_config.ShowProcessesTab)
            _tabs.Add(new ProcessesTab(_windowSystem, _stats));
        if (_config.ShowDetailsTab)
            _tabs.Add(new DetailsTab(_windowSystem, _stats));
    }

    // Opens the modal settings dialog. The refresh interval applies live (the update loop reads
    // _config each cycle); tab-visibility changes require an app restart to rebuild the tab set,
    // which the dialog notes. Persisted config is written to disk by the dialog on Save.
    private void OpenSettings()
    {
        SettingsDialog.Show(_windowSystem, _config, updated =>
        {
            // Apply live-updatable settings in place so the running update loop picks them up.
            _config.RefreshIntervalMs = updated.RefreshIntervalMs;
            _config.ShowOverviewTab = updated.ShowOverviewTab;
            _config.ShowProcessesTab = updated.ShowProcessesTab;
            _config.ShowDetailsTab = updated.ShowDetailsTab;
        });
    }

    // Index of the Overview tab so startup always lands there — not just "tab 0", because tabs
    // are added conditionally (ShowOverviewTab etc.) and Overview could be reordered or absent.
    // Falls back to 0 when Overview isn't present.
    private int OverviewTabIndex
    {
        get
        {
            var index = _tabs.FindIndex(t => t is OverviewTab);
            return index >= 0 ? index : 0;
        }
    }

    private void BuildTabSection(Window mainWindow, GpuSnapshot initialSnapshot)
    {
        var builder = new TabControlBuilder()
            .WithHeaderStyle(TabHeaderStyle.AccentedSeparator)
            .Fill()
            .WithAlignment(HorizontalAlignment.Stretch);

        foreach (var tab in _tabs)
            builder = builder.AddTab(tab.Name, tab.BuildPanel(initialSnapshot, mainWindow.Width));

        _tabControl = builder.Build();
        _tabControl.ActiveTabIndex = OverviewTabIndex;
        _tabControl.BackgroundColor = UIConstants.BaseBg;
        _tabControl.TabChanged += (sender, e) =>
        {
            _windowSystem.Animations.Animate(
                1.0f, 0.0f,
                TimeSpan.FromMilliseconds(UIConstants.FadeInMs),
                easing: EasingFunctions.EaseInOut,
                onUpdate: intensity =>
                {
                },
                onComplete: () =>
                {
                });
        };
        mainWindow.AddControl(_tabControl);
    }

    // Tab hotkeys start at F2 (F1 is conventionally Help) and map to a tab TYPE, not a fixed
    // index — so they obey config: F2 Overview · F3 Processes · F4 Details. If a tab is hidden
    // (ShowXTab=false) it isn't in _tabs, so its key resolves to nothing and does nothing.
    private bool HandleTabShortcut(ConsoleKey key) => key switch
    {
        ConsoleKey.F2 => SelectTab<OverviewTab>(),
        ConsoleKey.F3 => SelectTab<ProcessesTab>(),
        ConsoleKey.F4 => SelectTab<DetailsTab>(),
        _ => false
    };

    // Selects the tab of the given type if it is present (i.e. enabled in config). Returns whether
    // the selection was applied — false when that tab is hidden, so the caller can treat the key
    // as unhandled.
    private bool SelectTab<T>() where T : ITab
    {
        var index = _tabs.FindIndex(t => t is T);
        if (index < 0 || _tabControl == null)
            return false;

        _tabControl.ActiveTabIndex = index;
        return true;
    }

    #endregion

    #region Bottom Status Bar

    private void BuildBottomStatusBar(Window mainWindow)
    {
        mainWindow.AddControl(Controls.RuleBuilder().StickyBottom().WithColor(UIConstants.SeparatorColor).Build());

        // Interactive StatusBarControl: each hint is a real clickable item that fires its action
        // on click AND is triggerable by the matching hotkey. The stats legend sits on the right
        // (updated live via the named right item / statsLegend).
        var builder = Controls.StatusBar();
        if (_config.ShowOverviewTab)
            builder.AddLeft("F2", "Overview", () => SelectTab<OverviewTab>());
        if (_config.ShowProcessesTab)
            builder.AddLeft("F3", "Processes", () => SelectTab<ProcessesTab>());
        if (_config.ShowDetailsTab)
            builder.AddLeft("F4", "Details", () => SelectTab<DetailsTab>());

        var statusBar = builder
            .AddLeftSeparator()
            .AddLeft("F9", "Settings", OpenSettings)
            .AddLeft("F10", "Exit", () => _windowSystem.Shutdown())
            .AddRightText(FormatStatsLegend(_stats.ReadSnapshot()))
            .WithBackgroundColor(UIConstants.HeaderBg)
            .WithForegroundColor(UIConstants.MutedText)
            .WithShortcutForegroundColor(UIConstants.Accent)
            .WithName("bottomStatusBar")
            .StickyBottom()
            .Build();

        _statusBar = statusBar;
        mainWindow.AddControl(statusBar);
    }

    #endregion

    #region Update Loop

    private async Task UpdateLoopAsync(Window window, CancellationToken cancellationToken)
    {
        await PrimeStatsAsync(cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var snapshot = _stats.ReadSnapshot();

                _windowSystem.EnqueueOnUIThread(() =>
                {
                    UpdateClock(window);

                    UpdateActiveTab(snapshot);
                    UpdateBottomStats(window, snapshot);
                });
            }
            catch (Exception ex)
            {
                _windowSystem.LogService.LogError("Update loop error", ex, "cxnvmon");
            }

            try
            {
                await Task.Delay(_config.RefreshIntervalMs, cancellationToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    private async Task PrimeStatsAsync(CancellationToken cancellationToken)
    {
        try
        {
            _stats.ReadSnapshot();
            await Task.Delay(300, cancellationToken);
        }
        catch
        {
        }
    }

    private void UpdateClock(Window window)
    {
        var clock = window.FindControl<MarkupControl>("topStatusClock");
        if (clock != null)
        {
            var timeStr = DateTime.Now.ToString("HH:mm:ss");
            clock.SetContent(new List<string> { $"[{UIConstants.MutedText.ToMarkup()}]{timeStr}[/]" });
        }
    }

    private void UpdateActiveTab(GpuSnapshot snapshot)
    {
        if (_tabControl == null) return;
        var i = _tabControl.ActiveTabIndex;
        if (i >= 0 && i < _tabs.Count)
            _tabs[i].UpdatePanel(snapshot);
    }

    private void UpdateBottomStats(Window window, GpuSnapshot snapshot)
    {
        if (_statusBar == null || snapshot.Gpus.Count == 0)
            return;

        // The stats legend is the single right-side item; update its label in place.
        var legend = _statusBar.RightItems.LastOrDefault();
        if (legend != null)
            legend.Label = FormatStatsLegend(snapshot);
    }

    // Right-aligned live GPU/MEM readout for the bottom status bar. StatusBarItem labels DO parse
    // markup, so colors follow the usage thresholds. Note: interpolate Color via .ToMarkup()
    // (emits valid rgb(...)); a bare Color stringifies to "Color(r,g,b)", which is not valid markup.
    private static string FormatStatsLegend(GpuSnapshot snapshot)
    {
        if (snapshot.Gpus.Count == 0)
            return "No GPU";

        var gpu = snapshot.Gpus[0];
        var utilColor = UIConstants.ThresholdColor(gpu.UtilizationPercent).ToMarkup();
        var memColor = UIConstants.ThresholdColor(gpu.MemoryUsedPercent).ToMarkup();
        var muted = UIConstants.MutedText.ToMarkup();
        return $"[{muted}]GPU 0[/] [{utilColor}]{gpu.UtilizationPercent:F0}%[/] [{muted}]•[/] [{muted}]MEM[/] [{memColor}]{gpu.MemoryUsedPercent:F0}%[/]";
    }

    #endregion
}
