namespace Argus.Probes;

/// <summary>
/// A source of signals. Everything Argus knows arrives through one of these.
///
/// Contract: <see cref="PollAsync"/> runs on a background thread, must not
/// touch the UI, must not throw (the host will log and carry on, but a probe
/// that throws every tick is a probe that is lying to you), and must return
/// quickly. Anything slower than its own interval starves the others.
/// </summary>
public interface IProbe
{
    string Name { get; }

    /// <summary>How often this probe wants to run. The host honours it per-probe.</summary>
    TimeSpan Interval { get; }

    Task<IReadOnlyList<Signal>> PollAsync(CancellationToken ct);
}

/// <summary>
/// A probe that pushes rather than polls, so the host never wakes it on a
/// timer. Used where Windows will already tell us something changed and
/// burning a poll would be pure waste.
/// </summary>
public interface IPushProbe : IProbe
{
    event Action<IReadOnlyList<Signal>>? Updated;
    void Start();
    void Stop();
}
