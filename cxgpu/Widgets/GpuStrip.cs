using cxgpu.Gpu;
using cxgpu.Helpers;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Builders;

namespace cxgpu.Widgets;

/// <summary>
/// The multi-GPU selector strip: one clickable tile per GPU, led by a DASH tile for the fleet view.
///
/// Owns its click hit-testing, which is the reason this is a type rather than a handful of helpers.
/// Tile widths are measured with <c>MarkupParser.StripLength</c> and recorded as spans, so the
/// measurement and the hit-test have to agree — keeping both here means a change to how a tile is
/// drawn cannot silently desynchronise from how it is clicked. That desync was a real bug: square
/// brackets read as markup tags, measured short, and drifted every span.
/// </summary>
internal sealed class GpuStrip
{
    /// <summary>Sentinel index for the fleet/dashboard tile, which is not a GPU.</summary>
    public const int DashboardIndex = -1;

    private const string MarkupName = "gpu_strip_markup";
    private const string CardName = "gpu_strip_card";

    // Gap between tiles, in display columns. Two columns of card background between slabs, so the
    // slabs read as separate objects rather than one continuous band.
    private const string TileGap = "  ";

    // Tile enclosures: inward-pointing angle quotes (U+2039/203A) on EVERY tile, so each one reads as
    // the same enclosed control and the difference between them is state rather than shape. Selection
    // is carried by colour — full accent on the selected pair, a heavily muted accent on the rest —
    // together with the lifted background slab.
    //
    // These must not be "[" or "]": the width used for click hit-testing comes from
    // MarkupParser.StripLength, which parses those as the start of a markup tag and would measure the
    // tile short, drifting every click span. The angle quotes carry no markup meaning and are
    // single-width (verified), so they cannot shift the columns.
    private const string TileOpen = "‹";
    private const string TileClose = "›";

    // Recorded per (row, column-span) because tiles wrap: a single row would clip the last tiles on a
    // narrow terminal, and a fleet view that hides part of the fleet is worse than useless.
    private readonly List<(int Start, int End, int GpuIndex, int Row)> _spans = new();

    private readonly Func<int, bool> _selectGpu;
    private readonly Func<GpuSnapshot, int> _selectedIndex;

    /// <param name="selectedIndex">
    /// Resolves which index is selected FOR A GIVEN SNAPSHOT, or <see cref="DashboardIndex"/>. Takes
    /// the snapshot rather than being a stored value so the highlighted tile always agrees with the
    /// data being drawn, and so the strip never triggers a read of its own.
    /// </param>
    /// <param name="selectGpu">Selects an index; returns whether that changed anything.</param>
    public GpuStrip(Func<GpuSnapshot, int> selectedIndex, Func<int, bool> selectGpu)
    {
        _selectedIndex = selectedIndex;
        _selectGpu = selectGpu;
    }

    /// <summary>Refreshes the tile text in place, re-recording the hit-test spans.</summary>
    public void Update(IWindowControl root, GpuSnapshot snapshot, int width,
                       Func<IWindowControl, string, MarkupControl?> find)
    {
        var markup = find(root, MarkupName);
        markup?.SetContent(Content(snapshot, width));
    }

    /// <summary>
    /// The full strip content: the (possibly wrapped) tile rows followed by the hint line. Calling
    /// this re-records the click spans, so it must be what produces whatever is on screen.
    /// </summary>
    public List<string> Content(GpuSnapshot snapshot, int width)
    {
        var lines = BuildLines(snapshot, width);
        lines.Add(HintLine());
        return lines;
    }

    /// <summary>
    /// Builds the strip card. Returns null for a single GPU, where a selector would be noise.
    /// </summary>
    public ScrollablePanelControl? Build(GpuSnapshot snapshot, int width,
                                        Func<ScrollablePanelControl> buildCard)
    {
        if (snapshot.Gpus.Count <= 1) return null;

        var card = buildCard();
        card.Name = CardName;

        var builder = new MarkupBuilder().WithName(MarkupName);
        foreach (var line in Content(snapshot, width))
            builder.AddLine(line);
        var markup = builder.Build();

        markup.MouseClick += (_, e) =>
        {
            // Hit-test both the row and the column: tiles may wrap onto several rows, and the hint
            // line (the row after the last tile row) is decoration, so it matches nothing.
            foreach (var (start, end, gpuIndex, row) in _spans)
            {
                if (e.Position.Y != row) continue;
                if (e.Position.X < start || e.Position.X >= end) continue;
                if (_selectGpu(gpuIndex)) e.Handled = true;
                return;
            }
        };

        card.AddControl(markup);
        return card;
    }

    // Builds the tile rows and (re)records the click hit-test spans. The selected tile is accented
    // and sits on a lifted background slab, so the selection reads at a glance.
    //
    // Tiles WRAP onto additional rows when they don't fit the available width. Spans are recorded per
    // row so click hit-testing stays correct on wrapped rows.
    private List<string> BuildLines(GpuSnapshot snapshot, int availableWidth)
    {
        _spans.Clear();

        var muted = UIConstants.MutedText.ToMarkup();
        var accent = UIConstants.Accent.ToMarkup();
        int selectedIndex = _selectedIndex(snapshot);

        var rows = new List<string>();
        var sb = new System.Text.StringBuilder();
        int column = 0;
        int row = 0;

        // The DASHBOARD chip leads the strip: it is the fleet view, so it reads before the individual
        // cards, and putting it first keeps its position stable however many GPUs come and go.
        {
            bool selected = selectedIndex == DashboardIndex;
            string plain = $"{TileOpen}DASH {snapshot.Gpus.Count} GPUs{TileClose}";
            int width = SharpConsoleUI.Parsing.MarkupParser.StripLength(plain);

            var slab = (selected ? UIConstants.TileSelectedBg : UIConstants.TileBg).ToMarkup();
            var enclosure = selected ? $"{accent} bold" : UIConstants.TileBracket.ToMarkup();
            var labelColor = selected ? accent : UIConstants.PrimaryText.ToMarkup();

            sb.Append($"[{UIConstants.PrimaryText.ToMarkup()} on {slab}]");
            sb.Append($"[{enclosure}]{TileOpen}[/]");
            sb.Append($"[{labelColor} bold]DASH[/] ");
            sb.Append($"[{muted}]{snapshot.Gpus.Count} GPUs[/]");
            sb.Append($"[{enclosure}]{TileClose}[/]");
            sb.Append("[/]");

            _spans.Add((column, column + width, DashboardIndex, row));
            column += width;
        }

        foreach (var gpu in snapshot.Gpus)
        {
            bool selected = gpu.Index == selectedIndex;

            // Tiles are FIXED-WIDTH with right-aligned numbers, so values line up column-wise across
            // tiles. Ragged tiles were a big part of why the strip read as one undifferentiated run
            // of text: with alignment, a hot GPU's digits sit directly under a cool one's.
            string plain =
                $"{TileOpen}GPU {gpu.Index}  " +
                $"{GpuFormat.UtilBar(gpu.UtilizationPercent)} {gpu.UtilizationPercent,3:F0}%  " +
                $"m{gpu.MemoryUsedPercent,3:F0}%  t{gpu.TemperatureC,3:F0}°" +
                $"{TileClose}";
            int width = SharpConsoleUI.Parsing.MarkupParser.StripLength(plain);

            // Wrap when this tile (plus its leading gap) would overflow. Never wrap the first tile
            // of a row — an over-wide tile has to clip rather than loop forever.
            if (column > 0 && column + TileGap.Length + width > availableWidth)
            {
                rows.Add(sb.ToString());
                sb.Clear();
                column = 0;
                row++;
            }

            // The gap is plain card background — outside both slabs — so the eye reads two separate
            // objects with space between them rather than one continuous band.
            if (column > 0)
            {
                sb.Append(TileGap);
                column += TileGap.Length;
            }

            // Each tile sits on its own background SLAB, which is what turns it from text-in-a-stream
            // into a discrete object. Both slabs sit ABOVE the card background — a tile is clickable,
            // and a raised surface reads as a control where a recessed one reads as inert. The selected
            // slab is lifted further and its label accented.
            var slab = (selected ? UIConstants.TileSelectedBg : UIConstants.TileBg).ToMarkup();
            var labelColor = selected ? accent : UIConstants.PrimaryText.ToMarkup();

            // NOTE: the foreground is stated explicitly before "on" — a bare "[on <bg>]" tag takes a
            // different parser branch and did not paint the slab in practice.
            sb.Append($"[{UIConstants.PrimaryText.ToMarkup()} on {slab}]");
            var enclosure = selected ? $"{accent} bold" : UIConstants.TileBracket.ToMarkup();
            sb.Append($"[{enclosure}]{TileOpen}[/]");
            sb.Append($"[{labelColor} bold]GPU {gpu.Index}[/]  ");
            // Utilization gets a mini-bar: a pre-attentive height cue you can scan for "which one is
            // hot" without reading any digits — the actual point of a fleet strip.
            sb.Append($"[{UIConstants.ThresholdColor(gpu.UtilizationPercent).ToMarkup()} bold]" +
                      $"{GpuFormat.UtilBar(gpu.UtilizationPercent)} {gpu.UtilizationPercent,3:F0}%[/]  ");
            // Mem/temp keep single-letter prefixes instead of icons: eight repeated emoji carried no
            // distinguishing information and dominated the row visually.
            sb.Append($"[{muted}]m[/][{UIConstants.ThresholdColor(gpu.MemoryUsedPercent).ToMarkup()}]{gpu.MemoryUsedPercent,3:F0}%[/]  ");
            sb.Append($"[{muted}]t[/][{GpuFormat.TemperatureColor(gpu).ToMarkup()}]{gpu.TemperatureC,3:F0}°[/]");
            sb.Append($"[{enclosure}]{TileClose}[/]");
            sb.Append("[/]");

            _spans.Add((column, column + width, gpu.Index, row));
            column += width;
        }

        if (sb.Length > 0 || rows.Count == 0)
            rows.Add(sb.ToString());

        return rows;
    }

    // The strip's second line: the key hints, so the selector documents itself.
    private static string HintLine()
    {
        var muted = UIConstants.MutedText.ToMarkup();
        var accent = UIConstants.Accent.ToMarkup();
        return $"[{muted}]select GPU:[/] [{accent}][[[/] [{accent}]]][/] [{muted}]prev/next  ·[/] " +
               $"[{accent}]1[/][{muted}]-[/][{accent}]9[/] [{muted}]direct  ·  click a tile[/]";
    }
}
