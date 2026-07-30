using cxgpu.Configuration;
using cxgpu.Helpers;
using cxgpu.Gpu;
using cxgpu.Gpu.Alerts;
using cxgpu.Tabs;
using cxgpu.Widgets;
using SharpConsoleUI;
using SharpConsoleUI.Animation;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Configuration;
using SharpConsoleUI.Core;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;
using SharpConsoleUI.Helpers;
using SharpConsoleUI.Rendering;

namespace cxgpu.Dashboard;

internal sealed class DashboardWindow
{
    private readonly ConsoleWindowSystem _windowSystem;
    private readonly IGpuStatsProvider _stats;
    private readonly CxgpuConfig _config;

    private Window? _mainWindow;
    private readonly List<ITab> _tabs = new();

    // Whether this host has more than one GPU, latched from the first snapshot. Drives whether the
    // process list is scoped to a single GPU — on a single-GPU box scoping would just be a way to
    // accidentally hide processes.
    private bool _multiGpu;
    private TabControl? _tabControl;
    private StatusBarControl? _statusBar;

    // The alert-center status-bar item, mutated in place as events fire (see UpdateAlertBadge).
    private StatusBarItem? _alertItem;

    private readonly AlertEngine _alerts;

    // Live toast ids keyed by condition, so at most one toast exists per (gpu, metric) — an
    // oscillating card would otherwise stack sticky toasts until the screen is unusable.
    private readonly Dictionary<(int, EventMetric), string> _alertToasts = new();

    public DashboardWindow(
        ConsoleWindowSystem windowSystem,
        IGpuStatsProvider stats,
        CxgpuConfig config)
    {
        _windowSystem = windowSystem;
        _stats = stats;
        _config = config;

        // Thresholds are resolved per card through the config (card -> vendor -> built-in default)
        // rather than baked into the engine, so a user override takes effect without the engine
        // knowing config exists.
        _alerts = new AlertEngine(config.Alerts.ResolveFor);

        // Point temperature COLOURING at the same thresholds the alerts use, so the colour on screen
        // and the alert that fires agree, and a per-card override moves both together.
        //
        // Resolution needs the device info, which a GpuSample does not carry, so it is looked up by
        // index from a cache refreshed once per tick. Calling ReadDeviceInfo() here directly would
        // spawn a vendor subprocess for EVERY coloured temperature on EVERY frame.
        GpuFormat.TemperatureThresholds = gpu =>
            _thresholdCache.TryGetValue(gpu.Index, out var pair)
                ? pair
                : AlertThresholds.NvidiaConsumer.TemperatureC;
    }

    // Per-GPU temperature thresholds, refreshed once per update tick (see RefreshThresholdCache).
    private readonly Dictionary<int, ThresholdPair?> _thresholdCache = new();

    // Session peaks and throttle time, for the Overview's SESSION section and the exit summary.
    private readonly SessionStats _session = new(DateTime.UtcNow);

    /// <summary>
    /// Prints what the session's GPUs actually did — peaks, throttle time, critical events.
    ///
    /// Silent when nothing fired: a clean run should exit without ceremony, and a summary that always
    /// prints stops being read. Call AFTER the window system has stopped, so this reaches the user's
    /// scrollback rather than the alternate screen buffer.
    /// </summary>
    public void PrintSessionSummary()
    {
        if (!_session.AnythingHappened) return;

        var now = DateTime.UtcNow;
        var deviceInfos = _stats.ReadDeviceInfo();

        Console.WriteLine();
        Console.WriteLine($"cxgpu session summary ({GpuFormat.Duration(_session.Elapsed(now))})");

        foreach (var (index, stats) in _session.All.OrderBy(kv => kv.Key))
        {
            var name = deviceInfos.FirstOrDefault(d => d.Index == index)?.Name ?? $"GPU {index}";

            var parts = new List<string> { $"peak {stats.PeakTemperatureC:F0}°C" };
            if (stats.HasPower) parts.Add($"peak {stats.PeakPowerWatts:F0} W");

            var throttled = _session.ThrottledFor(index, now);
            if (throttled > TimeSpan.Zero) parts.Add($"throttled {GpuFormat.Duration(throttled)}");
            if (stats.CriticalEvents > 0) parts.Add($"{stats.CriticalEvents} critical");

            Console.WriteLine($"  GPU {index} · {name}: {string.Join(", ", parts)}");
        }

        Console.WriteLine();
    }

    /// <summary>
    /// Re-resolves each card's thresholds. Called once per tick rather than per render, since device
    /// info comes from a vendor subprocess and the answer only changes when config or hardware does.
    /// </summary>
    private void RefreshThresholdCache(IReadOnlyList<GpuDeviceInfo> deviceInfos)
    {
        foreach (var info in deviceInfos)
            _thresholdCache[info.Index] = _config.Alerts.ResolveFor(info).TemperatureC;
    }

    private GpuBackendRegistry? Registry => _stats as GpuBackendRegistry;

    // Announces operations slow enough to notice — currently the GPU switch, whose repaint costs
    // several blocking vendor-tool reads.
    private BusyIndicator _busy = null!;

    public void Create()
    {
        _mainWindow = new WindowBuilder(_windowSystem)
            .WithTitle("cxgpu - NVIDIA GPU Monitor")
            .WithColors(UIConstants.PrimaryText, UIConstants.BaseBg)
            .WithBackgroundGradient(
                ColorGradient.FromColors(UIConstants.BaseBg, UIConstants.BaseEnd),
                GradientDirection.DiagonalDown)
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
                if (e.KeyInfo.Key == ConsoleKey.F1 || e.KeyInfo.KeyChar == '?')
                {
                    OpenHelp();
                    e.Handled = true;
                    return;
                }
                if (HandleTabShortcut(e.KeyInfo.Key))
                {
                    e.Handled = true;
                    return;
                }
                if (HandleProcessShortcut(e.KeyInfo))
                {
                    e.Handled = true;
                    return;
                }
                if (HandleGpuSelectionShortcut(e.KeyInfo))
                    e.Handled = true;
            })
            .Build();

        if (_mainWindow == null) return;
        var mainWindow = _mainWindow;

        _busy = new BusyIndicator(_windowSystem);

        // Register the GPU backends as framework plugins now that the window system exists. They were
        // constructed and probed before it did (the factory runs first), so registration is a separate
        // step from probing. It only affects discoverability — plugin state, events, and service
        // lookup by name — since the app reads the backends through their typed interface.
        Registry?.RegisterWithPluginSystem(_windowSystem);

        BuildTopStatusBar(mainWindow);
        mainWindow.AddControl(Controls.RuleBuilder().StickyTop().WithColor(UIConstants.SeparatorColor).Build());

        // Read the first snapshot BEFORE creating tabs: the GPU count decides whether the process
        // list scopes to one GPU, and the tabs capture that decision at construction.
        var initialSnapshot = _stats.ReadSnapshot();
        _multiGpu = initialSnapshot.Gpus.Count > 1;

        CreateTabs();
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
        // Name the vendors actually serving data rather than a hardcoded platform string: with two
        // backends now possible, "Linux (NVIDIA)" would be wrong on an AMD-only or hybrid machine.
        // Falls back to the platform label in demo mode (which says DEMO) or when nothing probed.
        var systemInfo = ActiveVendorLabel() ?? GpuStatsFactory.GetPlatformName();

        var topStatusBar = Controls.HorizontalGrid()
            .StickyTop()
            .WithAlignment(HorizontalAlignment.Stretch)
            .Column(col =>
                col.Add(Controls.Markup($"[{UIConstants.Accent.ToMarkup()} bold]cxgpu[/] [{UIConstants.MutedText.ToMarkup()}]• {systemInfo}[/]")
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

    // "NVIDIA + AMD", or a single vendor's name — from the backends that actually probed, so the
    // header describes this machine. Null when there is nothing to report (demo mode, or no GPU), so
    // the caller falls back to the platform label.
    private string? ActiveVendorLabel()
    {
        var backends = Registry?.ActiveBackends;
        if (backends == null || backends.Count == 0) return null;

        var vendors = backends
            .Select(b => b.InfoVia().Vendor)
            .Where(v => !string.IsNullOrWhiteSpace(v) && v != "Demo")
            .Distinct()
            .ToList();

        return vendors.Count == 0 ? null : string.Join(" + ", vendors);
    }

    #endregion

    #region Tabs

    private void CreateTabs()
    {
        if (_config.ShowOverviewTab)
            _tabs.Add(new OverviewTab(_windowSystem, _stats, _config, _busy.Run, _session));
        if (_config.ShowProcessesTab)
            // The process list follows the Overview's GPU selection (and only scopes at all when
            // there's more than one GPU), so switching GPU switches both views together.
            _tabs.Add(new ProcessesTab(
                _windowSystem,
                _stats,
                selectedGpuIndex: () => SelectedGpuIndex,
                isMultiGpu: () => _multiGpu));
        // Details tab retired: the Overview left panel is now the full device spec-sheet, so a
        // separate Details tab would just duplicate it. (DetailsTab.cs / the ShowDetailsTab config
        // are kept so it can be re-enabled if ever needed.)
    }

    // Opens the modal settings dialog. The refresh interval applies live (the update loop reads
    // _config each cycle); tab-visibility changes require an app restart to rebuild the tab set,
    // which the dialog notes. Persisted config is written to disk by the dialog on Save.
    private void OpenSettings()
    {
        // Pass the live backends so the dialog can render their own declared settings generically.
        var backends = Registry?.ActiveBackends;
        SettingsDialog.Show(_windowSystem, _config, updated =>
        {
            // Apply live-updatable settings in place so the running update loop picks them up.
            _config.RefreshIntervalMs = updated.RefreshIntervalMs;
            _config.ShowOverviewTab = updated.ShowOverviewTab;
            _config.ShowProcessesTab = updated.ShowProcessesTab;
            _config.ShowDetailsTab = updated.ShowDetailsTab;
            // Backend enable/disable and backend-declared settings take effect on restart: the
            // registry probes once at startup, so re-applying them live would not change what loaded.
            _config.EnableNvidiaBackend = updated.EnableNvidiaBackend;
            _config.EnableAmdBackend = updated.EnableAmdBackend;
            _config.BackendSettings = updated.BackendSettings;
        }, backends);
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
        mainWindow.AddControl(_tabControl);
    }

    // Tab hotkeys start at F2 (F1 is conventionally Help) and map to a tab TYPE, not a fixed
    // index — so they obey config: F2 Overview · F3 Processes. If a tab is hidden (ShowXTab=false)
    // it isn't in _tabs, so its key resolves to nothing and does nothing.
    private bool HandleTabShortcut(ConsoleKey key) => key switch
    {
        ConsoleKey.F2 => SelectTab<OverviewTab>(),
        ConsoleKey.F3 => SelectTab<ProcessesTab>(),
        _ => false
    };

    // Opens the keyboard-shortcut overlay. Told whether this host is multi-GPU so it can mark the
    // GPU-selection keys as inapplicable rather than advertising keys that do nothing here.
    private void OpenHelp() => HelpDialog.Show(_windowSystem, _multiGpu);

    // 'k' signals the process selected in the Processes tab. Scoped to that tab: elsewhere the key
    // has no meaning, and it must not be a global "kill something" binding.
    private bool HandleProcessShortcut(ConsoleKeyInfo keyInfo)
    {
        if (keyInfo.KeyChar is not ('k' or 'K')) return false;
        if (_tabControl == null) return false;

        var i = _tabControl.ActiveTabIndex;
        if (i < 0 || i >= _tabs.Count) return false;
        if (_tabs[i] is not ProcessesTab processes) return false;

        processes.ShowSignalDialogForSelection();
        return true;
    }

    // GPU selection keys (multi-GPU Architecture C): '[' / ']' step through the GPUs and '1'–'9'
    // jump straight to a GPU index (key '1' = GPU 0, matching how the tiles are labelled starting
    // at 0 — the digit is the ORDINAL, not the index, so '1' is the first GPU).
    // Matched on KeyChar, not ConsoleKey: bracket/digit keys vary by keyboard layout, and KeyChar
    // is what the user actually typed. On a single-GPU box these are all no-ops (CycleGpu/SelectGpu
    // return false), so the keys stay unhandled and don't swallow anything.
    private bool HandleGpuSelectionShortcut(ConsoleKeyInfo keyInfo)
    {
        var overview = _tabs.OfType<OverviewTab>().FirstOrDefault();
        if (overview == null) return false;

        switch (keyInfo.KeyChar)
        {
            case '[': return overview.CycleGpu(-1);
            case ']': return overview.CycleGpu(1);
        }

        if (keyInfo.KeyChar >= '1' && keyInfo.KeyChar <= '9')
            return overview.SelectGpu(keyInfo.KeyChar - '1');

        return false;
    }

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

        // The alert item. Held as a field so its label and colour can be updated live as events fire,
        // rather than rebuilding the bar. Starts hidden: an empty alert center is not worth a slot,
        // and chrome that is always present stops meaning anything.
        _alertItem = new StatusBarItem
        {
            Shortcut = "!",
            Label = "Alerts",
            IsVisible = false,
            OnClick = () => AlertPortal.Toggle(_windowSystem, _alerts)
        };

        var statusBar = builder
            .AddLeftSeparator()
            .AddLeft("?", "Help", OpenHelp)
            .AddLeft("F9", "Settings", OpenSettings)
            .AddLeft("F10", "Exit", () => _windowSystem.Shutdown())
            // The alert badge sits at the FAR RIGHT, after the stats legend: it reports state rather
            // than offering navigation, so it belongs with the readouts and not among the shortcut
            // hints — and a hint list whose contents shift as alerts come and go is harder to build
            // muscle memory against. Last position keeps it in a fixed corner, so its appearance is
            // noticeable and its location predictable.
            .AddRightText(FormatStatsLegend(_stats.ReadSnapshot(), SelectedGpuIndex))
            .AddRight(_alertItem)
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

    /// <summary>
    /// Folds a snapshot into the alert engine, then reflects the result in the badge, the portal and
    /// any toasts. Called once per refresh from the update loop, on the UI thread.
    /// </summary>
    private void UpdateAlerts(GpuSnapshot snapshot)
    {
        var deviceInfos = _stats.ReadDeviceInfo();

        // Colouring follows the configured thresholds even when alerting is switched off — the colours
        // are about reading the numbers correctly, not about being notified.
        RefreshThresholdCache(deviceInfos);

        // Peaks are recorded regardless of the Enabled flag: they are a record of what the hardware
        // did, not a notification, and a user who turned alerts off still wants to know how hot it got.
        _session.Observe(snapshot);

        if (!_config.Alerts.Enabled) return;

        var now = DateTime.UtcNow;
        var changes = _alerts.Evaluate(snapshot, deviceInfos, now);
        _session.Observe(changes, now);

        UpdateAlertBadge();
        ShowAlertToasts(changes);

        // Only rebuild the open portal when something actually changed — it is rebuilt from scratch,
        // and doing that every tick would fight the user's scroll position.
        if (changes.Any && AlertPortal.IsOpen)
            AlertPortal.Refresh(_windowSystem, _alerts);
    }

    /// <summary>
    /// Shows the alert item with a count and severity colour, or hides it when nothing is active.
    ///
    /// Hidden at zero on purpose: chrome that is always present stops carrying information, and the
    /// point of a badge is that its appearance means something happened.
    /// </summary>
    private void UpdateAlertBadge()
    {
        if (_alertItem == null) return;

        int active = _alerts.Active.Count;
        int total = _alerts.History.Count;

        // EVERY setter on StatusBarItem calls OnItemChanged -> Invalidate(Relayout), so assigning
        // unconditionally would force a full status-bar relayout on every tick. That starved the rest
        // of the UI: hints stopped rendering and the window stopped responding to F10/Ctrl+C.
        // Only touch a property when its value actually changes.

        // Visible once ANYTHING has happened this session, not only while something is active: the
        // portal keeps resolved events precisely so "did it throttle while I was away?" can be
        // answered, and hiding the only way to open it the moment a condition clears would make that
        // history unreachable. A machine that has been clean all session still shows no new chrome.
        bool visible = total > 0;
        if (_alertItem.IsVisible != visible) _alertItem.IsVisible = visible;
        if (!visible) return;

        // Active count while something is live; the session total once everything has cleared, so the
        // label says what clicking it will show.
        var label = active > 0
            ? (active == 1 ? "1 alert" : $"{active} alerts")
            : (total == 1 ? "1 past" : $"{total} past");
        if (_alertItem.Label != label) _alertItem.Label = label;

        // Muted once nothing is active — the history is still reachable, but it is no longer something
        // demanding attention.
        var colour = active == 0
            ? UIConstants.MutedText
            : _alerts.WorstActive == EventSeverity.Critical
                ? UIConstants.Critical
                : UIConstants.Warning;
        if (_alertItem.LabelForeground != colour) _alertItem.LabelForeground = colour;
    }

    /// <summary>
    /// Raises toasts for newly raised events and dismisses those of resolved ones.
    ///
    /// Warning toasts auto-dismiss; Critical toasts are sticky, because a thermal throttle that
    /// scrolled past unseen defeats the point of alerting at all.
    ///
    /// AT MOST ONE TOAST PER (gpu, metric): a card oscillating around a threshold would otherwise
    /// stack sticky toasts until the screen is unusable, so a new event for a condition dismisses the
    /// previous toast for that same condition first.
    /// </summary>
    private void ShowAlertToasts(AlertChanges changes)
    {
        foreach (var e in changes.Resolved)
            DismissAlertToast(e.Key);

        foreach (var e in changes.Raised)
        {
            bool critical = e.Severity == EventSeverity.Critical;
            if (critical ? !_config.Alerts.ToastOnCritical : !_config.Alerts.ToastOnWarning)
                continue;

            DismissAlertToast(e.Key);

            var id = _windowSystem.ToastService.Show(
                $"GPU {e.GpuIndex}: {e.Description}",
                critical ? NotificationSeverity.Danger : NotificationSeverity.Warning,
                new ToastOptions(
                    Timeout: critical ? null : WarningToastMs,
                    Sticky: critical,
                    // TOP right, away from both the alert badge and the portal it opens — a sticky
                    // critical toast anchored bottom-right sat directly on top of the badge,
                    // permanently hiding the one control that reaches the event history.
                    Position: ToastPosition.TopRight));

            _alertToasts[e.Key] = id;
        }
    }

    private void DismissAlertToast((int, EventMetric) key)
    {
        if (!_alertToasts.TryGetValue(key, out var id)) return;
        _windowSystem.ToastService.Dismiss(id);
        _alertToasts.Remove(key);
    }

    private const int WarningToastMs = 6000;

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
                    UpdateAlerts(snapshot);
                });
            }
            catch (Exception ex)
            {
                _windowSystem.LogService.LogError("Update loop error", ex, "cxgpu");
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
            legend.Label = FormatStatsLegend(snapshot, SelectedGpuIndex);
    }

    // The GPU the Overview is showing, so the status-bar legend reports the same card the user is
    // looking at rather than always GPU 0. Falls back to 0 when the Overview tab is disabled.
    private int SelectedGpuIndex =>
        _tabs.OfType<OverviewTab>().FirstOrDefault()?.SelectedGpuIndex ?? 0;

    // Right-aligned live GPU/MEM readout for the bottom status bar. StatusBarItem labels DO parse
    // markup, so colors follow the usage thresholds. Note: interpolate Color via .ToMarkup()
    // (emits valid rgb(...)); a bare Color stringifies to "Color(r,g,b)", which is not valid markup.
    private static string FormatStatsLegend(GpuSnapshot snapshot, int selectedGpuIndex)
    {
        if (snapshot.Gpus.Count == 0)
            return "No GPU";

        var gpu = snapshot.Gpus.FirstOrDefault(g => g.Index == selectedGpuIndex) ?? snapshot.Gpus[0];
        var utilColor = UIConstants.ThresholdColor(gpu.UtilizationPercent).ToMarkup();
        var memColor = UIConstants.ThresholdColor(gpu.MemoryUsedPercent).ToMarkup();
        var muted = UIConstants.MutedText.ToMarkup();
        return $"[{muted}]GPU {gpu.Index}[/] [{utilColor}]{gpu.UtilizationPercent:F0}%[/] [{muted}]•[/] [{muted}]MEM[/] [{memColor}]{gpu.MemoryUsedPercent:F0}%[/]";
    }

    #endregion
}
