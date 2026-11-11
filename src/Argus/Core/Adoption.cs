using System.IO;

namespace Argus.Core;

/// <summary>What Argus could read about a process it found serving a page.</summary>
public sealed record FoundProcess
{
    public string ImagePath { get; init; } = "";
    public string CommandLine { get; init; } = "";
    public string WorkingDirectory { get; init; } = "";
}

/// <summary>
/// Turns a local web server Argus noticed into a target it remembers.
///
/// A found row used to live only as long as the port answered: stop the
/// server and the row was gone, with nothing to start it again from. Every new
/// project therefore had to be written into settings.json by hand before the
/// panel was any use for it, which is the chore this removes.
///
/// The rule that Argus only runs a command out of settings.json still holds.
/// What changes is who writes the entry. The command saved here is not a guess:
/// it is the command line, program and folder the server was running with when
/// it was found, copied as read. Nothing is run at the moment of adoption, and
/// Start still only happens on a click.
///
/// Pure on purpose - no process table, no disk beyond one existence check - so
/// the shaping of a target can be read and reasoned about on its own.
/// </summary>
public static class Adoption
{
    /// <summary>
    /// The target to remember for this server, or null when too little could
    /// be read to be worth a row that outlives the process.
    /// </summary>
    /// <param name="parent">
    /// The owner's parent process. A Python virtual environment's python.exe
    /// is a launcher that re-runs the base interpreter with the same
    /// arguments, and it is the base interpreter that holds the socket.
    /// Starting that directly loses the environment, so when the parent is the
    /// same program with the same arguments, the parent is what gets saved.
    /// </param>
    public static WatchTarget? From(LocalWeb web, FoundProcess owner, FoundProcess? parent,
                                    IEnumerable<string> takenNames)
    {
        if (string.IsNullOrWhiteSpace(web.Title)) return null;

        var (_, args) = Split(owner.CommandLine);
        var image = owner.ImagePath;
        var folder = owner.WorkingDirectory;

        if (parent is not null && parent.ImagePath.Length > 0)
        {
            var (_, parentArgs) = Split(parent.CommandLine);
            var sameProgram = string.Equals(Path.GetFileName(parent.ImagePath),
                Path.GetFileName(owner.ImagePath), StringComparison.OrdinalIgnoreCase);

            if (sameProgram && args.Length > 0
                && string.Equals(parentArgs, args, StringComparison.Ordinal))
            {
                image = parent.ImagePath;
                if (parent.WorkingDirectory.Length > 0) folder = parent.WorkingDirectory;
            }
        }

        // Start needs all three. A server whose folder cannot be read (another
        // user, a 32-bit process) would be started from the wrong place and
        // fail in a way that looks like Argus's fault, so it gets a remembered
        // row without a Start button instead.
        var startable = image.Length > 0 && folder.Length > 0 && File.Exists(image);

        var name = web.Title!.Trim();
        var taken = new HashSet<string>(takenNames, StringComparer.OrdinalIgnoreCase);
        if (taken.Contains(name)) name = $"{name} :{web.Port}";
        if (taken.Contains(name)) return null;

        var ran = $"{image} {args}".Trim();

        return new WatchTarget
        {
            Name = name,
            Note = "found by Argus",
            What = $"Added by Argus when it found this answering on port {web.Port}. "
                 + (startable
                     ? $"Start runs what it was running then: {ran} (in {folder}). "
                     : "Argus could not read how it was started, so there is no Start button. ")
                 + "Edit this entry in settings.json, or set Enabled to false to hide it.",
            Port = web.Port,
            HealthPath = "/",
            // The arguments are the most specific thing about a dev server:
            // every python.exe looks alike until you read what it was asked to
            // run. Only used when the port is closed; an open port is matched
            // by whoever holds it.
            ProcessMatch = args.Length >= 8 ? args : (image.Length > 0 ? image : null),
            StartCommand = startable ? image : null,
            StartArgs = startable && args.Length > 0 ? args : null,
            WorkingDirectory = startable ? folder : null,
        };
    }

    /// <summary>A command line as its program and everything after it.</summary>
    public static (string Program, string Args) Split(string commandLine)
    {
        var s = commandLine.Trim();
        if (s.Length == 0) return ("", "");

        if (s[0] == '"')
        {
            var close = s.IndexOf('"', 1);
            return close < 0
                ? (s[1..], "")
                : (s[1..close], s[(close + 1)..].Trim());
        }

        var space = s.IndexOf(' ');
        return space < 0 ? (s, "") : (s[..space], s[(space + 1)..].Trim());
    }
}
