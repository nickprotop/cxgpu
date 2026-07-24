using SharpConsoleUI;

namespace cxnvmon.Helpers;

internal static class UIConstants
{
    public const int RefreshIntervalMs = 1000;
    public const int PrimeDelayMs = 300;
    public const int FadeInMs = 300;
    public const int TabCrossfadeMs = 200;
    public const int MaxHistoryPoints = 50;

    // Wide-layout left text column. Sized to the widest readout line
    // ("Clocks: SM: 1650 MHz, Mem: 9501 MHz" ~= 42 cols) plus a little padding, so the
    // graphs column (Star) gets ALL remaining width instead of a wasteful proportional split.
    public const int FixedTextColumnWidth = 44;
    public const int SeparatorColumnWidth = 1;

    public static readonly Color BaseBg = new(0x0d, 0x11, 0x17);
    public static readonly Color BaseEnd = new(0x1a, 0x23, 0x32);
    public static readonly Color HeaderBg = new(0x0a, 0x0e, 0x14);
    public static readonly Color RightPanelBg = new(0x10, 0x14, 0x1a, 210);
    public static readonly Color CardBg = new(0x14, 0x1c, 0x28, 180);
    public static readonly Color SeparatorColor = new(0x1e, 0x2a, 0x3a);
    public static readonly Color PrimaryText = new(0xc8, 0xd4, 0xe0);
    public static readonly Color MutedText = new(0x4a, 0x60, 0x70);
    public static readonly Color Accent = Color.Cyan1;

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
