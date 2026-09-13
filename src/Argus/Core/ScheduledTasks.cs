using TaskService = Microsoft.Win32.TaskScheduler.TaskService;

namespace Argus.Core;

/// <summary>
/// Which scheduled task a process belongs to.
///
/// The Task Scheduler starts its work from the service host, so everything it
/// runs reports a parent of svchost.exe. That is true and completely useless:
/// it names the mechanism and never the job. A panel that says "a console
/// appeared, from svchost" has told its owner nothing they can act on, which
/// is how it gets ignored.
///
/// Running tasks carry the pid of the process they started, so the answer is
/// available for as long as the task is still going - which for anything worth
/// noticing is long enough. The name is cached the first time it is asked for,
/// because the console that provoked the question usually outlives the answer.
/// </summary>
public static class ScheduledTasks
{
    private static readonly Dictionary<int, string> Cache = [];

    /// <summary>Insertion order, so the ceiling evicts the oldest entry.</summary>
    private static readonly Queue<int> Order = new();

    private static readonly object Gate = new();

    /// <summary>Rate limit. An enumeration is COM, not a dictionary lookup.</summary>
    private static DateTimeOffset _lastSweep = DateTimeOffset.MinValue;
    private static readonly TimeSpan MinGap = TimeSpan.FromSeconds(2);

    /// <summary>Processes the Task Scheduler runs its work under.</summary>
    public static bool IsSchedulerHost(string processName) =>
        processName.Equals("svchost.exe", StringComparison.OrdinalIgnoreCase) ||
        processName.Equals("services.exe", StringComparison.OrdinalIgnoreCase) ||
        processName.Equals("taskeng.exe", StringComparison.OrdinalIgnoreCase) ||
        processName.Equals("taskhostw.exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The task that owns this pid, or empty if it is not a task or the task
    /// has already finished.
    /// </summary>
    public static string NameOf(int pid)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(pid, out var known)) return known;
            if (DateTimeOffset.Now - _lastSweep < MinGap) return "";
            _lastSweep = DateTimeOffset.Now;
        }

        try
        {
            using var service = new TaskService();

            // Includes tasks hidden from the Task Scheduler UI. A hidden task
            // is exactly the kind that flashes a console at 03:00 and leaves
            // its owner with no idea what it was.
            foreach (var running in service.GetRunningTasks(true))
            {
                using (running)
                {
                    var enginePid = (int)running.EnginePID;
                    if (enginePid == 0) continue;

                    lock (Gate)
                    {
                        if (Cache.TryAdd(enginePid, running.Name)) Order.Enqueue(enginePid);
                        else Cache[enginePid] = running.Name;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"running task lookup failed: {ex.Message}");
            return "";
        }

        lock (Gate)
        {
            // Bounded, oldest out first. Clearing the lot would make a busy
            // machine forget tasks it had already named, so the panel would
            // show "started by svchost" again for something it knew a minute
            // ago.
            while (Order.Count > 256)
            {
                Cache.Remove(Order.Dequeue());
            }

            return Cache.TryGetValue(pid, out var name) ? name : "";
        }
    }

    private static IReadOnlyDictionary<int, string>? _running;
    private static DateTimeOffset _runningTaken = DateTimeOffset.MinValue;

    /// <summary>
    /// Every task running right now, by pid.
    ///
    /// Cached for a couple of seconds because the breakdown asks for it
    /// several times while assembling one view, and each uncached ask is a COM
    /// object and a full enumeration. Running tasks do not turn over fast
    /// enough for two seconds of staleness to be visible.
    /// </summary>
    public static IReadOnlyDictionary<int, string> Running()
    {
        lock (Gate)
        {
            if (_running is not null && DateTimeOffset.Now - _runningTaken < TimeSpan.FromSeconds(2))
                return _running;
        }

        var fresh = ReadRunning();

        lock (Gate)
        {
            _running = fresh;
            _runningTaken = DateTimeOffset.Now;
            return fresh;
        }
    }

    private static IReadOnlyDictionary<int, string> ReadRunning()
    {
        try
        {
            using var service = new TaskService();
            var map = new Dictionary<int, string>();
            foreach (var running in service.GetRunningTasks(true))
            {
                using (running)
                {
                    var pid = (int)running.EnginePID;
                    if (pid != 0) map[pid] = running.Name;
                }
            }
            return map;
        }
        catch (Exception ex)
        {
            Log.Warn($"running task enumeration failed: {ex.Message}");
            return new Dictionary<int, string>();
        }
    }
}
