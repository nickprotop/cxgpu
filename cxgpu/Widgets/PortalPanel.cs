using SharpConsoleUI;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Drawing;
using SharpConsoleUI.Events;
using SharpConsoleUI.Layout;

namespace cxgpu.Widgets;

/// <summary>
/// A bordered container for desktop-portal content: hosts one arbitrary control and draws the frame
/// around it.
///
/// Subclassing <see cref="PortalContentBase"/> is the framework's supported way to give a portal
/// laid-out content — its PaintDOM measures the hosted <see cref="PortalContentBase.Content"/> with
/// TIGHT constraints so the child fills the border-shrunk area. A plain container inside a portal
/// collapses its child instead, which renders as an empty box.
/// </summary>
internal sealed class PortalPanel : PortalContentBase, IInteractiveControl
{
    private readonly System.Drawing.Rectangle _bounds;

    public PortalPanel(IWindowControl content, System.Drawing.Rectangle bounds,
                       Color border, Color background)
    {
        _bounds = bounds;
        BorderStyle = BoxChars.Rounded;
        BorderColor = border;
        BorderBackgroundColor = background;
        Content = content;

        // Without this the hosted control never sees a key: a focusable control ignores input unless
        // it has focus, so arrow navigation and Enter would silently do nothing.
        if (content is IFocusableControl focusable)
            PortalFocusedControl = focusable;
    }

    public override System.Drawing.Rectangle GetPortalBounds() => _bounds;

    // The base does NOT auto-forward mouse events to the hosted child, so clicking a row inside the
    // portal would do nothing without this. The base applies the border offset.
    public override bool ProcessMouseEvent(MouseEventArgs args) => ProcessHostedMouseEvent(args);

    /// <summary>
    /// Keys the hosted control does not consume return false, which lets the framework fall them
    /// through to global shortcuts — so the app's own hotkeys keep working while a portal is open.
    /// </summary>
    public bool ProcessKey(ConsoleKeyInfo key) =>
        Content is IInteractiveControl interactive && interactive.ProcessKey(key);

    public bool IsEnabled { get; set; } = true;

    // The border and the hosted child are painted by the base; there is no extra chrome to draw.
    protected override void PaintPortalContent(CharacterBuffer buffer, LayoutRect bounds,
                                               LayoutRect clipRect, Color defaultFg, Color defaultBg)
    {
    }
}
