using System.Collections.Concurrent;
using Argus.Core;

namespace Argus.Probes;

/// <summary>
/// Runs every probe on its own schedule, on one background thread, and hands
/// the merged result to the UI.
///
/// One loop, not one timer per probe. A dozen independent timers is a dozen
/// independent reasons for the CPU to leave its idle state, and on a laptop
/// that is measured in battery rather than percent.
/// </summary>
public sealed class ProbeHost : IDisposable
{
    private readonly List<IProbe> _probes = [];
    private readonly Dictionary<string, DateTimeOffset> _nextDue = [];
    private readonly ConcurrentDictionary<string, IReadOnlyList<Signal>> _latest = new();

    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    /// <summary>Raised on a background thread. Marshal before touching the UI.</summary>
    public event Action<IReadOnlyList<Signal>>? SignalsChanged;

    public void Add(IProbe probe)
    {
        _probes.Add(probe);
        _nextDue[probe.Name] = DateTimeOffset.MinValue;

        if (probe is IPushProbe push)
        {
            push.Updated += signals =>
            {
                _latest[probe.Name] = signals;
                Publish();
            };
        }
    }

    public void Start()
    {
        foreach (var p in _probes.OfType<IPushProbe>())
        {
            try { p.Start(); }
            catch (Exception ex) { Log.Error($"probe {p.Name} failed to start", ex); }
        }

        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var now = DateTimeOffset.Now;
            var soonest = TimeSpan.FromSeconds(5);

            foreach (var probe in _probes)
            {
                if (ct.IsCancellationRequested) break;

                if (_nextDue[probe.Name] <= now)
                {
                    try
                    {
                        var signals = await probe.PollAsync(ct).ConfigureAwait(false);
                        _latest[probe.Name] = signals;
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        // A probe that fails is not a reason for the panel to
                        // die. It keeps its last known signals and tries again.
                        Log.Error($"probe {probe.Name} threw", ex);
                    }

                    _nextDue[probe.Name] = DateTimeOffset.Now + probe.Interval;
                }

                var wait = _nextDue[probe.Name] - DateTimeOffset.Now;
                if (wait < soonest) soonest = wait;
            }

            Publish();

            // Sleep until the next probe is actually due rather than waking on
            // a fixed tick to discover there is nothing to do.
            if (soonest < TimeSpan.FromMilliseconds(250)) soonest = TimeSpan.FromMilliseconds(250);
            try { await Task.Delay(soonest, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void Publish()
    {
        var merged = new List<Signal>(64);
        foreach (var probe in _probes)
        {
            if (_latest.TryGetValue(probe.Name, out var signals)) merged.AddRange(signals);
        }
        SignalsChanged?.Invoke(merged);
    }

    /// <summary>Forces every probe to run on the next pass. Used by the manual refresh.</summary>
    public void RefreshNow()
    {
        foreach (var key in _nextDue.Keys.ToList()) _nextDue[key] = DateTimeOffset.MinValue;
    }

    public void Dispose()
    {
        _cts.Cancel();

        foreach (var p in _probes.OfType<IPushProbe>())
        {
            try { p.Stop(); } catch { /* shutting down anyway */ }
        }

        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { /* shutting down anyway */ }

        foreach (var p in _probes.OfType<IDisposable>()) p.Dispose();
        _cts.Dispose();
    }
}
