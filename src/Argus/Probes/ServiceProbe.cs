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

    public async Task<IReadOnlyList<Signal>> PollAsync(CancellationToken ct)
    {
        var results = new List<Signal>(settings.Targets.Count);

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
        foreach (var target in settings.Targets)
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
                Sub = sub,
                State = state,
                Pid = proc?.Pid ?? 0,
                Order = order++,
            });
        }

        return results;
    }

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

    private static string Trim(string exe) =>
        exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe[..^4] : exe;
}
