using System.Net.Http;
using System.Net.NetworkInformation;
using Argus.Core;

namespace Argus.Probes;

/// <summary>
/// Is the thing up, and is it actually answering?
///
/// Those are two different questions and the panel shows both. A port that
/// accepts a connection only proves something bound to it; a process wedged
/// mid-startup looks identical to a healthy one until you ask it for a page.
/// </summary>
public sealed class ServiceProbe(ArgusSettings settings, ProcessScanner scanner) : IProbe
{
    public string Name => "service";
    public TimeSpan Interval => TimeSpan.FromSeconds(Math.Max(5, settings.ServiceIntervalSeconds));

    // One client for the life of the app. A new HttpClient per poll is the
    // classic way to exhaust sockets on a machine that never reboots.
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(2),
        DefaultRequestHeaders = { { "User-Agent", "Argus" } },
    };

    // Kept per probe, not per poll: it remembers which ports turned out not to
    // be web servers so it does not wait on them again every few seconds.
    private readonly LocalWebScan _discovery = new();

    public async Task<IReadOnlyList<Signal>> PollAsync(CancellationToken ct)
    {
        var results = new List<Signal>(settings.Targets.Count + 1);

        // All listening ports in one call, rather than one connect attempt per
        // target. Also correct in a way a connect test is not: it sees the
        // listener even when the service refuses our specific request.
        HashSet<int> listening;
        try
        {
            listening = IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Select(e => e.Port)
                .ToHashSet();
        }
        catch (Exception ex)
        {
            Log.Warn($"tcp listener enumeration failed: {ex.Message}");
            listening = [];
        }

        var order = 0;

        // Argus first, on a line of its own: what the watchman itself costs,
        // the same working-set figure the header and Processes section use.
        // No buttons and no port - it is not something to start or stop from here.
        results.Add(new Signal
        {
            Id = "svc:argus",
            Group = SignalGroup.Service,
            Title = "Argus",
            Sub = "this panel",
            State = SignalState.Live,
            Memory = null,   // set by the header tick; see Signal.Memory
            Explain = "Argus itself. The figure is its working set: the RAM it is holding right now, "
                    + "the same measure as the Processes section. Shut it down from the tray menu.",
            Order = order++,
        });

        foreach (var target in settings.AllTargets)
        {
            if (!target.Enabled) continue;
            ct.ThrowIfCancellationRequested();

            // A schedule-only target (no port, no process) belongs to the
            // schedule probe. Skipping it here avoids two lines for one thing.
            if (target.Port == 0 && string.IsNullOrEmpty(target.ProcessMatch)) continue;

            var portOpen = target.Port != 0 && listening.Contains(target.Port);

            // Prefer whoever actually holds the socket. A command-line match is
            // a guess that can catch an unrelated shell; the port owner is the
            // service by definition. The match is only a fallback for targets
            // with no port at all.
            var proc = ResolveProcess(target, scanner, portOpen);

            SignalState state;
            string detail;
            string? sub;

            if (portOpen)
            {
                var healthy = await CheckHealthAsync(target, ct).ConfigureAwait(false);
                state = healthy ? SignalState.Live : SignalState.Warn;
                detail = $":{target.Port}";
                sub = healthy
                    ? (proc is not null ? $"{Trim(proc.Name)} pid {proc.Pid}" : target.Note)
                    : "port open, not answering";
            }
            else if (proc is not null && target.Port == 0)
            {
                // A program with no port (Clio) has nothing to bind: running
                // is the whole of healthy, not a half-started server.
                state = SignalState.Live;
                detail = "running";
                sub = $"{Trim(proc.Name)} pid {proc.Pid}";
            }
            else if (proc is not null)
            {
                // Process alive but nothing bound: almost always mid-start, or
                // it crashed the listener and kept the interpreter alive.
                state = SignalState.Warn;
                detail = target.Port == 0 ? "running" : $":{target.Port}";
                sub = $"process up, port closed : pid {proc.Pid}";
            }
            else
            {
                state = SignalState.Idle;
                detail = target.Port == 0 ? "stopped" : $":{target.Port}";
                sub = target.Note;
            }

            var explain = new System.Text.StringBuilder();

            // One plain sentence first. Without it this is a port number and a
            // pid, which tells you what is true but not what the thing is.
            explain.AppendLine(string.IsNullOrWhiteSpace(target.What)
                ? (target.Port != 0
                    ? $"A local service on port {target.Port}. Argus checks the port is open, then "
                      + "asks it for a page - the two are not the same thing."
                    : "A background process Argus watches for.")
                : target.What.Trim());

            explain.AppendLine();

            if (target.Port != 0)
            {
                explain.AppendLine($"Port    {target.Port} - " + (portOpen
                    ? (state == SignalState.Live ? "open and answering" : "open, but not answering")
                    : "closed, nothing listening"));
                explain.AppendLine($"Address http://127.0.0.1:{target.Port}{target.HealthPath}");
            }

            explain.AppendLine(proc is not null
                ? $"Process {Trim(proc.Name)}, pid {proc.Pid}"
                : "Process not running");

            // Running things get a Stop, stopped things get a Start, and a
            // target Argus has no way to start or find offers neither.
            var running = proc is not null || portOpen;
            var control = running
                ? (ServiceControl.CanStop(target) ? ControlAction.Stop : ControlAction.None)
                : (ServiceControl.CanStart(target) ? ControlAction.Start : ControlAction.None);

            results.Add(new Signal
            {
                Control = control,
                ControlTarget = target.Name,
                Explain = explain.ToString().TrimEnd(),
                Id = "svc:" + target.Name,
                Group = SignalGroup.Service,
                Title = target.Name,
                Detail = detail,
                Memory = proc is null ? "" : TreeMemory(proc.Pid),
                Sub = sub,
                State = state,
                Pid = proc?.Pid ?? 0,
                Order = order++,
            });
        }

        if (settings.DiscoverLocalServices)
        {
            await AddDiscoveredAsync(results, order, ct).ConfigureAwait(false);
        }

        return results;
    }

    /// <summary>
    /// Local web servers nobody configured, listed under the ones who were.
    ///
    /// These carry no buttons on purpose. Argus only ever runs a command that
    /// was written down in settings.json, and a port it noticed by itself does
    /// not come with one.
    /// </summary>
    private async Task AddDiscoveredAsync(List<Signal> results, int order, CancellationToken ct)
    {
        var configured = settings.Targets.Where(t => t.Port != 0).Select(t => t.Port).ToHashSet();

        IReadOnlyList<LocalWeb> found;
        try
        {
            found = await _discovery.ScanAsync(configured, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"local web scan failed: {ex.Message}");
            return;
        }

        foreach (var web in found)
        {
            // Only things that said what they are. A row reading
            // "localhost:45654" is a port number wearing a name badge: it
            // takes up a line and answers nothing, and several of them turn
            // the section into noise.
            if (string.IsNullOrWhiteSpace(web.Title)) continue;

            var proc = web.Pid != 0 ? scanner.TryGet(web.Pid) : null;

            // Written into settings now, so from the next poll this is an
            // ordinary target: it stays listed when it stops and has buttons.
            // This round it is still shown as found, which is what it is.
            if (settings.RememberDiscovered && proc is not null) Remember(web, proc);
            var process = proc is not null ? Trim(proc.Name) : "unknown process";

            var explain = new System.Text.StringBuilder();
            explain.AppendLine(
                "A local web server Argus found listening, rather than one you configured. "
                + "It answered a request on / just now, which is what separates a service "
                + "from the dozens of system ports that are always bound.");
            explain.AppendLine();
            explain.AppendLine($"Port    {web.Port} - open and answering");
            explain.AppendLine($"Address http://127.0.0.1:{web.Port}/");
            explain.AppendLine(proc is not null
                ? $"Process {process}, pid {proc.Pid}"
                : "Process owner could not be read");
            explain.AppendLine();
            explain.AppendLine(settings.RememberDiscovered
                ? "Argus has just written this into settings.json with the command it is "
                  + "running, so within a few seconds it becomes an ordinary row that stays "
                  + "listed when it stops and can be started again."
                : "No Start or Stop here: RememberDiscovered is off, so this row lasts only "
                  + "as long as the port answers. Add it to Targets in settings.json to get "
                  + "the buttons, a note and a health path.");

            results.Add(new Signal
            {
                Id = $"svc:discovered:{web.Port}",
                Group = SignalGroup.Service,
                // The page's own title beats anything Argus could invent, and
                // is usually the name of the project behind it.
                Title = web.Title!,
                Detail = $":{web.Port}",
                Memory = proc is null ? "" : TreeMemory(proc.Pid),
                Sub = $"found : {process}" + (proc is not null ? $" pid {proc.Pid}" : ""),
                State = SignalState.Live,
                Pid = web.Pid,
                Explain = explain.ToString().TrimEnd(),
                // After every configured target, whatever their count.
                Order = 1000 + web.Port,
            });
        }
    }

    private void Remember(LocalWeb web, ProcInfo proc)
    {
        try
        {
            // Checked again here, not only in the scan's skip list: the scan
            // answers from memory for up to 45 seconds, and a port remembered
            // a moment ago would otherwise be remembered twice.
            if (settings.Targets.Any(t => t.Port == web.Port)) return;

            var parent = proc.ParentPid > 0 ? scanner.TryGet(proc.ParentPid) : null;
            var target = Adoption.From(web, Describe(proc), parent is null ? null : Describe(parent),
                                       settings.AllTargets.Select(t => t.Name));
            if (target is null) return;

            settings.Remember(target);
            Log.Info($"remembered {target.Name} on port {web.Port}: "
                   + (target.StartCommand is null
                       ? "no start command could be read"
                       : $"{target.StartCommand} {target.StartArgs} in {target.WorkingDirectory}"));
        }
        catch (Exception ex)
        {
            Log.Warn($"could not remember the service on port {web.Port}: {ex.Message}");
        }
    }

    private FoundProcess Describe(ProcInfo p) => new()
    {
        ImagePath = scanner.GetImagePath(p.Pid),
        CommandLine = scanner.GetCommandLine(p.Pid),
        WorkingDirectory = Native.CurrentDirectory(p.Pid),
    };

    /// <summary>
    /// Which live process is this target, if any.
    ///
    /// Public because the breakdown view has to reach the same answer as the
    /// row it was opened from. Two independent guesses at "which process is
    /// the service" is how a panel ends up disagreeing with itself.
    /// </summary>
    public static ProcInfo? ResolveProcess(WatchTarget target, ProcessScanner scanner, bool portOpen)
    {
        ProcInfo? proc = null;
        if (portOpen)
        {
            // TryGet rather than Current: the breakdown calls this from a
            // thread-pool thread, and that is exactly the caller the scanner's
            // gate exists for.
            var ownerPid = Native.PidListeningOn(target.Port);
            if (ownerPid != 0) proc = scanner.TryGet(ownerPid);
        }

        return proc ?? (string.IsNullOrEmpty(target.ProcessMatch)
            ? null
            : scanner.FindByCommandLine(target.ProcessMatch));
    }

    /// <summary>True when something is listening on this target's port right now.</summary>
    public static bool IsPortOpen(int port)
    {
        if (port == 0) return false;
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners().Any(e => e.Port == port);
        }
        catch { return false; }
    }

    private static async Task<bool> CheckHealthAsync(WatchTarget target, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(target.HealthPath)) return true;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head,
                $"http://127.0.0.1:{target.Port}{target.HealthPath}");
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                                       .ConfigureAwait(false);

            // Any answer at all means something is listening and awake. A 404
            // from a live server is a healthier sign than a timeout.
            return (int)resp.StatusCode < 500;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
        catch (HttpRequestException)
        {
            // Some servers reject HEAD outright. Fall back to a GET before
            // calling it unhealthy.
            try
            {
                using var resp = await Http.GetAsync(
                    $"http://127.0.0.1:{target.Port}{target.HealthPath}",
                    HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                return (int)resp.StatusCode < 500;
            }
            catch { return false; }
        }
        catch { return false; }
    }

    /// <summary>
    /// RAM for a service: the process and everything under it. A launcher
    /// script, the interpreter it starts and that interpreter's workers are one
    /// service to the person reading the row, so they are one number.
    /// </summary>
    private string TreeMemory(int pid)
    {
        var tree = scanner.TreeOf(pid);
        if (tree.Count == 0) return "";
        scanner.FillMemory(tree);
        return ProcessProbe.FormatMb(tree.Sum(p => p.MemoryMb));
    }

    private static string Trim(string exe) =>
        exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe[..^4] : exe;
}
