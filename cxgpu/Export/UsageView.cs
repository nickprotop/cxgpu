namespace cxgpu.Export;

/// <summary>
/// How <c>--gpu-usage</c> renders: everything that shapes the one-shot table, and nothing that
/// decides whether it runs.
///
/// <para>WHY IT EXISTS. <see cref="ExportOptions"/> reached ELEVEN positional parameters — four
/// describing an exporter endpoint and seven describing a table — and every one of them arrived as
/// a reasonable single addition to a list that was already too long. That is how these grow: nobody
/// writes an eleven-parameter record, they write "ten plus mine", and the cost only appears at the
/// call site, where <c>new(false, 9835, "localhost", false, true, "", null, false, null, 5,
/// "memory")</c> is a row of literals whose meaning depends entirely on counting positions.</para>
///
/// <para>THE TEST IS WHETHER A NAME FITS, and one does: these five answer "what should the table
/// look like", while Prometheus/Port/Host/NoUi answer "should we serve metrics". Two questions, two
/// records. Transposing <c>Color</c> and <c>AppendProcesses</c> used to compile — both are bools —
/// and would have silently rendered the wrong thing; named members make that a build error.</para>
/// </summary>
/// <param name="Format">Empty for the table, <c>json</c> for machine-readable output.</param>
/// <param name="Color">Force colour on (true), off (false), or auto-detect (null).</param>
/// <param name="AppendProcesses">Also render the per-process table below the GPU grid.</param>
/// <param name="WatchInterval">Seconds between refreshes, or null for a single shot.</param>
/// <param name="Top">Show only the N largest processes, or null for all of them.</param>
/// <param name="Sort">Which column orders the process table.</param>
internal sealed record UsageView(
    string Format = "",
    bool? Color = null,
    bool AppendProcesses = false,
    int? WatchInterval = null,
    int? Top = null,
    string Sort = UsageView.DefaultSort)
{
    /// <summary>How the process table is ordered when nobody said. Memory, because that is the
    /// column people open a GPU monitor to look at.</summary>
    public const string DefaultSort = "memory";

    public const string JsonFormat = "json";

    /// <summary>The default view: a plain table, no processes, one shot.</summary>
    public static UsageView Default => new();

    /// <summary>True when the caller asked for JSON rather than a table.</summary>
    public bool IsJson => Format == JsonFormat;

    /// <summary>
    /// Whether to emit colour, given that the caller may have said nothing.
    ///
    /// <para>Auto-detection is the null case: colour when a terminal is attached, plain text when
    /// the output is being piped or redirected. An explicit <c>--color</c>/<c>--no-color</c>
    /// overrides it in both directions, which is what makes the flags worth having at all.</para>
    /// </summary>
    public bool UseColor(bool outputRedirected) => Color ?? !outputRedirected;
}
