using System.Linq;
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

        // Codes that are not failures, however alarming they look:
        //   0x00041301  the task is running right now
        //   0x00041325  queued, waiting its turn
        //   0x800710E0  a fire was skipped because the last run is still going,
        //               which is exactly what MultipleInstances=IgnoreNew means
        var failed = code != 0
                  && code != 0x41301
                  && code != 0x41325
                  && unchecked((uint)code) != 0x800710E0;

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
    /// What this row is, in one plain sentence, then the few facts worth
    /// having.
    ///
    /// The sentence comes first and always exists. An earlier version led with
    /// the task name and the literal command line, which answers "what does it
    /// run" but not "what is it for" - and the second question is the one
    /// someone actually has when they click a label they wrote months ago.
    /// </summary>
    private static string BuildExplain(WatchTarget target, SchedTask task, bool failed, int code)
    {
        var sb = new System.Text.StringBuilder();

        sb.AppendLine(string.IsNullOrWhiteSpace(target.What)
            ? "A Windows scheduled task. Argus counts down to its next run and says whether the "
              + "last one actually worked."
            : target.What.Trim());

        sb.AppendLine();

        try
        {
            var triggers = task.Definition.Triggers;
            if (triggers.Count > 0)
                sb.AppendLine($"Fires   {string.Join("; ", triggers.Select(DescribeTrigger))}");
        }
        catch { /* a task we cannot read is still worth describing */ }

        var ago = task.LastRunTime > new DateTime(2000, 1, 1)
            ? $", {ProcessProbe.Ago(new DateTimeOffset(task.LastRunTime))} ago"
            : "";

        sb.AppendLine(failed
            ? $"Last    FAILED - {Explain(code)}"
            : code == 0 || task.LastRunTime <= new DateTime(2000, 1, 1)
                ? (string.IsNullOrEmpty(ago) ? "Last    has not run yet" : $"Last    ok{ago}")
                : $"Last    {Explain(code)}{ago}");

        if (task.NextRunTime > DateTime.Now && task.NextRunTime < DateTime.Now.AddYears(1))
            sb.AppendLine($"Next    {task.NextRunTime:ddd HH:mm}");

        try
        {
            var exec = task.Definition.Actions
                .OfType<Microsoft.Win32.TaskScheduler.ExecAction>()
                .FirstOrDefault();

            if (exec is not null)
            {
                var cmd = exec.Path;
                if (!string.IsNullOrWhiteSpace(exec.Arguments)) cmd += " " + exec.Arguments;
                sb.AppendLine($"Runs    {Shorten(cmd, 150)}");
            }
        }
        catch { /* as above */ }

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
        0x00041325 => "queued, waiting its turn",
        0x800710E0 => "skipped - the previous run was still going",
        _          => $"0x{unchecked((uint)code):X8}",
    };
}
