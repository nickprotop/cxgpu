using cxgpu.Configuration;
using cxgpu.Dashboard;
using cxgpu.Export;
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

        ExportOptions export;
        try
        {
            export = ExportOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            // Argument errors are reported before anything starts, and plainly — a monitoring endpoint
            // that failed to come up must never look like it succeeded.
            Console.Error.WriteLine($"cxgpu: {ex.Message}");
            return 2;
        }

        try
        {
            var config = CxgpuConfig.Load();
            var stats = GpuStatsFactory.Create(args, config);

            if (export.NoUi)
                return await RunHeadlessAsync(stats, export);

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

            // Serving alongside the UI: the exporter reads the same provider, so a scrape and the
            // screen can never disagree. Started BEFORE the UI so a port collision fails immediately
            // rather than after the terminal has been taken over.
            using var exporter = StartExporter(stats, export);

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

    /// <summary>
    /// Starts the exporter, or returns null when it was not requested. Failures are fatal by design —
    /// a scraper pointed at a port with nothing behind it is worse than a startup error.
    /// </summary>
    private static PrometheusExporter? StartExporter(IGpuStatsProvider stats, ExportOptions export)
    {
        if (!export.Prometheus) return null;

        var exporter = new PrometheusExporter(stats, export.Port, export.Host);
        exporter.Start();
        return exporter;
    }

    /// <summary>
    /// The headless path: an exporter and nothing else.
    ///
    /// No ConsoleWindowSystem, no alternate screen, no input handling — this is meant to run under
    /// systemd or backgrounded with &amp;, so it logs plainly to stdout and exits cleanly on SIGTERM
    /// or Ctrl+C rather than needing to be killed.
    /// </summary>
    private static async Task<int> RunHeadlessAsync(IGpuStatsProvider stats, ExportOptions export)
    {
        PrometheusExporter exporter;
        try
        {
            exporter = new PrometheusExporter(stats, export.Port, export.Host);
            exporter.Start();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"cxgpu: {ex.Message}");
            return 1;
        }

        using (exporter)
        {
            var gpuCount = stats.ReadSnapshot().Gpus.Count;
            Console.WriteLine($"cxgpu {AppVersion} exporter on http://{export.DisplayHost}:{export.Port}/metrics");

            // A public bind is stated explicitly rather than left implied by a flag typed once: this
            // exposes hostnames, GPU models and process counts to anything that can reach the port.
            if (export.IsPublic)
                Console.WriteLine($"Listening on {export.DisplayHost} — reachable from the network.");

            Console.WriteLine($"Serving {gpuCount} GPU(s). Ctrl+C to stop.");

            // Both signals resolve the same wait, so `systemctl stop` and Ctrl+C behave identically.
            var stop = new TaskCompletionSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.TrySetResult(); };
            AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.TrySetResult();

            await stop.Task;
            Console.WriteLine("cxgpu exporter stopped.");
        }

        return 0;
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
              --prometheus    Serve Prometheus metrics at /metrics. Fails if the port
                              is taken rather than quietly picking another.
              --port PORT     Port for the exporter (default {PrometheusExporter.DefaultPort}).
              --bind ADDRESS  Interface to bind (default {ExportOptions.DefaultHost}). Use 0.0.0.0
                              to expose the exporter on the network.
              --no-ui         Run the exporter without the TUI. Requires --prometheus.

            Export examples:
              cxgpu --prometheus                     UI plus metrics on localhost
              cxgpu --prometheus --no-ui             headless exporter
              cxgpu --prometheus --no-ui &           the same, backgrounded
              cxgpu --prometheus --port 9100 --bind 0.0.0.0
                                                     served on all interfaces
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
