using cxnvmon.Stats;
using SharpConsoleUI.Controls;

namespace cxnvmon.Tabs;

internal interface ITab
{
    string Name { get; }
    string PanelControlName { get; }
    IWindowControl BuildPanel(GpuSnapshot initialSnapshot, int windowWidth);
    void UpdatePanel(GpuSnapshot snapshot);
    void HandleResize(int newWidth, int newHeight);
}
