using System.Collections.ObjectModel;
using Brisk.Services;

namespace Brisk.ViewModels;

public sealed class LagEventVm(LagEvent e)
{
    public LagEvent Event { get; } = e;
    public string When
    {
        get
        {
            var d = Event.Start.Date;
            string day = d == DateTime.Today ? "Today" : d == DateTime.Today.AddDays(-1) ? "Yesterday" : Event.Start.ToString("ddd d MMM");
            return $"{day}, {Event.Start:t}";
        }
    }
    public string Duration => Event.Seconds >= 60 ? $"{Event.Seconds / 60:0} min {Event.Seconds % 60:0} s" : $"{Event.Seconds:0} s";
    public string Headline => Event.Headline;
    public string Detail => Event.Detail;
    public string KindsText => string.Join(" + ", Event.Kinds);
    public string CulpritsText => Event.Culprits.Count == 0 ? "" : "Top causes: " + string.Join(", ", Event.Culprits);
    public string Level => Event.Seconds >= 30 ? "danger" : "warn";
}

/// <summary>The lag black box: every slowdown Brisk saw, and who caused it.</summary>
public sealed class HistoryViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    public ObservableCollection<LagEventVm> Events { get; } = [];

    private string _summary = "";
    public string Summary { get => _summary; private set => Set(ref _summary, value); }

    public bool IsEmpty => Events.Count == 0;

    public bool RecordInBackground
    {
        get => _main.Settings.RecordInBackground;
        set
        {
            _main.Settings.RecordInBackground = value;
            _main.Settings.Save();
            _main.Recorder.Enabled = value;
            Raise();
            _main.ShowToast(value ? "Brisk will keep recording slowdowns from the tray when you close it."
                                  : "Recording is off. Closing Brisk now exits it.");
        }
    }

    public bool StartWithWindows
    {
        get { try { return Settings.StartWithWindows; } catch { return false; } }
        set
        {
            try
            {
                Settings.StartWithWindows = value;
                _main.ShowToast(value ? "Brisk will start quietly in the tray when you sign in." : "Brisk won't start with Windows.");
            }
            catch (Exception ex) { _main.ShowToast("Couldn't change that: " + ex.Message); }
            Raise();
        }
    }

    public RelayCommand ClearCommand { get; }

    public HistoryViewModel(MainViewModel main)
    {
        _main = main;
        foreach (var e in _main.Recorder.Events) Events.Add(new LagEventVm(e));
        ClearCommand = new RelayCommand(Clear, () => Events.Count > 0);
        Update();
    }

    public void Add(LagEvent e)
    {
        Events.Insert(0, new LagEventVm(e));
        Update();
    }

    private async void Clear()
    {
        if (!await _main.AskAsync("Clear lag history?", "All recorded slowdowns will be removed.", "Clear")) return;
        _main.Recorder.Clear();
        Events.Clear();
        Update();
    }

    private void Update()
    {
        Raise(nameof(IsEmpty));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        var week = Events.Where(e => e.Event.Start >= DateTime.Now.AddDays(-7)).ToList();
        if (Events.Count == 0)
        {
            Summary = "No slowdowns recorded yet";
            _main.Dashboard.LastLag = null;
            return;
        }
        var common = week.SelectMany(e => e.Event.Culprits.Take(1)).GroupBy(c => c).OrderByDescending(g => g.Count()).FirstOrDefault();
        Summary = $"{week.Count} slowdown{(week.Count == 1 ? "" : "s")} in the last 7 days"
                + (common != null ? $"  ·  most common cause: {common.Key}" : "");
        var last = Events[0];
        _main.Dashboard.LastLag = $"Last slowdown: {last.When.ToLowerInvariant()}, {last.Duration}. {last.Headline}.";
    }
}
