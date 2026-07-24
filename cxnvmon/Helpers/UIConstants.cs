using SharpConsoleUI;
using SharpConsoleUI.Helpers;

namespace cxnvmon.Helpers;

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
    public static readonly Color SeparatorColor = new(0x1e, 0x2a, 0x3a);
    public static readonly Color PrimaryText = new(0xc8, 0xd4, 0xe0);
    public static readonly Color MutedText = new(0x4a, 0x60, 0x70);
    public static readonly Color Accent = Color.Cyan1;

    // Card header text: readable but softened — PrimaryText mixed a third of the way toward
    // MutedText, so titles are clearly legible without the harsh full-bright look.
    public static readonly Color CardTitle = PrimaryText.Mix(MutedText, 0.35);

    public static readonly Color Critical = new(0xff, 0x6b, 0x6b);
    public static readonly Color Warning = new(0xff, 0xd9, 0x3d);
    public static readonly Color Normal = new(0x4e, 0xcd, 0xc4);

    public static readonly Color BarUnfilledColor = new(0x1e, 0x2a, 0x3a);

    // Sparkline gradients
    public static readonly Color[] SparkCpuTotal = [new(0x0d, 0x94, 0x88), new(0x4e, 0xcd, 0xc4), new(0xff, 0xd9, 0x3d), new(0xff, 0x6b, 0x6b)];
    public static readonly Color[] SparkMemUsed = [new(0x1a, 0x6b, 0x4a), new(0x4e, 0xcd, 0xc4), new(0xff, 0xd9, 0x3d), new(0xff, 0x6b, 0x6b)];

    public static Color ThresholdColor(double value) => value switch
    {
        < 60 => Normal,
        < 85 => Warning,
        _ => Critical
    };
}
