using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Argus.Core;

public enum DockMode
{
    /// <summary>
    /// Default. Argus sits at the bottom of the window stack, on the desktop
    /// layer. Windows open over it the way they open over desktop icons.
    /// </summary>
    Desktop,

    /// <summary>Floats above everything, for when you actually want to read it.</summary>
    Overlay,

    /// <summary>
    /// Registers as a Windows appbar and permanently carves its strip out of
    /// the desktop work area, so maximised windows stop at its edge. Same
    /// mechanism as the taskbar. Costs real screen width.
    /// </summary>
    Reserve,
}

public enum PanelEdge { Right, Left }

/// <summary>One thing Argus is asked to keep an eye on.</summary>
public sealed class WatchTarget
{
    public string Name { get; set; } = "";

    /// <summary>Shown under the name. Keep it to a few words.</summary>
    public string Note { get; set; } = "";

    /// <summary>
    /// What this thing actually is, in a sentence, shown on hover. Edit it
    /// freely - it is yours, and a name you chose six months ago will not
    /// mean much to you six months from now.
    /// </summary>
    public string What { get; set; } = "";

    /// <summary>Local port to test for a listener. 0 to skip.</summary>
    public int Port { get; set; }

    /// <summary>
    /// Optional HTTP path probed when the port is open. A port that accepts a
    /// connection is not the same as a service that works, and the difference
    /// is exactly the kind of thing this panel exists to show.
    /// </summary>
    public string? HealthPath { get; set; }

    /// <summary>Task Scheduler task backing this target, if any. Drives the countdown.</summary>
    public string? ScheduledTask { get; set; }

    /// <summary>Substring matched against process command lines to find the live process.</summary>
    public string? ProcessMatch { get; set; }

    /// <summary>
    /// What to run to start this. A .cmd or .bat is run through cmd with no
    /// window; anything else is launched directly.
    ///
    /// Leave it empty and the Start button falls back to running ScheduledTask,
    /// if one is set. Leave both empty and there is no Start button - Argus
    /// never guesses how to start something.
    /// </summary>
    public string? StartCommand { get; set; }

    /// <summary>Arguments for StartCommand. Optional.</summary>
    public string? StartArgs { get; set; }

    /// <summary>
    /// A polite way to stop this, tried before any kill: an app that saves on
    /// exit (Clio: "Clio.exe --quit") should be asked, not shot. If the process
    /// is still alive five seconds later, Stop falls back to the kill.
    /// </summary>
    public string? StopCommand { get; set; }

    /// <summary>Arguments for StopCommand. Optional.</summary>
    public string? StopArgs { get; set; }

    /// <summary>Working directory. Defaults to the folder StartCommand lives in.</summary>
    public string? WorkingDirectory { get; set; }

    public bool Enabled { get; set; } = true;
}

/// <summary>
/// A known remedy for a known breakage, offered as a button in the Fixes
/// section.
///
/// This is the one thing on the panel that changes the machine rather than
/// reporting on it, so it follows the same rule as Start: Argus only ever runs
/// a command the user put in settings.json. Nothing is inferred or discovered,
/// and an empty Fixes list means no section at all.
///
/// Elevated by default. A repair that a normal process could perform is rarely
/// worth a button, and the ones that matter - drivers, services, anything under
/// Program Files - all need admin.
/// </summary>
public sealed class FixAction
{
    public string Name { get; set; } = "";

    /// <summary>Shown under the name. Keep it to a few words.</summary>
    public string Note { get; set; } = "";

    /// <summary>What this repairs and why, in a sentence or two, shown on hover.</summary>
    public string What { get; set; } = "";

    /// <summary>Executable to run. A .ps1 is run through PowerShell with no window.</summary>
    public string Command { get; set; } = "";

    /// <summary>Arguments for Command. Optional.</summary>
    public string? Args { get; set; }

    /// <summary>Working directory. Defaults to the folder Command lives in.</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>
    /// Run as administrator, which means a UAC prompt on every press. On by
    /// default; set false for a repair that genuinely does not need it.
    /// </summary>
    public bool Elevated { get; set; } = true;

    public bool Enabled { get; set; } = true;
}

public sealed class ArgusSettings
{
    // ------------------------------------------------------------ placement

    public DockMode DockMode { get; set; } = DockMode.Desktop;
    public PanelEdge Edge { get; set; } = PanelEdge.Right;

    /// <summary>Index into the monitor list. The screen button cycles this.</summary>
    public int ScreenIndex { get; set; }

    /// <summary>Logical (DPI-independent) width. Height follows the screen unless pinned.</summary>
    public double Width { get; set; } = 330;

    /// <summary>0 means "fill the chosen screen vertically".</summary>
    public double Height { get; set; }

    // ---- remembered placement -------------------------------------------
    //
    // Where the user last dragged or resized the panel to, in PHYSICAL pixels.
    // Physical because that is the only unit that means the same thing on both
    // of this machine's displays; a logical rectangle would be correct on one
    // and wrong on the other.
    //
    // Once set, this wins over the edge calculation. Wherever the panel was
    // left is where it comes back, until the user moves it again or asks for
    // another screen.

    public bool HasPlacement { get; set; }
    public int PlacedLeft { get; set; }
    public int PlacedTop { get; set; }
    public int PlacedWidth { get; set; }
    public int PlacedHeight { get; set; }

    public double MarginTop { get; set; } = 8;
    public double MarginBottom { get; set; } = 8;
    public double MarginEdge { get; set; } = 8;

    public double Opacity { get; set; } = 0.96;

    // ---- breakdown window -----------------------------------------------
    //
    // Where the "what is under this" window was last left, in logical pixels.
    // Logical rather than physical, unlike the panel's own placement: this
    // window is dragged by hand to wherever suits and is never positioned by
    // calculation, so WPF's own coordinates are the ones that round-trip.

    public bool HasBreakdownPlacement { get; set; }
    public double BreakdownLeft { get; set; }
    public double BreakdownTop { get; set; }
    public double BreakdownWidth { get; set; }
    public double BreakdownHeight { get; set; }

    public bool RunOnStartup { get; set; } = true;

    // ------------------------------------------------------------ behaviour

    /// <summary>Seconds between process sweeps. Below 2 is waste; nothing moves that fast.</summary>
    public int ProcessIntervalSeconds { get; set; } = 3;

    /// <summary>Seconds between service health checks.</summary>
    public int ServiceIntervalSeconds { get; set; } = 15;

    /// <summary>Seconds between Task Scheduler reads. Next-run times barely move.</summary>
    public int ScheduleIntervalSeconds { get; set; } = 30;

    /// <summary>How many processes to list. The panel is a glance, not an audit.</summary>
    public int ProcessListLimit { get; set; } = 14;

    /// <summary>Hide processes below this working set, in MB, from the general list.</summary>
    public double ProcessMinMemoryMb { get; set; } = 40;

    /// <summary>
    /// Log every console host that appears, with its parent. This is what
    /// catches a window that flashes up and vanishes before you can read it.
    /// </summary>
    public bool GhostCatcher { get; set; } = true;

    /// <summary>How long a caught spawn stays on the panel, in minutes.</summary>
    public int GhostRetentionMinutes { get; set; } = 120;

    public bool ShowClaudeCode { get; set; } = true;

    // ------------------------------------------------------------ targets

    public List<WatchTarget> Targets { get; set; } = [];

    /// <summary>
    /// Also list local ports that answer HTTP but have no target configured.
    ///
    /// The configured list is the one you curate: it has notes, health paths
    /// and Start buttons. This is everything else you happen to be running,
    /// so a dev server you started an hour ago and forgot is on the panel
    /// without having to be written down first. Discovered rows report only -
    /// Argus will not offer to start or stop something nobody described.
    /// </summary>
    public bool DiscoverLocalServices { get; set; } = true;

    /// <summary>
    /// How tall the Services section may grow before it scrolls inside itself,
    /// in device-independent pixels. Roughly 60 to a row.
    ///
    /// It is the one section whose length is not something you chose, so it is
    /// the one that has to be stopped from pushing the rest off the bottom.
    /// Zero removes the cap.
    /// </summary>
    public double ServicesMaxHeight { get; set; } = 300;

    /// <summary>
    /// Repairs offered at the bottom of the panel. Empty by default, and an
    /// empty list means the section does not appear - unlike Targets there are
    /// no examples here, because an example repair is a button that does
    /// something unhelpful to a real machine.
    /// </summary>
    public List<FixAction> Fixes { get; set; } = [];

    // ------------------------------------------------------------ storage

    [JsonIgnore]
    public static string Directory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Argus");

    [JsonIgnore]
    public static string FilePath => Path.Combine(Directory, "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static ArgusSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<ArgusSettings>(File.ReadAllText(FilePath), JsonOpts);
                if (loaded is not null)
                {
                    if (loaded.Targets.Count == 0) loaded.Targets = DefaultTargets();
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"settings unreadable, falling back to defaults: {ex.Message}");
        }

        var fresh = new ArgusSettings { Targets = DefaultTargets() };
        fresh.Save();
        return fresh;
    }

    public void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            // Write-then-move, so a crash mid-write cannot leave a truncated
            // settings file that loses the placement on next boot.
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOpts));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"settings not saved: {ex.Message}");
        }
    }

    /// <summary>
    /// First-run examples, not a real configuration.
    ///
    /// They exist so the panel has something to show before it is set up, and
    /// to document the shape of a target: a port to test, a path to fetch, a
    /// scheduled task to count down to, a process to match. Replace them with
    /// your own in settings.json - Argus only falls back to these when that
    /// file is missing or its target list is empty.
    /// </summary>
    private static List<WatchTarget> DefaultTargets() =>
    [
        new() { Name = "Web app", Note = "local dev server", Port = 3000,
                HealthPath = "/", ProcessMatch = "node",
                StartCommand = @"C:\path\to\your\start.cmd",
                What = "Example target. A development server on port 3000, matched to whichever "
                     + "node process has that port open. Edit or delete this in settings.json." },

        new() { Name = "API", Note = "backend service", Port = 8080,
                HealthPath = "/health",
                What = "Example target showing a health path. Argus tests the port first, then "
                     + "asks for /health - a port that accepts a connection is not the same as a "
                     + "service that answers, and the panel shows the difference." },

        new() { Name = "Nightly job", Note = "scheduled task",
                ScheduledTask = "Example Task",
                What = "Example of a schedule-only target. Give it the name of a Windows "
                     + "scheduled task and the panel counts down to its next run, then reports "
                     + "whether the last one actually succeeded." },
    ];
}
