using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Argus.Core;

/// <summary>
/// Starting and stopping the things Argus watches.
///
/// This is the one part of Argus that acts rather than observes, so it is
/// deliberately narrow:
///
///   - It only ever runs a command the user put in settings.json. Nothing is
///     inferred, guessed, or discovered. A target with no StartCommand and no
///     ScheduledTask simply has no Start button.
///   - It only ever stops the exact process Argus already matched and showed
///     on the panel, plus that process's children. It never searches for
///     something to kill.
///   - It refuses to stop itself, and refuses anything with a system pid.
///
/// Killing the wrong process is the worst thing this panel could do, so the
/// rule is that the thing being stopped must be the thing already on screen.
/// </summary>
internal static class ServiceControl
{
    /// <summary>Pids that are never a valid target, whatever the match says.</summary>
    private static readonly int[] Forbidden = [0, 4];

    public static bool CanStart(WatchTarget t) =>
        !string.IsNullOrWhiteSpace(t.StartCommand) || !string.IsNullOrWhiteSpace(t.ScheduledTask);

    public static bool CanStop(WatchTarget t) =>
        !string.IsNullOrWhiteSpace(t.ProcessMatch);

    // ------------------------------------------------------------------ start

    public static string Start(WatchTarget target)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(target.StartCommand))
                return StartCommand(target);

            if (!string.IsNullOrWhiteSpace(target.ScheduledTask))
                return StartTask(target);

            return "nothing configured to start";
        }
        catch (Exception ex)
        {
            Log.Error($"start {target.Name} failed", ex);
            return ex.Message;
        }
    }

    private static string StartCommand(WatchTarget target)
    {
        var command = Environment.ExpandEnvironmentVariables(target.StartCommand!);
        var workDir = string.IsNullOrWhiteSpace(target.WorkingDirectory)
            ? Path.GetDirectoryName(command) ?? ""
            : Environment.ExpandEnvironmentVariables(target.WorkingDirectory);

        var isScript = command.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                    || command.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

        var psi = new ProcessStartInfo
        {
            // A batch file needs a shell to run it, and a shell means a console
            // window - which is precisely the thing Argus was built to hunt. So
            // it is launched with no window at all. The launcher scripts here
            // already refuse to start a second copy, so this stays idempotent.
            FileName = isScript ? "cmd.exe" : command,
            Arguments = isScript
                ? $"/c \"{command}\" {target.StartArgs}".TrimEnd()
                : target.StartArgs ?? "",
            WorkingDirectory = Directory.Exists(workDir) ? workDir : "",
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var p = Process.Start(psi);
        Log.Info($"started {target.Name}: {psi.FileName} {psi.Arguments}");
        return p is null ? "did not start" : $"started (pid {p.Id})";
    }

    private static string StartTask(WatchTarget target)
    {
        // schtasks rather than the TaskScheduler library: running a task needs
        // no more than this, and it keeps the start path independent of the
        // probe that reads them.
        var psi = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            Arguments = $"/Run /TN \"{target.ScheduledTask}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var p = Process.Start(psi);
        if (p is null) return "could not run schtasks";

        p.WaitForExit(5000);
        Log.Info($"started {target.Name} via task {target.ScheduledTask}");
        return p.ExitCode == 0 ? "task started" : $"schtasks exit {p.ExitCode}";
    }

    // -------------------------------------------------------------------- fix

    /// <summary>
    /// Runs a repair from the Fixes section.
    ///
    /// Same rule as Start - only ever a command out of settings.json - with two
    /// differences that come from what a repair is.
    ///
    /// It elevates, so it cannot use the launch path Start uses. Start runs
    /// with UseShellExecute false precisely so it can suppress the console
    /// window, and elevation requires UseShellExecute true. Hiding the window
    /// is therefore a request (WindowStyle plus PowerShell's own -WindowStyle)
    /// rather than a guarantee, and the UAC prompt is visible by design: a
    /// button that quietly makes administrative changes is worse than one that
    /// asks.
    ///
    /// It also does not wait. A repair restarts services and verifies ports; it
    /// takes a minute, and blocking the UI thread on it would freeze the panel.
    /// The script writes its own log, and the panel reports what happened next
    /// to the button.
    /// </summary>
    public static string RunFix(FixAction fix)
    {
        if (string.IsNullOrWhiteSpace(fix.Command))
            return "nothing configured to run";

        try
        {
            var command = Environment.ExpandEnvironmentVariables(fix.Command);

            if (!File.Exists(command))
            {
                Log.Warn($"fix {fix.Name}: {command} does not exist");
                return "script not found";
            }

            var workDir = string.IsNullOrWhiteSpace(fix.WorkingDirectory)
                ? Path.GetDirectoryName(command) ?? ""
                : Environment.ExpandEnvironmentVariables(fix.WorkingDirectory);

            var isPs1 = command.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase);

            var psi = new ProcessStartInfo
            {
                FileName = isPs1 ? "powershell.exe" : command,
                Arguments = isPs1
                    ? $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{command}\" {fix.Args}".TrimEnd()
                    : fix.Args ?? "",
                WorkingDirectory = Directory.Exists(workDir) ? workDir : "",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                Verb = fix.Elevated ? "runas" : "",
            };

            using var p = Process.Start(psi);
            Log.Info($"fix {fix.Name}: started {psi.FileName} {psi.Arguments}");
            return p is null ? "did not start" : "running - this takes a minute";
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // 1223 is ERROR_CANCELLED: the UAC prompt was dismissed. Not a
            // failure worth logging as one - the user said no.
            return "cancelled at the prompt";
        }
        catch (Exception ex)
        {
            Log.Error($"fix {fix.Name} failed", ex);
            return ex.Message;
        }
    }

    // ------------------------------------------------------------------- stop

    /// <summary>
    /// Stops the service.
    ///
    /// Whatever owns the port is what gets stopped, and only if that fails does
    /// it fall back to the matched process. That ordering was arrived at the
    /// hard way. These launchers use `start` to detach the real server, so the
    /// server is NOT a child of the script Argus matched - killing the matched
    /// process tree left the port held and the service running. Worse, a
    /// command-line substring match can catch anything that merely mentions the
    /// path, including a shell someone is typing in.
    ///
    /// Whoever holds the socket is the service, by definition. Nothing else is.
    /// </summary>
    public static string Stop(WatchTarget target, int matchedPid)
    {
        var pid = target.Port != 0 ? Native.PidListeningOn(target.Port) : 0;
        var how = "port owner";

        if (pid == 0)
        {
            pid = matchedPid;
            how = "matched process";
        }

        if (pid <= 0 || Forbidden.Contains(pid))
            return "nothing running to stop";

        if (pid == Environment.ProcessId)
        {
            Log.Warn($"refused to stop {target.Name}: that is Argus itself");
            return "refused - that is Argus";
        }

        try
        {
            using var p = Process.GetProcessById(pid);

            if (!string.IsNullOrWhiteSpace(target.StopCommand) && AskToStop(target, p))
            {
                Log.Info($"stopped {target.Name} via its stop command: pid {pid}");
                return "stopped";
            }

            // The launchers are cmd wrappers around a python or node server, so
            // killing only the matched process would orphan the actual service
            // and leave the port held.
            p.Kill(entireProcessTree: true);
            p.WaitForExit(5000);

            // Confirm rather than assume. Reporting "stopped" while the port is
            // still held would be the panel lying to the user, which is worse
            // than not having the button at all.
            if (target.Port != 0)
            {
                for (var i = 0; i < 20 && Native.PidListeningOn(target.Port) != 0; i++)
                    Thread.Sleep(100);

                var still = Native.PidListeningOn(target.Port);
                if (still != 0)
                {
                    Log.Warn($"{target.Name}: killed pid {pid} but port {target.Port} " +
                             $"is still held by pid {still}");
                    return $"port still held by pid {still}";
                }
            }

            Log.Info($"stopped {target.Name} via {how}: pid {pid} and children");
            return "stopped";
        }
        catch (ArgumentException)
        {
            return "already gone";
        }
        catch (Exception ex)
        {
            Log.Error($"stop {target.Name} failed", ex);
            return ex.Message;
        }
    }

    /// <summary>
    /// Runs the configured StopCommand and waits for the process to leave.
    /// False means it is still there, and the caller falls back to the kill.
    /// </summary>
    private static bool AskToStop(WatchTarget target, Process p)
    {
        try
        {
            var command = Environment.ExpandEnvironmentVariables(target.StopCommand!);
            using var asker = Process.Start(new ProcessStartInfo
            {
                FileName = command,
                Arguments = target.StopArgs ?? "",
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (p.WaitForExit(5000)) return true;
            Log.Warn($"{target.Name}: stop command ran but pid {p.Id} is still up; killing");
        }
        catch (Exception ex)
        {
            Log.Warn($"{target.Name}: stop command failed ({ex.Message}); killing");
        }
        return false;
    }
}
