using System.Runtime.InteropServices;

namespace Argus.Core;

/// <summary>
/// Machine-wide CPU and memory for the panel header.
///
/// Uses GetSystemTimes rather than PerformanceCounter. A PerformanceCounter
/// for "% Processor Time" drags in the whole PDH stack, costs several MB, and
/// takes a second to give its first honest reading. Two kernel counters
/// differenced by hand cost nothing and are exact.
/// </summary>
public sealed class SystemStats
{
    private long _prevIdle, _prevKernel, _prevUser;
    private bool _primed;

    public double CpuPercent { get; private set; }
    public double MemUsedGb { get; private set; }
    public double MemTotalGb { get; private set; }
    public double MemPercent { get; private set; }

    /// <summary>Working set of Argus itself, in MB. Shown because it should be held to account.</summary>
    public double SelfMb { get; private set; }

    public void Update()
    {
        UpdateCpu();
        UpdateMemory();
        UpdateSelf();
    }

    private void UpdateCpu()
    {
        if (!Native.GetSystemTimes(out var idle, out var kernel, out var user)) return;

        if (_primed)
        {
            // Kernel time already includes idle time, so idle is subtracted out
            // rather than added in. Getting this backwards is the classic way
            // to report a permanently busy machine.
            var idleDelta = idle - _prevIdle;
            var totalDelta = (kernel - _prevKernel) + (user - _prevUser);

            if (totalDelta > 0)
                CpuPercent = Math.Clamp(100.0 * (totalDelta - idleDelta) / totalDelta, 0, 100);
        }

        _prevIdle = idle;
        _prevKernel = kernel;
        _prevUser = user;
        _primed = true;
    }

    private void UpdateMemory()
    {
        var status = new Native.MEMORYSTATUSEX
        {
            dwLength = (uint)Marshal.SizeOf<Native.MEMORYSTATUSEX>(),
        };

        if (!Native.GlobalMemoryStatusEx(ref status)) return;

        MemTotalGb = status.ullTotalPhys / 1024d / 1024d / 1024d;
        MemUsedGb = (status.ullTotalPhys - status.ullAvailPhys) / 1024d / 1024d / 1024d;
        MemPercent = status.dwMemoryLoad;
    }

    private void UpdateSelf()
    {
        try
        {
            // Environment.WorkingSet is a plain syscall. Process.GetCurrentProcess()
            // would allocate a managed Process object every two seconds forever.
            SelfMb = Environment.WorkingSet / 1024d / 1024d;
        }
        catch { /* not worth failing the header over */ }
    }
}
