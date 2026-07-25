using cxgpu.Gpu;

namespace cxgpu.Tests;

/// <summary>
/// Tests for amdgpu fdinfo value parsing. The unit suffix is not optional to handle: the kernel
/// writes "912452 KiB", and treating that as a bare number would report GiB-scale memory in KiB.
/// </summary>
public class AmdSysfsParserTests
{
    [Theory]
    [InlineData("912452 KiB", 912452)]
    [InlineData("1024 KiB", 1024)]
    [InlineData("0 KiB", 0)]
    public void ParsesKibValues(string raw, double expected)
    {
        Assert.Equal(expected, AmdSysfsReader.ParseKib(raw));
    }

    [Fact]
    public void ConvertsMibToKib()
    {
        Assert.Equal(2048, AmdSysfsReader.ParseKib("2 MiB"));
    }

    [Fact]
    public void ConvertsGibToKib()
    {
        Assert.Equal(1024 * 1024, AmdSysfsReader.ParseKib("1 GiB"));
    }

    [Fact]
    public void TreatsBareNumberAsKib()
    {
        Assert.Equal(4096, AmdSysfsReader.ParseKib("4096"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("N/A")]
    [InlineData("-")]
    public void UnparseableValuesBecomeZeroNotAnException(string raw)
    {
        // fdinfo is read per-process in a hot loop; a throw here would take down the whole process
        // list rather than losing one row.
        Assert.Equal(0, AmdSysfsReader.ParseKib(raw));
    }

    [Fact]
    public void ToleratesSurroundingWhitespace()
    {
        Assert.Equal(512, AmdSysfsReader.ParseKib("  512 KiB \n"));
    }

    [Fact]
    public void ParsesFractionalValues()
    {
        Assert.Equal(1536, AmdSysfsReader.ParseKib("1.5 MiB"));
    }
}
