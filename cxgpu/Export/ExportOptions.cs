using System.Net;

using SharpConsoleUI.Logging;

namespace cxgpu.Export;

/// <summary>
/// The export-related command-line options, parsed once so Program does not carry the string handling.
/// </summary>
/// <param name="Prometheus">Whether to serve metrics.</param>
/// <param name="Port">Port to serve on.</param>
/// <param name="Host">Interface to bind. Loopback unless explicitly widened.</param>
/// <param name="NoUi">Run headless — exporter only, no TUI.</param>
/// <param name="GpuUsage">Print a GPU usage snapshot and exit.</param>
/// <param name="View">
/// How that snapshot is rendered — see <see cref="UsageView"/>. Bundled rather than spread across
/// this record because the two halves answer different questions: the four fields above decide
/// whether to serve metrics, these decide what a table looks like. It reached eleven positional
/// parameters before anyone counted.
/// </param>
internal sealed record ExportOptions(
    bool Prometheus, int Port, string Host, bool NoUi,
    bool GpuUsage, UsageView View,
    LogLevel LogLevel, string? LogFilePath)
{
    public static ExportOptions None => new(
        false, PrometheusExporter.DefaultPort, DefaultHost, false, false, UsageView.Default,
        DefaultLogLevel, null);

    /// <summary>
    /// Warnings and errors only. The log panel shares the screen with the dashboard, so anything
    /// chattier has to be asked for.
    /// </summary>
    public const LogLevel DefaultLogLevel = LogLevel.Warning;

    public const string DefaultFormat = "";

    /// <summary>Kept as an alias so callers that already say <c>ExportOptions.JsonFormat</c> still
    /// read naturally; the value itself belongs to the view.</summary>
    public const string JsonFormat = UsageView.JsonFormat;

    /// <summary>
    /// Loopback by default. A monitor that silently listened on every interface of someone's laptop
    /// would be an unpleasant surprise, so widening it has to be deliberate.
    /// </summary>
    public const string DefaultHost = "localhost";

    /// <summary>
    /// Whether this binding is reachable from outside the machine — used to warn on startup, since
    /// exposing GPU telemetry to the network deserves to be visible in the log rather than implied by
    /// a flag the user typed once.
    /// </summary>
    public bool IsPublic =>
        Host is not ("localhost" or "127.0.0.1" or "::1");

    /// <summary>The address as written for a human: "+" is the listener's spelling, not the user's.</summary>
    public string DisplayHost => Host == "+" ? "0.0.0.0" : Host;

    /// <summary>
    /// Parses the export flags. Both forms are accepted for value-taking options — <c>--port 9835</c>
    /// and <c>--port=9835</c> — because a tool that takes only one of them is a papercut every time
    /// you misremember which.
    ///
    /// <code>
    ///   --gpu-usage     Print GPU usage snapshot and exit.
    ///   --format FMT    Output format for --gpu-usage (json).
    ///   --prometheus    Serve Prometheus metrics at /metrics. Fails if the port
    ///                   is taken rather than quietly picking another.
    ///   --port PORT     Port for the exporter (default {PrometheusExporter.DefaultPort}).
    ///   --bind ADDRESS  Interface to bind (default {ExportOptions.DefaultHost}). Use 0.0.0.0
    ///                   to expose the exporter on the network.
    ///   --no-ui         Run the exporter without the TUI. Requires --prometheus.
    ///   --color         Enable colored terminal output for --gpu-usage (auto-detected by default).
    ///   --no-color      Disable colored terminal output for --gpu-usage.
    ///   --append-processes  Also render a process table below the GPU grid.
    ///   --watch [SECS]      Continuously refresh --gpu-usage (default 2s, max 3600s).
    ///                        Press q to stop. Only works with table format.
    /// </code>
    /// </summary>
    /// <exception cref="ArgumentException">
    /// When the arguments cannot be satisfied: an unparseable port or address, a value-taking flag
    /// with no value, or an option that only makes sense alongside one that was not given. Every case
    /// is refused rather than silently defaulted — a monitoring endpoint that came up somewhere other
    /// than where it was asked to is worse than one that failed to start.
    /// </exception>
    public static ExportOptions Parse(string[] args)
    {
        bool prometheus = false;
        bool noUi = false;
        bool gpuUsage = false;
        int? port = null;
        string? host = null;
        string? format = null;
        bool? color = null;
         bool appendProcesses = false;
        int? watchInterval = null;
        int? top = null;
        string? sort = null;
        LogLevel? logLevel = null;
        string? logFilePath = null;

         for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            switch (Split(arg, args, ref i))
            {
                case ("--prometheus", null):
                    prometheus = true;
                    break;

                // --prometheus takes no value. Accepting one silently (as a port, say) would mean two
                // spellings for the same setting, so it is reported instead.
                case ("--prometheus", _):
                    throw new ArgumentException(
                        "--prometheus takes no value; use --port PORT to choose a port.");

                case ("--port", var value):
                    port = ParsePort(Required(value, "--port", "a port number (1-65535)"));
                    break;

                case ("--bind", var value):
                    host = ParseHost(Required(value, "--bind", "an address (e.g. 0.0.0.0)"));
                    break;

                case ("--no-ui", _):
                    noUi = true;
                    break;

                case ("--gpu-usage", _):
                    gpuUsage = true;
                    break;

                case ("--format", null):
                    // Split already consumed the next arg if present;
                    // null means --format with no value or --format=
                    throw new ArgumentException("--format requires a value (e.g. json).");

                case ("--format", var value):
                    format = Required(value, "--format", "an output format (json).");
                    break;

                case ("--color", null):
                    color = true;
                    break;

                case ("--no-color", null):
                    color = false;
                    break;

                case ("--append-processes", null):
                    appendProcesses = true;
                    break;

                case ("--watch", null):
                    watchInterval = 2; // default 2 seconds when bare --watch
                    break;

                case ("--watch", var value):
                    watchInterval = ParseWatchInterval(
                        Required(value, "--watch", "an interval in seconds (e.g. 2)"));
                    break;

                case ("--top", null):
                // Split already consumed the next arg if present;
                // null means --top with no value or --top=
                throw new ArgumentException("--top requires a value (e.g. 10).");

                case ("--top", var value):
                top = ParseTop(Required(value, "--top", "a positive integer (e.g. 10)"));
                break;

                case ("--sort", null):
                // Split already consumed the next arg if present;
                // null means --sort with no value or --sort=
                throw new ArgumentException("--sort requires a value (e.g. memory).");

                case ("--sort", var value):
                sort = ValidateSort(Required(value, "--sort", "a sort criterion (memory, sm, pid, name)"));
                break;

                case ("--log-level", var value):
                    logLevel = ParseLogLevel(
                        Required(value, "--log-level", "error, warn, info, or debug"));
                    break;

                case ("--log-file", var value):
                    logFilePath = Required(value, "--log-file", "a file path");
                    break;

                // An unrecognised --export-looking flag is REFUSED, not ignored. Falling through
                // meant "--prometheus-host=0.0.0.0" started on localhost and said nothing — the
                // endpoint came up somewhere other than where it was asked to, which is the exact
                // silent failure this parser exists to prevent. Only flags in this namespace are
                // checked; --demo and friends belong to other parsers.
                case (var name, _) when IsExportFlag(name):
                    throw new ArgumentException(
                        $"Unknown option '{name}'. Did you mean --prometheus, --port, --bind or --no-ui?");
            }
        }

        // --port and --bind configure an endpoint that would not exist. Refused rather than ignored:
        // silently discarding "--port 9100" means the scraper points at the wrong place.
        if (!prometheus && port.HasValue)
            throw new ArgumentException("--port requires --prometheus.");
        if (!prometheus && host != null)
            throw new ArgumentException("--bind requires --prometheus.");

        // --no-ui alone would start a process with no interface and no endpoint: nothing to observe,
        // and no way to stop it but a signal.
        if (noUi && !prometheus)
            throw new ArgumentException("--no-ui requires --prometheus (there would be nothing to serve).");

        if (gpuUsage && prometheus)
            throw new ArgumentException("--gpu-usage and --prometheus cannot be used together.");
        if (gpuUsage && noUi)
            throw new ArgumentException("--gpu-usage and --no-ui cannot be used together.");

        if (gpuUsage && !string.IsNullOrEmpty(format) && format != JsonFormat)
            throw new ArgumentException($"--format '{format}' is not supported. Use 'json'.");

        if (watchInterval.HasValue && !gpuUsage)
            throw new ArgumentException("--watch requires --gpu-usage.");
        if (watchInterval.HasValue && !string.IsNullOrEmpty(format))
            throw new ArgumentException("--watch is only supported with the default table format.");

        // BOTH SHAPE A TABLE THAT IS NOT BEING PRINTED. Refused rather than ignored, for the same
        // reason --port is: a user who typed "--top 5" and got every row would conclude the flag is
        // broken, and silently dropping an argument is how that belief starts.
        if (top.HasValue && !appendProcesses)
            throw new ArgumentException("--top requires --append-processes.");
        if (sort != null && !appendProcesses)
            throw new ArgumentException("--sort requires --append-processes.");

        return new ExportOptions(
            prometheus,
            port ?? PrometheusExporter.DefaultPort,
            host ?? DefaultHost,
            noUi,
            gpuUsage,
            new UsageView(
                Format: format ?? DefaultFormat,
                Color: color,
                AppendProcesses: appendProcesses,
                WatchInterval: watchInterval,
                Top: top,
                Sort: sort ?? UsageView.DefaultSort),
            logLevel ?? DefaultLogLevel,
            logFilePath);
    }

    /// <summary>
    /// Splits "--name=value" into its parts, or consumes the next argument as the value for a
    /// value-taking flag written as "--name value". Returns a null value for a bare flag.
    /// </summary>
    private static (string Name, string? Value) Split(string arg, string[] args, ref int i)
    {
        int eq = arg.IndexOf('=');
        if (eq > 0)
        {
            // "--watch=" is the same as a bare "--watch": an empty value is no value, not the empty
            // string, so the flag's own default applies rather than a parse failure.
            var value = arg[(eq + 1)..];
            return (arg[..eq], value.Length == 0 ? null : value);
        }

        // Only flags that TAKE a value consume the next argument, and only when it does not itself
        // look like a flag — otherwise "cxgpu --port --no-ui" would swallow --no-ui as the port.
        if (arg is "--port" or "--bind" or "--format" or "--watch" or "--top" or "--sort"
            or "--log-level" or "--log-file")
        {
            if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                return (arg, args[++i]);

            // Reported by Required() below, which knows what the flag wanted.
            return (arg, null);
        }

        return (arg, null);
    }

    /// <summary>
    /// Whether a flag belongs to this parser's namespace, so a misspelling of one of OUR options is
    /// caught while other parsers' flags (--demo, --help) pass through untouched.
    /// </summary>
    private static bool IsExportFlag(string name) =>
        name.StartsWith("--prometheus", StringComparison.Ordinal) ||
        name.StartsWith("--gpu-usage", StringComparison.Ordinal) ||
        name.StartsWith("--watch", StringComparison.Ordinal) ||
        name.StartsWith("--log-level", StringComparison.Ordinal) ||
        name.StartsWith("--log-file", StringComparison.Ordinal) ||
        name is "--format" or "--port" or "--bind" or "--metrics" or "--exporter" or "--listen" or "--host" or "--usage" or "--color" or "--no-color" or "--append-processes" or "--top" or "--sort";

    private static string Required(string? value, string flag, string what) =>
        value ?? throw new ArgumentException($"{flag} requires {what}.");

    private static int ParsePort(string raw)
    {
        if (!int.TryParse(raw, out var port) || port is < 1 or > 65535)
            throw new ArgumentException($"Invalid port: '{raw}' (expected 1-65535).");

        return port;
    }

    private static int ParseWatchInterval(string raw)
    {
        if (!int.TryParse(raw, out var v) || v < 1 || v > 3600)
            throw new ArgumentException(
                $"Invalid watch interval: '{raw}' (expected 1-3600 seconds).");
        return v;
    }

    private static int ParseTop(string raw)
    {
    if (!int.TryParse(raw, out var top) || top < 1)
    throw new ArgumentException($"Invalid --top value: '{raw}' (expected a positive integer > 0).");
    return top;
    }

    private static string ValidateSort(string raw)
    {
    var sort = raw.ToLowerInvariant();
    if (sort is not ("memory" or "sm" or "pid" or "name"))
    throw new ArgumentException($"Invalid sort criterion: '{raw}' (expected memory, sm, pid, or name).");
    return sort;
    }

    /// <summary>
    /// Maps the flag's spelling onto <see cref="LogLevel"/>. "warn" is accepted alongside the
    /// enum's own "warning" because that is how the help text spells it and how most tools do.
    /// </summary>
    private static LogLevel ParseLogLevel(string raw) => raw.ToLowerInvariant() switch
    {
        "error" => LogLevel.Error,
        "warn" or "warning" => LogLevel.Warning,
        "info" => LogLevel.Information,
        "debug" => LogLevel.Debug,
        _ => throw new ArgumentException(
            $"Invalid --log-level '{raw}'. Expected error, warn, info, or debug."),
    };

    /// <summary>
    /// Validates the bind address. Accepts a hostname, a literal IP, or the wildcards that mean "every
    /// interface" — but rejects anything unparseable, because a typo'd address silently falling back
    /// to loopback would look like a firewall problem for hours.
    /// </summary>
    private static string ParseHost(string raw)
    {
        var host = raw.Trim();
        if (host.Length == 0)
            throw new ArgumentException("--bind requires an address (e.g. 0.0.0.0).");

        // Wildcards are passed through as-is: HttpListener spells "all interfaces" as "+", and both
        // 0.0.0.0 and * are the forms people actually type.
        if (host is "0.0.0.0" or "*" or "+" or "::")
            return "+";

        if (IPAddress.TryParse(host, out _)) return host;

        // A hostname is legitimate (binding a specific interface by name), but it must at least look
        // like one rather than a mistyped flag.
        if (host.StartsWith('-') || host.Contains(' '))
            throw new ArgumentException($"Invalid bind address: '{raw}'.");

        return host;
    }
}
