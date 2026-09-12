using System.Diagnostics;
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
        _tick?.Stop();
        _host?.Dispose();
        _tray?.Dispose();

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
