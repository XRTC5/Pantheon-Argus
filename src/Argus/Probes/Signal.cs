namespace Argus.Probes;

public enum SignalState
{
    Unknown,
    /// <summary>Running and answering.</summary>
    Live,
    /// <summary>Deliberately not running. Not a problem.</summary>
    Idle,
    /// <summary>Running but something is off, or a deadline is close.</summary>
    Warn,
    /// <summary>Should be running and is not, or failed.</summary>
    Bad,
}

/// <summary>What button, if any, this row offers.</summary>
public enum ControlAction
{
    None,
    Start,
    Stop,

    /// <summary>
    /// Run a one-shot repair. Not the same idea as Start or Stop: those are
    /// about a thing being up or down, this is a known remedy for a known
    /// breakage. It runs elevated, because a breakage worth keeping a button
    /// for is usually one an ordinary process cannot undo.
    /// </summary>
    Fix,
}

public enum SignalGroup
{
    Service,
    Schedule,
    Ghost,
    Claude,
    Process,

    /// <summary>
    /// Always-on bots (Discord and the like): targets with Category "bots".
    /// Built by the service probe exactly like a service row, only filed
    /// under their own heading.
    /// </summary>
    Bot,

    /// <summary>
    /// Repairs, pinned to the bottom of the panel. Not a status section -
    /// nothing in it is running or failing. These are the buttons for when
    /// something in one of the sections above is.
    /// </summary>
    Fix,
}

/// <summary>
/// One line on the panel. Probes produce these; the UI renders them and knows
/// nothing about where they came from. Immutable so a probe thread can hand
/// one to the UI thread without a lock.
/// </summary>
public sealed record Signal
{
    public required string Id { get; init; }
    public required SignalGroup Group { get; init; }
    public required string Title { get; init; }

    /// <summary>Right-hand column. Short: a port, a PID, a countdown.</summary>
    public string Detail { get; init; } = "";

    /// <summary>
    /// RAM, shown as its own figure beside Detail ("85M"). Empty for rows where
    /// memory means nothing - a scheduled task between runs, a stopped service.
    /// Null means "leave the row's figure alone": the Argus row is kept current
    /// by the header's 2-second tick, and the panel re-applies the last service
    /// sweep every time any probe reports, which would put a stale number back.
    /// </summary>
    public string? Memory { get; init; } = "";

    /// <summary>Second line, dimmed. Optional.</summary>
    public string? Sub { get; init; }

    public SignalState State { get; init; } = SignalState.Unknown;

    /// <summary>When the thing described happened, or is due.</summary>
    public DateTimeOffset? At { get; init; }

    /// <summary>Lower sorts first inside a group.</summary>
    public int Order { get; init; }

    /// <summary>Process id, when this signal is about one. 0 otherwise.</summary>
    public int Pid { get; init; }

    /// <summary>
    /// The long answer, shown on hover: what this row actually is, and the real
    /// command behind it.
    ///
    /// A panel this narrow can only ever show a label, and a label you chose
    /// six months ago tells you nothing you did not already half-know. The name
    /// is the handle; this is the explanation.
    /// </summary>
    public string? Explain { get; init; }

    /// <summary>Which button this row shows. None for anything not controllable.</summary>
    public ControlAction Control { get; init; } = ControlAction.None;

    /// <summary>The watch target this row controls, by name.</summary>
    public string? ControlTarget { get; init; }
}
