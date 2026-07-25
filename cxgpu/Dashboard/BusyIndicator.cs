using SharpConsoleUI;
using SharpConsoleUI.Core;
using SharpConsoleUI.Drawing;
using SharpConsoleUI.Helpers;
using SharpConsoleUI.Layout;

namespace cxgpu.Dashboard;

/// <summary>
/// Shows a transient "working" toast around an operation slow enough to notice.
///
/// Switching GPU in the Overview costs roughly 390 ms on the development box, essentially all of it
/// blocking subprocess work: one snapshot means four nvidia-smi invocations plus the AMD reads, and a
/// switch needs several of those plus the device info. Caching them was tried and rejected — a
/// monitoring tool that shows stale numbers is worse than one that visibly takes a moment, and that
/// is exactly what the cache did when its invalidation had a bug. So the work stays honest and the UI
/// says it is happening.
///
/// Uses ToastService, NOT NotificationStateService: the latter creates a real window and calls
/// SetActiveWindow, which would steal focus and pop a dialog for something that is only a status
/// message. A toast is a lightweight render overlay — the right weight for "this is in progress".
/// </summary>
internal sealed class BusyIndicator
{
    // How far the view is dimmed while busy. Enough that the content visibly recedes and the toast
    // becomes the focal point, not so much that the numbers become unreadable — they are still
    // correct, just superseded.
    private const float DimIntensity = 0.45f;

    private readonly ConsoleWindowSystem _windowSystem;
    private string? _activeId;

    // The dim is a paint-time overlay held for the duration of the work, using the same
    // PostBufferPaint hook the window fade animations use — so it composites over the finished frame
    // rather than requiring every control to know it is disabled.
    private Window? _dimmedWindow;
    private SharpConsoleUI.Windows.WindowRenderer.BufferPaintDelegate? _dimHandler;

    public BusyIndicator(ConsoleWindowSystem windowSystem) => _windowSystem = windowSystem;

    /// <summary>
    /// Runs <paramref name="work"/> with a busy toast visible in the bottom-right corner.
    ///
    /// Everything happens on the UI thread, which is the honest arrangement: the work IS synchronous
    /// (blocking vendor-tool reads), so pretending otherwise would add complexity without making it
    /// faster. The toast exists so the pause is explained rather than mysterious.
    /// </summary>
    public void Run(string message, Action work)
    {
        Dim();
        Show(message);

        // The work MUST be deferred, not called here. The renderer only paints between UI-thread
        // callbacks, so doing Show -> work -> Hide inline means no frame is ever produced with the
        // toast and dim visible: the screen would freeze for the duration and then jump straight to
        // the finished state, which is precisely the behaviour the indicator exists to replace.
        //
        // Queuing the work lets the current callback return, the frame render with the indicator up,
        // and the expensive part run on the next turn.
        _windowSystem.EnqueueOnUIThread(() =>
        {
            try
            {
                work();
            }
            finally
            {
                // finally, so a throwing operation cannot leave the view dimmed or the toast stuck.
                Hide();
                Undim();
            }
        });
    }

    /// <summary>
    /// Dims the main window by compositing a translucent black overlay after the frame is painted.
    /// Uses the same PostBufferPaint hook as the window fade animations, so nothing in the control
    /// tree needs to know about it.
    /// </summary>
    private void Dim()
    {
        Undim();

        var window = _windowSystem.Windows.Values.FirstOrDefault();
        if (window == null) return;

        void Overlay(CharacterBuffer buffer, LayoutRect dirtyRegion, LayoutRect clipRect) =>
            ColorBlendHelper.ApplyColorOverlay(buffer, Color.Black, DimIntensity);

        try
        {
            _dimHandler = Overlay;
            _dimmedWindow = window;
            window.PostBufferPaint += Overlay;
            window.Invalidate(Invalidation.Repaint);
        }
        catch
        {
            _dimHandler = null;
            _dimmedWindow = null;
        }
    }

    private void Undim()
    {
        if (_dimmedWindow == null || _dimHandler == null) return;

        try
        {
            _dimmedWindow.PostBufferPaint -= _dimHandler;
            _dimmedWindow.Invalidate(Invalidation.Repaint);
        }
        catch
        {
        }
        finally
        {
            _dimHandler = null;
            _dimmedWindow = null;
        }
    }

    private void Show(string message)
    {
        Hide();   // never stack indicators

        try
        {
            _activeId = _windowSystem.ToastService.Show(
                message,
                NotificationSeverity.Info,
                // Sticky: dismissed explicitly when the work finishes, rather than on a guess about
                // how long it will take.
                new ToastOptions(Sticky: true, Position: ToastPosition.BottomRight));
        }
        catch
        {
            // A missing indicator must never break the operation it was describing.
            _activeId = null;
        }
    }

    private void Hide()
    {
        if (_activeId == null) return;

        try
        {
            _windowSystem.ToastService.Dismiss(_activeId);
        }
        catch
        {
        }
        finally
        {
            _activeId = null;
        }
    }
}
