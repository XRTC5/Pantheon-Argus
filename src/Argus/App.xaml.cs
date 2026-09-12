using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Argus.Core;

namespace Argus;

public partial class App : Application
{
    /// <summary>
    /// Named kernel mutex, so a second Argus cannot start.
    ///
    /// This matters more than usual here. Two instances in Reserve mode would
    /// each register an appbar and carve their own strip out of the desktop,
    /// and two ghost catchers would double-report every console window. The
    /// Run key plus an already-running instance after an update is exactly how
    /// that happens by accident.
    /// </summary>
    private Mutex? _single;

    protected override void OnStartup(StartupEventArgs e)
    {
        _single = new Mutex(initiallyOwned: true, "Local\\ArgusSingleInstance", out var isFirst);
        if (!isFirst)
        {
            Log.Info("another Argus is already running; this one is standing down");
            Shutdown();
            return;
        }

        // Software rendering, deliberately, and against the usual advice.
        //
        // WPF defaults to Direct3D. On this machine that means loading both
        // vendors' user-mode display drivers - nvgpucomp64, nvd3dumx,
        // igd9dxva64, igc64 - which between them map over 250MB and dominate
        // the working set of an app whose own heap is a few megabytes.
        //
        // The usual objection to software rendering is animation cost, and
        // Argus has no animation: solid fills, 1px rules, text, repainted only
        // when a value actually changes. It pays CPU for the handful of small
        // repaints it does, and stops paying for a GPU pipeline it never uses.
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            // A watchman that dies silently is worse than no watchman. Log it,
            // keep the panel alive, and let the next sweep carry on.
            Log.Error("unhandled UI exception", args.Exception);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("unhandled exception", args.ExceptionObject as Exception);

        var settings = ArgusSettings.Load();
        var window = new MainWindow(settings);
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _single?.ReleaseMutex();
        _single?.Dispose();
        base.OnExit(e);
    }
}
