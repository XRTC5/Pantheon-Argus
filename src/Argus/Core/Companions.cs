using System.IO;
using System.Text.Json;

namespace Argus.Core;

/// <summary>
/// Sibling apps that announce themselves, so Argus can watch them without
/// anyone editing settings.json.
///
/// Argus and Clio are separate downloads and each works alone. When both are
/// on the machine, Clio writes %APPDATA%\Clio\app.json on every start with the
/// path it is running from, and Argus shows a Clio row with Start and Stop.
/// The row is synthesised on each read and never saved into Targets, so it
/// cannot go stale when Clio is rebuilt elsewhere, and it vanishes if Clio is
/// removed. A Clio target the user has written by hand takes precedence.
/// </summary>
internal static class Companions
{
    private static readonly string ClioFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Clio", "app.json");

    // The probes read targets every few seconds; the file changes only when
    // Clio starts. Re-read it only when its timestamp moves.
    private static DateTime _stamp;
    private static WatchTarget? _clio;

    private sealed record Announcement(string? ExePath);

    public static IEnumerable<WatchTarget> Discover(IReadOnlyCollection<WatchTarget> configured)
    {
        var clio = Clio();
        if (clio is null) yield break;

        var handled = configured.Any(t =>
            string.Equals(t.Name, clio.Name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(t.ProcessMatch, clio.ProcessMatch, StringComparison.OrdinalIgnoreCase));

        if (!handled) yield return clio;
    }

    private static WatchTarget? Clio()
    {
        try
        {
            if (!File.Exists(ClioFile))
            {
                // Forget the stamp too: a file restored with its old timestamp
                // (moved back, or restored from backup) must be read again.
                _stamp = default;
                return _clio = null;
            }

            var stamp = File.GetLastWriteTimeUtc(ClioFile);
            if (stamp == _stamp) return _clio;
            _stamp = stamp;

            var exe = JsonSerializer.Deserialize<Announcement>(File.ReadAllText(ClioFile))?.ExePath;
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) return _clio = null;

            return _clio = new WatchTarget
            {
                Name = "Clio",
                Note = "sticky notes",
                What = "Clio, the sticky notes that stow into one hub. Found automatically because Clio is " +
                       "installed here. Stop asks Clio to save and exit (Clio.exe --quit) rather than " +
                       "killing it, so nothing typed is lost and no dead icon is left in the tray.",
                Port = 0,
                ProcessMatch = "Clio.exe",
                StartCommand = exe,
                StopCommand = exe,
                StopArgs = "--quit",
                WorkingDirectory = Path.GetDirectoryName(exe),
            };
        }
        catch (Exception ex)
        {
            Log.Warn($"companion file unreadable: {ex.Message}");
            _stamp = default;
            return _clio = null;
        }
    }
}
