using cxgpu.Helpers;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Core;
using SharpConsoleUI.Layout;

namespace cxgpu.Widgets;

/// <summary>
/// The alert center: a flyout listing GPU events, opening UPWARD from its status-bar item.
///
/// A desktop portal rather than a dialog window — it must not steal focus, must not go modal, and
/// must leave the graphs visible behind it (DimBackground stays false) so the card an alert is about
/// can be watched while reading it.
///
/// SKELETON: rows are stubs. The event source arrives with the alert engine; this proves the
/// anchoring, the click path, and the toggle behaviour first, because those fail visually while the
/// engine fails logically — testing both at once means guessing which layer is wrong.
///
/// KNOWN LIMITATION, verified by test: while the portal is open the app's F2/F3/'['/']' shortcuts do
/// not fire. The framework falls unconsumed keys through to <c>TryHandleGlobalShortcut</c>, which
/// consults a REGISTERED shortcut dictionary — and this app binds its keys on the main window's
/// OnKeyPressed instead, so they are not reachable from there. Accepted rather than worked around:
/// the portal is a transient flyout that Esc, a click outside, or its own badge all dismiss, and
/// registering every app shortcut globally is a larger change than this behaviour justifies.
/// </summary>
internal static class AlertPortal
{
    private static DesktopPortal? _open;

    private const int Width = 62;
    // Border top + bottom, plus the table's header row.
    private const int Chrome = 3;
    private const int MaxRows = 12;

    /// <summary>Whether the portal is currently showing (drives the status-bar item's state).</summary>
    public static bool IsOpen => _open != null;

    /// <summary>
    /// Toggles the portal. Safe to call from a status-bar click handler, which is what the two guards
    /// below exist for.
    /// </summary>
    public static void Toggle(ConsoleWindowSystem ws)
    {
        // A second press closes it.
        if (_open != null)
        {
            ws.DesktopPortalService.RemovePortal(_open);
            _open = null;
            return;
        }

        // A click on the item whose portal is open already dismissed it as a click-outside on the
        // press; ignore the click half so it closes rather than close-then-reopen.
        if (PortalHost.SuppressReopen(typeof(AlertPortal)))
            return;

        // One portal at a time. No-op when none is open.
        PortalHost.CloseAll(ws);

        var rows = StubRows();
        int height = Math.Clamp(rows.Count + Chrome, Chrome + 1, MaxRows + Chrome);
        var rect = PortalHost.AnchorAbove(ws, Width, height);

        // Explicit column widths: with NoBorder there are no separators between cells, so
        // auto-sized columns render as one run of text ("17:42:10GPU 0thermal throttle").
        var table = Controls.Table()
            .AddColumn("Time", TextJustification.Left, 10)
            .AddColumn("GPU", TextJustification.Left, 7)
            .AddColumn("Event", TextJustification.Left)
            // PortalPanel draws the rounded border; an inner border would double it.
            .NoBorder()
            .WithVerticalAlignment(VerticalAlignment.Fill);

        foreach (var (time, gpu, text) in rows)
            table.AddRow(time, gpu, text);

        var content = new PortalPanel(table.Build(), rect, PortalHost.Border, PortalHost.Surface);

        _open = ws.DesktopPortalService.CreatePortal(new DesktopPortalOptions(
            Content: content,
            Bounds: rect,
            DismissOnClickOutside: true,
            // DimBackground stays false (the default): the graphs behind the portal remain readable.
            OnDismiss: () =>
            {
                _open = null;
                PortalHost.NotifyDismissed(typeof(AlertPortal));
            }));
    }

    /// <summary>Closes the portal if open. Used on shutdown so it cannot outlive the window.</summary>
    public static void Close(ConsoleWindowSystem ws)
    {
        if (_open == null) return;
        ws.DesktopPortalService.RemovePortal(_open);
        _open = null;
    }

    // Placeholder content, replaced by the engine's event list in step 3. Deliberately shaped like
    // real events (a resolved one included) so the layout is exercised at its real width.
    private static List<(string Time, string Gpu, string Text)> StubRows()
    {
        var muted = UIConstants.MutedText.ToMarkup();
        var critical = UIConstants.Critical.ToMarkup();
        var warning = UIConstants.Warning.ToMarkup();

        return new List<(string, string, string)>
        {
            ("17:42:10", "GPU 0", $"[{critical}]thermal throttle[/]"),
            ("17:39:55", "GPU 1", $"[{warning}]power cap[/]"),
            ("17:31:02", "GPU 0", $"[{muted}]VRAM 94% — resolved[/]"),
        };
    }
}
