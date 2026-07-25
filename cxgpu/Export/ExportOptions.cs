namespace cxgpu.Export;

/// <summary>
/// The export-related command-line options, parsed once so Program does not carry the string handling.
/// </summary>
/// <param name="Prometheus">Whether to serve metrics.</param>
/// <param name="Port">Port to serve on.</param>
/// <param name="Host">Interface to bind. Localhost unless explicitly widened.</param>
/// <param name="NoUi">Run headless — exporter only, no TUI.</param>
internal sealed record ExportOptions(bool Prometheus, int Port, string Host, bool NoUi)
{
    public static ExportOptions None => new(false, PrometheusExporter.DefaultPort, "localhost", false);

    /// <summary>
    /// Parses <c>--prometheus[=PORT]</c>, <c>--prometheus-host=HOST</c> and <c>--no-ui</c>.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// When the arguments cannot be satisfied — an unparseable port, or --no-ui without
    /// --prometheus, which would leave the process doing nothing observable.
    /// </exception>
    public static ExportOptions Parse(string[] args)
    {
        bool prometheus = false;
        bool noUi = false;
        int port = PrometheusExporter.DefaultPort;
        string host = "localhost";

        foreach (var arg in args)
        {
            if (arg == "--no-ui")
            {
                noUi = true;
            }
            else if (arg == "--prometheus")
            {
                prometheus = true;
            }
            else if (arg.StartsWith("--prometheus=", StringComparison.Ordinal))
            {
                prometheus = true;
                var raw = arg["--prometheus=".Length..];
                if (!int.TryParse(raw, out port) || port is < 1 or > 65535)
                    throw new ArgumentException($"Invalid --prometheus port: '{raw}' (expected 1-65535).");
            }
            else if (arg.StartsWith("--prometheus-host=", StringComparison.Ordinal))
            {
                host = arg["--prometheus-host=".Length..];
                if (host.Length == 0)
                    throw new ArgumentException("--prometheus-host= requires a value (e.g. 0.0.0.0).");
            }
        }

        // --no-ui alone would start a process with no interface and no endpoint: nothing to observe and
        // no way to stop it except a signal. Refused rather than silently doing nothing.
        if (noUi && !prometheus)
            throw new ArgumentException("--no-ui requires --prometheus (there would be nothing to serve).");

        return new ExportOptions(prometheus, port, host, noUi);
    }
}
