using cxgpu.Gpu;

namespace cxgpu.Tests;

/// <summary>
/// Tests for PCI address normalization. This is the config key for per-card settings, so the same
/// card read through different vendor paths MUST normalize identically — otherwise a threshold saved
/// against one card silently stops applying, or applies to a different card.
///
/// The input formats below are real: nvidia-smi's eight-digit domain and the sysfs four-digit form
/// were both captured from this machine.
/// </summary>
public class GpuIdentityTests
{
    [Fact]
    public void NvidiaAndSysfsFormsOfTheSameCardNormalizeIdentically()
    {
        // The whole point of the type. nvidia-smi pads the domain to 8 digits; sysfs uses 4.
        var fromNvidiaSmi = GpuIdentity.NormalizePciAddress("00000000:01:00.0");
        var fromSysfs = GpuIdentity.NormalizePciAddress("0000:01:00.0");

        Assert.Equal(fromSysfs, fromNvidiaSmi);
        Assert.Equal("0000:01:00.0", fromNvidiaSmi);
    }

    [Theory]
    [InlineData("00000000:01:00.0", "0000:01:00.0")]   // nvidia-smi, verified live
    [InlineData("0000:c6:00.0", "0000:c6:00.0")]       // AMD sysfs, verified live
    [InlineData("0000:C6:00.0", "0000:c6:00.0")]       // case is not significant
    [InlineData("  0000:01:00.0  ", "0000:01:00.0")]   // surrounding whitespace
    public void NormalizesKnownForms(string raw, string expected)
    {
        Assert.Equal(expected, GpuIdentity.NormalizePciAddress(raw));
    }

    [Fact]
    public void DefaultsMissingDomainToZero()
    {
        // Some tools emit the bare bus:device form.
        Assert.Equal("0000:01:00.0", GpuIdentity.NormalizePciAddress("01:00.0"));
    }

    [Fact]
    public void PadsShortFields()
    {
        Assert.Equal("0000:01:00.0", GpuIdentity.NormalizePciAddress("0:1:0.0"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[N/A]")]
    [InlineData("N/A")]
    [InlineData("not-an-address")]
    [InlineData("0000:01:00")]      // no function suffix
    [InlineData("0000:01:00.")]     // empty function
    [InlineData("0000:zz:00.0")]    // non-hex field
    [InlineData("0000.0")]          // too few fields
    [InlineData("0:1:2:3.0")]       // too many fields
    public void UnrecognisableInputYieldsEmptyNotAFabricatedKey(string? raw)
    {
        // "" means "no identity" and must fall through to vendor defaults. Returning a partial or
        // guessed address would bind a card's settings to something that isn't that card.
        Assert.Equal("", GpuIdentity.NormalizePciAddress(raw));
    }

    [Fact]
    public void NullIsSafe()
    {
        Assert.Equal("", GpuIdentity.NormalizePciAddress(null));
    }

    [Fact]
    public void NormalizationIsIdempotent()
    {
        // Config round-trips through this: a stored key re-normalized must not drift.
        var once = GpuIdentity.NormalizePciAddress("00000000:01:00.0");
        Assert.Equal(once, GpuIdentity.NormalizePciAddress(once));
    }

    [Fact]
    public void DistinctCardsStayDistinct()
    {
        var a = GpuIdentity.NormalizePciAddress("00000000:01:00.0");
        var b = GpuIdentity.NormalizePciAddress("0000:c6:00.0");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void DistinguishesFunctionsOnTheSameDevice()
    {
        // Multi-function cards differ only in the suffix; collapsing them would merge two GPUs.
        Assert.NotEqual(
            GpuIdentity.NormalizePciAddress("0000:01:00.0"),
            GpuIdentity.NormalizePciAddress("0000:01:00.1"));
    }
}
