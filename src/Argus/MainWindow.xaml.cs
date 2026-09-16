using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Argus.Core;
using Argus.Probes;
using Argus.Views;

namespace Argus;

public partial class MainWindow : Window
{
    private readonly ArgusSettings _settings;
    private readonly PanelViewModel _vm = new();
    private readonly SystemStats _stats = new();
    private readonly ProcessScanner _scanner = new();

    private DockManager? _dock;
    private BreakdownWindow? _breakdown;

    /// <summary>Set while Argus is shutting down, so the breakdown really closes.</summary>
    private bool _closing;

    /// <summary>True while a breakdown is being assembled on a background thread.</summary>
    private volatile bool _detailBusy;
    private ProbeHost? _host;
    private TrayIcon? _tray;
    private DispatcherTimer? _tick;

    /// <summary>Ticks since launch, for the occasional housekeeping pass.</summary>
    private int _ticks;

    /// <summary>
    /// Set while settings are being written into the controls, so the change
    /// handlers do not write them straight back.
    ///
    /// Starts TRUE, and that is load-bearing. WPF coerces a Slider's Value up
    /// to its Minimum while InitializeComponent runs, which raises
    /// ValueChanged long before OnLoaded. With this flag starting false, the
    /// constructor itself overwrote the saved width and opacity with the
    /// slider minimums and persisted them - settings silently reset to 220px
    /// and 35% on every launch. OnLoaded clears it once the real values are in.
    /// </summary>
    private bool _loading = true;

    private bool _overlayTemporary;

    public MainWindow(ArgusSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        DataContext = _vm;

        Width = _settings.Width;
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _dock = new DockManager(this, _settings);
        _dock.PlacementChanged += () =>
        {
            if (SettingsPane.Visibility == Visibility.Visible) LoadSettingsPane();
            else UpdateWidthLabel();
        };
        _dock.Attach();
        UpdateModeTag();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _tray = new TrayIcon(
            onShow: BringToFront,
            onToggleOverlay: ToggleOverlay,
            onNextScreen: () => { _dock?.CycleScreen(); UpdateScreenHelp(); },
            onSettings: () => SetSettingsPane(true),
            onQuit: Quit);

        _host = new ProbeHost();
        _host.Add(new ProcessProbe(_settings, _scanner));
        _host.Add(new ServiceProbe(_settings, _scanner));
        _host.Add(new ScheduleProbe(_settings));
        _host.Add(new ClaudeCodeProbe(_settings));
        _host.Add(new FixProbe(_settings));

        _host.SignalsChanged += OnSignals;
        _host.Start();

        // One timer for the whole window. Two seconds is enough for a clock
        // that shows minutes and a meter that is a rough gauge, and it halves
        // the number of times this thread has to be woken.
        _tick = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _tick.Tick += (_, _) => UpdateHeader();
        _tick.Start();
        UpdateHeader();

        LoadSettingsPane();
        Startup.Sync(_settings);
        Log.Info("argus up");
    }

    // ------------------------------------------------------------- signals

    private void OnSignals(IReadOnlyList<Signal> signals)
    {
        // Probes run off-thread; the collections they feed are bound to the UI.
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background,
                new Action<IReadOnlyList<Signal>>(OnSignals), signals);
            return;
        }

        _vm.Sync(signals);

        // The breakdown is live while it is open: a tree that stops updating
        // the moment you look at it is a screenshot, not a panel.
        if (_breakdown?.IsVisible == true) RefreshDetail();
    }

    // ------------------------------------------------------------ breakdown

    /// <summary>
    /// Opens the pane that shows what a row is actually made of - the process
    /// tree under it, the consoles it opened, the task that owns it.
    ///
    /// The panel groups by kind, which is right for a glance and wrong for a
    /// question: one piece of work appears in three sections and reads as
    /// three unrelated things. This is the view that puts it back together.
    /// </summary>
    private void Breakdown_Click(object sender, RoutedEventArgs e)
    {
        // Handled, so opening the breakdown does not also collapse the
        // explanation the button is sitting in.
        e.Handled = true;

        if ((sender as FrameworkElement)?.DataContext is not SignalRow row) return;

        _vm.Detail.Open(row.Id, row.Title);
        RefreshDetail();

        if (_breakdown is null)
        {
            _breakdown = new BreakdownWindow(_vm.Detail, _settings);

            // Hidden rather than closed, every time. A closed WPF window cannot
            // be shown again, and this one is opened and dismissed casually.
            _breakdown.Closing += (_, args) =>
            {
                if (_closing) return;
                args.Cancel = true;
                _breakdown.Hide();
            };

            _breakdown.PlaceNear(this);
        }
        else if (!_breakdown.IsVisible)
        {
            _breakdown.PlaceNear(this);
        }

        _breakdown.Show();
        _breakdown.Activate();
    }

    /// <summary>
    /// Rebuilds the open breakdown, off the UI thread.
    ///
    /// Off-thread because assembling one is not free: it reads the process
    /// table, walks a tree, and asks Task Scheduler which tasks are running -
    /// a COM enumeration. Doing that on the dispatcher every few seconds is
    /// how a panel that boasts about costing nothing starts costing something,
    /// and it would stutter the whole window while it ran.
    /// </summary>
    private void RefreshDetail()
    {
        // One build at a time. If a sweep lands while the last one is still
        // going, skip it - the next sweep is three seconds away and the answer
        // would be the same.
        if (_detailBusy) return;
        _detailBusy = true;

        var rowId = _vm.Detail.RowId;
        var rowTitle = _vm.Detail.RowTitle;

        Task.Run(() =>
        {
            BreakdownReport? report = null;

            try
            {
                report = Breakdown.Build(rowId, rowTitle, _settings, _scanner);
            }
            catch (Exception ex)
            {
                // A breakdown that throws must not take the panel with it. The
                // window stays open showing whatever it last managed to build.
                Log.Error("breakdown failed", ex);
            }

            try
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    _detailBusy = false;

                    // Dropped if the user has moved on to another row while
                    // this was building; that row's own build is already on
                    // its way.
                    if (report is not null && _vm.Detail.RowId == rowId) _vm.Detail.Show(report);
                }));
            }
            catch (Exception)
            {
                // The dispatcher is shutting down. Clear the flag here instead,
                // so a build cannot be left permanently blocked by a marshal
                // that never arrived.
                _detailBusy = false;
            }
        });
    }



    private void UpdateHeader()
    {
        _stats.Update();

        _vm.Clock = DateTime.Now.ToString("HH:mm");
        _vm.CpuText = $"CPU {_stats.CpuPercent,3:0}%";
        _vm.MemText = $"RAM {_stats.MemUsedGb:0.0}/{_stats.MemTotalGb:0}G";
        _vm.CpuFraction = _stats.CpuPercent / 100.0;
        _vm.MemFraction = _stats.MemPercent / 100.0;

        // Argus reports its own footprint. A watchman that will not be watched
        // is not one you should trust to stay small.
        _vm.FootText = $"{_scanner.Count} processes   argus {_stats.SelfMb:0}M";

        Housekeep();
    }

    /// <summary>
    /// Hands back the pages startup needed and will never read again: XAML
    /// parsing, JIT, the settings load, the first process sweep.
    ///
    /// Once, and only once. A recurring trim was tried and measured: the
    /// working set drops to ~41MB and then faults straight back to ~70MB
    /// within thirty seconds, because those pages are genuinely in use. All it
    /// buys is a briefly prettier number in Task Manager, paid for in page
    /// faults. The startup pages are the only ones actually dead.
    /// </summary>
    private void Housekeep()
    {
        const int settled = 6;   // ~12s in, once startup has stopped moving

        if (_ticks++ != settled) return;

        try
        {
            Native.EmptyWorkingSet(Native.GetCurrentProcess());
        }
        catch (Exception ex)
        {
            Log.Warn($"working set trim failed: {ex.Message}");
        }
    }

    // -------------------------------------------------------------- chrome

    private void Header_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.ClickCount == 2) { _dock?.CycleScreen(); UpdateScreenHelp(); return; }

        // Just move it. Where the user lets go is where it stays: DockManager
        // records the final rectangle on WM_EXITSIZEMOVE and restores exactly
        // that next launch. No snapping, no correction afterwards.
        try { DragMove(); }
        catch (InvalidOperationException) { /* mouse already released */ }
    }

    private void BringToFront()
    {
        Show();
        WindowState = WindowState.Normal;

        // In desktop mode Activate() is immediately undone by the Z-order hook,
        // which is correct and also unhelpful if the user just asked to see it.
        // Flip to overlay for the occasion instead.
        if (_settings.DockMode == DockMode.Desktop)
        {
            _overlayTemporary = true;
            _settings.DockMode = DockMode.Overlay;
            _dock?.Apply();
            UpdateModeTag();
        }

        Activate();
    }

    private void ToggleOverlay()
    {
        _settings.DockMode = _settings.DockMode == DockMode.Overlay ? DockMode.Desktop : DockMode.Overlay;
        _overlayTemporary = false;
        _settings.Save();
        _dock?.Apply();
        UpdateModeTag();
        LoadSettingsPane();
    }

    private void UpdateModeTag()
    {
        ModeTag.Text = _settings.DockMode switch
        {
            DockMode.Overlay => "ON TOP",
            DockMode.Reserve => "RESERVED",
            _ => "DESKTOP",
        };

        OverlayButton.Content = _settings.DockMode == DockMode.Overlay ? "DROP BACK" : "OVERLAY";
    }

    // ------------------------------------------------------------- buttons

    /// <summary>
    /// Click a row to show what it actually is. Click again to fold it away.
    ///
    /// Only one row stays open at a time: the panel is a glance, and half a
    /// dozen open explanations would push everything else off the bottom.
    /// </summary>
    private void Row_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SignalRow row) return;
        if (!row.CanExpand) return;

        var opening = !row.IsExpanded;

        foreach (var section in new[] { _vm.Services, _vm.Schedule, _vm.Claude, _vm.Ghosts, _vm.Processes })
        {
            foreach (var other in section.Rows) other.IsExpanded = false;
        }

        row.IsExpanded = opening;
        e.Handled = true;
    }

    /// <summary>
    /// Start or stop the service on this row, or run the repair on it.
    ///
    /// The pid handed to Stop is the one already displayed, not a fresh search,
    /// so what gets stopped is exactly what was on screen when the button was
    /// pressed.
    /// </summary>
    private void Control_Click(object sender, RoutedEventArgs e)
    {
        // Marked handled so pressing the button does not also toggle the row.
        e.Handled = true;

        if ((sender as FrameworkElement)?.DataContext is not SignalRow row) return;
        if (row.ControlTarget is null) return;

        // A repair is looked up in its own list. It is not a watch target and
        // has no pid, no port and no running state - only a command.
        if (row.Control == ControlAction.Fix)
        {
            var fix = _settings.Fixes.FirstOrDefault(f => f.Name == row.ControlTarget);
            if (fix is null) return;

            var outcome = ServiceControl.RunFix(fix);

            // Recorded as well as shown: this row has no state for the next
            // sweep to replace the message with, so the probe puts it back.
            FixLog.Record(fix.Name, outcome);
            row.Note(outcome);
            _host?.RefreshNow();
            return;
        }

        var target = _settings.Targets.FirstOrDefault(t => t.Name == row.ControlTarget);
        if (target is null) return;

        var result = row.Control == ControlAction.Start
            ? ServiceControl.Start(target)
            : ServiceControl.Stop(target, row.Pid);

        // Say what happened on the row itself rather than in a dialog, and let
        // the next sweep replace it with the truth a few seconds later.
        row.Note(result);

        _host?.RefreshNow();
    }

    private void Overlay_Click(object sender, RoutedEventArgs e) => ToggleOverlay();

    private void Screen_Click(object sender, RoutedEventArgs e)
    {
        _dock?.CycleScreen();
        UpdateScreenHelp();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        _host?.RefreshNow();
        UpdateHeader();
    }

    private void Settings_Click(object sender, RoutedEventArgs e) =>
        SetSettingsPane(SettingsPane.Visibility != Visibility.Visible);

    private void SetSettingsPane(bool open)
    {
        SettingsPane.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        if (open) LoadSettingsPane();
    }

    private void Quit_Click(object sender, RoutedEventArgs e) => Quit();

    private void OpenSettingsFile_Click(object sender, RoutedEventArgs e)
    {
        _settings.Save();
        OpenInShell(ArgusSettings.FilePath);
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e) => OpenInShell(Log.LogPath);

    private static void OpenInShell(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn($"could not open {path}: {ex.Message}");
        }
    }

    // ------------------------------------------------------- settings pane

    private void LoadSettingsPane()
    {
        _loading = true;
        try
        {
            ModeDesktop.IsChecked = _settings.DockMode == DockMode.Desktop;
            ModeOverlay.IsChecked = _settings.DockMode == DockMode.Overlay;
            ModeReserve.IsChecked = _settings.DockMode == DockMode.Reserve;

            ModeHelp.Text = _settings.DockMode switch
            {
                DockMode.Desktop => "Pinned to the desktop. Windows open over it, the way they open over desktop icons.",
                DockMode.Overlay => "Floats above every window. Covers whatever is beneath it.",
                DockMode.Reserve => "Windows shrinks the desktop so maximised windows stop at this edge. Costs real screen width.",
                _ => "",
            };

            EdgeLeft.IsChecked = _settings.Edge == PanelEdge.Left;
            EdgeRight.IsChecked = _settings.Edge == PanelEdge.Right;

            WidthSlider.Value = Math.Clamp(_settings.Width, WidthSlider.Minimum, WidthSlider.Maximum);
            OpacitySlider.Value = Math.Clamp(_settings.Opacity, OpacitySlider.Minimum, OpacitySlider.Maximum);
            IntervalSlider.Value = Math.Clamp(_settings.ProcessIntervalSeconds,
                                              IntervalSlider.Minimum, IntervalSlider.Maximum);

            GhostCheck.IsChecked = _settings.GhostCatcher;
            ClaudeCheck.IsChecked = _settings.ShowClaudeCode;
            StartupCheck.IsChecked = _settings.RunOnStartup;

            UpdateWidthLabel();
            UpdateOpacityLabel();
            UpdateIntervalLabel();
            UpdateScreenHelp();

            VersionLabel.Text = $"Argus 1.0  ::  settings in {ArgusSettings.Directory}";
        }
        finally { _loading = false; }
    }

    private void UpdateWidthLabel() =>
        WidthLabel.Text = $"Width  {_settings.Width:0} px  (drag the left edge too)";

    private void UpdateOpacityLabel() =>
        OpacityLabel.Text = $"Opacity  {_settings.Opacity * 100:0}%";

    private void UpdateIntervalLabel() =>
        IntervalLabel.Text = $"Process sweep every {_settings.ProcessIntervalSeconds}s";

    private void UpdateScreenHelp()
    {
        if (_dock is null) return;

        var screen = _dock.TargetScreen;
        var which = screen.IsPrimary ? "main display" : "second display";
        ScreenHelp.Text = $"On the {which}  ::  {screen.Bounds.Width} x {screen.Bounds.Height}" +
                          $"  ::  {_dock.ScreenCount} attached";
        UpdateModeTag();
    }

    private void Mode_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var tag = (sender as FrameworkElement)?.Tag as string;
        _settings.DockMode = tag switch
        {
            "Overlay" => DockMode.Overlay,
            "Reserve" => DockMode.Reserve,
            _ => DockMode.Desktop,
        };

        _overlayTemporary = false;
        _settings.Save();
        _dock?.Apply();
        LoadSettingsPane();
    }

    private void Edge_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var edge = (sender as FrameworkElement)?.Tag as string == "Left"
            ? PanelEdge.Left
            : PanelEdge.Right;

        // An explicit edge press is a request to abandon the free position and
        // go back to a full-height dock, so it clears the remembered rectangle.
        _dock?.RedockToEdge(edge);
        LoadSettingsPane();
    }

    private void Width_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        _settings.Width = e.NewValue;
        _dock?.ApplyWidth(e.NewValue);
        UpdateWidthLabel();
    }

    private void Opacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        _settings.Opacity = e.NewValue;
        _settings.Save();
        _dock?.ApplyOpacity();
        UpdateOpacityLabel();
    }

    private void Interval_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        _settings.ProcessIntervalSeconds = (int)e.NewValue;
        _settings.Save();
        UpdateIntervalLabel();
    }

    private void Ghost_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.GhostCatcher = GhostCheck.IsChecked == true;
        _settings.Save();
        _host?.RefreshNow();
    }

    private void Claude_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.ShowClaudeCode = ClaudeCheck.IsChecked == true;
        _settings.Save();
        _host?.RefreshNow();
    }

    private void Startup_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.RunOnStartup = StartupCheck.IsChecked == true;
        _settings.Save();
        Startup.Set(_settings.RunOnStartup);
    }

    // ------------------------------------------------------------ shutdown

    private void Quit()
    {
        Log.Info("argus shutting down");
        _closing = true;
        _tick?.Stop();
        _host?.Dispose();
        _tray?.Dispose();

        // Where the breakdown window was left is only in memory until now; a
        // drag does not deserve a file write per pixel.
        if (_breakdown is not null)
        {
            _settings.Save();
            _breakdown.Close();
        }

        // Unregisters the appbar. Skipping this leaves a permanent dead strip
        // of reserved desktop that survives the process and needs a shell
        // restart to clear.
        _dock?.Detach();

        Application.Current.Shutdown();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_overlayTemporary)
        {
            _settings.DockMode = DockMode.Desktop;
            _settings.Save();
        }
        _dock?.Detach();
    }
}
