using cxnvmon.Configuration;
using cxnvmon.Dashboard;
using cxnvmon.Stats;
using SharpConsoleUI;
using SharpConsoleUI.Configuration;
using SharpConsoleUI.Drivers;
using SharpConsoleUI.Helpers;

namespace cxnvmon;

internal class Program
{
    static async Task<int> Main(string[] args)
    {
        try
        {
            var config = CxnvmonConfig.Load();
            var stats = GpuStatsFactory.Create();

            var windowSystem = new ConsoleWindowSystem(
                new NetConsoleDriver(RenderMode.Buffer),
                options: new ConsoleWindowSystemOptions(
                    ShowTopPanel: false,
                    ShowBottomPanel: false,
                    InstallSynchronizationContext: true));

            windowSystem.PanelStateService.TopStatus =
                $"cxnvmon - GPU Monitor ({GpuStatsFactory.GetPlatformName()})";

            Console.CancelKeyPress += (sender, e) =>
            {
                windowSystem.LogService.LogInfo("Ctrl+C received, shutting down...");
                e.Cancel = true;
                windowSystem.Shutdown(0);
            };

            var dashboard = new DashboardWindow(windowSystem, stats, config);
            dashboard.Create();

            windowSystem.LogService.LogInfo("Starting cxnvmon");
            await Task.Run(() => windowSystem.Run());
            windowSystem.LogService.LogInfo("cxnvmon stopped");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Clear();
            ExceptionFormatter.WriteException(ex);
            return 1;
        }
    }
}
