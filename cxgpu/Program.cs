using cxgpu.Configuration;
using cxgpu.Dashboard;
using cxgpu.Gpu;
using SharpConsoleUI;
using SharpConsoleUI.Configuration;
using SharpConsoleUI.Drivers;
using SharpConsoleUI.Helpers;

namespace cxgpu;

internal class Program
{
    static async Task<int> Main(string[] args)
    {
        if (args.Any(a => a is "--help" or "-h" or "--version" or "-v"))
        {
            PrintUsage(args.Any(a => a is "--version" or "-v"));
            return 0;
        }

        try
        {
            var config = CxgpuConfig.Load();
            var stats = GpuStatsFactory.Create(args, config);

            var windowSystem = new ConsoleWindowSystem(
                new NetConsoleDriver(RenderMode.Buffer),
                options: new ConsoleWindowSystemOptions(
                    ShowTopPanel: false,
                    ShowBottomPanel: false,
                    InstallSynchronizationContext: true));

            windowSystem.PanelStateService.TopStatus =
                $"cxgpu - GPU Monitor ({GpuStatsFactory.GetPlatformName(args)})";

            Console.CancelKeyPress += (sender, e) =>
            {
                windowSystem.LogService.LogInfo("Ctrl+C received, shutting down...");
                e.Cancel = true;
                windowSystem.Shutdown(0);
            };

            var dashboard = new DashboardWindow(windowSystem, stats, config);
            dashboard.Create();

            windowSystem.LogService.LogInfo("Starting cxgpu");
            await Task.Run(() => windowSystem.Run());
            windowSystem.LogService.LogInfo("cxgpu stopped");

            // Printed AFTER Run() returns, so the TUI has released the terminal and this lands in the
            // user's scrollback rather than being wiped by the alternate screen buffer.
            if (config.Alerts.SessionSummaryOnExit)
                dashboard.PrintSessionSummary();

            return 0;
        }
        catch (Exception ex)
        {
            Console.Clear();
            ExceptionFormatter.WriteException(ex);
            return 1;
        }
    }

    private static string AppVersion =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "dev";

    private static void PrintUsage(bool versionOnly)
    {
        if (versionOnly)
        {
            Console.WriteLine($"cxgpu {AppVersion}");
            return;
        }

        Console.WriteLine($"""
            cxgpu {AppVersion} - GPU monitor for the terminal (NVIDIA + AMD)

            Usage:
              cxgpu [options]

            Options:
              --demo[=N]      Run against N simulated GPUs (default {DemoBackend.DefaultDemoGpuCount},
                              max {DemoBackend.MaxDemoGpuCount}) instead of real hardware. Useful for
                              exercising the multi-GPU view on a single-GPU machine, or
                              with no NVIDIA driver at all. Also settable via
                              CXGPU_FAKE_GPUS=N.
              -h, --help      Show this help and exit.
              -v, --version   Show the version and exit.

            Keys:
              F2 / F3         Overview / Processes tab
              [ / ]           Previous / next GPU        (multi-GPU only)
              1-9             Select GPU directly        (multi-GPU only)
              Right / Enter   Expand a process row       (Processes tab)
              k               Signal selected process    (Processes tab)
              ? / F1          Keyboard shortcuts
              F9              Settings
              F10 / Esc       Quit

            Config: ~/.config/cxgpu/config.json  (delete to reset to defaults)
            """);
    }
}
