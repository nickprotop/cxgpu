using System.Net;
using System.Text;
using cxgpu.Gpu;

namespace cxgpu.Export;

/// <summary>
/// A minimal HTTP endpoint serving <c>/metrics</c> in Prometheus text format.
///
/// This turns cxgpu from an interactive tool into something with a daemon's concerns — a listening
/// socket, a lifecycle, a failure mode when the port is taken — so the defaults are deliberately
/// conservative:
///
/// - Binds LOCALHOST unless told otherwise. A monitor that silently exposed a port on every interface
///   of someone's laptop would be a surprise, and an unpleasant one.
/// - Fails LOUDLY when the port is unavailable, rather than falling back to another one. A scraper
///   configured against 9835 finding nothing there is a worse outcome than a startup error.
/// - Reads through the same <see cref="IGpuStatsProvider"/> the UI uses, so exported numbers cannot
///   drift from what the screen shows.
/// </summary>
internal sealed class PrometheusExporter : IDisposable
{
    /// <summary>
    /// Default port. 9835 is unassigned in the Prometheus default-port registry, so it will not
    /// collide with a well-known exporter on the same host.
    /// </summary>
    public const int DefaultPort = 9835;

    private readonly HttpListener _listener = new();
    private readonly IGpuStatsProvider _stats;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public int Port { get; }
    public string Host { get; }

    public PrometheusExporter(IGpuStatsProvider stats, int port = DefaultPort,
                              string host = "localhost")
    {
        _stats = stats;
        Port = port;
        Host = host;
        _listener.Prefixes.Add($"http://{host}:{port}/");
    }

    /// <summary>
    /// Starts listening. Throws when the port is unavailable — deliberately, see the type summary.
    /// </summary>
    public void Start()
    {
        try
        {
            _listener.Start();
        }
        catch (HttpListenerException ex)
        {
            throw new InvalidOperationException(
                $"Cannot listen on http://{Host}:{Port}/ — {ex.Message}. " +
                "Another process may already hold the port; pass --prometheus=PORT to choose another.",
                ex);
        }

        _loop = Task.Run(() => ServeAsync(_cts.Token));
    }

    private async Task ServeAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception) when (token.IsCancellationRequested || !_listener.IsListening)
            {
                // Shutting down: GetContextAsync throws when the listener is stopped underneath it.
                return;
            }
            catch (Exception)
            {
                // A single failed accept must not take the exporter down — the next one may succeed.
                continue;
            }

            try
            {
                Respond(context);
            }
            catch (Exception)
            {
                // A broken pipe (scraper timed out and hung up) is routine; never fatal.
            }
        }
    }

    private void Respond(HttpListenerContext context)
    {
        var path = context.Request.Url?.AbsolutePath ?? "/";

        // A bare / points at the metrics path rather than 404ing, since typing the host into a browser
        // is the first thing anyone does when checking whether the exporter is up.
        if (path is "/" or "")
        {
            WriteText(context, 200,
                $"cxgpu exporter\nMetrics: http://{Host}:{Port}/metrics\n", "text/plain");
            return;
        }

        if (path != "/metrics")
        {
            WriteText(context, 404, "Not found\n", "text/plain");
            return;
        }

        string body;
        try
        {
            body = PrometheusFormatter.Render(_stats.ReadSnapshot(), _stats.ReadDeviceInfo());
        }
        catch (Exception ex)
        {
            // A vendor tool that failed this scrape is a 503, NOT an empty 200: an empty body reads as
            // "no GPUs present", which would silently zero out a dashboard.
            WriteText(context, 503, $"Failed to read GPU stats: {ex.Message}\n", "text/plain");
            return;
        }

        WriteText(context, 200, body, "text/plain; version=0.0.4; charset=utf-8");
    }

    private static void WriteText(HttpListenerContext context, int status, string body,
                                  string contentType)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Response.StatusCode = status;
        context.Response.ContentType = contentType;
        context.Response.ContentLength64 = bytes.Length;
        context.Response.OutputStream.Write(bytes, 0, bytes.Length);
        context.Response.Close();
    }

    public void Dispose()
    {
        _cts.Cancel();
        if (_listener.IsListening) _listener.Stop();
        _listener.Close();

        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // The serve loop faulting during teardown is not worth propagating from Dispose.
        }

        _cts.Dispose();
    }
}
