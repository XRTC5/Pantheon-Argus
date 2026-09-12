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
            var proc = string.IsNullOrEmpty(target.ProcessMatch)
                ? null
                : scanner.FindByCommandLine(target.ProcessMatch);

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
            if (!string.IsNullOrWhiteSpace(target.What))
            {
                explain.AppendLine(target.What.Trim());
                explain.AppendLine();
            }
            if (target.Port != 0)
            {
                explain.AppendLine($"Address:  http://127.0.0.1:{target.Port}{target.HealthPath}");
                explain.AppendLine(portOpen
                    ? "Port:  open, something is listening"
                    : "Port:  closed, nothing is listening");
            }
            explain.AppendLine(proc is not null
                ? $"Process:  {proc.Name}  pid {proc.Pid}"
                : "Process:  not running");
            if (proc is not null)
            {
                var cmd = scanner.GetCommandLine(proc.Pid);
                if (!string.IsNullOrWhiteSpace(cmd))
                    explain.AppendLine($"Command:  {(cmd.Length > 240 ? cmd[..240] + "..." : cmd)}");
            }

            results.Add(new Signal
            {
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
