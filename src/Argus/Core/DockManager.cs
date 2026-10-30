using System.Windows;
using System.Windows.Interop;

namespace Argus.Core;

/// <summary>
/// Everything about where Argus sits and what it sits behind.
///
/// The three modes are genuinely different mechanisms, not three settings on
/// one mechanism:
///
///   Desktop  The window is forced to the bottom of the Z-order and re-forced
///            every time anything tries to move it. Other windows open over it
///            exactly as they open over desktop icons. This is the default
///            because it is what a pinned desktop widget should do.
///
///   Overlay  Topmost. For when you want to read it without minimising work.
///
///   Reserve  A registered Windows appbar. Windows permanently shrinks the
///            desktop work area, so a maximised window physically stops at the
///            panel edge. Same mechanism the taskbar uses. Costs real width.
///
/// The desktop mode is deliberately NOT the WorkerW reparenting trick that
/// wallpaper tools use. Reparenting into the shell breaks per-monitor DPI and
/// makes coordinates relative to a window we do not own - on a machine with
/// two different scale factors that is a guaranteed source of blurry,
/// mis-placed panels. Bottom-of-Z-order gets the same result and stays a
/// normal, properly scaled, clickable top-level window.
///
/// PLACEMENT RULE: wherever the user drags or resizes the panel to is where it
/// stays, exactly, until they move it again. Argus does not re-snap it to an
/// edge behind their back. The edge calculation below is used only to pick a
/// sensible starting rectangle - first run, a hop to another display, or the
/// rescue when a remembered position no longer lands on any attached monitor.
///
/// EVERY coordinate in this file is a physical pixel. WPF's Left, Top,
/// ActualWidth and ActualHeight are device-independent units, and mixing the
/// two is not a rounding error: at 250% scale a window physically at x=2995
/// reports Left around 1198, which is how an earlier version of this class
/// decided a panel on the right of the screen belonged on the left.
/// </summary>
public sealed class DockManager
{
    private readonly Window _window;
    private readonly ArgusSettings _settings;

    private IntPtr _hwnd;
    private HwndSource? _source;
    private bool _appBarRegistered;
    private int _appBarCallbackMessage;

    /// <summary>Set while we are moving ourselves, so our own moves are not fought or recorded.</summary>
    private bool _positioning;

    public DockManager(Window window, ArgusSettings settings)
    {
        _window = window;
        _settings = settings;
    }

    /// <summary>Raised after the user finishes dragging or resizing.</summary>
    public event Action? PlacementChanged;

    public void Attach()
    {
        _hwnd = new WindowInteropHelper(_window).Handle;
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);

        // A tool window stays out of Alt-Tab and off the taskbar. The tray icon
        // is how Argus is reached, not a taskbar button.
        var ex = Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE);
        ex |= Native.WS_EX_TOOLWINDOW;
        ex &= ~Native.WS_EX_APPWINDOW;
        Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE, ex);

        ApplyOpacity();
        Apply();
    }

    public void ApplyOpacity()
    {
        // Straight through WPF now. The native layered-window route was tried
        // first and silently failed: WPF clears WS_EX_LAYERED whenever
        // AllowsTransparency is false, so the call returned without error and
        // changed nothing. With AllowsTransparency true, WPF owns the layered
        // window and Window.Opacity is the supported way in.
        _window.Opacity = Math.Clamp(_settings.Opacity, 0.2, 1.0);
    }

    /// <summary>Applies the current mode and position. Safe to call repeatedly.</summary>
    public void Apply()
    {
        if (_hwnd == IntPtr.Zero) return;

        if (_settings.DockMode != DockMode.Reserve && _appBarRegistered) UnregisterAppBar();

        if (_settings.DockMode == DockMode.Reserve)
        {
            // An appbar has to be a full-height strip; the shell has no notion
            // of reserving space around a floating rectangle.
            SetTopmost(false);
            RegisterAppBar();
            return;
        }

        MoveTo(ResolveRect());

        if (_settings.DockMode == DockMode.Overlay) SetTopmost(true);
        else { SetTopmost(false); SendToBottom(); }
    }

    /// <summary>
    /// Where the panel should be right now: the exact rectangle the user last
    /// left it at, if that is still somewhere they can see it, otherwise a
    /// freshly computed edge dock.
    /// </summary>
    private Native.RECT ResolveRect()
    {
        if (_settings.HasPlacement)
        {
            var saved = new Native.RECT
            {
                Left = _settings.PlacedLeft,
                Top = _settings.PlacedTop,
                Right = _settings.PlacedLeft + _settings.PlacedWidth,
                Bottom = _settings.PlacedTop + _settings.PlacedHeight,
            };

            if (IsReachable(saved)) return saved;

            // A display was switched off, or the laptop undocked, and the
            // remembered rectangle now sits in dead space. Falling back beats
            // leaving the panel somewhere the user cannot reach it.
            Log.Info("saved placement is off-screen; re-docking to the edge");
        }

        return EdgeRect();
    }

    /// <summary>
    /// True when enough of the rectangle overlaps an attached display for the
    /// user to grab it. A sliver on screen is as bad as none.
    /// </summary>
    private static bool IsReachable(Native.RECT r)
    {
        const int needed = 120;

        foreach (var m in Monitors.All())
        {
            var overlapX = Math.Min(r.Right, m.Bounds.Right) - Math.Max(r.Left, m.Bounds.Left);
            var overlapY = Math.Min(r.Bottom, m.Bounds.Bottom) - Math.Max(r.Top, m.Bounds.Top);
            if (overlapX >= needed && overlapY >= needed) return true;
        }

        return false;
    }

    /// <summary>
    /// A sensible default rectangle: full working height against the chosen
    /// edge of the chosen display. Used on first run, for the screen hop, and
    /// as the rescue when a remembered position is no longer on any monitor.
    ///
    /// The width in settings is logical, so it is scaled by the DPI of the
    /// destination monitor. The panel then looks the same size to the eye on
    /// both displays rather than being the same pixel count on each.
    /// </summary>
    private Native.RECT EdgeRect()
    {
        var screen = Monitors.At(_settings.ScreenIndex);
        var area = screen.WorkArea;
        var scale = screen.Scale;

        var minWidth = (int)(160 * scale);
        var width = (int)Math.Round(_settings.Width * scale);
        var mTop = (int)Math.Round(_settings.MarginTop * scale);
        var mBottom = (int)Math.Round(_settings.MarginBottom * scale);
        var mEdge = (int)Math.Round(_settings.MarginEdge * scale);

        width = Math.Clamp(width, minWidth, Math.Max(minWidth, area.Width - mEdge * 2));

        var height = Math.Clamp(area.Height - mTop - mBottom, (int)(120 * scale), area.Height);
        var top = area.Top + mTop;

        var left = _settings.Edge == PanelEdge.Right
            ? area.Right - width - mEdge
            : area.Left + mEdge;

        return new Native.RECT { Left = left, Top = top, Right = left + width, Bottom = top + height };
    }

    private void MoveTo(Native.RECT r)
    {
        _positioning = true;
        try
        {
            Native.SetWindowPos(_hwnd, IntPtr.Zero, r.Left, r.Top, r.Width, r.Height,
                (uint)(Native.SWP.NOZORDER | Native.SWP.NOACTIVATE));
        }
        finally { _positioning = false; }
    }

    private void SetTopmost(bool on)
    {
        Native.SetWindowPos(_hwnd, on ? Native.HWND_TOPMOST : Native.HWND_NOTOPMOST,
            0, 0, 0, 0,
            (uint)(Native.SWP.NOMOVE | Native.SWP.NOSIZE | Native.SWP.NOACTIVATE));
    }

    private void SendToBottom()
    {
        Native.SetWindowPos(_hwnd, Native.HWND_BOTTOM, 0, 0, 0, 0,
            (uint)(Native.SWP.NOMOVE | Native.SWP.NOSIZE | Native.SWP.NOACTIVATE));
    }

    // ------------------------------------------------------------- placement

    internal MonitorInfo TargetScreen
    {
        get
        {
            // When the user has placed the panel by hand, the display it is
            // actually sitting on is the truth, not a stale index in settings.
            if (_settings.HasPlacement && _hwnd != IntPtr.Zero &&
                Native.GetWindowRect(_hwnd, out var r) && r.Width > 0)
            {
                return Monitors.Containing(r.Left + r.Width / 2, r.Top + r.Height / 2);
            }

            return Monitors.At(_settings.ScreenIndex);
        }
    }

    public int ScreenCount => Monitors.All().Count;

    /// <summary>
    /// Records exactly where the user left the panel, in physical pixels.
    /// Called when a drag or resize finishes, never while one is in progress.
    /// </summary>
    public void CapturePlacement()
    {
        if (_hwnd == IntPtr.Zero || _positioning) return;
        if (_settings.DockMode == DockMode.Reserve) return;   // the shell owns the rect
        if (!Native.GetWindowRect(_hwnd, out var r)) return;
        if (r.Width < 80 || r.Height < 80) return;            // minimised or mid-animation

        _settings.PlacedLeft = r.Left;
        _settings.PlacedTop = r.Top;
        _settings.PlacedWidth = r.Width;
        _settings.PlacedHeight = r.Height;
        _settings.HasPlacement = true;

        // Keep the logical width and screen index in step, so the width slider
        // and the screen hop still start from what the user can actually see.
        var screen = Monitors.Containing(r.Left + r.Width / 2, r.Top + r.Height / 2);
        var all = Monitors.All();
        var index = all.FindIndex(m => m.Device == screen.Device);

        _settings.Width = Math.Round(r.Width / screen.Scale);
        _settings.ScreenIndex = index < 0 ? 0 : index;
        _settings.Edge = r.Left + r.Width / 2 > screen.Bounds.Left + screen.Bounds.Width / 2
            ? PanelEdge.Right
            : PanelEdge.Left;

        _settings.Save();
        PlacementChanged?.Invoke();
    }

    /// <summary>
    /// Moves to the next display. On a dual-screen laptop this is the
    /// one-button hop between the main panel and the second one.
    ///
    /// Deliberately discards the remembered rectangle: a position and height
    /// that suited a 2160-tall display means nothing on a 1571-tall one.
    /// </summary>
    public void CycleScreen()
    {
        var all = Monitors.All();
        if (all.Count <= 1) return;

        var current = TargetScreen;
        var index = all.FindIndex(m => m.Device == current.Device);
        _settings.ScreenIndex = ((index < 0 ? 0 : index) + 1) % all.Count;

        _settings.HasPlacement = false;
        _settings.Save();

        Apply();
        CapturePlacement();
    }

    /// <summary>Re-docks to an edge of the current display, abandoning the free position.</summary>
    public void RedockToEdge(PanelEdge edge)
    {
        var current = TargetScreen;
        var all = Monitors.All();
        var index = all.FindIndex(m => m.Device == current.Device);

        _settings.ScreenIndex = index < 0 ? 0 : index;
        _settings.Edge = edge;
        _settings.HasPlacement = false;
        _settings.Save();

        Apply();
        CapturePlacement();
    }

    /// <summary>
    /// Applies a width from the settings slider, keeping the panel against
    /// whichever side it is already nearest so it does not appear to jump.
    /// </summary>
    public void ApplyWidth(double logicalWidth)
    {
        if (_hwnd == IntPtr.Zero) return;
        if (_settings.DockMode == DockMode.Reserve) { SetAppBarPosition(); return; }
        if (!Native.GetWindowRect(_hwnd, out var r)) return;

        var screen = Monitors.Containing(r.Left + r.Width / 2, r.Top + r.Height / 2);
        var width = (int)Math.Round(logicalWidth * screen.Scale);

        var anchoredRight = r.Left + r.Width / 2 > screen.Bounds.Left + screen.Bounds.Width / 2;
        var left = anchoredRight ? r.Right - width : r.Left;

        MoveTo(new Native.RECT
        {
            Left = left,
            Top = r.Top,
            Right = left + width,
            Bottom = r.Bottom,
        });

        CapturePlacement();
    }

    // --------------------------------------------------------------- appbar

    private void RegisterAppBar()
    {
        if (_appBarRegistered) return;

        _appBarCallbackMessage = Native.RegisterWindowMessage("ArgusAppBarMessage");

        var data = new Native.APPBARDATA
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.APPBARDATA>(),
            hWnd = _hwnd,
            uCallbackMessage = (uint)_appBarCallbackMessage,
        };

        if (Native.SHAppBarMessage(Native.ABM_NEW, ref data) == IntPtr.Zero)
        {
            Log.Warn("appbar registration refused; staying in desktop mode");
            _settings.DockMode = DockMode.Desktop;
            MoveTo(ResolveRect());
            SendToBottom();
            return;
        }

        _appBarRegistered = true;
        SetAppBarPosition();
    }

    private void SetAppBarPosition()
    {
        if (!_appBarRegistered) return;

        var screen = Monitors.At(_settings.ScreenIndex);
        var width = (int)Math.Round(_settings.Width * screen.Scale);

        var data = new Native.APPBARDATA
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.APPBARDATA>(),
            hWnd = _hwnd,
            uEdge = _settings.Edge == PanelEdge.Right ? Native.ABE_RIGHT : Native.ABE_LEFT,
            rc = new Native.RECT
            {
                Top = screen.Bounds.Top,
                Bottom = screen.Bounds.Bottom,
                Left = _settings.Edge == PanelEdge.Right ? screen.Bounds.Right - width : screen.Bounds.Left,
                Right = _settings.Edge == PanelEdge.Right ? screen.Bounds.Right : screen.Bounds.Left + width,
            },
        };

        // The shell gets to veto the rectangle, then hands back what it will
        // actually allow. Ignoring the reply is how appbars end up overlapping
        // the taskbar.
        Native.SHAppBarMessage(Native.ABM_QUERYPOS, ref data);
        Native.SHAppBarMessage(Native.ABM_SETPOS, ref data);

        MoveTo(data.rc);
    }

    private void UnregisterAppBar()
    {
        if (!_appBarRegistered) return;

        var data = new Native.APPBARDATA
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.APPBARDATA>(),
            hWnd = _hwnd,
        };

        Native.SHAppBarMessage(Native.ABM_REMOVE, ref data);
        _appBarRegistered = false;
    }

    // --------------------------------------------------------------- wndproc

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case Native.WM_WINDOWPOSCHANGING when _settings.DockMode == DockMode.Desktop && !_positioning:
            {
                // This is what makes desktop mode stick. Anything that tries to
                // raise the window - a click, a focus steal, another app calling
                // BringWindowToTop - gets rewritten to "insert after bottom"
                // before it takes effect.
                var pos = System.Runtime.InteropServices.Marshal
                    .PtrToStructure<Native.WINDOWPOS>(lParam);

                pos.hwndInsertAfter = Native.HWND_BOTTOM;
                pos.flags &= ~(uint)Native.SWP.NOZORDER;

                System.Runtime.InteropServices.Marshal.StructureToPtr(pos, lParam, false);
                break;
            }

            case Native.WM_NCHITTEST when _settings.DockMode != DockMode.Reserve:
            {
                // A borderless window has no resize frame, and AllowsTransparency
                // removes the one WPF would otherwise fake for ResizeMode, so
                // "drag an edge to resize" was untrue: there was no edge. Width
                // still moved through the slider, which hid it; height could not
                // move at all. This hands Windows a resize band around the rim.
                var hit = ResizeHit(lParam);
                if (hit != 0) { handled = true; return hit; }
                break;
            }

            case Native.WM_EXITSIZEMOVE:
                // The one honest moment to record placement: the user has let
                // go. Recording during the drag would write hundreds of times a
                // second and save wherever the pointer happened to be mid-gesture.
                CapturePlacement();
                break;

            case Native.WM_DISPLAYCHANGE:
                // Undocking, or a display being switched off. Recompute
                // rather than hang off a screen that no longer exists.
                _window.Dispatcher.BeginInvoke(new Action(Apply));
                break;

            case Native.WM_DPICHANGED:
            {
                // Windows hands back the rectangle the window should occupy at
                // the new scale. Honouring it is what keeps the panel crisp and
                // correctly sized when it crosses between the two displays.
                if (!_positioning)
                {
                    var suggested = System.Runtime.InteropServices.Marshal
                        .PtrToStructure<Native.RECT>(lParam);
                    MoveTo(suggested);
                    CapturePlacement();
                }
                handled = true;
                break;
            }

            default:
                if (_appBarRegistered && msg == _appBarCallbackMessage)
                {
                    _window.Dispatcher.BeginInvoke(new Action(SetAppBarPosition));
                }
                break;
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// The hit-test code for a point on the rim, or zero for the interior.
    /// lParam carries the cursor in physical screen pixels, the same space as
    /// GetWindowRect, so the band is scaled by DPI to stay a grabbable 6dip.
    /// </summary>
    private IntPtr ResizeHit(IntPtr lParam)
    {
        if (!Native.GetWindowRect(_hwnd, out var r)) return IntPtr.Zero;

        // Sign-extend each half: on a monitor left of the primary, x is negative.
        var x = (short)((long)lParam & 0xFFFF);
        var y = (short)(((long)lParam >> 16) & 0xFFFF);

        var screen = Monitors.Containing(r.Left + r.Width / 2, r.Top + r.Height / 2);
        var band = (int)Math.Ceiling(6 * screen.Scale);

        var left = x < r.Left + band;
        var right = x >= r.Right - band;
        var top = y < r.Top + band;
        var bottom = y >= r.Bottom - band;

        var code = (top, bottom, left, right) switch
        {
            (true, _, true, _) => Native.HTTOPLEFT,
            (true, _, _, true) => Native.HTTOPRIGHT,
            (_, true, true, _) => Native.HTBOTTOMLEFT,
            (_, true, _, true) => Native.HTBOTTOMRIGHT,
            (true, _, _, _) => Native.HTTOP,
            (_, true, _, _) => Native.HTBOTTOM,
            (_, _, true, _) => Native.HTLEFT,
            (_, _, _, true) => Native.HTRIGHT,
            _ => 0,
        };
        return code;
    }

    public void Detach()
    {
        UnregisterAppBar();
        _source?.RemoveHook(WndProc);
        _source = null;
    }
}
