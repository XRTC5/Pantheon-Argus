using System.Runtime.InteropServices;
using System.Text;

namespace Argus.Core;

public sealed record ProcInfo
{
    public required int Pid { get; init; }
    public required int ParentPid { get; init; }
    public required string Name { get; init; }

    /// <summary>Resolved lazily and cached. A command line never changes for a live PID.</summary>
    public string CommandLine { get; set; } = "";
    public string ImagePath { get; set; } = "";

    /// <summary>Working set in MB, filled in only for processes we actually show.</summary>
    public double MemoryMb { get; set; }

    /// <summary>True if this process owns a visible top-level window with a title.</summary>
    public bool HasWindow { get; set; }

    public bool IsForeground { get; set; }
    public DateTimeOffset FirstSeen { get; init; } = DateTimeOffset.Now;
}

public sealed record SpawnRecord
{
    public required int Pid { get; init; }
    public required string Name { get; init; }
    public required DateTimeOffset At { get; init; }
    public string ParentName { get; init; } = "";
    public int ParentPid { get; init; }
    public string CommandLine { get; init; } = "";

    /// <summary>
    /// True when this spawn is the kind that throws a console window on screen.
    /// </summary>
    public bool IsConsole { get; init; }
}

/// <summary>
/// One sweep of the process table, shared by every probe that needs it.
///
/// Deliberately built on Toolhelp rather than Process.GetProcesses() or WMI.
/// Toolhelp returns pid, parent pid and image name for the whole machine in a
/// single kernel call with no per-process handle and no managed Process object
/// to dispose. On a 300-process machine that is the difference between a sweep
/// costing under a millisecond and one costing a hundred.
/// </summary>
public sealed class ProcessScanner
{
    /// <summary>Anything here, appearing out of nowhere, means a console window appeared.</summary>
    private static readonly string[] ConsoleHosts =
        ["conhost.exe", "cmd.exe", "powershell.exe", "pwsh.exe", "wscript.exe", "cscript.exe"];

    private Dictionary<int, ProcInfo> _current = [];
    private readonly Dictionary<int, string> _cmdCache = [];
    private readonly List<SpawnRecord> _spawns = [];
    private bool _primed;

    public IReadOnlyDictionary<int, ProcInfo> Current => _current;
    public IReadOnlyList<SpawnRecord> RecentSpawns => _spawns;

    public int Count => _current.Count;

    public void Sweep(int spawnRetentionMinutes)
    {
        var next = new Dictionary<int, ProcInfo>(_current.Count + 16);
        var snap = Native.CreateToolhelp32Snapshot(Native.TH32CS_SNAPPROCESS, 0);
        if (snap == Native.INVALID_HANDLE_VALUE) return;

        try
        {
            var entry = new Native.PROCESSENTRY32W
            {
                dwSize = (uint)Marshal.SizeOf<Native.PROCESSENTRY32W>(),
            };

            if (!Native.Process32FirstW(snap, ref entry)) return;

            do
            {
                var pid = (int)entry.th32ProcessID;
                if (pid == 0) continue;

                if (_current.TryGetValue(pid, out var existing) && existing.Name == entry.szExeFile)
                {
                    // Same PID, same image: carry the record forward untouched
                    // so FirstSeen and the resolved command line survive.
                    next[pid] = existing;
                }
                else
                {
                    next[pid] = new ProcInfo
                    {
                        Pid = pid,
                        ParentPid = (int)entry.th32ParentProcessID,
                        Name = entry.szExeFile,
                    };
                }
            }
            while (Native.Process32NextW(snap, ref entry));
        }
        finally
        {
            Native.CloseHandle(snap);
        }

        DetectSpawns(next, spawnRetentionMinutes);
        _current = next;
        _primed = true;

        // A PID that has gone cannot come back as the same process, so its
        // cached command line is dead weight. Left unchecked this dictionary
        // is the one thing in Argus that would actually grow without bound.
        if (_cmdCache.Count > _current.Count * 2)
        {
            foreach (var dead in _cmdCache.Keys.Where(k => !_current.ContainsKey(k)).ToList())
                _cmdCache.Remove(dead);
        }
    }

    private void DetectSpawns(Dictionary<int, ProcInfo> next, int retentionMinutes)
    {
        // The first sweep would otherwise report all 300 running processes as
        // brand new spawns, which is true and completely useless.
        if (_primed)
        {
            foreach (var (pid, info) in next)
            {
                if (_current.ContainsKey(pid)) continue;

                var isConsole = ConsoleHosts.Contains(info.Name, StringComparer.OrdinalIgnoreCase);

                // Only pay for the command line on things worth naming. Resolving
                // it for every short-lived helper process would undo the point of
                // using Toolhelp in the first place.
                var cmd = isConsole ? ResolveCommandLine(pid) : "";

                next.TryGetValue(info.ParentPid, out var parent);
                var parentName = parent?.Name
                                 ?? (_current.TryGetValue(info.ParentPid, out var old) ? old.Name : "");

                _spawns.Add(new SpawnRecord
                {
                    Pid = pid,
                    Name = info.Name,
                    At = DateTimeOffset.Now,
                    ParentPid = info.ParentPid,
                    ParentName = parentName,
                    CommandLine = cmd,
                    IsConsole = isConsole,
                });
            }
        }

        var cutoff = DateTimeOffset.Now.AddMinutes(-retentionMinutes);
        _spawns.RemoveAll(s => s.At < cutoff);

        // Hard ceiling regardless of retention, so a fork bomb cannot make the
        // watchman the biggest process on the machine.
        const int hardCap = 400;
        if (_spawns.Count > hardCap)
            _spawns.RemoveRange(0, _spawns.Count - hardCap);
    }

    /// <summary>
    /// Marks which processes own a real window. One EnumWindows pass, not one
    /// call per process.
    /// </summary>
    public void MarkWindows()
    {
        foreach (var p in _current.Values)
        {
            p.HasWindow = false;
            p.IsForeground = false;
        }

        Native.EnumWindows(static (hwnd, _) =>
        {
            if (!Native.IsWindowVisible(hwnd)) return true;
            if (Native.GetWindowTextLength(hwnd) == 0) return true;

            Native.GetWindowThreadProcessId(hwnd, out var pid);
            if (Instance is { } self && self._current.TryGetValue((int)pid, out var info))
                info.HasWindow = true;

            return true;
        }, IntPtr.Zero);

        var fg = Native.GetForegroundWindow();
        if (fg != IntPtr.Zero)
        {
            Native.GetWindowThreadProcessId(fg, out var fgPid);
            if (_current.TryGetValue((int)fgPid, out var info)) info.IsForeground = true;
        }
    }

    /// <summary>
    /// EnumWindows takes a plain callback, so the scanner has to be reachable
    /// from a static context. Set once at construction.
    /// </summary>
    public static ProcessScanner? Instance { get; private set; }

    public ProcessScanner() => Instance = this;

    public void FillMemory(IEnumerable<ProcInfo> subset)
    {
        foreach (var p in subset)
        {
            var h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)p.Pid);
            if (h == IntPtr.Zero) continue;
            try
            {
                if (Native.K32GetProcessMemoryInfo(h, out var counters,
                        (uint)Marshal.SizeOf<Native.PROCESS_MEMORY_COUNTERS>()))
                {
                    p.MemoryMb = counters.WorkingSetSize / 1024d / 1024d;
                }
            }
            finally { Native.CloseHandle(h); }
        }
    }

    public string GetCommandLine(int pid)
    {
        if (_cmdCache.TryGetValue(pid, out var cached)) return cached;
        var resolved = ResolveCommandLine(pid);
        _cmdCache[pid] = resolved;
        return resolved;
    }

    private string ResolveCommandLine(int pid)
    {
        if (_cmdCache.TryGetValue(pid, out var hit)) return hit;

        var h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (h == IntPtr.Zero) return "";

        var buffer = IntPtr.Zero;
        try
        {
            // Ask for the size first; the string is a UNICODE_STRING header
            // followed by its own buffer, so we cannot guess it.
            Native.NtQueryInformationProcess(h, Native.ProcessCommandLineInformation,
                                             IntPtr.Zero, 0, out var needed);
            if (needed <= 0 || needed > 64 * 1024) return "";

            buffer = Marshal.AllocHGlobal(needed);
            var status = Native.NtQueryInformationProcess(h, Native.ProcessCommandLineInformation,
                                                          buffer, needed, out _);
            if (status != 0) return "";

            var us = Marshal.PtrToStructure<Native.UNICODE_STRING>(buffer);
            if (us.Buffer == IntPtr.Zero || us.Length == 0) return "";

            var text = Marshal.PtrToStringUni(us.Buffer, us.Length / 2) ?? "";
            _cmdCache[pid] = text;
            return text;
        }
        catch
        {
            return "";
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            Native.CloseHandle(h);
        }
    }

    public string GetImagePath(int pid)
    {
        var h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (h == IntPtr.Zero) return "";
        try
        {
            var sb = new StringBuilder(512);
            var size = (uint)sb.Capacity;
            return Native.QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : "";
        }
        finally { Native.CloseHandle(h); }
    }

    /// <summary>Finds a live process whose command line or name contains <paramref name="needle"/>.</summary>
    public ProcInfo? FindByCommandLine(string needle)
    {
        foreach (var p in _current.Values)
        {
            if (p.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)) return p;

            // Only interpreters and servers are worth the command-line lookup;
            // scanning all 300 would defeat the cheap sweep.
            if (!IsInterpreter(p.Name)) continue;

            var cmd = GetCommandLine(p.Pid);
            if (cmd.Contains(needle, StringComparison.OrdinalIgnoreCase)) return p;
        }
        return null;
    }

    private static bool IsInterpreter(string name) =>
        name.Equals("python.exe", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("pythonw.exe", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("node.exe", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("uvicorn.exe", StringComparison.OrdinalIgnoreCase);
}
