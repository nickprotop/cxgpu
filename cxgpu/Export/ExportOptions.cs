using System.Net;

namespace cxgpu.Export;

/// <summary>
/// The export-related command-line options, parsed once so Program does not carry the string handling.
/// </summary>
/// <param name="Prometheus">Whether to serve metrics.</param>
/// <param name="Port">Port to serve on.</param>
/// <param name="Host">Interface to bind. Loopback unless explicitly widened.</param>
/// <param name="NoUi">Run headless — exporter only, no TUI.</param>
internal sealed record ExportOptions(bool Prometheus, int Port, string Host, bool NoUi)
{
    public static ExportOptions None => new(false, PrometheusExporter.DefaultPort, DefaultHost, false);

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
    /// Parses the export flags. Both spellings are accepted for each option — <c>--port 9835</c> and
    /// <c>--port=9835</c> — because a tool that takes only one of them is a papercut every time you
    /// misremember which.
    ///
    /// <code>
    ///   --prometheus              serve metrics (default port 9835, loopback)
    ///   --port PORT               port to serve on
    ///   --bind ADDRESS            interface to bind (0.0.0.0 for all)
    ///   --no-ui                   exporter only, no TUI
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
        int? port = null;
        string? host = null;

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            switch (Split(arg, args, ref i))
            {
                case ("--prometheus", null):
                    prometheus = true;
                    break;

                // Retained because it shipped in the first cut of this feature: --prometheus=PORT
                // overloaded the flag with its own port. --port is the documented spelling now, but
                // silently breaking a command someone already put in a systemd unit is not worth the
                // tidiness.
                case ("--prometheus", var legacyPort):
                    prometheus = true;
                    port = ParsePort(legacyPort!);
                    break;

                case ("--port", var value):
                    port = ParsePort(Required(value, "--port", "a port number (1-65535)"));
                    break;

                case ("--bind", var value):
                    host = ParseHost(Required(value, "--bind", "an address (e.g. 0.0.0.0)"));
                    break;

                // The original name for --bind, kept for the same reason as --prometheus=PORT.
                case ("--prometheus-host", var value):
                    host = ParseHost(Required(value, "--prometheus-host", "an address (e.g. 0.0.0.0)"));
                    break;

                case ("--no-ui", _):
                    noUi = true;
                    break;
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

        return new ExportOptions(
            prometheus,
            port ?? PrometheusExporter.DefaultPort,
            host ?? DefaultHost,
            noUi);
    }

    /// <summary>
    /// Splits "--name=value" into its parts, or consumes the next argument as the value for a
    /// value-taking flag written as "--name value". Returns a null value for a bare flag.
    /// </summary>
    private static (string Name, string? Value) Split(string arg, string[] args, ref int i)
    {
        int eq = arg.IndexOf('=');
        if (eq > 0)
            return (arg[..eq], arg[(eq + 1)..]);

        // Only flags that TAKE a value consume the next argument, and only when it does not itself
        // look like a flag — otherwise "cxgpu --port --no-ui" would swallow --no-ui as the port.
        if (arg is "--port" or "--bind" or "--prometheus-host")
        {
            if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                return (arg, args[++i]);

            // Reported by Required() below, which knows what the flag wanted.
            return (arg, null);
        }

        return (arg, null);
    }

    private static string Required(string? value, string flag, string what) =>
        value ?? throw new ArgumentException($"{flag} requires {what}.");

    private static int ParsePort(string raw)
    {
        if (!int.TryParse(raw, out var port) || port is < 1 or > 65535)
            throw new ArgumentException($"Invalid port: '{raw}' (expected 1-65535).");

        return port;
    }

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
