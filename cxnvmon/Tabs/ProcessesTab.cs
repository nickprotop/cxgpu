using System.Runtime.InteropServices;
using cxnvmon.Helpers;
using cxnvmon.Stats;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Core;
using SharpConsoleUI.Extensions;
using SharpConsoleUI.Layout;

namespace cxnvmon.Tabs;

/// <summary>
/// GPU process list, rendered as an inline-expand tree: each process is a collapsed row of aligned
/// columns, and expanding it reveals the full command plus per-process detail and the signal actions
/// in place — so the detail can be read without losing sight of the surrounding processes.
/// </summary>
internal class ProcessesTab : BaseResponsiveTab
{
    public override string Name => "Processes";
    public override string PanelControlName => "ProcessesPanel";

    protected override int LayoutThresholdWidth => 80;

    private const string TreeName = "processesTree";

    // Below this width the Mem%/Enc%/Dec% columns are dropped: PID, name, memory and SM% are what
    // actually get scanned, and squeezing seven numeric columns into a narrow pane helps no one.
    private const int WideColumnsMinWidth = 100;

    // === State that must survive the once-a-second refresh ===
    // Selection is tracked by PID, NOT row index: rows reorder as memory use changes, so an index
    // would silently drift onto a different process — which, for a kill action, is dangerous.
    private int? _selectedPid;

    // Likewise expansion: which processes the user has opened, by PID.
    private readonly HashSet<int> _expandedPids = new();

    private TreeControl? _tree;

    // True while the cursor sits on one of an expanded process's detail rows rather than a process
    // row. Refreshes must leave the cursor alone in that case (see PopulateTree).
    private bool _onDetailRow;

    // Freshest snapshot, so an action resolves against what's on screen now rather than whatever was
    // current when the tree was built.
    private GpuSnapshot? _latestSnapshot;

    private int _lastWidth = 120;

    // Which GPU's processes to show, supplied by the dashboard so the list follows the Overview.
    private readonly Func<int> _selectedGpuIndex;
    private readonly Func<bool> _isMultiGpu;

    public ProcessesTab(
        ConsoleWindowSystem windowSystem,
        IGpuStatsProvider stats,
        Func<int>? selectedGpuIndex = null,
        Func<bool>? isMultiGpu = null)
        : base(windowSystem, stats)
    {
        _selectedGpuIndex = selectedGpuIndex ?? (() => 0);
        _isMultiGpu = isMultiGpu ?? (() => false);
    }

    protected override List<string> BuildTextContent(GpuSnapshot snapshot) => new();
    protected override void BuildGraphsContent(ScrollablePanelControl panel, GpuSnapshot snapshot) { }
    protected override void UpdateHistory(GpuSnapshot snapshot) { }

    public override IWindowControl BuildPanel(GpuSnapshot initialSnapshot, int windowWidth)
    {
        _lastWidth = windowWidth;

        var grid = new GridControl
        {
            Name = PanelControlName,
            VerticalAlignment = VerticalAlignment.Fill,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Margin(1, 0, 1, 1),
            BackgroundColor = UIConstants.BaseBg,
            ForegroundColor = UIConstants.PrimaryText
        };

        // Seed the GPU filter from the Overview's selection, so arriving here shows the card the user
        // was just looking at. From then on the toolbar owns the scope — it can express "All GPUs",
        // which the Overview's single-selection cannot.
        if (_gpuFilter == null && _isMultiGpu())
            _gpuFilter = _selectedGpuIndex();

        grid.ColumnDefinitions.Add(GridLength.Star(1.0));
        grid.RowDefinitions.Add(GridLength.Auto());    // toolbar
        grid.RowDefinitions.Add(GridLength.Auto());    // column header
        grid.RowDefinitions.Add(GridLength.Star(1.0)); // tree

        // Place(control, ROW, COL, rowSpan, colSpan).
        grid.Place(BuildToolbar(initialSnapshot), 0, 0, 1, 1);
        grid.Place(BuildHeader(), 1, 0, 1, 1);
        grid.Place(BuildTree(initialSnapshot), 2, 0, 1, 1);

        return grid;
    }

    #region Toolbar

    private const string GpuFilterName = "processesGpuFilter";
    private const string SortSelectorName = "processesSort";

    // Dropdown option order must match the enum's, since selection arrives as an index.
    private static readonly (string Label, ProcessSort Sort)[] SortOptions =
    {
        ("GPU mem", ProcessSort.Memory),
        ("SM %", ProcessSort.Sm),
        ("PID", ProcessSort.Pid),
        ("Name", ProcessSort.Name)
    };

    /// <summary>
    /// A toolbar above the table: which GPU to show, and how to order it. Built from the live snapshot
    /// so the GPU list names real devices ("GPU 1 · AMD …") rather than bare indices — with several
    /// vendors present, an index alone doesn't say which card it is.
    /// </summary>
    private IWindowControl BuildToolbar(GpuSnapshot snapshot)
    {
        var deviceInfos = Stats.ReadDeviceInfo();

        var gpuFilter = Controls.Dropdown()
            .WithName(GpuFilterName)
            .WithPrompt("GPU:")
            .WithWidth(28);

        // "All" first, so index 0 is always the unfiltered view regardless of GPU count.
        gpuFilter.AddItem("All GPUs");
        foreach (var gpu in snapshot.Gpus)
        {
            var name = deviceInfos.FirstOrDefault(d => d.Index == gpu.Index)?.Name;
            gpuFilter.AddItem(string.IsNullOrWhiteSpace(name)
                ? $"GPU {gpu.Index}"
                : $"GPU {gpu.Index} · {Truncate(name, 16)}");
        }

        gpuFilter
            .SelectedIndex(FilterToDropdownIndex(snapshot))
            .OnSelectionChanged((_, index) =>
            {
                // Index 0 is "All"; the rest map positionally onto the snapshot's GPUs.
                _gpuFilter = index <= 0 || index - 1 >= snapshot.Gpus.Count
                    ? null
                    : snapshot.Gpus[index - 1].Index;
                RefreshTree();
            });

        var sortSelector = Controls.Dropdown()
            .WithName(SortSelectorName)
            .WithPrompt("Sort:")
            .WithWidth(18)
            .AddItems(SortOptions.Select(o => o.Label).ToArray())
            .SelectedIndex(Array.FindIndex(SortOptions, o => o.Sort == _sort))
            .OnSelectionChanged((_, index) =>
            {
                if (index >= 0 && index < SortOptions.Length)
                    _sort = SortOptions[index].Sort;
                RefreshTree();
            });

        return Controls.Toolbar()
            .WithName("processesToolbar")
            .WithSpacing(2)
            // Below-line, not above: the rule should sit between the toolbar and the table it governs,
            // so the eye reads controls → rule → data.
            .WithBelowLine(true)
            .WithBelowLineColor(UIConstants.SeparatorColor)
            .Add(gpuFilter.Build())
            .Add(sortSelector.Build())
            .Build();
    }

    // Maps the current filter back to a dropdown index (0 = All, otherwise position + 1).
    private int FilterToDropdownIndex(GpuSnapshot snapshot)
    {
        if (_gpuFilter is not int index) return 0;
        var position = snapshot.Gpus.ToList().FindIndex(g => g.Index == index);
        return position < 0 ? 0 : position + 1;
    }

    // Re-populates the tree in place after a toolbar change, so the new filter/order shows at once
    // rather than on the next refresh tick.
    private void RefreshTree()
    {
        if (_tree == null) return;
        var snapshot = _latestSnapshot ?? Stats.ReadSnapshot();
        PopulateTree(_tree, snapshot);
    }

    #endregion

    #region Column layout

    // Column widths, in display cells. The collapsed row is composed as one fixed-width string, which
    // is what keeps the columns aligned inside a tree (the control itself has no column concept).
    private const int GpuColWidth = 5;
    private const int PidWidth = 6;
    private const int NameWidth = 28;
    private const int MemWidth = 9;
    private const int PctWidth = 6;

    // Width of the tree's own expand indicator + indent, which sits to the LEFT of our formatted row.
    // Every process node is given a child so it always renders "[+] "/"[-] " at a constant width —
    // a childless node renders no indicator at all and its row would sit 4 columns out of line.
    private const string HeaderPad = "     ";

    private bool WideColumns => _lastWidth >= WideColumnsMinWidth;

    private IWindowControl BuildHeader()
    {
        var muted = UIConstants.MutedText.ToMarkup();
        var head = $"{HeaderPad}{"GPU",-GpuColWidth}{"PID",PidWidth}  {"NAME",-NameWidth}{"GPU MEM",MemWidth}  {"SM%",PctWidth}";
        if (WideColumns)
            head += $"{"MEM%",PctWidth}{"ENC%",PctWidth}{"DEC%",PctWidth}";

        return Controls.Markup()
            .WithName("processesHeader")
            .AddLine($"[{muted} bold]{head}[/]")
            .WithAlignment(HorizontalAlignment.Left)
            .Build();
    }

    #endregion

    /// <summary>How the process list is ordered. Memory first — it is what operators scan for.</summary>
    private enum ProcessSort { Memory, Sm, Pid, Name }

    private ProcessSort _sort = ProcessSort.Memory;

    // Which GPU to show, as a global index; null means "all GPUs". Distinct from the Overview's
    // selection: the toolbar makes the scope explicit and adds an all-GPUs view the Overview cannot
    // express, so this tab owns its own filter once the toolbar exists.
    private int? _gpuFilter;

    // The processes shown: filtered to one GPU (or all), then sorted. Ties break on PID so the order
    // is stable between ticks — otherwise equal-memory rows would swap places and the cursor would
    // appear to drift.
    private List<GpuProcessSample> VisibleProcesses(GpuSnapshot snapshot)
    {
        IEnumerable<GpuProcessSample> procs = snapshot.Processes;

        if (_gpuFilter is int index)
            procs = procs.Where(p => p.GpuIndex == index);

        return _sort switch
        {
            ProcessSort.Sm => procs.OrderByDescending(p => p.SmPercent ?? -1).ThenBy(p => p.Pid).ToList(),
            ProcessSort.Pid => procs.OrderBy(p => p.Pid).ToList(),
            ProcessSort.Name => procs.OrderBy(p => ShortenPath(p.Name), StringComparer.OrdinalIgnoreCase)
                                     .ThenBy(p => p.Pid).ToList(),
            _ => procs.OrderByDescending(p => p.MemoryUsedMb).ThenBy(p => p.Pid).ToList()
        };
    }

    // Capabilities of the GPU whose processes are being shown, so the empty state can distinguish
    // "nothing running" from "this source cannot tell us".
    private GpuCapabilities? SelectedGpuCapabilities(GpuSnapshot snapshot)
    {
        if (snapshot.Gpus.Count == 0) return null;
        // With no filter ("All GPUs") there is no single owning backend, so fall back to the first
        // card's capabilities — the actions it gates are per-row anyway, and the empty-state wording
        // only needs to be right when a specific GPU is in view.
        if (_gpuFilter is not int index) return snapshot.Gpus[0].Caps;

        return (snapshot.Gpus.FirstOrDefault(g => g.Index == index) ?? snapshot.Gpus[0]).Caps;
    }

    private List<GpuProcessSample> CurrentProcesses() =>
        VisibleProcesses(_latestSnapshot ?? Stats.ReadSnapshot());

    // A percentage cell. A null value means pmon reported "-" (idle, unsupported, or unprivileged),
    // which is NOT the same claim as 0%, so it renders as a muted dash.
    private static string PercentCell(double? value, int width = PctWidth)
    {
        if (value is null)
            return $"[{UIConstants.MutedText.ToMarkup()}]{"-".PadLeft(width)}[/]";

        var text = $"{value.Value:F0}%";
        return $"[{UIConstants.ThresholdColor(value.Value).ToMarkup()}]{text.PadLeft(width)}[/]";
    }

    // The collapsed row: fixed-width columns so everything lines up under the header.
    private string RowText(GpuProcessSample proc)
    {
        var text = UIConstants.PrimaryText.ToMarkup();
        var muted = UIConstants.MutedText.ToMarkup();

        var row =
            // The GPU a process belongs to. Previously implicit — the tab showed one GPU's processes
            // with nothing on screen saying which — and essential once "All GPUs" is selectable.
            $"[{muted}]{proc.GpuIndex.ToString().PadRight(GpuColWidth)}[/]" +
            $"[{text}]{proc.Pid,PidWidth}[/]  " +
            $"[{text}]{Truncate(ShortenPath(proc.Name), NameWidth),-NameWidth}[/]" +
            $"[{muted}]{proc.MemoryUsedMb,MemWidth:F0}[/]  " +
            PercentCell(proc.SmPercent);

        if (WideColumns)
            row += PercentCell(proc.MemPercent) + PercentCell(proc.EncPercent) + PercentCell(proc.DecPercent);

        return row;
    }

    #region Tree building

    private IWindowControl BuildTree(GpuSnapshot snapshot)
    {
        var tree = Controls.Tree()
            .WithName(TreeName)
            .WithIndent("   ")
            .WithColors(UIConstants.PrimaryText, UIConstants.BaseBg)
            .WithHighlightColors(UIConstants.PrimaryText, UIConstants.TileSelectedBg)
            .WithAlignment(HorizontalAlignment.Stretch)
            .WithVerticalAlignment(VerticalAlignment.Fill)
            .Build();

        // Activating the detail's action row opens the signal dialog. Activating a process row is
        // left to the control, which toggles expansion — the discoverable default.
        tree.NodeActivated += (s, e) =>
        {
            if (e.Node?.Tag is not ActionTag action) return;
            var proc = CurrentProcesses().FirstOrDefault(p => p.Pid == action.Pid);
            if (proc != null) ShowSignalDialog(proc);
        };
        tree.SelectedNodeChanged += (s, e) =>
        {
            // Track the owning process so the signal dialog knows its target. _onDetailRow records
            // whether the cursor is on a CHILD: a refresh must not yank it back up to the parent row
            // (which would fight the user mid-navigation and mis-target Enter).
            switch (e.Node?.Tag)
            {
                case int pid:
                    _selectedPid = pid;
                    _onDetailRow = false;
                    break;
                case ActionTag action:
                    _selectedPid = action.Pid;
                    _onDetailRow = true;
                    break;
                default:
                    // A plain detail line (Command / GPU): still inside a process's subtree.
                    _onDetailRow = true;
                    break;
            }
        };
        // Remember what the user opened, so a refresh doesn't collapse it under them.
        tree.NodeExpandCollapse += (s, e) =>
        {
            if (e.Node?.Tag is not int pid) return;
            if (e.Node.IsExpanded) _expandedPids.Add(pid);
            else _expandedPids.Remove(pid);
        };

        _tree = tree;
        _latestSnapshot = snapshot;
        PopulateTree(tree, snapshot);
        return tree;
    }

    // Rebuilds the tree's NODES (not the control) from a snapshot, restoring selection and expansion
    // by PID. Replacing the control instead would drop keyboard focus and collapse everything.
    private void PopulateTree(TreeControl tree, GpuSnapshot snapshot)
    {
        var procs = VisibleProcesses(snapshot);
        var muted = UIConstants.MutedText.ToMarkup();

        // FAST PATH: when the same processes are present in the same order, update the existing
        // nodes' text in place. Rebuilding the tree every second would destroy whichever node the
        // cursor is on — which makes an expanded node's detail rows unreachable, since the cursor is
        // thrown away between keypresses.
        var currentPids = tree.RootNodes
            .Select(n => n.Tag is int pid ? pid : -1)
            .ToList();
        if (currentPids.Count == procs.Count &&
            currentPids.SequenceEqual(procs.Select(p => p.Pid)))
        {
            for (int i = 0; i < procs.Count; i++)
                UpdateNodeInPlace(tree.RootNodes[i], procs[i]);
            return;
        }

        // SLOW PATH: the process set changed, so the tree is rebuilt.
        tree.Clear();

        if (procs.Count == 0)
        {
            var scope = _gpuFilter is int gpuIndex ? $" on GPU {gpuIndex}" : "";

            // "None running" and "we cannot tell" are different claims, and only the capability
            // distinguishes them: reading AMD through the rocm-smi CLI yields no per-process data at
            // all (--showpids reports nothing), so reporting "no processes" there would be a
            // measurement we never made.
            var caps = SelectedGpuCapabilities(snapshot);
            bool canMeasure = caps?.PerProcessMemory ?? true;

            var headline = canMeasure
                ? $"[{muted}]No compute processes{scope}[/]"
                : $"[{muted}]Per-process data not available{scope}[/]";
            var detail = canMeasure
                ? "No processes are currently using this GPU."
                : "This GPU's data source cannot attribute usage to processes.";

            var empty = new TreeNode(headline);
            // Collapse the placeholder's detail child so the empty state stays one quiet line.
            empty.AddChild(new TreeNode($"[{muted}]{detail}[/]"));
            empty.IsExpanded = false;
            tree.AddRootNode(empty);
            return;
        }

        // Whether the owning backend can actually deliver a signal. False for the demo backend, whose
        // PIDs are synthetic and could otherwise collide with real processes.
        bool canSignal = SelectedGpuCapabilities(snapshot)?.ProcessSignal ?? true;

        foreach (var proc in procs)
        {
            var node = new TreeNode(RowText(proc)) { Tag = proc.Pid };

            // Every process gets a detail child: it carries the expanded information AND guarantees
            // the node renders an expand indicator, which is what keeps all rows column-aligned.
            AddDetailChildren(node, proc, canSignal);

            // TreeNode.IsExpanded defaults to TRUE, so collapse unless the user opened this PID.
            node.IsExpanded = _expandedPids.Contains(proc.Pid);

            tree.AddRootNode(node);
        }

        // Re-anchor the cursor on the remembered PID — but ONLY when it was on a process row. If the
        // user has navigated into an expanded node's detail, moving the cursor here would drag them
        // back to the parent on every tick, making the detail rows impossible to reach.
        if (_selectedPid.HasValue && !_onDetailRow)
        {
            var node = tree.FindNodeByTag(_selectedPid.Value);
            if (node != null) tree.SelectNode(node);
        }
    }

    // Refreshes a node's text (and its detail lines) without replacing the node, so selection,
    // expansion and focus all survive. Only mutates Text — never the node identity.
    private void UpdateNodeInPlace(TreeNode node, GpuProcessSample proc)
    {
        node.Text = RowText(proc);

        // Detail children are [Command, GPU/Memory/Enc-Dec, Actions]; only the middle line carries
        // live values. Guard on the count so a layout change here can't index out of range.
        if (node.Children.Count >= 2)
            node.Children[1].Text = DetailStatsLine(proc);
    }

    // The expanded detail: the full command path (which the truncated NAME column loses), the
    // process type, and the actions. Deliberately built from data already in the snapshot — no new
    // provider surface, nothing that can fail or need privileges.
    private void AddDetailChildren(TreeNode node, GpuProcessSample proc, bool canSignal)
    {
        var muted = UIConstants.MutedText.ToMarkup();
        var accent = UIConstants.Accent.ToMarkup();

        node.AddChild(new TreeNode($"[{muted}]Command[/]  [{accent}]{proc.Name}[/]"));
        node.AddChild(new TreeNode(DetailStatsLine(proc)));

        // The action row is a node whose activation opens the signal picker: TreeControl holds text,
        // not arbitrary controls, so the buttons live in a confirm dialog rather than inline.
        //
        // Backends that cannot signal (the demo backend, whose PIDs are synthetic) get a disabled
        // notice instead of a live action — so an impossible operation is never offered, rather than
        // being offered and then failing.
        var actions = canSignal
            ? new TreeNode(
                $"[{UIConstants.Warning.ToMarkup()}]▸ Signal this process…[/]  [{muted}](Enter)[/]")
            {
                // Tagged distinctly so activation can tell an action row from a process row.
                Tag = new ActionTag(proc.Pid)
            }
            : new TreeNode($"[{muted}]▸ Signalling not available for this GPU's data source[/]");

        node.AddChild(actions);
    }

    // The live-values line of the expanded detail. Shared by the initial build and the in-place
    // refresh so the two can't drift apart.
    private static string DetailStatsLine(GpuProcessSample proc)
    {
        var muted = UIConstants.MutedText.ToMarkup();
        var text = UIConstants.PrimaryText.ToMarkup();

        var encDec = proc.EncPercent is null && proc.DecPercent is null
            ? $"[{muted}]-[/]"
            : $"{PercentCell(proc.EncPercent, 1).Trim()} [{muted}]/[/] {PercentCell(proc.DecPercent, 1).Trim()}";

        return
            $"[{muted}]GPU[/]      [{text}]{proc.GpuIndex}[/]     " +
            $"[{muted}]Memory[/]  [{text}]{proc.MemoryUsedMb:F0} MB[/]     " +
            $"[{muted}]SM[/]  {PercentCell(proc.SmPercent, 1).Trim()}     " +
            $"[{muted}]Enc/Dec[/]  {encDec}";
    }

    /// <summary>Marks a tree node as the "signal this process" action row for a given PID.</summary>
    private sealed record ActionTag(int Pid);

    #endregion

    protected override void UpdateGraphControls(IWindowControl grid, GpuSnapshot snapshot)
    {
        if (grid is not GridControl gridControl)
            return;

        _latestSnapshot = snapshot;

        if (_tree != null)
        {
            PopulateTree(_tree, snapshot);
            return;
        }

        gridControl.ClearControls();
        gridControl.Place(BuildHeader(), 0, 0, 1, 1);
        gridControl.Place(BuildTree(snapshot), 1, 0, 1, 1);
    }

    public override void HandleResize(int newWidth, int newHeight)
    {
        bool wideBefore = WideColumns;
        _lastWidth = newWidth;

        // Column set changed: refresh the header and every row.
        if (wideBefore != WideColumns)
        {
            var window = FindMainWindow();
            var header = window?.FindControl<MarkupControl>("processesHeader");
            if (header != null)
            {
                var muted = UIConstants.MutedText.ToMarkup();
                var head = $"{HeaderPad}{"PID",PidWidth}  {"NAME",-NameWidth}{"GPU MEM",MemWidth}  {"SM%",PctWidth}";
                if (WideColumns)
                    head += $"{"MEM%",PctWidth}{"ENC%",PctWidth}{"DEC%",PctWidth}";
                header.SetContent(new List<string> { $"[{muted} bold]{head}[/]" });
            }

            if (_tree != null && _latestSnapshot != null)
                PopulateTree(_tree, _latestSnapshot);
        }

        base.HandleResize(newWidth, newHeight);
    }

    #region Text helpers

    // Process names arrive as full paths, which would crowd out every other column. Keep the
    // executable plus one parent directory — enough to tell two "python" processes apart. The full
    // path is always available in the expanded detail.
    private static string ShortenPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 1) return path;

        var name = parts[^1];
        var parent = parts.Length >= 2 ? parts[^2] : "";
        return parent.Length > 0 ? $"{parent}/{name}" : name;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..Math.Max(0, max - 1)] + "…";

    #endregion

    #region Process actions (signal / kill)


    /// <summary>
    /// Opens the signal picker for the process under the cursor. Invoked from the expanded detail's
    /// action row and by the tab's 'k' shortcut.
    /// </summary>
    public void ShowSignalDialogForSelection()
    {
        if (_selectedPid is not int pid) return;

        // The keyboard route must honour the capability too, or 'k' would bypass the disabled action
        // row and offer to signal a synthetic PID.
        var snapshot = _latestSnapshot ?? Stats.ReadSnapshot();
        if (SelectedGpuCapabilities(snapshot)?.ProcessSignal == false)
        {
            Notify("Signalling not available",
                "This GPU's data source cannot signal processes.",
                NotificationSeverity.Info);
            return;
        }

        var proc = CurrentProcesses().FirstOrDefault(p => p.Pid == pid);
        if (proc != null) ShowSignalDialog(proc);
    }

    private void ShowSignalDialog(GpuProcessSample proc)
    {
        var accent = UIConstants.Accent.ToMarkup();
        var muted = UIConstants.MutedText.ToMarkup();

        Window? modalRef = null;
        var modal = new WindowBuilder(WindowSystem)
            .WithTitle($"Signal process {proc.Pid}")
            .WithSize(70, 13)
            .Centered()
            .WithBorderStyle(SharpConsoleUI.BorderStyle.Rounded)
            .WithBorderColor(UIConstants.Accent)
            .Resizable(false)
            .Minimizable(false)
            .Maximizable(false)
            .Closable(true)
            .WithColors(UIConstants.PrimaryText, UIConstants.BaseBg)
            // Esc must dismiss, and must CONSUME the key: the main window binds Esc to Shutdown, so
            // leaving it unhandled would quit the app from a dialog the user was trying to back out of.
            .OnKeyPressed((s, e) =>
            {
                if (e.KeyInfo.Key != ConsoleKey.Escape) return;
                if (modalRef != null) WindowSystem.CloseWindow(modalRef);
                e.Handled = true;
            })
            .Build();
        modalRef = modal;

        modal.AddControl(Controls.Markup()
            .AddLine($"[{muted}]Command[/]")
            .AddLine($"  [{accent}]{proc.Name}[/]")
            .AddLine("")
            .AddLine($"[{muted}]GPU[/] [{UIConstants.PrimaryText.ToMarkup()}]{proc.GpuIndex}[/]   " +
                     $"[{muted}]Memory[/] [{UIConstants.PrimaryText.ToMarkup()}]{proc.MemoryUsedMb:F0} MB[/]")
            .WithAlignment(HorizontalAlignment.Left)
            .WithMargin(1, 1, 1, 0)
            .Build());

        modal.AddControl(Controls.RuleBuilder().WithColor(UIConstants.SeparatorColor).Build());

        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        // Windows has no SIGTERM; don't offer a "graceful" button that would silently do the same
        // thing as the forceful one.
        var gracefulButton = Controls.Button(isWindows ? "Terminate" : "SIGTERM")
            .WithWidth(14)
            .WithBorder(ButtonBorderStyle.Rounded)
            .WithBorderColor(UIConstants.Warning)
            .WithBackgroundColor(Color.Transparent)
            .WithBorderBackgroundColor(Color.Transparent)
            .OnClick((s, e) => { modal.Close(); TrySignal(proc, force: isWindows); })
            .Build();

        var killButton = Controls.Button("SIGKILL")
            .WithWidth(14)
            .WithBorder(ButtonBorderStyle.Rounded)
            .WithBorderColor(UIConstants.Critical)
            .WithBackgroundColor(Color.Transparent)
            .WithBorderBackgroundColor(Color.Transparent)
            .OnClick((s, e) => { modal.Close(); ConfirmKill(proc); })
            .Build();

        var closeButton = Controls.Button("Cancel")
            .WithWidth(12)
            .WithBorder(ButtonBorderStyle.Rounded)
            .WithBorderColor(UIConstants.SeparatorColor)
            .WithBackgroundColor(Color.Transparent)
            .WithBorderBackgroundColor(Color.Transparent)
            .OnClick((s, e) => modal.Close())
            .Build();

        var buttonRow = isWindows
            ? HorizontalGridControl.ButtonRow(gracefulButton, closeButton)
            : HorizontalGridControl.ButtonRow(gracefulButton, killButton, closeButton);
        buttonRow.Margin = new Margin(0, 1, 0, 0);
        modal.AddControl(buttonRow);

        // Cancel focused first, so a stray Enter dismisses rather than signals.
        closeButton.RequestFocus();

        WindowSystem.AddWindow(modal);
        WindowSystem.SetActiveWindow(modal);
    }

    // Second gate in front of SIGKILL: the process gets no chance to clean up, and on a GPU box the
    // victim is often a long-running training job.
    private void ConfirmKill(GpuProcessSample proc)
    {
        var muted = UIConstants.MutedText.ToMarkup();

        Window? modalRef = null;
        var modal = new WindowBuilder(WindowSystem)
            .WithTitle("Confirm SIGKILL")
            .WithSize(64, 11)
            .Centered()
            .WithBorderStyle(SharpConsoleUI.BorderStyle.Rounded)
            .WithBorderColor(UIConstants.Critical)
            .Resizable(false)
            .Minimizable(false)
            .Maximizable(false)
            .Closable(true)
            .WithColors(UIConstants.PrimaryText, UIConstants.BaseBg)
            // Esc cancels. On a destructive confirmation this matters most: backing out must always
            // be possible from the keyboard, and must not fall through to the app's Esc=quit binding.
            .OnKeyPressed((s, e) =>
            {
                if (e.KeyInfo.Key != ConsoleKey.Escape) return;
                if (modalRef != null) WindowSystem.CloseWindow(modalRef);
                e.Handled = true;
            })
            .Build();
        modalRef = modal;

        modal.AddControl(Controls.Markup()
            .AddLine($"[{UIConstants.Critical.ToMarkup()} bold]SIGKILL cannot be caught or ignored.[/]")
            .AddLine("")
            .AddLine($"[{muted}]Process[/] [{UIConstants.PrimaryText.ToMarkup()}]{proc.Pid}[/]  " +
                     $"[{UIConstants.Accent.ToMarkup()}]{ShortenPath(proc.Name)}[/]")
            .AddLine($"[{muted}]will be terminated immediately, losing unsaved work.[/]")
            .WithAlignment(HorizontalAlignment.Left)
            .WithMargin(1, 1, 1, 0)
            .Build());

        var confirmButton = Controls.Button("Kill it")
            .WithWidth(14)
            .WithBorder(ButtonBorderStyle.Rounded)
            .WithBorderColor(UIConstants.Critical)
            .WithBackgroundColor(Color.Transparent)
            .WithBorderBackgroundColor(Color.Transparent)
            .OnClick((s, e) => { modal.Close(); TrySignal(proc, force: true); })
            .Build();

        var cancelButton = Controls.Button("Cancel")
            .WithWidth(12)
            .WithBorder(ButtonBorderStyle.Rounded)
            .WithBorderColor(UIConstants.SeparatorColor)
            .WithBackgroundColor(Color.Transparent)
            .WithBorderBackgroundColor(Color.Transparent)
            .OnClick((s, e) => modal.Close())
            .Build();

        var row = HorizontalGridControl.ButtonRow(confirmButton, cancelButton);
        row.Margin = new Margin(0, 1, 0, 0);
        modal.AddControl(row);

        // Cancel focused by default: the destructive action is never one keystroke away.
        cancelButton.RequestFocus();

        WindowSystem.AddWindow(modal);
        WindowSystem.SetActiveWindow(modal);
    }

    // Sends the signal and reports what actually happened. Never assumes success: "permission
    // denied" (another user's process) and "already exited" are both normal here and are surfaced
    // distinctly rather than as a generic failure.
    private void TrySignal(GpuProcessSample proc, bool force)
    {
        var name = ShortenPath(proc.Name);
        var signal = force ? GpuSignal.Kill : GpuSignal.Terminate;

        // Routed to the backend that OWNS this GPU rather than performed here: which mechanism can
        // stop a process is a vendor/platform property, and going through the backend is what makes
        // the demo backend's refusal impossible to bypass from the UI.
        var backend = (Stats as GpuBackendRegistry)?.BackendForGpu(proc.GpuIndex);
        var result = backend?.SignalProcess(proc.Pid, signal) ?? GpuSignalResult.NotSupported;

        var verb = force ? "SIGKILL" : "SIGTERM";
        switch (result)
        {
            case GpuSignalResult.Ok:
                Notify($"✓ {verb} sent to {proc.Pid}",
                    force ? $"{name} was force terminated" : $"{name} was asked to exit",
                    NotificationSeverity.Info);
                break;

            case GpuSignalResult.NotSupported:
                Notify("Signalling not available",
                    $"{name}: this GPU's data source cannot signal processes.",
                    NotificationSeverity.Info);
                break;

            case GpuSignalResult.PermissionDenied:
                Notify($"⚠ {verb} failed for {proc.Pid}",
                    $"{name}: permission denied — it belongs to another user",
                    NotificationSeverity.Warning);
                break;

            case GpuSignalResult.NoSuchProcess:
                Notify($"Process {proc.Pid} no longer exists",
                    $"{name} has already exited",
                    NotificationSeverity.Info);
                break;

            default:
                Notify($"⚠ {verb} failed for {proc.Pid}",
                    $"{name}: the signal could not be delivered",
                    NotificationSeverity.Warning);
                break;
        }
    }

    private void Notify(string title, string message, NotificationSeverity severity) =>
        WindowSystem.NotificationStateService.ShowNotification(
            title, message, severity,
            blockUi: false, timeout: 4000, parentWindow: FindMainWindow());

    #endregion
}
