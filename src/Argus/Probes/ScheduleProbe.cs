using Argus.Core;

// No blanket using for the scheduler library: it defines its own Task, which
// would make every bare "Task" in this async file ambiguous. Importing the
// three types actually needed keeps Task meaning what it means everywhere else.
using SchedTask = Microsoft.Win32.TaskScheduler.Task;
using TaskService = Microsoft.Win32.TaskScheduler.TaskService;
using TaskState = Microsoft.Win32.TaskScheduler.TaskState;

namespace Argus.Probes;

/// <summary>
/// When does the next pull run, and did the last one actually work?
///
/// The second half matters more than it looks. A task can report Ready, have a
/// sensible next-run time, and be failing on every single fire. That was true
/// of a task on the machine Argus was written for: it returned 0xC000013A
/// (killed by its console closing) every thirty minutes for weeks while looking
/// perfectly healthy in the Task Scheduler UI.
/// </summary>
public sealed class ScheduleProbe(ArgusSettings settings) : IProbe
{
    public string Name => "schedule";
    public TimeSpan Interval => TimeSpan.FromSeconds(Math.Max(15, settings.ScheduleIntervalSeconds));

    public Task<IReadOnlyList<Signal>> PollAsync(CancellationToken ct)
    {
        var results = new List<Signal>();
        var order = 0;

        try
        {
            using var service = new TaskService();

            foreach (var target in settings.Targets)
            {
                if (!target.Enabled || string.IsNullOrEmpty(target.ScheduledTask)) continue;
                ct.ThrowIfCancellationRequested();

                var task = service.GetTask(target.ScheduledTask);
                if (task is null)
                {
                    results.Add(new Signal
                    {
                        Id = "sched:" + target.Name,
                        Group = SignalGroup.Schedule,
                        Title = target.Name,
                        Detail = "missing",
                        Sub = $"no task named {target.ScheduledTask}",
                        State = SignalState.Bad,
                        Order = order++,
                    });
                    continue;
                }

                results.Add(Describe(target, task, order++));
            }
        }
        catch (Exception ex)
        {
            Log.Error("schedule probe failed", ex);
        }

        return Task.FromResult<IReadOnlyList<Signal>>(results);
    }

    private static Signal Describe(WatchTarget target, SchedTask task, int order)
    {
        var next = task.NextRunTime;
        var last = task.LastRunTime;
        var code = task.LastTaskResult;
        var running = task.State == TaskState.Running;

        var hasNext = next > DateTime.Now && next < DateTime.Now.AddYears(1);
        var detail = running ? "running now"
                   : hasNext ? Until(next)
                   : "on demand";

        // 0 is success. 0x41301 is "still running" and is not a failure.
        var failed = code != 0 && code != 0x41301;

        var state = running ? SignalState.Live
                  : failed ? SignalState.Bad
                  : hasNext ? SignalState.Idle
                  : SignalState.Idle;

        string sub;
        if (failed)
        {
            sub = $"last run FAILED {Explain(code)}";
        }
        else if (last > new DateTime(2000, 1, 1))
        {
            sub = $"last ok {ProcessProbe.Ago(new DateTimeOffset(last))} ago";
            if (hasNext) sub += $" : {next:HH:mm}";
        }
        else
        {
            sub = target.Note;
        }

        return new Signal
        {
            Explain = BuildExplain(target, task, failed, code),
            Id = "sched:" + target.Name,
            Group = SignalGroup.Schedule,
            Title = target.Name,
            Detail = detail,
            Sub = sub,
            State = state,
            At = hasNext ? new DateTimeOffset(next) : null,
            Order = order,
        };
    }

    /// <summary>
    /// The long-form answer for the hover: what the task is for, the literal
    /// command line it runs, when it fires, and how it last went.
    ///
    /// Read out of the task rather than described from memory, so it cannot
    /// drift out of date when the task is edited.
    /// </summary>
    private static string BuildExplain(WatchTarget target, SchedTask task, bool failed, int code)
    {
        var sb = new System.Text.StringBuilder();

        if (!string.IsNullOrWhiteSpace(target.What))
        {
            sb.AppendLine(target.What.Trim());
            sb.AppendLine();
        }

        sb.AppendLine($"Scheduled task:  {task.Name}");

        try
        {
            foreach (var action in task.Definition.Actions)
            {
                if (action is Microsoft.Win32.TaskScheduler.ExecAction exec)
                {
                    var cmd = exec.Path;
                    if (!string.IsNullOrWhiteSpace(exec.Arguments)) cmd += " " + exec.Arguments;
                    sb.AppendLine($"Runs:  {Shorten(cmd, 240)}");
                }
                else
                {
                    sb.AppendLine($"Runs:  {action.ActionType}");
                }
            }

            foreach (var trigger in task.Definition.Triggers)
                sb.AppendLine($"Fires:  {DescribeTrigger(trigger)}");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"(could not read the task definition: {ex.Message})");
        }

        if (task.LastRunTime > new DateTime(2000, 1, 1))
            sb.AppendLine($"Last ran:  {task.LastRunTime:ddd HH:mm}");

        sb.AppendLine(failed
            ? $"Last result:  FAILED - {Explain(code)}"
            : "Last result:  ok");

        if (task.NextRunTime > DateTime.Now)
            sb.AppendLine($"Next:  {task.NextRunTime:ddd HH:mm}");

        return sb.ToString().TrimEnd();
    }

    private static string DescribeTrigger(Microsoft.Win32.TaskScheduler.Trigger trigger)
    {
        var repeat = trigger.Repetition?.Interval ?? TimeSpan.Zero;
        var every = repeat > TimeSpan.Zero
            ? $", then every {(repeat.TotalHours >= 1 ? $"{repeat.TotalHours:0.#}h" : $"{repeat.TotalMinutes:0}m")}"
            : "";

        return trigger switch
        {
            Microsoft.Win32.TaskScheduler.DailyTrigger d => $"daily at {d.StartBoundary:HH:mm}{every}",
            Microsoft.Win32.TaskScheduler.TimeTrigger t  => $"at {t.StartBoundary:HH:mm}{every}",
            Microsoft.Win32.TaskScheduler.LogonTrigger   => "when you log in",
            Microsoft.Win32.TaskScheduler.BootTrigger    => "at boot",
            _ => trigger.TriggerType.ToString().ToLowerInvariant() + every,
        };
    }

    private static string Shorten(string text, int max) =>
        text.Length <= max ? text : text[..max] + "...";

    private static string Until(DateTime when)
    {
        var d = when - DateTime.Now;
        if (d.TotalSeconds < 0) return "due";
        if (d.TotalMinutes < 1) return $"{d.TotalSeconds:0}s";
        if (d.TotalMinutes < 60) return $"{d.TotalMinutes:0}m";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours}h {d.Minutes:0}m";
        return $"{d.TotalDays:0}d";
    }

    /// <summary>
    /// Task Scheduler result codes are unsigned NTSTATUS values shown as
    /// signed ints. The ones that actually show up are worth naming, because
    /// "0xC000013A" tells you nothing and "console was closed" tells you
    /// exactly what to go and fix.
    /// </summary>
    private static string Explain(int code) => unchecked((uint)code) switch
    {
        0xC000013A => "console closed (CTRL+C) - the window is being dismissed",
        0x80070002 => "file not found",
        0x80070005 => "access denied",
        0x00000001 => "exit code 1",
        0x00000002 => "exit code 2",
        0x00041306 => "terminated by the scheduler",
        0x00041325 => "queued",
        _          => $"0x{unchecked((uint)code):X8}",
    };
}
