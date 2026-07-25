using cxgpu.Helpers;
using SharpConsoleUI;

namespace cxgpu.Widgets;

/// <summary>
/// Shared anchoring and open/close bookkeeping for desktop portals.
///
/// Portals are framework render overlays, NOT windows: they draw above every window, are not modal,
/// and do not take part in any window's control tree. That is what makes a status-bar flyout possible
/// without the focus-stealing and Esc-consuming a dialog would bring.
/// </summary>
internal static class PortalHost
{
    /// <summary>Inset from the screen edge, matching the status bar's own padding.</summary>
    private const int AnchorX = 2;

    /// <summary>
    /// An upward anchor: a width x height box whose BOTTOM edge sits directly above the app's status
    /// bar.
    ///
    /// "Opening upward" needs no special support — portal bounds are absolute screen space with a
    /// top-left origin, so fixing the bottom edge and subtracting the height grows the box upward.
    ///
    /// <c>DesktopBottomRight.Y</c> is NOT sufficient on its own here. It subtracts the height of the
    /// framework's BottomPanel, and this app does not use one: its status bar is a StatusBarControl
    /// living inside the main window, which the framework does not know about. So the reported last
    /// desktop row is the row the status bar is drawn on, and a portal anchored there overlaps it.
    /// <see cref="StatusBarRows"/> is subtracted to clear our own chrome.
    /// </summary>
    public static System.Drawing.Rectangle AnchorAbove(ConsoleWindowSystem ws, int width, int height)
    {
        int bottom = ws.DesktopBottomRight.Y - StatusBarRows;
        int y = Math.Max(0, bottom - height + 1);
        return new System.Drawing.Rectangle(AnchorX, y, width, height);
    }

    /// <summary>
    /// As <see cref="AnchorAbove"/>, but flush to the RIGHT edge — for a portal opened from a
    /// right-hand status-bar item, so the flyout appears under the control that summoned it rather
    /// than across the screen from it.
    /// </summary>
    public static System.Drawing.Rectangle AnchorAboveRight(ConsoleWindowSystem ws, int width, int height)
    {
        int bottom = ws.DesktopBottomRight.Y - StatusBarRows;
        int y = Math.Max(0, bottom - height + 1);
        int x = Math.Max(0, ws.DesktopBottomRight.X - width - AnchorX + 1);
        return new System.Drawing.Rectangle(x, y, width, height);
    }

    /// <summary>
    /// Rows our own bottom chrome occupies: the StatusBarControl and the rule above it
    /// (see DashboardWindow.BuildBottomStatusBar).
    /// </summary>
    private const int StatusBarRows = 3;

    // A single physical click on a status-bar item arrives as TWO mouse dispatches: the first
    // (Button1Pressed) counts as a click OUTSIDE the open portal and dismisses it, and the second
    // (Button1Clicked) then reaches the item and would RE-open it — so an open portal could never be
    // closed by clicking its own item. Bottom-panel routing runs before portal hit-testing, so this is
    // structural rather than incidental.
    //
    // Recording WHICH portal was just dismissed lets an Open() from the click's second half be
    // suppressed for that same portal, while clicking a DIFFERENT item still closes the old one and
    // opens the new.
    private const double ReopenSuppressMs = 250;
    private static object? _lastDismissedKey;
    private static DateTime _lastDismiss = DateTime.MinValue;

    /// <summary>Records that <paramref name="key"/>'s portal was just dismissed. Call from OnDismiss.</summary>
    public static void NotifyDismissed(object key)
    {
        _lastDismissedKey = key;
        _lastDismiss = DateTime.UtcNow;
    }

    /// <summary>Whether an Open() for this portal is the second half of the click that just closed it.</summary>
    public static bool SuppressReopen(object key) =>
        ReferenceEquals(_lastDismissedKey, key) &&
        (DateTime.UtcNow - _lastDismiss).TotalMilliseconds < ReopenSuppressMs;

    /// <summary>Closes any open portal. Harmless when none is open.</summary>
    public static void CloseAll(ConsoleWindowSystem ws) => ws.DesktopPortalService.DismissAllPortals();

    /// <summary>
    /// The portal surface. Uses the tile background — the same lifted surface the strip's clickable
    /// chips sit on, so an overlay reads as raised above the app rather than as part of it.
    /// </summary>
    public static Color Surface => UIConstants.TileBg;

    /// <summary>The portal border.</summary>
    public static Color Border => UIConstants.Accent;
}
