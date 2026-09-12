using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace Argus.Core;

/// <summary>
/// The notification-area icon, and the only way back to Argus when it is
/// sitting on the desktop layer behind everything else.
///
/// Built on Shell_NotifyIcon directly. The WinForms NotifyIcon would do the
/// same job, but dragging in System.Windows.Forms for it made that assembly
/// the largest module in the process once the GPU drivers were gone. The menu
/// is a WPF ContextMenu, so it also inherits the panel's dark styling instead
/// of being a stock light-grey shell menu next to it.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int IconId = 1;

    private readonly HwndSource _sink;
    private readonly ContextMenu _menu;
    private readonly MenuItem _overlayItem;
    private readonly Action _onShow;

    private IntPtr _hIcon;
    private bool _added;

    /// <summary>Explorer broadcasts this when it restarts, so the icon can be re-added.</summary>
    private readonly int _taskbarCreated;

    public TrayIcon(Action onShow, Action onToggleOverlay, Action onNextScreen,
                    Action onSettings, Action onQuit)
    {
        _onShow = onShow;

        // A message-only window: never visible, never in the Z-order, exists
        // purely to receive the tray callback.
        _sink = new HwndSource(new HwndSourceParameters("ArgusTraySink")
        {
            ParentWindow = new IntPtr(Native.HWND_MESSAGE),
            Width = 1,
            Height = 1,
        });
        _sink.AddHook(WndProc);

        _taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");

        _overlayItem = NewItem("Bring to front", onToggleOverlay);

        _menu = new ContextMenu();
        _menu.Items.Add(NewItem("Show Argus", onShow, bold: true));
        _menu.Items.Add(_overlayItem);
        _menu.Items.Add(NewItem("Move to next screen", onNextScreen));
        _menu.Items.Add(new Separator());
        _menu.Items.Add(NewItem("Settings", onSettings));
        _menu.Items.Add(new Separator());
        _menu.Items.Add(NewItem("Shut Argus down", onQuit));

        Add();
    }

    private static MenuItem NewItem(string header, Action action, bool bold = false)
    {
        var item = new MenuItem { Header = header };
        if (bold) item.FontWeight = FontWeights.SemiBold;
        item.Click += (_, _) => action();
        return item;
    }

    private void Add()
    {
        _hIcon = LoadOwnIcon();

        var data = NewData();
        data.uFlags = Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP;
        data.uCallbackMessage = Native.WM_TRAYICON;
        data.hIcon = _hIcon;
        data.szTip = "Argus";

        _added = Native.Shell_NotifyIcon(Native.NIM_ADD, ref data);
        if (!_added) Log.Warn("tray icon could not be added");
    }

    private Native.NOTIFYICONDATA NewData() => new()
    {
        cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.NOTIFYICONDATA>(),
        hWnd = _sink.Handle,
        uID = IconId,
    };

    private static IntPtr LoadOwnIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                var small = new IntPtr[1];
                // The small icon is the one the tray wants; extracting only
                // that avoids materialising the 256px image for nothing.
                if (Native.ExtractIconEx(exe, 0, null, small, 1) > 0 && small[0] != IntPtr.Zero)
                    return small[0];
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"tray icon not loaded from exe: {ex.Message}");
        }

        return IntPtr.Zero;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == _taskbarCreated && _taskbarCreated != 0)
        {
            // Explorer restarted and took every tray icon with it.
            _added = false;
            Add();
            handled = true;
            return IntPtr.Zero;
        }

        if (msg != Native.WM_TRAYICON) return IntPtr.Zero;

        switch ((int)lParam)
        {
            case Native.WM_LBUTTONUP:
            case Native.WM_LBUTTONDBLCLK:
                _onShow();
                handled = true;
                break;

            case Native.WM_RBUTTONUP:
                ShowMenu();
                handled = true;
                break;
        }

        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        // Without this the menu opens but will not close when the user clicks
        // away from it, because no window owning the menu holds the foreground.
        Native.SetForegroundWindow(_sink.Handle);

        _menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        _menu.IsOpen = true;
    }

    /// <summary>Keeps the menu wording honest about what the toggle will do.</summary>
    public void SetOverlayState(bool isOverlay) =>
        _overlayItem.Header = isOverlay ? "Send back to desktop" : "Bring to front";

    public void Dispose()
    {
        if (_added)
        {
            // Without this the icon stays in the tray as a dead ghost until the
            // user hovers over it, which would be a poor showing for this app.
            var data = NewData();
            Native.Shell_NotifyIcon(Native.NIM_DELETE, ref data);
            _added = false;
        }

        if (_hIcon != IntPtr.Zero)
        {
            Native.DestroyIcon(_hIcon);
            _hIcon = IntPtr.Zero;
        }

        _sink.RemoveHook(WndProc);
        _sink.Dispose();
    }
}
