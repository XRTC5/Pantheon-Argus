using System.IO;
using Argus.Core;

namespace Argus.Probes;

/// <summary>
/// What Claude Code is doing right now.
///
/// Claude Code appends to a JSONL transcript per session under
/// %USERPROFILE%\.claude\projects\&lt;project-slug&gt;\&lt;session-id&gt;.jsonl.
/// A file growing means a session is live; a file that stopped growing five
/// minutes ago means it is idle. That is the whole signal, and Windows will
/// tell us about it for free.
///
/// This is a push probe on purpose. Polling a directory tree every few seconds
/// to ask "did anything change" is exactly the kind of pointless wake-up this
/// panel is supposed to avoid, when FileSystemWatcher already knows.
/// </summary>
public sealed class ClaudeCodeProbe : IPushProbe, IDisposable
{
    private readonly ArgusSettings _settings;
    private readonly string _root;
    private FileSystemWatcher? _watcher;

    /// <summary>Last write seen per project slug. Small, bounded by project count.</summary>
    private readonly Dictionary<string, DateTimeOffset> _activity = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _gate = new();

    public event Action<IReadOnlyList<Signal>>? Updated;

    public string Name => "claude";

    /// <summary>
    /// Only used as a slow backstop so the "idle for 12m" text keeps counting
    /// up when nothing is being written. The real updates arrive by event.
    /// </summary>
    public TimeSpan Interval => TimeSpan.FromSeconds(20);

    public ClaudeCodeProbe(ArgusSettings settings)
    {
        _settings = settings;
        _root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", "projects");
    }

    public void Start()
    {
        if (!_settings.ShowClaudeCode || !Directory.Exists(_root)) return;

        // Seed from what is already on disk, so the panel is correct the
        // instant it appears rather than only after the next keystroke.
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(_root))
            {
                var newest = Directory.EnumerateFiles(dir, "*.jsonl")
                    .Select(f => new FileInfo(f).LastWriteTime)
                    .DefaultIfEmpty(DateTime.MinValue)
                    .Max();

                if (newest > DateTime.MinValue)
                    _activity[Path.GetFileName(dir)] = new DateTimeOffset(newest);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"claude seed failed: {ex.Message}");
        }

        try
        {
            _watcher = new FileSystemWatcher(_root, "*.jsonl")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                // Claude Code writes in bursts. A small buffer drops events
                // under load, and a dropped event here costs us nothing that
                // the next write will not correct.
                InternalBufferSize = 16 * 1024,
                EnableRaisingEvents = true,
            };

            _watcher.Changed += OnChanged;
            _watcher.Created += OnChanged;
            _watcher.Error += (_, e) => Log.Warn($"claude watcher error: {e.GetException().Message}");
        }
        catch (Exception ex)
        {
            Log.Error("claude watcher could not start", ex);
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        var slug = SlugOf(e.FullPath);
        if (slug is null) return;

        lock (_gate) _activity[slug] = DateTimeOffset.Now;

        // Push straight through. The host coalesces on the UI side, so a burst
        // of writes does not become a burst of repaints.
        Updated?.Invoke(Build());
    }

    private string? SlugOf(string fullPath)
    {
        try
        {
            var rel = Path.GetRelativePath(_root, fullPath);
            var sep = rel.IndexOf(Path.DirectorySeparatorChar);
            return sep <= 0 ? null : rel[..sep];
        }
        catch { return null; }
    }

    public Task<IReadOnlyList<Signal>> PollAsync(CancellationToken ct)
        => Task.FromResult(Build());

    private IReadOnlyList<Signal> Build()
    {
        if (!_settings.ShowClaudeCode) return [];

        List<KeyValuePair<string, DateTimeOffset>> snapshot;
        lock (_gate) snapshot = [.. _activity];

        var results = new List<Signal>(snapshot.Count);
        var order = 0;

        foreach (var (slug, at) in snapshot.OrderByDescending(kv => kv.Value))
        {
            var idle = DateTimeOffset.Now - at;

            // Anything quiet for over a day is history, not status.
            if (idle.TotalHours > 24) continue;

            var state = idle.TotalSeconds < 90 ? SignalState.Live
                      : idle.TotalMinutes < 30 ? SignalState.Warn
                      : SignalState.Idle;

            results.Add(new Signal
            {
                Explain =
                    $"A Claude Code session in {Pretty(slug)}.\n\n" +
                    (state == SignalState.Live
                        ? "Writing to its transcript right now, so it is actively working."
                        : $"Last wrote {ProcessProbe.Ago(at)} ago, so it is sitting idle.") +
                    $"\n\nTranscript:  ~/.claude/projects/{slug}/",
                Id = "claude:" + slug,
                Group = SignalGroup.Claude,
                Title = Pretty(slug),
                Detail = state == SignalState.Live ? "working" : ProcessProbe.Ago(at),
                Sub = state == SignalState.Live ? "writing now" : "idle",
                State = state,
                At = at,
                Order = order++,
            });
        }

        return results;
    }

    /// <summary>Turns the on-disk slug "C--dev-claude-obsidian" back into "dev/claude-obsidian".</summary>
    private static string Pretty(string slug)
    {
        var s = slug;
        if (s.Length > 3 && s[1] == '-' && s[2] == '-') s = s[3..];
        return s.Replace('-', '/');
    }

    public void Stop()
    {
        if (_watcher is null) return;
        _watcher.EnableRaisingEvents = false;
        _watcher.Changed -= OnChanged;
        _watcher.Created -= OnChanged;
        _watcher.Dispose();
        _watcher = null;
    }

    public void Dispose() => Stop();
}
