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

    /// <summary>
    /// Terminal session. 0 is the service session, which has no desktop.
    /// -1 when it could not be read. Resolved once and carried forward; a
    /// process never changes session.
    /// </summary>
    public int SessionId { get; set; } = -1;

    /// <summary>Working directory, resolved lazily and only where it earns its cost.</summary>
    public string WorkingDirectory { get; set; } = "";

    /// <summary>
    /// Raw creation file time, resolved lazily. 0 means it could not be read,
    /// which is not the same as "very old" and must not be compared as if it
    /// were.
    /// </summary>
    public long StartedAt { get; set; }
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

    /// <summary>
    /// Terminal session the console appeared in. Argus's own session means it
    /// really was on screen; session 0 means it structurally could not be.
    /// </summary>
    public int SessionId { get; init; } = -1;

    /// <summary>
    /// The scheduled task that owns this process, when Task Scheduler started
    /// it. Empty for everything else.
    ///
    /// Settable, unlike the rest of this record, because naming the task is a
    /// COM call that must not happen while the scanner holds its lock. The
    /// record is published first and named a moment later.
    /// </summary>
    public string TaskName { get; set; } = "";

    /// <summary>True when this console appeared on a desktop Argus can see.</summary>
    public bool OnOurDesktop => SessionId >= 0 && SessionId == ProcessScanner.OwnSession;
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
    /// <summary>
    /// The session Argus itself is running in. A console in any other session
    /// cannot have appeared on the desktop Argus is pinned to, whatever the
    /// process table says.
    /// </summary>
    public static readonly int OwnSession = Native.SessionOf(Environment.ProcessId);

    /// <summary>
    /// Processes that host a console. One appearing means a console was
    /// created - which is not the same as a window being drawn, and the
    /// difference is the whole of <see cref="SpawnRecord.OnOurDesktop"/>.
    /// </summary>
    private static readonly string[] ConsoleHosts =
        ["conhost.exe", "cmd.exe", "powershell.exe", "pwsh.exe", "wscript.exe", "cscript.exe"];

    private Dictionary<int, ProcInfo> _current = [];
    private readonly Dictionary<int, string> _cmdCache = [];
    private readonly List<SpawnRecord> _spawns = [];
    private bool _primed;

    /// <summary>
    /// Guards the tables and the ProcInfo records in them.
    ///
    /// For most of this class's life there was nothing to guard: the probe
    /// thread swept, the probes read, and nothing else touched it. The
    /// breakdown view broke that assumption - it reads the same tables to
    /// answer a question the user just asked, from whichever thread asked, and
    /// enumerating this list while the sweep is adding to it throws.
    ///
    /// What the gate covers is the tables: the dictionary, the spawn list, the
    /// command-line cache, and every method that walks or edits them. What it
    /// does not cover is a ProcInfo once a reference to it has been handed out
    /// - the records are shared, not copied, so that a resolved working
    /// directory or creation time stays resolved rather than being read out of
    /// a process again every few seconds. A breakdown can therefore render a
    /// memory figure or a window flag from the sweep either side of the one it
    /// asked about. Those fields are word-sized and written whole, so the
    /// worst case is a number one sweep old on a panel that redraws every
    /// three seconds, which is not worth a copy of the process table to avoid.
    /// </summary>
    private readonly object _gate = new();

    /// <summary>
    /// A copy, deliberately. Handing out the live dictionary is how a caller
    /// on another thread ends up enumerating it mid-sweep.
    /// </summary>
    public IReadOnlyDictionary<int, ProcInfo> Snapshot()
    {
        lock (_gate) return new Dictionary<int, ProcInfo>(_current);
    }

    /// <summary>
    /// The live table, for the probe thread only. Everything off that thread
    /// wants <see cref="Snapshot"/> or <see cref="TryGet"/>.
    /// </summary>
    public IReadOnlyDictionary<int, ProcInfo> Current => _current;

    /// <summary>One process by pid, for callers that want one and not a copy of the table.</summary>
    public ProcInfo? TryGet(int pid)
    {
        lock (_gate) return _current.TryGetValue(pid, out var found) ? found : null;
    }

    public IReadOnlyList<SpawnRecord> RecentSpawns
    {
        get { lock (_gate) return _spawns.ToList(); }
    }

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
                        // Resolved here, once per process rather than per
                        // sweep: it needs no handle and never changes.
                        SessionId = Native.SessionOf(pid),
                    };
                }
            }
            while (Native.Process32NextW(snap, ref entry));
        }
        finally
        {
            Native.CloseHandle(snap);
        }

        List<SpawnRecord> needAttribution;

        lock (_gate)
        {
            needAttribution = DetectSpawns(next, spawnRetentionMinutes);
            _current = next;
            _primed = true;
        }

        // Deliberately outside the gate. Naming a scheduled task is a COM
        // enumeration, and holding the lock across it would stall every read
        // the panel and the breakdown make for as long as Task Scheduler feels
        // like taking - which defeats the point of building breakdowns off the
        // UI thread in the first place. The record is already published; the
        // name lands on it a moment later, and a string assignment is atomic.
        foreach (var spawn in needAttribution)
        {
            spawn.TaskName = ScheduledTasks.NameOf(spawn.Pid);
        }

        // A PID that has gone cannot come back as the same process, so its
        // cached command line is dead weight. Left unchecked this dictionary
        // is the one thing in Argus that would actually grow without bound.
        lock (_gate)
        {
            if (_cmdCache.Count > _current.Count * 2)
            {
                foreach (var dead in _cmdCache.Keys.Where(k => !_current.ContainsKey(k)).ToList())
                    _cmdCache.Remove(dead);
            }
        }
    }

    /// <summary>
    /// Records what just appeared, and returns the console spawns whose owning
    /// scheduled task still needs looking up. That lookup is the caller's job,
    /// outside the lock.
    /// </summary>
    private List<SpawnRecord> DetectSpawns(Dictionary<int, ProcInfo> next, int retentionMinutes)
    {
        var needAttribution = new List<SpawnRecord>();

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

                var record = new SpawnRecord
                {
                    Pid = pid,
                    Name = info.Name,
                    At = DateTimeOffset.Now,
                    ParentPid = info.ParentPid,
                    ParentName = parentName,
                    CommandLine = cmd,
                    IsConsole = isConsole,
                    SessionId = info.SessionId,
                };

                _spawns.Add(record);

                // Only ask Task Scheduler about consoles that look like its
                // work. Anything the Scheduler starts hangs off the service
                // host, and that is the one case where the parent name is
                // true and useless: "svchost" names the mechanism, never the
                // job. Asking about every spawn would be an enumeration per
                // shell you open.
                if (isConsole && ScheduledTasks.IsSchedulerHost(parentName))
                    needAttribution.Add(record);
            }
        }

        var cutoff = DateTimeOffset.Now.AddMinutes(-retentionMinutes);
        _spawns.RemoveAll(s => s.At < cutoff);

        // Hard ceiling regardless of retention, so a fork bomb cannot make the
        // watchman the biggest process on the machine.
        const int hardCap = 400;
        if (_spawns.Count > hardCap)
            _spawns.RemoveRange(0, _spawns.Count - hardCap);

        return needAttribution;
    }

    /// <summary>
    /// Marks which processes own a real window. One EnumWindows pass, not one
    /// call per process.
    /// </summary>
    public void MarkWindows()
    {
        lock (_gate) MarkWindowsLocked();
    }

    private void MarkWindowsLocked()
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
        lock (_gate) FillMemoryLocked(subset);
    }

    private static void FillMemoryLocked(IEnumerable<ProcInfo> subset)
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
        lock (_gate)
        {
            if (_cmdCache.TryGetValue(pid, out var cached)) return cached;
            var resolved = ResolveCommandLine(pid);
            _cmdCache[pid] = resolved;
            return resolved;
        }
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
    /// <summary>
    /// Every process descended from this one, at any depth, plus the process
    /// itself. Walks child lists built from the sweep rather than asking
    /// Windows, so a tree of forty costs one pass over the table.
    /// </summary>
    public List<ProcInfo> TreeOf(int rootPid)
    {
        lock (_gate) return TreeOfLocked(rootPid);
    }

    private List<ProcInfo> TreeOfLocked(int rootPid)
    {
        var tree = new List<ProcInfo>();
        if (!_current.TryGetValue(rootPid, out var root)) return tree;

        var children = new Dictionary<int, List<ProcInfo>>();
        foreach (var p in _current.Values)
        {
            if (p.Pid == p.ParentPid) continue;
            if (!children.TryGetValue(p.ParentPid, out var list))
                children[p.ParentPid] = list = [];
            list.Add(p);
        }

        // Iterative, and every pid visited at most once, so a cycle cannot
        // hang the walk.
        var seen = new HashSet<int>();
        var queue = new Queue<ProcInfo>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            var p = queue.Dequeue();
            if (!seen.Add(p.Pid)) continue;
            tree.Add(p);

            if (!children.TryGetValue(p.Pid, out var kids)) continue;

            foreach (var kid in kids)
            {
                if (kid.Pid == p.Pid) continue;

                // Windows reuses pids, and a process keeps its dead parent's
                // pid in the table forever. So "parent pid matches" is not the
                // same as "child of": without this, an unrelated live tree
                // gets hung off whatever inherited the number. A child cannot
                // predate its parent.
                //
                // The check only binds where both times are readable. A
                // process in another session refuses the handle and reports 0,
                // and 0 means no opinion rather than "started at the epoch" -
                // so for a session 0 tree this is no protection at all, which
                // is worth knowing before trusting the shape of one.
                var parentStart = StartedAt(p);
                var kidStart = StartedAt(kid);

                if (parentStart != 0 && kidStart != 0 && kidStart < parentStart) continue;

                queue.Enqueue(kid);
            }
        }

        return tree;
    }

    /// <summary>When a process started, resolved once and cached on the record.</summary>
    private static long StartedAt(ProcInfo p)
    {
        if (p.StartedAt == 0) p.StartedAt = Native.CreationTimeOf(p.Pid);
        return p.StartedAt;
    }

    /// <summary>Working directory, resolved once per process and cached on it.</summary>
    public string DirectoryOf(ProcInfo p)
    {
        lock (_gate)
        {
            if (p.WorkingDirectory.Length == 0) p.WorkingDirectory = Native.CurrentDirectory(p.Pid);
            return p.WorkingDirectory;
        }
    }

    /// <summary>
    /// Caught consoles worth showing, with the duplicates dropped.
    ///
    /// One console event is rarely one process. Running a single command
    /// through a shell spawns cmd, the powershell it calls, and the conhost
    /// that Windows attaches to whichever of them owns the console - three
    /// entries describing one thing, listed as three separate incidents with
    /// three separate timestamps. Only the outermost is the event; everything
    /// below it is that event's own plumbing, and is still there in the
    /// breakdown of whatever started it.
    /// </summary>
    public List<SpawnRecord> ConsoleSpawns()
    {
        List<SpawnRecord> caught;
        lock (_gate) caught = _spawns.Where(s => s.IsConsole).ToList();

        var byPid = caught.Select(s => s.Pid).ToHashSet();

        return caught.Where(s => !byPid.Contains(s.ParentPid)).ToList();
    }

    public ProcInfo? FindByCommandLine(string needle)
    {
        // The lock is reentrant, so GetCommandLine below is free to take it
        // again on this thread.
        lock (_gate) return FindByCommandLineLocked(needle);
    }

    private ProcInfo? FindByCommandLineLocked(string needle)
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
