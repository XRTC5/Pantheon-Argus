using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32;
using Microsoft.Win32.TaskScheduler;
using TaskService = Microsoft.Win32.TaskScheduler.TaskService;

namespace Argus.Core;

/// <summary>
/// Run-on-login, via a logon-triggered scheduled task.
///
/// This was the per-user Run key until 2026-09-14, on the reasoning that a
/// logon-triggered task running an interactive process is what flashes a
/// console on this machine twice a day. That reasoning does not survive
/// contact with the binary: Argus is a WinExe, so it has no console to
/// allocate and no conhost to flash. The flashes come from the PTT tasks,
/// which run powershell.exe.
///
/// What the Run key does cost is measurable. Explorer holds the Run key for
/// ~10s after the shell is ready (Serialize\StartupDelayInMSec), then starts
/// each entry several seconds apart while boot I/O settles, in registry
/// enumeration order - and "Argus", added last, is last. Measured on the
/// 2026-09-14 boot: explorer at +34s, Argus at +123s, with the app itself
/// logging "argus up" one second after its process started. Eighty-nine
/// seconds of a two-minute wait were spent queued behind OneDrive, Medal and
/// LGHUB. A watchman that arrives two minutes into the shift is not watching.
///
/// A logon trigger with no delay is serviced by the Task Scheduler at session
/// creation and skips Explorer's throttle entirely.
///
/// Two constraints on the task definition, both load-bearing:
///   - InteractiveToken, never S4U. S4U runs on a non-interactive window
///     station: the process would start and the panel would never paint.
///   - Priority set explicitly. A task definition defaults to priority 7
///     (below-normal CPU and I/O), which is how a task-launched GUI ends up
///     slower than the Run key it replaced.
/// </summary>
internal static class Startup
{
    private const string TaskName = "Argus";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Argus";

    public static bool IsEnabled()
    {
        try
        {
            using var service = new TaskService();
            using var task = service.GetTask(TaskName);
            if (task is not null) return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"startup state unreadable: {ex.Message}");
        }

        // Still true if task registration was refused and we fell back.
        return RunKeyValue() is not null;
    }

    public static void Set(bool enabled)
    {
        if (!enabled)
        {
            RemoveTask();
            RemoveRunKey();
            Log.Info("startup removed");
            return;
        }

        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            Log.Warn("cannot resolve own path; startup not registered");
            return;
        }

        if (RegisterTask(exe))
        {
            // Both mechanisms at once would start Argus twice; the single
            // instance mutex would shut the second one down, but the second
            // launch is still a second cold start of WPF at login.
            RemoveRunKey();
            Log.Info($"startup registered as scheduled task: {exe}");
            return;
        }

        // Registering in the root task folder can be refused - a locked-down
        // policy, or Task Scheduler itself unhealthy. Late is better than
        // never, so fall back to the old mechanism rather than silently
        // leaving the user with no panel at login.
        SetRunKey(exe);
        Log.Warn($"task registration refused; fell back to the Run key: {exe}");
    }

    /// <summary>Brings the machine in line with settings, and repairs a stale path after a move.</summary>
    public static void Sync(ArgusSettings settings)
    {
        if (!settings.RunOnStartup)
        {
            if (IsEnabled()) Set(false);
            return;
        }

        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;

        try
        {
            using var service = new TaskService();
            using var task = service.GetTask(TaskName);

            // Rebuilding to a new folder leaves the old path registered and
            // silently stops Argus appearing at login. Repair it every start.
            // This is also the migration path off the Run key: no task yet
            // means either a fresh install or the pre-2026-09-14 arrangement,
            // and Set() clears the Run value either way.
            if (task is null || !RegisteredPathMatches(task, exe) || RunKeyValue() is not null)
                Set(true);
        }
        catch (Exception ex)
        {
            Log.Warn($"startup sync failed: {ex.Message}");
        }
    }

    private static bool RegisteredPathMatches(Microsoft.Win32.TaskScheduler.Task task, string exe)
    {
        foreach (var action in task.Definition.Actions)
            if (action is ExecAction exec)
                return string.Equals(exec.Path?.Trim('"'), exe, StringComparison.OrdinalIgnoreCase);

        return false;
    }

    private static bool RegisterTask(string exe)
    {
        try
        {
            using var service = new TaskService();
            var definition = service.NewTask();

            definition.RegistrationInfo.Author = "Argus";
            definition.RegistrationInfo.Description =
                "Starts the Argus panel at logon, ahead of Explorer's Run key throttle.";

            definition.Principal.LogonType = TaskLogonType.InteractiveToken;
            definition.Principal.RunLevel = TaskRunLevel.LUA;
            definition.Principal.UserId = WindowsIdentity.GetCurrent().Name;

            // A desk panel on a laptop that is asleep on battery half the week
            // has to be exempt from every power condition, or it runs on mains
            // only and looks broken everywhere else.
            definition.Settings.DisallowStartIfOnBatteries = false;
            definition.Settings.StopIfGoingOnBatteries = false;
            definition.Settings.ExecutionTimeLimit = TimeSpan.Zero;
            definition.Settings.MultipleInstances = TaskInstancesPolicy.IgnoreNew;
            definition.Settings.Priority = ProcessPriorityClass.Normal;
            definition.Settings.StartWhenAvailable = false;

            definition.Triggers.Add(new LogonTrigger
            {
                UserId = WindowsIdentity.GetCurrent().Name,
                Delay = TimeSpan.Zero,
            });

            definition.Actions.Add(new ExecAction(
                $"\"{exe}\"",
                arguments: null,
                workingDirectory: System.IO.Path.GetDirectoryName(exe)));

            service.RootFolder.RegisterTaskDefinition(
                TaskName,
                definition,
                TaskCreation.CreateOrUpdate,
                userId: null,
                password: null,
                TaskLogonType.InteractiveToken);

            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"task registration failed: {ex.Message}");
            return false;
        }
    }

    private static void RemoveTask()
    {
        try
        {
            using var service = new TaskService();
            if (service.GetTask(TaskName) is not null)
                service.RootFolder.DeleteTask(TaskName, exceptionOnNotExists: false);
        }
        catch (Exception ex)
        {
            Log.Warn($"task removal failed: {ex.Message}");
        }
    }

    private static string? RunKeyValue()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) as string;
        }
        catch (Exception ex)
        {
            Log.Warn($"run key unreadable: {ex.Message}");
            return null;
        }
    }

    private static void SetRunKey(string exe)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.SetValue(ValueName, $"\"{exe}\"");
        }
        catch (Exception ex)
        {
            Log.Error("startup registration failed", ex);
        }
    }

    private static void RemoveRunKey()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Log.Warn($"run key cleanup failed: {ex.Message}");
        }
    }
}
