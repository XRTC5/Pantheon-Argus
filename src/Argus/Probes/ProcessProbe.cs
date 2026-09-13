using Argus.Core;

namespace Argus.Probes;

/// <summary>
/// The process list, plus the spawn log that catches windows you never get to
/// read. One sweep feeds both, and the service probe reads the same table
/// rather than sweeping again.
/// </summary>
public sealed class ProcessProbe(ArgusSettings settings, ProcessScanner scanner) : IProbe
{
    public string Name => "process";
    public TimeSpan Interval => TimeSpan.FromSeconds(Math.Max(2, settings.ProcessIntervalSeconds));

    /// <summary>
    /// Things that are always noise on this machine. Not a blocklist of
    /// "system" processes, which would hide the interesting ones too, just the
    /// handful that are guaranteed to be running and never worth a line.
    /// </summary>
    private static readonly HashSet<string> Noise = new(StringComparer.OrdinalIgnoreCase)
    {
        "svchost.exe", "csrss.exe", "wininit.exe", "services.exe", "lsass.exe",
        "smss.exe", "fontdrvhost.exe", "dwm.exe", "Registry", "MemCompression",
        "System", "Idle", "SearchHost.exe", "RuntimeBroker.exe", "sihost.exe",
        "ctfmon.exe", "taskhostw.exe", "dllhost.exe", "WmiPrvSE.exe",
        "SecurityHealthService.exe", "audiodg.exe", "conhost.exe",
    };

    public Task<IReadOnlyList<Signal>> PollAsync(CancellationToken ct)
    {
        scanner.Sweep(settings.GhostRetentionMinutes);
        scanner.MarkWindows();

        var results = new List<Signal>(settings.ProcessListLimit + 16);

        // ---------------------------------------------------------- processes
        // Group by image name. Chrome with 40 renderers is one line that says
        // 40, not 40 lines that say Chrome.
        var groups = scanner.Current.Values
            .Where(p => !Noise.Contains(p.Name))
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new
            {
                Name = g.Key,
                Count = g.Count(),
                Lead = g.OrderByDescending(p => p.HasWindow)
                        .ThenBy(p => p.Pid)
                        .First(),
                Members = g.ToList(),
            })
            .ToList();

        // Memory is the expensive part, so only the candidates get measured.
        foreach (var g in groups) scanner.FillMemory(g.Members);

        var ranked = groups
            .Select(g => new
            {
                g.Name,
                g.Count,
                g.Lead,
                Mem = g.Members.Sum(m => m.MemoryMb),
            })
            .Where(g => g.Mem >= settings.ProcessMinMemoryMb || g.Lead.HasWindow)
            .OrderByDescending(g => g.Lead.IsForeground)
            .ThenByDescending(g => g.Mem)
            .Take(settings.ProcessListLimit);

        var order = 0;
        foreach (var g in ranked)
        {
            var label = g.Count > 1 ? $"{Strip(g.Name)}  x{g.Count}" : Strip(g.Name);

            var lead = scanner.GetCommandLine(g.Lead.Pid);

            var what = g.Lead.IsForeground
                ? "The program you are using right now."
                : g.Lead.HasWindow
                    ? "A program with a window open, not currently in front."
                    : "A background program - no window, it just runs.";

            var procExplain =
                $"{what}\n\n" +
                (g.Count > 1
                    ? $"Count   {g.Count} processes, {FormatMb(g.Mem)} between them\n"
                    : $"Memory  {FormatMb(g.Mem)}\n") +
                $"Pid     {g.Lead.Pid}" +
                (string.IsNullOrWhiteSpace(lead)
                    ? ""
                    : $"\nRuns    {(lead.Length > 150 ? lead[..150] + "..." : lead)}");

            results.Add(new Signal
            {
                Explain = procExplain,
                Id = "proc:" + g.Name,
                Group = SignalGroup.Process,
                Title = label,
                Detail = FormatMb(g.Mem),
                Sub = g.Lead.IsForeground ? "foreground"
                    : g.Lead.HasWindow ? "window"
                    : "background",
                State = g.Lead.IsForeground ? SignalState.Live : SignalState.Idle,
                Pid = g.Lead.Pid,
                Order = order++,
            });
        }

        // ------------------------------------------------------------- ghosts
        if (settings.GhostCatcher)
        {
            // ConsoleSpawns drops the conhost that every console process
            // drags in with it, so one script running once is one line rather
            // than two describing the same event.
            var ghosts = scanner.ConsoleSpawns()
                .OrderByDescending(s => s.At)
                .Take(8);

            var gOrder = 0;
            foreach (var g in ghosts)
            {
                results.Add(new Signal
                {
                    Explain = ExplainGhost(g),
                    Id = $"ghost:{g.Pid}",
                    Group = SignalGroup.Ghost,
                    Title = TitleOf(g),
                    Detail = Ago(g.At),
                    Sub = Describe(g),
                    // A console that appeared on this desktop is the thing
                    // being hunted, and is flagged. One in another session
                    // could not have been seen by anybody - it is listed as
                    // background work, not raised as something that happened
                    // to you. Flagging those is how a watchman teaches its
                    // owner to stop reading it.
                    State = g.OnOurDesktop ? SignalState.Warn : SignalState.Idle,
                    At = g.At,
                    Pid = g.Pid,
                    Order = gOrder++,
                });
            }
        }

        return Task.FromResult<IReadOnlyList<Signal>>(results);
    }

    /// <summary>
    /// The whole point of this section: a console window appeared, and this is
    /// who opened it and what it was told to run.
    /// </summary>
    private static string ExplainGhost(SpawnRecord g)
    {
        var sb = new System.Text.StringBuilder();

        // The first line is the finding, and for a long time it was wrong.
        // Argus does not watch windows here - it watches for console host
        // processes appearing, and calls that a window. In session 0 there is
        // no desktop to draw on, so the same evidence means the opposite
        // thing: work ran quietly, exactly as intended.
        if (g.OnOurDesktop)
        {
            sb.AppendLine($"A console opened on your desktop at {g.At:HH:mm:ss}.");
        }
        else if (g.SessionId == 0)
        {
            sb.AppendLine($"Background work started at {g.At:HH:mm:ss}. Nothing appeared on screen.");
            sb.AppendLine();
            sb.AppendLine("It runs in session 0, the service session, which has no desktop at all - "
                        + "a window there is not hidden, it is impossible. This is what a correctly "
                        + "configured scheduled task looks like.");
        }
        else
        {
            sb.AppendLine($"A console started at {g.At:HH:mm:ss} in another session (session "
                        + $"{(g.SessionId < 0 ? "unknown" : g.SessionId.ToString())}), "
                        + "so it was not on your desktop.");
        }

        sb.AppendLine();

        if (!string.IsNullOrEmpty(g.TaskName))
            sb.AppendLine($"Scheduled task:  {g.TaskName}");

        sb.AppendLine($"Program:  {g.Name}  (pid {g.Pid})");

        // Naming the service host as the parent is technically true and
        // practically useless, so where a task is known it is named instead.
        if (!string.IsNullOrEmpty(g.TaskName) && ScheduledTasks.IsSchedulerHost(g.ParentName))
            sb.AppendLine($"Started by:  the Task Scheduler, via {Strip(g.ParentName)} (pid {g.ParentPid})");
        else
            sb.AppendLine(string.IsNullOrEmpty(g.ParentName)
                ? $"Started by:  pid {g.ParentPid}"
                : $"Started by:  {g.ParentName}  (pid {g.ParentPid})");

        if (!string.IsNullOrWhiteSpace(g.CommandLine))
        {
            sb.AppendLine();
            sb.AppendLine("Command:");
            sb.AppendLine(g.CommandLine.Length > 400 ? g.CommandLine[..400] + "..." : g.CommandLine);
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine(g.SessionId == 0
                ? "The command line is not readable from here: a session 0 process runs under a "
                  + "token this panel cannot open. The task name above is the better answer anyway."
                : "No command line readable - it had already exited, or it belongs to another user.");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// What to call a caught console.
    ///
    /// "conhost" is the answer to a question nobody asked. It is the window
    /// host Windows attaches to a console, so it is present at every one of
    /// these events and identifies none of them. Where its parent survived
    /// long enough to be named, the parent is the program that actually ran.
    /// </summary>
    private static string TitleOf(SpawnRecord g)
    {
        if (!g.Name.Equals("conhost.exe", StringComparison.OrdinalIgnoreCase))
            return Strip(g.Name);

        return string.IsNullOrEmpty(g.ParentName)
            ? "console"
            : $"console for {Strip(g.ParentName)}";
    }

    private static string Describe(SpawnRecord g)
    {
        // A named task beats a parent every time. "from svchost (2444)" is the
        // line that made this whole section unreadable.
        if (!string.IsNullOrEmpty(g.TaskName))
            return g.OnOurDesktop
                ? $"scheduled task {g.TaskName}"
                : $"scheduled task {g.TaskName} : session 0, no window";

        var parent = string.IsNullOrEmpty(g.ParentName)
            ? $"pid {g.ParentPid}"
            : $"{Strip(g.ParentName)} ({g.ParentPid})";

        var where = g.OnOurDesktop ? "" : " : session 0, no window";
        if (g.SessionId > 0 && !g.OnOurDesktop) where = $" : session {g.SessionId}";

        // A conhost's own command line is the console handle it was passed and
        // nothing else - it is never the answer to "what ran". The title
        // already names the parent, so the line stays with who started it.
        var isHost = g.Name.Equals("conhost.exe", StringComparison.OrdinalIgnoreCase);

        if (isHost || string.IsNullOrWhiteSpace(g.CommandLine)) return $"from {parent}{where}";

        var cmd = g.CommandLine.Length > 90 ? g.CommandLine[..90] + "..." : g.CommandLine;
        return $"from {parent} : {cmd}{where}";
    }

    private static string Strip(string exe) =>
        exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe[..^4] : exe;

    private static string FormatMb(double mb) =>
        mb >= 1024 ? $"{mb / 1024:0.0}G" : $"{mb:0}M";

    internal static string Ago(DateTimeOffset when)
    {
        var d = DateTimeOffset.Now - when;
        if (d.TotalSeconds < 60) return $"{d.TotalSeconds:0}s";
        if (d.TotalMinutes < 60) return $"{d.TotalMinutes:0}m";
        if (d.TotalHours < 24) return $"{d.TotalHours:0}h";
        return $"{d.TotalDays:0}d";
    }
}
