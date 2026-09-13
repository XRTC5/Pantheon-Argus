using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Argus.Core;

namespace Argus.Views;

/// <summary>
/// The floating window that shows what one row is actually made of.
///
/// One window, reused. Opening a breakdown for another row re-points this one
/// rather than stacking a second: the question is always "what is under the
/// thing I am looking at now", and a pile of near-identical windows is its own
/// small mess to clear up.
///
/// Where the user leaves it is where it comes back, the same rule the panel
/// follows. A window that reopens in the middle of the screen every time is a
/// window that has to be moved every time.
/// </summary>
public partial class BreakdownWindow : Window
{
    private readonly ArgusSettings _settings;

    public BreakdownWindow(DetailViewModel model, ArgusSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        DataContext = model;

        if (settings.BreakdownWidth > 0) Width = settings.BreakdownWidth;
        if (settings.BreakdownHeight > 0) Height = settings.BreakdownHeight;

        // Esc closes it. A window with no title bar and one button should still
        // answer to the key every other dismissable thing answers to.
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Hide(); e.Handled = true; }
        };

        LocationChanged += (_, _) => Remember();
        SizeChanged += (_, _) => Remember();
    }

    /// <summary>
    /// Puts the window where it was last left, or beside the panel the first
    /// time, on the side with more room.
    /// </summary>
    public void PlaceNear(Window panel)
    {
        if (_settings.HasBreakdownPlacement)
        {
            Left = _settings.BreakdownLeft;
            Top = _settings.BreakdownTop;
            if (IsOnAScreen()) return;

            // The screen it was left on is gone. Fall through and place it
            // beside the panel rather than opening it off the desktop.
        }

        var gap = 8d;
        var leftOfPanel = panel.Left - Width - gap;

        Left = leftOfPanel >= 0 ? leftOfPanel : panel.Left + panel.Width + gap;
        Top = Math.Max(0, panel.Top + 40);
    }

    /// <summary>
    /// True when the remembered position still lands on a connected display.
    /// Unplugging a monitor is otherwise how a window becomes unreachable.
    /// </summary>
    private bool IsOnAScreen()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var x = (int)(Left * dpi.DpiScaleX);
        var y = (int)(Top * dpi.DpiScaleY);

        foreach (var monitor in Monitors.All())
        {
            // A corner is enough: a window whose top-left is on a screen can be
            // grabbed and moved, which is all this needs to guarantee.
            var bounds = monitor.Bounds;
            if (x >= bounds.Left && x < bounds.Right - 40 &&
                y >= bounds.Top && y < bounds.Bottom - 40) return true;
        }

        return false;
    }

    private void Remember()
    {
        if (!IsLoaded || WindowState != WindowState.Normal) return;

        _settings.HasBreakdownPlacement = true;
        _settings.BreakdownLeft = Left;
        _settings.BreakdownTop = Top;
        _settings.BreakdownWidth = Width;
        _settings.BreakdownHeight = Height;

        // Kept in memory, written on exit. A save here would be a file write
        // per pixel of a drag; the cost of that trade is that a crash or a
        // kill loses the last move, the same exposure the panel's own
        // placement already has.
    }

    private void Header_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        try { DragMove(); }
        catch (InvalidOperationException) { /* mouse already released */ }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Hide();
}
