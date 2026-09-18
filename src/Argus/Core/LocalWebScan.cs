using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;

namespace Argus.Core;

/// <summary>One local web server that was found rather than configured.</summary>
public sealed record LocalWeb
{
    public required int Port { get; init; }
    public required int Pid { get; init; }

    /// <summary>The page's own title, when it has one. The best name available.</summary>
    public string? Title { get; init; }
}

/// <summary>
/// Everything on this machine that answers HTTP, without being told about it
/// first.
///
/// Argus does not guess how to start or repair anything - that stays in
/// settings.json, where a person put it deliberately. Noticing what is already
/// listening is a different act: it reports, it never acts, and the rows it
/// produces have no buttons.
///
/// "Everything listening" on its own would be useless. A desktop has dozens of
/// RPC, driver and telemetry ports bound at any moment, and none of them are
/// what anyone means by a local service. Answering a GET on / is the filter:
/// it is what dev servers do and what those ports do not.
/// </summary>
public sealed partial class LocalWebScan
{
    // Short. This is a loopback request to something either answering at once
    // or not an HTTP server at all; there is no slow middle case worth waiting
    // for, and the scan runs on every poll.
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMilliseconds(900),
        DefaultRequestHeaders = { { "User-Agent", "Argus" } },
    };

    // Below 1024 is the system's; 49152 and up is the ephemeral range, where
    // ports belong to outbound sockets and are gone by the next poll.
    private const int LowestPort = 1024;
    private const int HighestPort = 49151;

    // How long to take a non-answer for granted. A port that turned out not to
    // be a web server is re-tried rarely, and immediately if its owner changes,
    // which is what happens when a dev server takes over a port a moment later.
    private static readonly TimeSpan RecheckSilent = TimeSpan.FromMinutes(2);

    // Concurrent because the probes run together: every port is asked at the
    // same time, and each writes its own result back here when it answers. A
    // plain Dictionary corrupts itself doing that, which is what happened.
    private readonly ConcurrentDictionary<int, Probe> _seen = new();

    private sealed record Probe
    {
        public required int Pid { get; init; }
        public required bool Answered { get; init; }
        public string? Title { get; init; }
        public DateTimeOffset At { get; init; } = DateTimeOffset.Now;
    }

    [GeneratedRegex(@"<title[^>]*>(.{1,120}?)</title>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleTag();

    /// <summary>
    /// Ports that are listening on loopback and answered HTTP, excluding any
    /// the caller already has a configured target for.
    /// </summary>
    public async Task<IReadOnlyList<LocalWeb>> ScanAsync(
        IReadOnlySet<int> skip, CancellationToken ct)
    {
        var candidates = Listening()
            .Where(p => p >= LowestPort && p <= HighestPort && !skip.Contains(p))
            .ToList();

        // Forget ports that stopped listening, so a restarted server on the
        // same port is probed afresh rather than answered from memory.
        var live = candidates.ToHashSet();
        foreach (var gone in _seen.Keys.Where(p => !live.Contains(p)).ToList())
        {
            _seen.TryRemove(gone, out _);
        }

        var work = new List<Task<LocalWeb?>>(candidates.Count);
        foreach (var port in candidates)
        {
            var pid = Native.PidListeningOn(port);

            if (_seen.TryGetValue(port, out var last) && last.Pid == pid)
            {
                // Known web server: still re-probed each poll, because "it
                // answered ten minutes ago" is exactly the stale claim this
                // panel exists to avoid.
                if (!last.Answered && DateTimeOffset.Now - last.At < RecheckSilent) continue;
            }

            work.Add(ProbeAsync(port, pid, ct));
        }

        var found = new List<LocalWeb>();
        foreach (var result in await Task.WhenAll(work).ConfigureAwait(false))
        {
            if (result is not null) found.Add(result);
        }

        // Anything skipped this round because it was known-silent stays out;
        // anything known-good that failed this round is already gone from the
        // list, which is the point.
        return found.OrderBy(w => w.Port).ToList();
    }

    private async Task<LocalWeb?> ProbeAsync(int port, int pid, CancellationToken ct)
    {
        string? title = null;
        bool answered;
        try
        {
            using var resp = await Http.GetAsync(
                $"http://127.0.0.1:{port}/", HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            // Any HTTP answer counts, including 401 and 404. A server that
            // refuses this particular request is still a server.
            answered = true;

            // The title only names the thing when the thing actually served a
            // page. An error page is titled after the error - a real scan came
            // back with a row called "Not Found", which says nothing about
            // what is on the port.
            if (resp.IsSuccessStatusCode)
            {
                title = await ReadTitleAsync(resp, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Timeout, connection reset, protocol mismatch: not a web server,
            // or not one that wants to talk to us. Either way, not a row.
            answered = false;
        }

        _seen[port] = new Probe { Pid = pid, Answered = answered, Title = title };
        return answered ? new LocalWeb { Port = port, Pid = pid, Title = title } : null;
    }

    private static async Task<string?> ReadTitleAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var type = resp.Content.Headers.ContentType?.MediaType;
        if (type is not null && !type.Contains("html", StringComparison.OrdinalIgnoreCase)) return null;

        try
        {
            // The head of the document is enough, and a few kilobytes keeps a
            // streaming endpoint from being read forever.
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var match = TitleTag().Match(body.Length > 8192 ? body[..8192] : body);
            if (!match.Success) return null;

            var text = WebUtility.HtmlDecode(match.Groups[1].Value).Trim();
            text = Regex.Replace(text, @"\s+", " ");
            return text.Length == 0 ? null : text;
        }
        catch { return null; }
    }

    private static IEnumerable<int> Listening()
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                // A server bound to 0.0.0.0 is reachable on loopback too, so it
                // counts; one bound to a specific LAN address is not ours.
                .Where(e => IPAddress.IsLoopback(e.Address)
                            || e.Address.Equals(IPAddress.Any)
                            || e.Address.Equals(IPAddress.IPv6Any))
                .Select(e => e.Port)
                .Distinct();
        }
        catch (Exception ex)
        {
            Log.Warn($"local web scan could not list listeners: {ex.Message}");
            return [];
        }
    }
}
