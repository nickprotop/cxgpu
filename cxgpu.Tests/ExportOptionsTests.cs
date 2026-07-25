using cxgpu.Export;

namespace cxgpu.Tests;

/// <summary>
/// Tests for the export command-line flags.
///
/// Argument handling fails silently by nature: a flag that is parsed wrong does not crash, it starts
/// the endpoint somewhere other than where it was asked to — which looks like a firewall problem for
/// hours. Most of these assert that a malformed invocation is REFUSED rather than defaulted.
/// </summary>
public class ExportOptionsTests
{
    [Fact]
    public void NoFlagsMeansNoExporter()
    {
        var options = ExportOptions.Parse(["--demo"]);

        Assert.False(options.Prometheus);
        Assert.False(options.NoUi);
    }

    [Fact]
    public void PrometheusAloneUsesTheDefaults()
    {
        var options = ExportOptions.Parse(["--prometheus"]);

        Assert.True(options.Prometheus);
        Assert.Equal(PrometheusExporter.DefaultPort, options.Port);
        Assert.Equal(ExportOptions.DefaultHost, options.Host);
    }

    [Fact]
    public void DefaultsToLoopback()
    {
        // The security-relevant default: a monitor silently listening on every interface of a laptop
        // would be an unpleasant surprise.
        Assert.False(ExportOptions.Parse(["--prometheus"]).IsPublic);
    }

    // ---- Both spellings ---------------------------------------------------------------------------

    [Theory]
    [InlineData("--port", "9100")]
    [InlineData("--port=9100", null)]
    public void AcceptsBothPortSpellings(string first, string? second)
    {
        string[] args = second == null
            ? ["--prometheus", first]
            : ["--prometheus", first, second];

        Assert.Equal(9100, ExportOptions.Parse(args).Port);
    }

    [Theory]
    [InlineData("--bind", "192.168.1.5")]
    [InlineData("--bind=192.168.1.5", null)]
    public void AcceptsBothBindSpellings(string first, string? second)
    {
        string[] args = second == null
            ? ["--prometheus", first]
            : ["--prometheus", first, second];

        Assert.Equal("192.168.1.5", ExportOptions.Parse(args).Host);
    }

    // ---- Wildcards --------------------------------------------------------------------------------

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("*")]
    [InlineData("+")]
    [InlineData("::")]
    public void WildcardFormsAllMeanAllInterfaces(string wildcard)
    {
        var options = ExportOptions.Parse(["--prometheus", "--bind", wildcard]);

        // Normalized to the listener's spelling, but displayed as the user would write it.
        Assert.Equal("+", options.Host);
        Assert.Equal("0.0.0.0", options.DisplayHost);
        Assert.True(options.IsPublic);
    }

    [Fact]
    public void LoopbackFormsAreNotPublic()
    {
        Assert.False(ExportOptions.Parse(["--prometheus", "--bind", "127.0.0.1"]).IsPublic);
        Assert.False(ExportOptions.Parse(["--prometheus", "--bind", "::1"]).IsPublic);
    }

    // ---- Refusals ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("99999")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("")]
    public void RejectsAnUnusablePort(string port)
    {
        Assert.Throws<ArgumentException>(() =>
            ExportOptions.Parse(["--prometheus", $"--port={port}"]));
    }

    [Fact]
    public void RejectsAPortFlagWithNoValue()
    {
        // Must not silently default: the user asked for a specific port and did not get one.
        Assert.Throws<ArgumentException>(() => ExportOptions.Parse(["--prometheus", "--port"]));
    }

    [Fact]
    public void DoesNotSwallowTheNextFlagAsAValue()
    {
        // "--port --no-ui" must fail rather than parsing "--no-ui" as the port and losing the flag.
        Assert.Throws<ArgumentException>(() =>
            ExportOptions.Parse(["--prometheus", "--port", "--no-ui"]));
    }

    [Fact]
    public void RejectsABindFlagWithNoValue()
    {
        Assert.Throws<ArgumentException>(() => ExportOptions.Parse(["--prometheus", "--bind"]));
    }

    [Fact]
    public void RejectsAnUnparseableBindAddress()
    {
        Assert.Throws<ArgumentException>(() =>
            ExportOptions.Parse(["--prometheus", "--bind=--oops"]));
    }

    [Fact]
    public void NoUiWithoutPrometheusIsRefused()
    {
        // Would start a process with no interface and no endpoint: nothing to observe, and no way to
        // stop it but a signal.
        Assert.Throws<ArgumentException>(() => ExportOptions.Parse(["--no-ui"]));
    }

    [Fact]
    public void PortWithoutPrometheusIsRefused()
    {
        // Silently discarding "--port 9100" points the scraper at the wrong place.
        Assert.Throws<ArgumentException>(() => ExportOptions.Parse(["--port", "9100"]));
    }

    [Fact]
    public void BindWithoutPrometheusIsRefused()
    {
        Assert.Throws<ArgumentException>(() => ExportOptions.Parse(["--bind", "0.0.0.0"]));
    }

    [Fact]
    public void PrometheusRejectsAValue()
    {
        // One spelling per setting: accepting --prometheus=9100 as a port would mean two ways to say
        // the same thing, and the error names the flag that does the job.
        var ex = Assert.Throws<ArgumentException>(() => ExportOptions.Parse(["--prometheus=9100"]));

        Assert.Contains("--port", ex.Message);
    }

    // ---- Composition ------------------------------------------------------------------------------

    [Fact]
    public void FullHeadlessInvocationParses()
    {
        var options = ExportOptions.Parse(
            ["--demo", "--prometheus", "--port", "9100", "--bind", "0.0.0.0", "--no-ui"]);

        Assert.True(options.Prometheus);
        Assert.True(options.NoUi);
        Assert.Equal(9100, options.Port);
        Assert.Equal("+", options.Host);
        Assert.True(options.IsPublic);
    }

    [Fact]
    public void UnrelatedArgumentsAreIgnored()
    {
        // --demo and friends belong to other parsers; this one must not choke on them.
        var options = ExportOptions.Parse(["--demo=4", "--prometheus", "--some-future-flag"]);

        Assert.True(options.Prometheus);
    }

    [Theory]
    [InlineData("--prometheus-host=0.0.0.0")]
    [InlineData("--prometheus-port=9100")]
    [InlineData("--listen=0.0.0.0")]
    [InlineData("--host=0.0.0.0")]
    public void MisspelledExportFlagsAreRefusedNotIgnored(string flag)
    {
        // Regression: --prometheus-host= used to fall through the switch, so the exporter came up on
        // localhost while the user believed they had bound 0.0.0.0. A flag that is silently discarded
        // is worse than one that errors — the endpoint exists, just not where it was asked for.
        Assert.Throws<ArgumentException>(() => ExportOptions.Parse(["--prometheus", flag]));
    }
}
