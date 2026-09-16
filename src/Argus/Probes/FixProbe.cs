using Argus.Core;

namespace Argus.Probes;

/// <summary>
/// The Fixes section: one row per repair configured in settings.json.
///
/// The odd one out among the probes, because it does not probe anything. Every
/// other section answers "what is true right now"; this one answers "what can
/// you do about it", and the answer does not change between sweeps.
///
/// It still lives behind <see cref="IProbe"/> rather than being built once at
/// startup, for one reason: the outcome of the last repair belongs on the row,
/// and that does change. Running on the host's loop means the result appears
/// and later ages out without any other part of the app knowing repairs exist.
/// </summary>
public sealed class FixProbe(ArgusSettings settings) : IProbe
{
    public string Name => "fix";

    // Nothing here is time-sensitive. This interval only governs how promptly
    // the last result appears and how promptly it ages out.
    public TimeSpan Interval => TimeSpan.FromSeconds(10);

    public Task<IReadOnlyList<Signal>> PollAsync(CancellationToken ct)
    {
        var results = new List<Signal>();
        var order = 0;

        foreach (var fix in settings.Fixes)
        {
            if (!fix.Enabled || string.IsNullOrWhiteSpace(fix.Name)) continue;
            ct.ThrowIfCancellationRequested();

            var explain = new System.Text.StringBuilder();

            explain.AppendLine(string.IsNullOrWhiteSpace(fix.What)
                ? "A repair. Pressing FIX runs the command below."
                : fix.What.Trim());

            explain.AppendLine();
            explain.AppendLine($"Runs    {fix.Command} {fix.Args}".TrimEnd());
            explain.AppendLine(fix.Elevated
                ? "As      administrator - Windows will ask before it runs"
                : "As      you, without elevation");

            // Say plainly that this one acts. Every other row on the panel only
            // reports, and a button that changes the machine should not look
            // like the ones that do not.
            explain.AppendLine();
            explain.AppendLine("This is the only kind of row that changes the machine.");

            results.Add(new Signal
            {
                Id = "fix:" + fix.Name,
                Group = SignalGroup.Fix,
                Title = fix.Name,
                Detail = "",
                Sub = FixLog.LastResult(fix.Name) ?? fix.Note,
                State = SignalState.Idle,
                Explain = explain.ToString().TrimEnd(),
                Control = ControlAction.Fix,
                ControlTarget = fix.Name,
                Order = order++,
            });
        }

        return Task.FromResult<IReadOnlyList<Signal>>(results);
    }
}
