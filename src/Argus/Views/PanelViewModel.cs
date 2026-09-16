using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Argus.Probes;

namespace Argus.Views;

/// <summary>One rendered row. Mutable so the list can be updated in place.</summary>
public sealed class SignalRow : INotifyPropertyChanged
{
    public string Id { get; }

    /// <summary>Which section this row belongs to. Fixed for the life of the row.</summary>
    public SignalGroup Group { get; }

    private string _title = "";
    private string _detail = "";
    private string? _sub;
    private SignalState _state;
    private int _pid;
    private string? _explain;
    private bool _expanded;
    private ControlAction _control;
    private string? _controlTarget;

    public SignalRow(Signal s)
    {
        Id = s.Id;
        Group = s.Group;
        Apply(s);
    }

    public string Title { get => _title; private set => Set(ref _title, value); }
    public string Detail { get => _detail; private set => Set(ref _detail, value); }
    public string? Sub { get => _sub; private set => Set(ref _sub, value); }
    public SignalState State { get => _state; private set => Set(ref _state, value); }
    public int Pid { get => _pid; private set => Set(ref _pid, value); }
    public string? Explain { get => _explain; private set => Set(ref _explain, value); }

    /// <summary>
    /// Whether this row is showing its explanation.
    ///
    /// Lives on the row rather than in the panel, and survives every refresh
    /// for free: rows are reconciled by id, so an expanded row stays expanded
    /// while its numbers keep updating underneath.
    /// </summary>
    public bool IsExpanded
    {
        get => _expanded;
        set => Set(ref _expanded, value);
    }

    /// <summary>True when there is an explanation worth expanding to.</summary>
    public bool CanExpand => !string.IsNullOrWhiteSpace(_explain);

    public ControlAction Control { get => _control; private set => Set(ref _control, value); }
    public string? ControlTarget { get => _controlTarget; private set => Set(ref _controlTarget, value); }

    /// <summary>Button caption, and empty when there is no button.</summary>
    public string ControlText => _control switch
    {
        ControlAction.Start => "START",
        ControlAction.Stop  => "STOP",
        ControlAction.Fix   => "FIX",
        _ => "",
    };

    public bool HasControl => _control != ControlAction.None;

    public bool HasSub => !string.IsNullOrWhiteSpace(_sub);

    /// <summary>
    /// Shows a one-off message in place of the subtitle - "stopped",
    /// "started (pid 1234)", or why it did not work.
    ///
    /// Deliberately not sticky: the next probe sweep overwrites it with real
    /// state a few seconds later, which is the answer that actually matters.
    /// </summary>
    public void Note(string message) => Sub = message;

    public void Apply(Signal s)
    {
        Title = s.Title;
        Detail = s.Detail;
        Sub = s.Sub;
        State = s.State;
        Pid = s.Pid;
        Explain = s.Explain;
        Control = s.Control;
        ControlTarget = s.ControlTarget;
        OnPropertyChanged(nameof(HasSub));
        OnPropertyChanged(nameof(CanExpand));
        OnPropertyChanged(nameof(ControlText));
        OnPropertyChanged(nameof(HasControl));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(name);
    }

    private void OnPropertyChanged(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// One titled block on the panel.
///
/// Rows are reconciled by id rather than cleared and refilled. Clearing an
/// ObservableCollection every three seconds throws away every container WPF
/// built, forces a full re-layout, and makes the list flicker and lose its
/// scroll position. Reconciling touches only what actually changed, which on a
/// quiet machine is nothing at all.
/// </summary>
public sealed class Section : INotifyPropertyChanged
{
    private readonly SignalGroup _group;
    private bool _visible;

    public Section(string title, SignalGroup group)
    {
        Title = title;
        _group = group;
    }

    public string Title { get; }
    public ObservableCollection<SignalRow> Rows { get; } = [];

    public bool IsVisible
    {
        get => _visible;
        private set
        {
            if (_visible == value) return;
            _visible = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsVisible)));
        }
    }

    public void Sync(IReadOnlyList<Signal> all)
    {
        var incoming = all.Where(s => s.Group == _group)
                          .OrderBy(s => s.Order)
                          .ToList();

        // Update or insert, in order.
        for (var i = 0; i < incoming.Count; i++)
        {
            var s = incoming[i];
            var existingIndex = IndexOf(s.Id);

            if (existingIndex < 0)
            {
                Rows.Insert(Math.Min(i, Rows.Count), new SignalRow(s));
            }
            else
            {
                Rows[existingIndex].Apply(s);
                if (existingIndex != i) Rows.Move(existingIndex, Math.Min(i, Rows.Count - 1));
            }
        }

        // Drop anything that went away.
        for (var i = Rows.Count - 1; i >= incoming.Count; i--) Rows.RemoveAt(i);

        IsVisible = Rows.Count > 0;
    }

    private int IndexOf(string id)
    {
        for (var i = 0; i < Rows.Count; i++)
            if (Rows[i].Id == id) return i;
        return -1;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>One group inside the breakdown pane.</summary>
public sealed class DetailGroup
{
    public required string Title { get; init; }
    public required IReadOnlyList<Argus.Core.BreakdownItem> Items { get; init; }
    public required string Empty { get; init; }
    public bool HasItems => Items.Count > 0;
    public bool IsEmpty => Items.Count == 0;
}

/// <summary>
/// The breakdown pane: what is actually underneath one row.
///
/// Rebuilt only when its content changes rather than on every sweep. The pane
/// is open while someone is reading it, and a list that reconstructs itself
/// three times a second loses the scroll position mid-sentence.
/// </summary>
public sealed class DetailViewModel : INotifyPropertyChanged
{
    private string _title = "";
    private string _summary = "";
    private bool _isOpen;
    private string _signature = "";

    public string Title { get => _title; private set => Set(ref _title, value); }
    public string Summary { get => _summary; private set => Set(ref _summary, value); }

    public bool IsOpen
    {
        get => _isOpen;
        set => Set(ref _isOpen, value);
    }

    /// <summary>The row this pane is describing, so a refresh can rebuild it.</summary>
    public string RowId { get; private set; } = "";
    public string RowTitle { get; private set; } = "";

    public ObservableCollection<DetailGroup> Groups { get; } = [];

    public void Open(string rowId, string rowTitle)
    {
        RowId = rowId;
        RowTitle = rowTitle;
        _signature = "";
        IsOpen = true;
    }

    public void Show(Argus.Core.BreakdownReport report)
    {
        Title = report.Title;
        Summary = report.Summary;

        // Cheap change detection. Everything rendered is in here, so if the
        // signature matches, redrawing would produce the identical pane.
        var signature = string.Join("|", report.Groups.Select(g =>
            g.Title + "#" + string.Join(",", g.Items.Select(i => i.Title + i.Detail + i.Sub))));

        if (signature == _signature) return;
        _signature = signature;

        Groups.Clear();
        foreach (var g in report.Groups)
        {
            Groups.Add(new DetailGroup { Title = g.Title, Items = g.Items, Empty = g.Empty });
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public sealed class PanelViewModel : INotifyPropertyChanged
{
    public DetailViewModel Detail { get; } = new();

    public Section Services { get; } = new("SERVICES", SignalGroup.Service);
    public Section Schedule { get; } = new("SCHEDULE", SignalGroup.Schedule);
    public Section Claude   { get; } = new("CLAUDE CODE", SignalGroup.Claude);
    public Section Ghosts   { get; } = new("CAUGHT CONSOLES", SignalGroup.Ghost);
    public Section Processes { get; } = new("PROCESSES", SignalGroup.Process);

    /// <summary>
    /// Last, and deliberately so. A repair is the rarest thing on this panel
    /// and the only one that changes the machine, so it sits below everything
    /// that merely reports.
    /// </summary>
    public Section Fixes { get; } = new("FIXES", SignalGroup.Fix);

    private string _clock = "";
    private string _cpuText = "";
    private string _memText = "";
    private string _footText = "";
    private double _cpuFraction;
    private double _memFraction;

    public string Clock { get => _clock; set => Set(ref _clock, value); }
    public string CpuText { get => _cpuText; set => Set(ref _cpuText, value); }
    public string MemText { get => _memText; set => Set(ref _memText, value); }
    public string FootText { get => _footText; set => Set(ref _footText, value); }
    public double CpuFraction { get => _cpuFraction; set => Set(ref _cpuFraction, value); }
    public double MemFraction { get => _memFraction; set => Set(ref _memFraction, value); }

    public void Sync(IReadOnlyList<Signal> signals)
    {
        Services.Sync(signals);
        Schedule.Sync(signals);
        Claude.Sync(signals);
        Ghosts.Sync(signals);
        Processes.Sync(signals);
        Fixes.Sync(signals);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
