using System.Runtime.InteropServices;

namespace Argus.Core;

/// <summary>
/// Every P/Invoke Argus needs, in one place.
///
/// Two rules held throughout this file:
///   1. Nothing here allocates per-tick if it can be avoided. The probe loop
///      runs every couple of seconds forever, so a sloppy array here is a
///      megabyte an hour of garbage.
///   2. Nothing here requires elevation. Argus watches; it never needs admin.
/// </summary>
internal static class Native
{
    // ---------------------------------------------------------------- windows

    public static readonly IntPtr HWND_TOP       = new(0);
    public static readonly IntPtr HWND_BOTTOM    = new(1);
    public static readonly IntPtr HWND_TOPMOST   = new(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new(-2);

    [Flags]
    public enum SWP : uint
    {
        NOSIZE         = 0x0001,
        NOMOVE         = 0x0002,
        NOZORDER       = 0x0004,
        NOREDRAW       = 0x0008,
        NOACTIVATE     = 0x0010,
        FRAMECHANGED   = 0x0020,
        SHOWWINDOW     = 0x0040,
        HIDEWINDOW     = 0x0080,
        NOOWNERZORDER  = 0x0200,
        NOSENDCHANGING = 0x0400,
    }

    public const int GWL_EXSTYLE = -20;
    public const int GWL_STYLE   = -16;

    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_APPWINDOW  = 0x00040000;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_TOPMOST    = 0x00000008;
    public const int WS_EX_LAYERED    = 0x00080000;

    public const uint LWA_ALPHA = 0x00000002;

    /// <summary>
    /// Whole-window alpha without WPF AllowsTransparency.
    ///
    /// This matters for cost, not looks. Setting AllowsTransparency on a WPF
    /// window drops it to software rendering for its entire life, which on a
    /// panel that is always on screen is a permanent tax on the CPU. A layered
    /// window gets the same translucency and keeps hardware acceleration.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    public const int WM_WINDOWPOSCHANGING = 0x0046;
    public const int WM_EXITSIZEMOVE      = 0x0232;
    public const int WM_DISPLAYCHANGE     = 0x007E;
    public const int WM_DPICHANGED        = 0x02E0;
    public const int WM_SETTINGCHANGE     = 0x001A;

    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWPOS
    {
        public IntPtr hwnd;
        public IntPtr hwndInsertAfter;
        public int x, y, cx, cy;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width  => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
                                           int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    public static int GetWindowLong(IntPtr hWnd, int nIndex)
        => (int)(long)GetWindowLongPtr64(hWnd, nIndex);

    public static void SetWindowLong(IntPtr hWnd, int nIndex, int value)
        => SetWindowLongPtr64(hWnd, nIndex, new IntPtr(value));

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    /// <summary>The window rectangle in PHYSICAL pixels, which is the only unit placement uses.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW")]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    // ---------------------------------------------------------------- appbar
    // Only used by the optional "reserve the strip" dock mode. Same mechanism
    // the taskbar itself uses to carve space out of the desktop work area.

    public const int ABM_NEW      = 0x0;
    public const int ABM_REMOVE   = 0x1;
    public const int ABM_QUERYPOS = 0x2;
    public const int ABM_SETPOS   = 0x3;

    public const int ABE_LEFT   = 0;
    public const int ABE_TOP    = 1;
    public const int ABE_RIGHT  = 2;
    public const int ABE_BOTTOM = 3;

    [StructLayout(LayoutKind.Sequential)]
    public struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public int uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    [DllImport("shell32.dll", SetLastError = true)]
    public static extern IntPtr SHAppBarMessage(int dwMessage, ref APPBARDATA pData);

    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", SetLastError = true,
               CharSet = CharSet.Unicode)]
    public static extern int RegisterWindowMessage(string lpString);

    // ---------------------------------------------------------------- dpi
    // A dual-screen laptop can run its two panels at different scale factors,
    // so "what DPI" is always a question about a specific monitor, never about
    // the machine.

    public const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    public enum MONITOR_DPI_TYPE { EFFECTIVE = 0, ANGULAR = 1, RAW = 2 }

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X, Y;
        public POINT(int x, int y) { X = x; Y = y; }
    }

    [DllImport("shcore.dll")]
    public static extern int GetDpiForMonitor(IntPtr hmonitor, MONITOR_DPI_TYPE dpiType,
                                              out uint dpiX, out uint dpiY);

    /// <summary>Scale factor of the monitor containing a physical point. 1.0 at 96 DPI.</summary>
    public static double ScaleAt(int x, int y)
    {
        try
        {
            var mon = MonitorFromPoint(new POINT(x, y), MONITOR_DEFAULTTONEAREST);
            if (mon != IntPtr.Zero && GetDpiForMonitor(mon, MONITOR_DPI_TYPE.EFFECTIVE,
                                                       out var dpiX, out _) == 0 && dpiX > 0)
            {
                return dpiX / 96.0;
            }
        }
        catch { /* shcore missing is not fatal; fall through to 1.0 */ }
        return 1.0;
    }

    // ---------------------------------------------------------------- process
    // Toolhelp gives name + pid + parent pid for every process in ONE sweep
    // with no per-process handle. That is the cheap way to do this; opening
    // 300 handles every two seconds is not.

    public const uint TH32CS_SNAPPROCESS = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);

    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint PROCESS_VM_READ                   = 0x0010;
    public static readonly IntPtr INVALID_HANDLE_VALUE   = new(-1);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true,
               CharSet = CharSet.Unicode)]
    public static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags,
                                                        System.Text.StringBuilder lpExeName,
                                                        ref uint lpdwSize);

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_MEMORY_COUNTERS
    {
        public uint cb;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool K32GetProcessMemoryInfo(IntPtr hProcess,
                                                      out PROCESS_MEMORY_COUNTERS counters,
                                                      uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetProcessTimes(IntPtr hProcess, out long creation, out long exit,
                                              out long kernel, out long user);

    /// <summary>
    /// When a process started, as a raw file time, or 0 when it cannot be read.
    ///
    /// Zero is common and not an error: a process belonging to another user or
    /// to session 0 refuses the handle. Callers must treat 0 as "no opinion"
    /// rather than "started at the epoch".
    /// </summary>
    public static long CreationTimeOf(int pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (h == IntPtr.Zero) return 0;

        try
        {
            return GetProcessTimes(h, out var creation, out _, out _, out _) ? creation : 0;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    // Command line without WMI. ProcessCommandLineInformation (60) landed in
    // Windows 8.1 and works on same-user processes with only QUERY_LIMITED.
    // A Win32_Process query would cost 100-300ms a sweep; this costs microseconds.
    public const int ProcessBasicInformation       = 0;
    public const int ProcessCommandLineInformation = 60;

    [StructLayout(LayoutKind.Sequential)]
    public struct UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [DllImport("ntdll.dll")]
    public static extern int NtQueryInformationProcess(IntPtr hProcess, int infoClass,
                                                       IntPtr buffer, int bufferLength,
                                                       out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ProcessIdToSessionId(uint dwProcessId, out uint pSessionId);

    [StructLayout(LayoutKind.Sequential)]
    private struct WTS_PROCESS_INFOW
    {
        public int SessionId;
        public int ProcessId;
        public IntPtr pProcessName;
        public IntPtr pUserSid;
    }

    [DllImport("wtsapi32.dll", SetLastError = true, EntryPoint = "WTSEnumerateProcessesW")]
    private static extern bool WTSEnumerateProcesses(IntPtr server, int reserved, int version,
                                                     out IntPtr info, out int count);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    private static Dictionary<int, int>? _wtsSessions;
    private static DateTime _wtsTaken = DateTime.MinValue;
    private static readonly object WtsGate = new();

    /// <summary>
    /// Which terminal session a process belongs to. Session 0 is the service
    /// session: it has no interactive desktop, so a console there is a console
    /// that cannot be seen, however loudly the process table says one appeared.
    ///
    /// Two routes, and the cheap one is not enough. ProcessIdToSessionId reads
    /// as though it needs no rights - it takes a pid, not a handle - but it
    /// fails with access denied for exactly the processes worth asking about:
    /// anything running as SYSTEM in session 0. Believing it cost this panel
    /// the answer to its own headline question, because a failure there is
    /// indistinguishable from "unknown" and unknown got treated as ordinary.
    ///
    /// So a denial falls through to the terminal services enumeration, which
    /// reports every process on the machine with its session and needs no
    /// elevation. It costs one call for the whole table rather than one per
    /// process, so the result is cached for a few seconds and shared.
    /// </summary>
    public static int SessionOf(int pid)
    {
        if (ProcessIdToSessionId((uint)pid, out var session)) return (int)session;

        lock (WtsGate)
        {
            if (_wtsSessions is null || (DateTime.UtcNow - _wtsTaken).TotalSeconds > 5)
            {
                _wtsSessions = EnumerateSessions();
                _wtsTaken = DateTime.UtcNow;
            }

            return _wtsSessions.TryGetValue(pid, out var found) ? found : -1;
        }
    }

    private static Dictionary<int, int> EnumerateSessions()
    {
        var map = new Dictionary<int, int>(400);

        if (!WTSEnumerateProcesses(IntPtr.Zero, 0, 1, out var info, out var count)) return map;

        try
        {
            var size = Marshal.SizeOf<WTS_PROCESS_INFOW>();
            for (var i = 0; i < count; i++)
            {
                var entry = Marshal.PtrToStructure<WTS_PROCESS_INFOW>(info + i * size);
                map[entry.ProcessId] = entry.SessionId;
            }
        }
        catch
        {
            // A partial map is still better than none: every pid it did read
            // is correct.
        }
        finally
        {
            WTSFreeMemory(info);
        }

        return map;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr baseAddress,
                                                IntPtr buffer, nuint size, out nuint read);

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    // Offsets into the 64-bit PEB and RTL_USER_PROCESS_PARAMETERS. Stable
    // since Vista and not going to move; the alternative is a WMI query per
    // process, which costs more than everything else Argus does combined.
    private const int PebProcessParameters = 0x20;
    private const int ParamsCurrentDirectory = 0x38;

    /// <summary>
    /// The working directory of a live process, or empty when it cannot be
    /// read: another user, another session, or already exited.
    ///
    /// A 32-bit target is not handled - its PEB does not have this shape - but
    /// it is not detected either. The offsets below land somewhere meaningless,
    /// the read fails its own bounds check, and the answer comes back empty.
    /// That is the right outcome by accident rather than by design, and it is
    /// written down so the next person does not read the guard below as
    /// covering it. That guard is on Argus's own bitness, not the target's.
    ///
    /// This is what binds a bare claude.exe to the project it is working in.
    /// Nothing else on the process reveals it: the command line is just the
    /// exe path.
    /// </summary>
    public static string CurrentDirectory(int pid)
    {
        if (IntPtr.Size != 8) return "";

        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ, false, (uint)pid);
        if (h == IntPtr.Zero) return "";

        var scratch = Marshal.AllocHGlobal(1024);
        try
        {
            if (NtQueryInformationProcess(h, ProcessBasicInformation, scratch,
                    Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _) != 0) return "";

            var peb = Marshal.PtrToStructure<PROCESS_BASIC_INFORMATION>(scratch).PebBaseAddress;
            if (peb == IntPtr.Zero) return "";

            if (!ReadProcessMemory(h, peb + PebProcessParameters, scratch, 8, out _)) return "";
            var parameters = Marshal.ReadIntPtr(scratch);
            if (parameters == IntPtr.Zero) return "";

            if (!ReadProcessMemory(h, parameters + ParamsCurrentDirectory, scratch, 16, out _)) return "";
            var length = (ushort)Marshal.ReadInt16(scratch);
            var buffer = Marshal.ReadIntPtr(scratch, 8);
            if (length == 0 || length > 1000 || buffer == IntPtr.Zero) return "";

            if (!ReadProcessMemory(h, buffer, scratch, length, out var read)) return "";
            return Marshal.PtrToStringUni(scratch, (int)read / 2)?.TrimEnd('\\') ?? "";
        }
        catch
        {
            return "";
        }
        finally
        {
            Marshal.FreeHGlobal(scratch);
            CloseHandle(h);
        }
    }

    // ------------------------------------------------------------ tray icon
    // Shell_NotifyIcon directly, rather than the WinForms NotifyIcon wrapper.
    // WinForms was the single largest module in the process after the GPU
    // drivers were dropped, loaded for this one feature.

    public const int NIM_ADD    = 0x0;
    public const int NIM_MODIFY = 0x1;
    public const int NIM_DELETE = 0x2;

    public const int NIF_MESSAGE = 0x01;
    public const int NIF_ICON    = 0x02;
    public const int NIF_TIP     = 0x04;

    public const int WM_APP        = 0x8000;
    public const int WM_TRAYICON   = WM_APP + 1;
    public const int WM_LBUTTONUP  = 0x0202;
    public const int WM_LBUTTONDBLCLK = 0x0203;
    public const int WM_RBUTTONUP  = 0x0205;

    public const int HWND_MESSAGE = -3;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
    }

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    public static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpData);

    /// <summary>Pulls an icon straight out of our own exe, so argus.ico ships once.</summary>
    [DllImport("shell32.dll", EntryPoint = "ExtractIconExW", CharSet = CharSet.Unicode)]
    public static extern int ExtractIconEx(string lpszFile, int nIconIndex,
                                           IntPtr[]? phiconLarge, IntPtr[]? phiconSmall,
                                           int nIcons);

    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>
    /// Required before showing a menu from a tray icon, or the menu refuses to
    /// dismiss when the user clicks elsewhere. A documented shell quirk.
    /// </summary>
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    // ------------------------------------------------------------ memory trim

    /// <summary>
    /// Asks Windows to reclaim the pages this process is not actually touching.
    ///
    /// Not a trick to flatter Task Manager: Argus genuinely goes idle between
    /// sweeps, and the startup pages - XAML parsing, JIT, settings load - are
    /// never read again. Windows pages back anything it turns out to need.
    /// </summary>
    [DllImport("psapi.dll")]
    public static extern bool EmptyWorkingSet(IntPtr hProcess);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetCurrentProcess();

    // ---------------------------------------------------------------- monitor
    // Replaces System.Windows.Forms.Screen, which was the last reason the
    // WinForms assembly was being loaded at all.

    public const int MONITORINFOF_PRIMARY = 0x1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [DllImport("user32.dll")]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip,
                                                  MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);

    // ------------------------------------------------------------ tcp owners
    // Which process owns a listening port. IPGlobalProperties can list the
    // ports but not who holds them, and "stop this service" means stopping
    // whatever is actually holding the socket - not whatever happened to match
    // a substring of a command line.

    public const int AF_INET = 2;
    public const int TCP_TABLE_OWNER_PID_LISTENER = 3;

    [StructLayout(LayoutKind.Sequential)]
    public struct MIB_TCPROW_OWNER_PID
    {
        public uint state;
        public uint localAddr;
        public byte localPort1, localPort2, localPort3, localPort4;
        public uint remoteAddr;
        public byte remotePort1, remotePort2, remotePort3, remotePort4;
        public uint owningPid;

        /// <summary>The port is big-endian in the first two bytes of the field.</summary>
        public readonly int LocalPort => (localPort1 << 8) | localPort2;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    public static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int dwOutBufLen,
                                                  bool sort, int ipVersion,
                                                  int tblClass, int reserved);

    /// <summary>Process id listening on a local TCP port, or 0 if nothing is.</summary>
    public static int PidListeningOn(int port)
    {
        var size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET,
                            TCP_TABLE_OWNER_PID_LISTENER, 0);
        if (size <= 0) return 0;

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, AF_INET,
                                    TCP_TABLE_OWNER_PID_LISTENER, 0) != 0)
            {
                return 0;
            }

            var count = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
            var ptr = buffer + 4;

            for (var i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(ptr);
                if (row.LocalPort == port) return (int)row.owningPid;
                ptr += rowSize;
            }
        }
        catch { /* a port we cannot resolve simply has no owner */ }
        finally { Marshal.FreeHGlobal(buffer); }

        return 0;
    }

    // ---------------------------------------------------------------- system

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);
}
