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
            var procExplain =
                $"{g.Name}\n\n" +
                (g.Count > 1
                    ? $"{g.Count} processes under this name, {FormatMb(g.Mem)} between them.\n"
                    : $"One process, {FormatMb(g.Mem)}.\n") +
                $"Leading pid:  {g.Lead.Pid}\n" +
                (g.Lead.IsForeground ? "This is the window you are using right now.\n"
                 : g.Lead.HasWindow ? "Has a visible window.\n"
                 : "No window - it runs in the background.\n") +
                (string.IsNullOrWhiteSpace(lead)
                    ? ""
                    : $"\nCommand:  {(lead.Length > 300 ? lead[..300] + "..." : lead)}");

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
            var ghosts = scanner.RecentSpawns
                .Where(s => s.IsConsole)
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
                    Title = Strip(g.Name),
                    Detail = Ago(g.At),
                    Sub = Describe(g),
                    // A console that appears unbidden is the thing being hunted,
                    // so it is flagged rather than merely listed.
                    State = SignalState.Warn,
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
        sb.AppendLine($"A console window appeared at {g.At:HH:mm:ss}.");
        sb.AppendLine();
        sb.AppendLine($"Program:  {g.Name}  (pid {g.Pid})");
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
            sb.AppendLine("No command line readable - it had already exited, or it belongs to another user.");
        }

        return sb.ToString().TrimEnd();
    }

    private static string Describe(SpawnRecord g)
    {
        var parent = string.IsNullOrEmpty(g.ParentName)
            ? $"pid {g.ParentPid}"
            : $"{Strip(g.ParentName)} ({g.ParentPid})";

        if (string.IsNullOrWhiteSpace(g.CommandLine)) return $"from {parent}";

        var cmd = g.CommandLine.Length > 90 ? g.CommandLine[..90] + "..." : g.CommandLine;
        return $"from {parent} : {cmd}";
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
