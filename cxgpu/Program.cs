using cxgpu.Configuration;
using cxgpu.Dashboard;
using cxgpu.Export;
using cxgpu.Gpu;
using SharpConsoleUI;
using SharpConsoleUI.Configuration;
using SharpConsoleUI.Drivers;
using SharpConsoleUI.Helpers;
using SharpConsoleUI.Logging;

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

            if (export.GpuUsage)
                return await RunGpuUsageAsync(stats, export);

            var windowSystem = new ConsoleWindowSystem(
                new NetConsoleDriver(RenderMode.Buffer),
                options: new ConsoleWindowSystemOptions(
                    ShowTopPanel: false,
                    ShowBottomPanel: false,
                    InstallSynchronizationContext: true));

            // Applies to the TUI log panel only. Headless mode keeps stdout for data and stderr for
            // errors, so there is no panel for a verbosity to apply to.
            windowSystem.LogService.MinimumLevel = export.LogLevel;

            // A file sink survives the alternate screen buffer being torn down, which is the only way
            // to read a startup failure or a crash after the terminal has been restored.
            if (export.LogFilePath is string logFile)
                windowSystem.LogService.EnableFileLogging(logFile);

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

    /// <summary>
    /// Fire-and-forget GPU usage snapshot.
    ///
    /// Reads one snapshot, prints it in the requested format, and exits. No TUI, no background
    /// tasks, no signal handling — just one read and one write.
    /// </summary>
    private static async Task<int> RunGpuUsageAsync(IGpuStatsProvider stats, ExportOptions export)
    {
        GpuSnapshot snapshot;
        IReadOnlyList<GpuDeviceInfo> deviceInfos;
        try
        {
            snapshot = stats.ReadSnapshot();
            deviceInfos = stats.ReadDeviceInfo();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"cxgpu: {ex.Message}");
            return 1;
        }

        if (snapshot.Gpus.Count == 0)
        {
            Console.Error.WriteLine(
                "cxgpu: No GPUs detected. " +
                "Make sure the NVIDIA driver or AMD ROCm is installed, or use --demo=N for testing.");
            return 1;
        }

        // Resolve color setting: null means auto-detect (TTY + NO_COLOR).
        // NO_COLOR takes precedence — if set, colors are always off.
        var noColor = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));
        var view = export.View;
        var useColor = !noColor && view.UseColor(Console.IsOutputRedirected);

        if (view.WatchInterval is { } interval)
            return await RunGpuUsageWatchAsync(stats, export, interval);

        var sw = new System.IO.StringWriter();
        if (view.IsJson)
            sw.Write(UsageFormatter.RenderJson(snapshot, deviceInfos));
        else
            UsageFormatter.RenderTable(snapshot, deviceInfos, sw, view, useColor);

        Console.Write(sw.ToString());
        return 0;
    }

    /// <summary>
    /// Continuously render GPU usage, refreshing in place until the user presses q.
    ///
    /// Uses ANSI cursor movement to overwrite the table on-screen rather than printing
    /// newline-after-newline. Hides the cursor on entry, restores it on exit.
    /// </summary>
    private static async Task<int> RunGpuUsageWatchAsync(
        IGpuStatsProvider stats, ExportOptions export, int intervalSec)
    {
        IReadOnlyList<GpuDeviceInfo> deviceInfos;
        try
        {
            deviceInfos = stats.ReadDeviceInfo();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"cxgpu: {ex.Message}");
            return 1;
        }

        try
        {
            var initial = stats.ReadSnapshot();
            if (initial.Gpus.Count == 0)
            {
                Console.Error.WriteLine(
                    "cxgpu: No GPUs detected. " +
                    "Make sure the NVIDIA driver or AMD ROCm is installed, or use --demo=N for testing.");
                return 1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"cxgpu: {ex.Message}");
            return 1;
        }

        var sw = new System.IO.StringWriter();
        var cts = new CancellationTokenSource();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        Console.WriteLine($"Watching GPU usage (interval: {intervalSec}s). Press q to stop. Ctrl+C to quit.");

        while (!cts.IsCancellationRequested)
        {
            GpuSnapshot snapshot;
            try
            {
                snapshot = stats.ReadSnapshot();
            }
            catch
            {
                await Task.Delay(1000, cts.Token);
                continue;
            }

            sw.GetStringBuilder().Clear();
            UsageFormatter.RenderTable(
                snapshot, deviceInfos, sw,
                export.View,
                export.View.UseColor(Console.IsOutputRedirected),
                watching: true);
            var rendered = sw.ToString();
            var lines = rendered.Split('\n');

            if (Console.IsOutputRedirected)
            {
                Console.WriteLine(rendered);
                await Task.Delay(TimeSpan.FromSeconds(intervalSec), cts.Token);
                continue;
            }

            try
            {
                Console.Write("\x1b[?25l"); // hide cursor
            }
            catch { /* best-effort */ }

            try
            {
                Console.Write("\x1b[H");
                Console.Write("\x1b[J");

                foreach (var line in lines)
                {
                    Console.Write(line + "\n");
                }

                Console.Write("\x1b[J");
            }
            catch { /* fall through to print mode */ }

            var delayMs = intervalSec * 1000;
            var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(delayMs);
            while (!cts.IsCancellationRequested)
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(true);
                    if (key.Key == ConsoleKey.Q)
                    {
                        Console.WriteLine();
                        Console.WriteLine("Stopping watch.");
                        cts.Cancel();
                        break;
                    }
                }
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) break;
                await Task.Delay(Math.Max(100, (int)remaining.TotalMilliseconds), cts.Token);
            }

            if (cts.IsCancellationRequested) break;
        }

        try { Console.Write("\x1b[?25h"); } catch { }

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
              --gpu-usage     Print current GPU usage and exit.
                              Shows Util, Mem, Temp, Power, Clock (SM/Mem),
                              Enc/Dec, Throttle status, and Fan.
                              With optional --format json for machine-readable output.
                              Use --watch to live-update the table (press q to stop).
                              Use --color / --no-color to override terminal color detection.
                              Use --append-processes to also show per-process GPU usage.
              --top N           Show only the top N processes by memory use.
                              Requires --append-processes. N must be > 0.
              --sort CRITERION  Sort processes by: memory (default), sm, pid, name.
                              Requires --append-processes.
              --log-level LEVEL   Log panel verbosity, TUI mode only: error, warn
                              (default), info, debug. Headless mode is unaffected.
              --log-file PATH Write logs to a file as well as the TUI log panel.
                              Outlives the TUI, so it survives a crash.

            Export examples:
              cxgpu --prometheus                     UI plus metrics on localhost
              cxgpu --prometheus --no-ui             headless exporter
              cxgpu --prometheus --no-ui &           the same, backgrounded
              cxgpu --gpu-usage                      one-shot GPU usage table
              cxgpu --gpu-usage --format json        one-shot GPU usage as JSON
              cxgpu --gpu-usage --watch              live-updating GPU usage
              cxgpu --gpu-usage --watch 5            refresh every 5 seconds
              cxgpu --gpu-usage --color              force colored output (even when piped)
              cxgpu --gpu-usage --append-processes    one-shot GPU usage with processes
              cxgpu --prometheus --port 9100 --bind 0.0.0.0
              served on all interfaces
              cxgpu --log-level debug                verbose diagnostics in the panel
              cxgpu --log-file /tmp/cxgpu.log        log to a file and the panel
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
