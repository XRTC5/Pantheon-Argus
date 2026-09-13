using Argus.Probes;

namespace Argus.Core;

/// <summary>One line inside a breakdown group.</summary>
public sealed record BreakdownItem
{
    public required string Title { get; init; }
    public string Detail { get; init; } = "";
    public string Sub { get; init; } = "";
    public SignalState State { get; init; } = SignalState.Idle;

    /// <summary>Indent depth, so a process tree reads as a tree.</summary>
    public int Depth { get; init; }
}

public sealed record BreakdownGroup
{
    public required string Title { get; init; }
    public required IReadOnlyList<BreakdownItem> Items { get; init; }

    /// <summary>Shown instead of the items when there are none. Never left blank.</summary>
    public string Empty { get; init; } = "nothing";
}

public sealed record BreakdownReport
{
    public required string Title { get; init; }
    public required string Summary { get; init; }
    public required IReadOnlyList<BreakdownGroup> Groups { get; init; }
}

/// <summary>
/// Everything that belongs to one row, gathered in one place.
///
/// The panel lists what is true, grouped by the kind of thing it is: services
/// here, consoles there, scheduled work somewhere else. That is the right
/// shape for a glance and the wrong shape for a question, because one piece of
/// work shows up in three sections at once and reads as three unrelated
/// events. A scheduled task, the powershell it runs, the console that
/// powershell drags in with it and the port it ends up holding are one thing.
/// This is the view that says so.
/// </summary>
public static class Breakdown
{
    public static BreakdownReport Build(string rowId, string rowTitle,
                                        ArgusSettings settings, ProcessScanner scanner)
    {
        var colon = rowId.IndexOf(':');
        var kind = colon < 0 ? rowId : rowId[..colon];
        var key = colon < 0 ? "" : rowId[(colon + 1)..];

        return kind switch
        {
            "svc" => ForService(key, settings, scanner),
            "sched" => ForSchedule(key, settings, scanner),
            "claude" => ForClaude(key, rowTitle, scanner),
            "ghost" => ForGhost(key, scanner),
            _ => ForProcess(key, scanner),
        };
    }

    // ------------------------------------------------------------- services

    private static BreakdownReport ForService(string name, ArgusSettings settings,
                                              ProcessScanner scanner)
    {
        var target = settings.Targets.FirstOrDefault(t =>
            t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        if (target is null)
            return Empty(name, "This target is no longer in settings.json.");

        var portOpen = ServiceProbe.IsPortOpen(target.Port);
        var proc = ServiceProbe.ResolveProcess(target, scanner, portOpen);
        var tree = proc is null ? [] : scanner.TreeOf(proc.Pid);

        var groups = new List<BreakdownGroup>
        {
            ProcessGroup("RUNNING UNDER IT", tree, scanner,
                proc is null ? "not running" : "just the one process"),
            ConsoleGroup(tree, scanner),
            ScheduleGroup(target.ScheduledTask, tree),
        };

        if (target.Port != 0)
        {
            var owner = portOpen ? Native.PidListeningOn(target.Port) : 0;
            groups.Insert(0, new BreakdownGroup
            {
                Title = "PORT",
                Empty = "no port configured",
                Items =
                [
                    new BreakdownItem
                    {
                        Title = $"port {target.Port}",
                        Detail = portOpen ? "open" : "closed",
                        Sub = owner != 0
                            ? $"held by pid {owner}"
                            : portOpen ? "owner not readable" : "nothing is listening",
                        State = portOpen ? SignalState.Live : SignalState.Idle,
                    },
                ],
            });
        }

        var summary = proc is null
            ? "Not running. Nothing below belongs to it right now."
            : $"Everything here hangs off {Strip(proc.Name)} (pid {proc.Pid}). Anything listed "
              + "under it is part of this service, not a separate thing.";

        return new BreakdownReport { Title = target.Name, Summary = summary, Groups = groups };
    }

    // ------------------------------------------------------------- schedule

    private static BreakdownReport ForSchedule(string name, ArgusSettings settings,
                                               ProcessScanner scanner)
    {
        var target = settings.Targets.FirstOrDefault(t =>
            t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        var taskName = target?.ScheduledTask ?? name;

        // A task that is running right now owns a process, and that process is
        // the thread tying this row to everything else on the panel.
        var running = ScheduledTasks.Running();
        var pid = running.FirstOrDefault(kv =>
            kv.Value.Equals(taskName, StringComparison.OrdinalIgnoreCase)).Key;

        var tree = pid == 0 ? [] : scanner.TreeOf(pid);

        var summary = pid == 0
            ? "Not running at the moment, so it owns no processes. It will again at its next fire."
            : $"Running now as pid {pid}. Everything below is this task, however separate it looks "
              + "in the sections above.";

        return new BreakdownReport
        {
            Title = target?.Name ?? taskName,
            Summary = summary,
            Groups =
            [
                ScheduleGroup(taskName, tree),
                ProcessGroup("RUNNING UNDER IT", tree, scanner, "not running right now"),
                ConsoleGroup(tree, scanner),
            ],
        };
    }

    // --------------------------------------------------------- claude code

    private static BreakdownReport ForClaude(string slug, string rowTitle, ProcessScanner scanner)
    {
        // The transcript folder is the only name Claude Code exposes, and it
        // is a flattened path. Rather than trying to unflatten it - which is
        // ambiguous, because a folder called claude-obsidian flattens exactly
        // the way a folder called claude\obsidian would - each live process is
        // flattened the same way and compared.
        var sessions = scanner.Snapshot().Values
            .Where(p => p.Name.Equals("claude.exe", StringComparison.OrdinalIgnoreCase))
            .Where(p => Slug(scanner.DirectoryOf(p)).Equals(slug, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var tree = new List<ProcInfo>();
        foreach (var session in sessions)
        {
            foreach (var p in scanner.TreeOf(session.Pid))
                if (tree.All(x => x.Pid != p.Pid)) tree.Add(p);
        }

        var summary = sessions.Count == 0
            ? "No live claude.exe is working in this folder - the transcript is there, but the "
              + "session that wrote it has exited."
            : $"{sessions.Count} session{(sessions.Count == 1 ? "" : "s")} in this folder. Every "
              + "shell, script and console below was started by it, which is why none of them are "
              + "separate events.";

        return new BreakdownReport
        {
            Title = rowTitle,
            Summary = summary,
            Groups =
            [
                ProcessGroup("WHAT IT IS RUNNING", tree, scanner, "no live session"),
                ConsoleGroup(tree, scanner),
                new BreakdownGroup
                {
                    Title = "TRANSCRIPT",
                    Items =
                    [
                        new BreakdownItem
                        {
                            Title = $"~/.claude/projects/{slug}/",
                            Sub = "the JSONL files Argus watches to know this session is alive",
                        },
                    ],
                },
            ],
        };
    }

    // -------------------------------------------------------------- ghosts

    private static BreakdownReport ForGhost(string pidText, ProcessScanner scanner)
    {
        if (!int.TryParse(pidText, out var pid))
            return Empty("console", "This row has no process behind it.");

        var spawn = scanner.RecentSpawns.FirstOrDefault(s => s.Pid == pid);
        var tree = scanner.TreeOf(pid);

        var summary = spawn is null
            ? "This console is no longer on record."
            : spawn.OnOurDesktop
                ? $"A console that opened on your desktop at {spawn.At:HH:mm:ss}."
                : $"Started at {spawn.At:HH:mm:ss} in session {spawn.SessionId}. No window: that "
                  + "session has no desktop to draw one on.";

        if (spawn is not null && !string.IsNullOrEmpty(spawn.TaskName))
            summary += $" It belongs to the scheduled task {spawn.TaskName}.";

        if (tree.Count == 0) summary += " It has since exited.";

        return new BreakdownReport
        {
            Title = spawn is null ? $"pid {pid}" : Strip(spawn.Name),
            Summary = summary,
            Groups =
            [
                ProcessGroup("STILL RUNNING", tree, scanner, "it has exited"),
                ConsoleGroup(tree, scanner),
                ScheduleGroup(spawn?.TaskName, tree),
            ],
        };
    }

    // ----------------------------------------------------------- processes

    private static BreakdownReport ForProcess(string image, ProcessScanner scanner)
    {
        var live = scanner.Snapshot();
        var members = live.Values
            .Where(p => p.Name.Equals(image, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Pid)
            .ToList();

        if (members.Count == 0) return Empty(Strip(image), "Nothing by that name is running now.");

        var memberPids = members.Select(p => p.Pid).ToHashSet();

        // Parents outside the group, deduplicated. Forty chrome renderers have
        // one parent worth naming, not forty.
        var parents = members
            .Select(p => p.ParentPid)
            .Where(pid => !memberPids.Contains(pid))
            .Distinct()
            .Select(pid => live.TryGetValue(pid, out var parent) ? parent : null)
            .Where(p => p is not null)
            .Select(p => p!)
            .ToList();

        var tree = new List<ProcInfo>();
        foreach (var m in members)
        {
            foreach (var p in scanner.TreeOf(m.Pid))
                if (tree.All(x => x.Pid != p.Pid)) tree.Add(p);
        }

        // Children only. The group's own processes are already listed above.
        var children = tree.Where(p => !memberPids.Contains(p.Pid)).ToList();

        var running = ScheduledTasks.Running();
        var ownedByTask = tree.Where(p => running.ContainsKey(p.Pid)).ToList();

        var summary = members.Count == 1
            ? $"One process, pid {members[0].Pid}."
            : $"{members.Count} processes sharing the name {Strip(image)}. They are one program, "
              + "not several.";

        var groups = new List<BreakdownGroup>
        {
            ProcessGroup("PROCESSES", members, scanner, "none"),
            new()
            {
                Title = "STARTED BY",
                Empty = "nothing still running",
                Items = [.. parents.Select(p => new BreakdownItem
                {
                    Title = Strip(p.Name),
                    Detail = $"pid {p.Pid}",
                    Sub = running.TryGetValue(p.Pid, out var task)
                        ? $"scheduled task {task}"
                        : ScheduledTasks.IsSchedulerHost(p.Name)
                            ? "the Task Scheduler"
                            : Where(p),
                })],
            },
            ProcessGroup("IT STARTED", children, scanner, "nothing"),
            ConsoleGroup(tree, scanner),
        };

        if (ownedByTask.Count > 0)
        {
            groups.Add(new BreakdownGroup
            {
                Title = "SCHEDULED",
                Items = [.. ownedByTask.Select(p => new BreakdownItem
                {
                    Title = running[p.Pid],
                    Detail = "running",
                    Sub = $"this is a scheduled task, running as pid {p.Pid}",
                    State = SignalState.Live,
                })],
            });
        }

        return new BreakdownReport { Title = Strip(image), Summary = summary, Groups = groups };
    }

    // ------------------------------------------------------------- shared

    private static BreakdownGroup ProcessGroup(string title, IReadOnlyList<ProcInfo> tree,
                                               ProcessScanner scanner, string empty)
    {
        scanner.FillMemory(tree);

        var byPid = new Dictionary<int, ProcInfo>();
        foreach (var p in tree) byPid[p.Pid] = p;

        var root = tree.Count > 0 ? tree[0].Pid : 0;

        return new BreakdownGroup
        {
            Title = title,
            Empty = empty,
            Items = [.. tree.Select(p => new BreakdownItem
            {
                Title = Strip(p.Name),
                Detail = p.MemoryMb >= 1 ? $"pid {p.Pid}  {p.MemoryMb:0}M" : $"pid {p.Pid}",
                Sub = Where(p),
                Depth = Depth(p, byPid, root),
                State = p.IsForeground ? SignalState.Live
                      : p.HasWindow ? SignalState.Warn
                      : SignalState.Idle,
            })],
        };
    }

    private static BreakdownGroup ConsoleGroup(IReadOnlyList<ProcInfo> tree, ProcessScanner scanner)
    {
        var pids = tree.Select(p => p.Pid).ToHashSet();

        // Both the console itself and anything it was the parent of: a console
        // that has already exited leaves no process to match, but the record of
        // its parent is still the link back to whoever is responsible.
        var consoles = scanner.ConsoleSpawns()
            .Where(s => pids.Contains(s.Pid) || pids.Contains(s.ParentPid))
            .OrderByDescending(s => s.At)
            .Take(12)
            .ToList();

        return new BreakdownGroup
        {
            Title = "CONSOLES IT OPENED",
            Empty = "none in the retention window",
            Items = [.. consoles.Select(s => new BreakdownItem
            {
                Title = Strip(s.Name),
                Detail = ProcessProbe.Ago(s.At) + " ago",
                Sub = s.OnOurDesktop
                    ? "appeared on your desktop"
                    : "session 0, nothing on screen",
                State = s.OnOurDesktop ? SignalState.Warn : SignalState.Idle,
            })],
        };
    }

    private static BreakdownGroup ScheduleGroup(string? taskName, IReadOnlyList<ProcInfo> tree)
    {
        var items = new List<BreakdownItem>();
        var running = ScheduledTasks.Running();

        if (!string.IsNullOrEmpty(taskName))
        {
            var live = running.Any(kv => kv.Value.Equals(taskName, StringComparison.OrdinalIgnoreCase));
            items.Add(new BreakdownItem
            {
                Title = taskName,
                Detail = live ? "running" : "waiting",
                Sub = live
                    ? "firing right now"
                    : "registered with Windows, waiting for its next trigger",
                State = live ? SignalState.Live : SignalState.Idle,
            });
        }

        // Any other task that happens to own a process in this tree. This is
        // how something started by a task rather than by hand shows its real
        // origin instead of looking like it appeared on its own.
        foreach (var p in tree)
        {
            if (!running.TryGetValue(p.Pid, out var other)) continue;
            if (other.Equals(taskName, StringComparison.OrdinalIgnoreCase)) continue;

            items.Add(new BreakdownItem
            {
                Title = other,
                Detail = "running",
                Sub = $"owns pid {p.Pid} in this tree",
                State = SignalState.Live,
            });
        }

        return new BreakdownGroup
        {
            Title = "SCHEDULED",
            Empty = "nothing scheduled for this",
            Items = items,
        };
    }

    /// <summary>Where a process lives, in the terms that matter on this panel.</summary>
    private static string Where(ProcInfo p)
    {
        if (p.SessionId == 0) return "session 0 - no desktop, so it can never show a window";
        if (p.IsForeground) return "in front right now";
        if (p.HasWindow) return "has a window open";
        return "background, no window";
    }

    private static int Depth(ProcInfo p, Dictionary<int, ProcInfo> byPid, int rootPid)
    {
        var depth = 0;
        var cursor = p;

        // Bounded: a tree deeper than eight is either a fork bomb or a lie,
        // and either way the indent has stopped meaning anything.
        while (cursor.Pid != rootPid && depth < 8)
        {
            if (!byPid.TryGetValue(cursor.ParentPid, out var parent)) break;
            cursor = parent;
            depth++;
        }

        return depth;
    }

    /// <summary>
    /// The same flattening Claude Code uses for its transcript folders: the
    /// drive colon and every separator become a dash.
    /// </summary>
    private static string Slug(string path) =>
        path.Replace(':', '-').Replace('\\', '-').Replace('/', '-');

    private static string Strip(string exe) =>
        exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe[..^4] : exe;

    private static BreakdownReport Empty(string title, string why) =>
        new() { Title = title, Summary = why, Groups = [] };
}
