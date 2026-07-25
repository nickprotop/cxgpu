using cxgpu.Gpu;
using cxgpu.Helpers;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;

namespace cxgpu.Widgets;

/// <summary>
/// The DASH tile's view: one <see cref="GpuHeroPanel"/> per GPU, wrapped into rows.
///
/// Separate from the strip because it answers a different question — the strip is "which GPU?", this
/// is "how is the fleet doing?" — and separate from the tab because it owns the per-GPU utilization
/// history that feeds the panel sparklines.
/// </summary>
internal sealed class GpuFleetGrid
{
    // Per-GPU utilization history for the hero sparklines. Shorter than the detail view's history:
    // the panels are narrow, so a longer window would only be discarded at render time.
    private readonly KeyedHistoryTracker<int> _histories = new(120);

    private readonly Action<int> _focusGpu;

    /// <param name="focusGpu">Called on double-click to make that GPU the current one.</param>
    public GpuFleetGrid(Action<int> focusGpu)
    {
        _focusGpu = focusGpu;
    }

    /// <summary>Records a frame of history for every GPU. Call once per refresh.</summary>
    public void RecordHistory(GpuSnapshot snapshot)
    {
        foreach (var gpu in snapshot.Gpus)
            _histories.Add(gpu.Index, gpu.UtilizationPercent);
    }

    /// <summary>History for one GPU, for callers building a panel outside the grid.</summary>
    public IReadOnlyList<double> HistoryFor(int gpuIndex) => _histories.Get(gpuIndex);

    /// <summary>
    /// Wrapping is done by GROUPING the GPUs and emitting one horizontal grid per row — the grid
    /// itself has no wrapping, and this is how ServerHub's dashboard does the same job.
    /// </summary>
    public void Build(ScrollablePanelControl panel, GpuSnapshot snapshot,
                      IReadOnlyList<GpuDeviceInfo> deviceInfos, int availableWidth)
    {
        int perRow = Math.Max(1, availableWidth / GpuHeroPanel.Width);

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
                    _histories.Get(gpu.Index), selected: false);

                WirePanel(heroPanel, gpu.Index);

                row.Column(col =>
                {
                    col.Width(GpuHeroPanel.Width);
                    col.Add(heroPanel);
                });
            }

            panel.AddControl(row.Build());
        }
    }

    public static int ProcessCountFor(GpuSnapshot snapshot, int gpuIndex) =>
        snapshot.Processes.Count(p => p.GpuIndex == gpuIndex);

    /// <summary>
    /// Double-click a panel to open that GPU's detail — which is just selecting its chip, so the strip
    /// and the view can never disagree about which GPU is current.
    ///
    /// No single-click handler by design: selection belongs to the strip's chips, and a panel that
    /// highlighted independently would be a second, competing notion of "current" for the user to
    /// reconcile.
    /// </summary>
    private void WirePanel(PanelControl panel, int gpuIndex)
    {
        panel.MouseDoubleClick += (_, e) =>
        {
            _focusGpu(gpuIndex);
            e.Handled = true;
        };
    }
}
