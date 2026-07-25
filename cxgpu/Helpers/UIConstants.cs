using SharpConsoleUI;
using SharpConsoleUI.Helpers;

namespace cxgpu.Helpers;

internal static class UIConstants
{
    public const int RefreshIntervalMs = 1000;
    public const int PrimeDelayMs = 300;
    public const int FadeInMs = 300;
    public const int TabCrossfadeMs = 200;
    public const int MaxHistoryPoints = 50;

    // Wide-layout left column. Fixed width sized to the spec-sheet's widest line
    // ("NVIDIA GeForce RTX 3090" ~= 23 cols) plus padding. (Auto() can't size to the
    // ScrollablePanel viewport, so the column is fixed and the graphs take the rest via Star.)
    public const int FixedTextColumnWidth = 28;
    public const int SeparatorColumnWidth = 1;

    public static readonly Color BaseBg = new(0x0d, 0x11, 0x17);
    public static readonly Color BaseEnd = new(0x1a, 0x23, 0x32);
    public static readonly Color HeaderBg = new(0x0a, 0x0e, 0x14);
    public static readonly Color RightPanelBg = new(0x10, 0x14, 0x1a, 210);
    // Left spec-sheet column: base nudged toward the cyan accent — a faint, designed cool tint
    // at 70% alpha (178/255) so the window gradient shows subtly through the sidebar.
    public static readonly Color LeftPanelBg = new(0x0e, 0x16, 0x20, 178);
    public static readonly Color CardBg = new(0x14, 0x1c, 0x28, 180);

    // Multi-GPU summary-strip tiles. Each tile is a bracketed background slab, so it reads as a
    // discrete object rather than text in a stream.
    //
    // Both slabs sit ABOVE the card background. Unselected tiles are RAISED, not recessed: a tile is
    // clickable, and a recessed surface reads as inert while a raised one reads as a control. The
    // selected slab is lifted further and nudged toward the cyan accent, so it still clearly wins.
    public static readonly Color TileBg = new(0x21, 0x2d, 0x3d);
    public static readonly Color TileSelectedBg = new(0x2b, 0x4a, 0x63);


    public static readonly Color SeparatorColor = new(0x1e, 0x2a, 0x3a);
    public static readonly Color PrimaryText = new(0xc8, 0xd4, 0xe0);
    public static readonly Color MutedText = new(0x4a, 0x60, 0x70);
    public static readonly Color Accent = Color.Cyan1;

    // Card header text: readable but softened — PrimaryText mixed a third of the way toward
    // MutedText, so titles are clearly legible without the harsh full-bright look.
    public static readonly Color CardTitle = PrimaryText.Mix(MutedText, 0.35);

    // The enclosing glyphs on an UNSELECTED tile: the ACCENT, dimmed well down toward the muted tone.
    // Sharing the selected tile's hue makes the enclosures read as one family — the same control in a
    // different state — where a neutral grey read as unrelated chrome. Kept dim enough that the
    // selected tile's full-brightness accent still wins the eye immediately.
    //
    // MUST be declared after MutedText: C# initializes static fields in TEXTUAL order, so computing
    // this above it silently yielded black (the Color default) and the glyphs rendered invisible
    // against the dark slab.
    public static readonly Color TileBracket = Accent.Mix(MutedText, 0.86);

    public static readonly Color Critical = new(0xff, 0x6b, 0x6b);
    public static readonly Color Warning = new(0xff, 0xd9, 0x3d);
    public static readonly Color Normal = new(0x4e, 0xcd, 0xc4);

    public static readonly Color BarUnfilledColor = new(0x1e, 0x2a, 0x3a);

    // Sparkline gradients
    public static readonly Color[] SparkCpuTotal = [new(0x0d, 0x94, 0x88), new(0x4e, 0xcd, 0xc4), new(0xff, 0xd9, 0x3d), new(0xff, 0x6b, 0x6b)];
    public static readonly Color[] SparkMemUsed = [new(0x1a, 0x6b, 0x4a), new(0x4e, 0xcd, 0xc4), new(0xff, 0xd9, 0x3d), new(0xff, 0x6b, 0x6b)];

    /// <summary>
    /// Colour for a PERCENTAGE (utilization, memory, fan, power-of-cap). The bands suit a 0-100 scale
    /// where 85%+ genuinely warrants attention.
    ///
    /// Deliberately NOT used for temperature: °C is not a percentage, and 85 as a critical point fires
    /// on a card doing ordinary work. Temperature goes through
    /// <see cref="GpuFormat.TemperatureColor"/>, which reads the same thresholds the alerts use, so the
    /// colour on screen and the alert that fires cannot disagree.
    /// </summary>
    public static Color ThresholdColor(double value) => value switch
    {
        < 60 => Normal,
        < 85 => Warning,
        _ => Critical
    };
}
