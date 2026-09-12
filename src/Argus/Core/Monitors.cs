namespace Argus.Core;

/// <summary>One display, in physical pixels.</summary>
internal sealed record MonitorInfo
{
    public required Native.RECT Bounds { get; init; }
    public required Native.RECT WorkArea { get; init; }
    public required bool IsPrimary { get; init; }
    public required string Device { get; init; }

    /// <summary>Scale factor of this display. Two displays often differ.</summary>
    public double Scale { get; init; } = 1.0;

    public int Width => Bounds.Width;
    public int Height => Bounds.Height;
}

/// <summary>
/// Display enumeration, replacing System.Windows.Forms.Screen.
///
/// Worth the thirty lines: Screen was the last thing keeping the WinForms
/// assembly loaded, and EnumDisplayMonitors answers in physical pixels with
/// each monitor's own DPI attached - which is what every placement calculation
/// in Argus actually wants on a machine with two different scale factors.
/// </summary>
internal static class Monitors
{
    public static List<MonitorInfo> All()
    {
        var found = new List<MonitorInfo>(2);

        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr _, ref Native.RECT _, IntPtr _) =>
        {
            var info = new Native.MONITORINFOEX
            {
                cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFOEX>(),
            };

            if (Native.GetMonitorInfo(hMonitor, ref info))
            {
                var scale = 1.0;
                if (Native.GetDpiForMonitor(hMonitor, Native.MONITOR_DPI_TYPE.EFFECTIVE,
                                            out var dpiX, out _) == 0 && dpiX > 0)
                {
                    scale = dpiX / 96.0;
                }

                found.Add(new MonitorInfo
                {
                    Bounds = info.rcMonitor,
                    WorkArea = info.rcWork,
                    IsPrimary = (info.dwFlags & Native.MONITORINFOF_PRIMARY) != 0,
                    Device = info.szDevice,
                    Scale = scale,
                });
            }

            return true;
        }, IntPtr.Zero);

        if (found.Count == 0)
        {
            // A machine with no enumerable display should be impossible, but a
            // placement routine that divides by zero here would take the panel
            // down on a docking glitch.
            found.Add(new MonitorInfo
            {
                Bounds = new Native.RECT { Left = 0, Top = 0, Right = 1280, Bottom = 720 },
                WorkArea = new Native.RECT { Left = 0, Top = 0, Right = 1280, Bottom = 720 },
                IsPrimary = true,
                Device = "fallback",
            });
        }

        // Primary first, then left-to-right, so ScreenIndex stays stable across
        // reboots rather than following enumeration order.
        found.Sort((a, b) =>
            a.IsPrimary != b.IsPrimary
                ? (a.IsPrimary ? -1 : 1)
                : a.Bounds.Left.CompareTo(b.Bounds.Left));

        return found;
    }

    public static MonitorInfo At(int index)
    {
        var all = All();
        return all[Math.Clamp(index, 0, all.Count - 1)];
    }

    public static MonitorInfo Containing(int x, int y)
    {
        foreach (var m in All())
        {
            if (x >= m.Bounds.Left && x < m.Bounds.Right &&
                y >= m.Bounds.Top && y < m.Bounds.Bottom)
            {
                return m;
            }
        }
        return At(0);
    }

    public static int IndexContaining(int x, int y)
    {
        var all = All();
        for (var i = 0; i < all.Count; i++)
        {
            var m = all[i];
            if (x >= m.Bounds.Left && x < m.Bounds.Right &&
                y >= m.Bounds.Top && y < m.Bounds.Bottom)
            {
                return i;
            }
        }
        return 0;
    }
}
