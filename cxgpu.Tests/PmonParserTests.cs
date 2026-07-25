using cxgpu.Gpu;

namespace cxgpu.Tests;

/// <summary>
/// Tests for nvidia-smi pmon parsing.
///
/// The fixtures are real captured output, not invented: the 9-column sample below is verbatim from
/// driver 595.84. The column set varies between drivers, which is exactly the bug these tests exist
/// to prevent — an earlier version read columns by fixed index and would report the "jpg" column as
/// the process command on any driver that emits jpg/ofa.
/// </summary>
public class PmonParserTests
{
    // Verbatim `nvidia-smi pmon -c 1` on driver 595.84 (10 columns incl. command).
    private static readonly string[] NineColumnOutput =
    [
        "# gpu         pid   type     sm    mem    enc    dec    jpg    ofa    command ",
        "# Idx           #    C/G      %      %      %      %      %      %    name ",
        "    0       7338     G      -      -      -      -      -      -    gnome-shell    ",
        "    0     100949     C     45     12      -      -      -      -    python         ",
        "    0     100973     C      7      3      -      -      -      -    llama-server   ",
    ];

    // Older drivers omit jpg/ofa. Same parser must handle both without a version check.
    private static readonly string[] SevenColumnOutput =
    [
        "# gpu        pid  type    sm   mem   enc   dec   command",
        "# Idx          #   C/G     %     %     %     %   name",
        "    0      12345     C    88    41     0     0   trainer",
    ];

    // A layout where the columns are in a DIFFERENT ORDER from the common one, so the field positions
    // genuinely move. Both fixtures above happen to place sm/mem at positions 3/4, which means a
    // hardcoded-index parser passes them both — this one is what actually pins the behaviour to the
    // header names. nvidia-smi's column order is not contractual across drivers and query forms.
    private static readonly string[] ReorderedColumnOutput =
    [
        "# gpu   pid   type   mem   sm   dec   enc   command",
        "# Idx     #    C/G     %    %     %     %   name",
        "    0  4242      C    30   77     5     9   renderer",
    ];

    [Fact]
    public void ParsesNineColumnDriverOutput()
    {
        var result = NvidiaBackend.ParsePmon(NineColumnOutput);

        Assert.Equal(3, result.Count);
        Assert.Equal(45, result[100949].Sm);
        Assert.Equal(12, result[100949].Mem);
    }

    [Fact]
    public void ParsesSevenColumnDriverOutput()
    {
        // The regression guard: with fixed-index parsing this either throws or reads the wrong field.
        var result = NvidiaBackend.ParsePmon(SevenColumnOutput);

        Assert.Equal(88, Assert.Contains(12345, result).Sm);
        Assert.Equal(41, result[12345].Mem);
        Assert.Equal(0, result[12345].Enc);
    }

    [Fact]
    public void LocatesColumnsByNameNotPosition()
    {
        // The real regression guard: sm and mem are SWAPPED relative to the usual layout, and enc/dec
        // are reversed. A parser reading fixed offsets returns mem's value for sm and vice versa —
        // plausible-looking numbers attributed to the wrong engine, which is worse than a crash.
        var result = NvidiaBackend.ParsePmon(ReorderedColumnOutput);

        var sample = Assert.Contains(4242, result);
        Assert.Equal(77, sample.Sm);
        Assert.Equal(30, sample.Mem);
        Assert.Equal(9, sample.Enc);
        Assert.Equal(5, sample.Dec);
    }

    [Fact]
    public void DashBecomesNullNotZero()
    {
        // The core null-vs-zero rule. pmon writes "-" for an idle or unsupported engine; rendering
        // that as 0% would claim a measurement that was never taken.
        var result = NvidiaBackend.ParsePmon(NineColumnOutput);

        Assert.Null(result[7338].Sm);
        Assert.Null(result[7338].Enc);
        Assert.Null(result[100949].Enc);
    }

    [Fact]
    public void ZeroIsPreservedAsMeasuredZero()
    {
        // Inverse of the above: a real 0 from the driver is data and must survive as 0, not become null.
        var result = NvidiaBackend.ParsePmon(SevenColumnOutput);

        Assert.Equal(0, result[12345].Dec);
        Assert.NotNull(result[12345].Dec);
    }

    [Fact]
    public void ReturnsEmptyForNoOutput()
    {
        Assert.Empty(NvidiaBackend.ParsePmon([]));
    }

    [Fact]
    public void ReturnsEmptyWhenHeaderIsMissing()
    {
        // Without a header there is no way to know what the columns mean; guessing would invent data.
        Assert.Empty(NvidiaBackend.ParsePmon(["    0   7338   G   -   -   gnome-shell"]));
    }

    [Fact]
    public void SkipsRowsWithUnparseablePid()
    {
        string[] lines =
        [
            "# gpu        pid  type    sm   mem   enc   dec   command",
            "    0          -     -     -     -     -     -   -",
            "    0      12345     C    50    20     0     0   real",
        ];

        var result = NvidiaBackend.ParsePmon(lines);

        Assert.Equal(50, Assert.Contains(12345, result).Sm);
        Assert.Single(result);
    }

    [Fact]
    public void IgnoresTheUnitsHeaderLine()
    {
        // The second "# Idx # C/G % %..." line must not be mistaken for the column-name header;
        // it has no "pid" field, which is how the parser tells them apart.
        var result = NvidiaBackend.ParsePmon(NineColumnOutput);

        Assert.DoesNotContain(result, kv => kv.Key == 0);
    }
}
