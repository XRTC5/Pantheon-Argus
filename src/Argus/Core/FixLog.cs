using System.Collections.Concurrent;

namespace Argus.Core;

/// <summary>
/// What each repair did the last time it was pressed.
///
/// A row's own <c>Note</c> is deliberately not sticky - the next sweep replaces
/// it with real state a few seconds later, which for a service is exactly
/// right. A repair has no state to be replaced with: it is a one-shot action
/// that takes about a minute and then the panel would go back to saying
/// nothing, as though the button had never been pressed.
///
/// So the outcome is kept here and the probe puts it back on the row every
/// sweep, until it ages out.
/// </summary>
internal static class FixLog
{
    /// <summary>How long a result stays on the row before it stops being news.</summary>
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(10);

    private static readonly ConcurrentDictionary<string, (string Message, DateTimeOffset At)> Entries = new();

    public static void Record(string fixName, string message) =>
        Entries[fixName] = (message, DateTimeOffset.Now);

    /// <summary>The last outcome for this repair, or null if there is nothing recent to say.</summary>
    public static string? LastResult(string fixName)
    {
        if (!Entries.TryGetValue(fixName, out var entry)) return null;

        var age = DateTimeOffset.Now - entry.At;
        if (age > Retention)
        {
            Entries.TryRemove(fixName, out _);
            return null;
        }

        return age < TimeSpan.FromMinutes(1)
            ? entry.Message
            : $"{entry.Message} - {(int)age.TotalMinutes}m ago";
    }
}
