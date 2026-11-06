using System.Collections.ObjectModel;
using System.Windows.Media;
using Brisk.Services;

namespace Brisk.ViewModels;

public sealed class BoostCandidate
{
    public required int Pid { get; init; }
    public required string Image { get; init; }
    public required string Title { get; init; }
    public ImageSource? Icon { get; init; }
    public double Gpu { get; init; }
    public string Subtitle => Gpu >= 1 ? $"{Image}  ·  GPU {Gpu:0}%" : Image;
}

/// <summary>Game boost page: pick the game, Brisk quiets everything else and restores it when the game closes.</summary>
public sealed class BoostViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private string? _preferImage;

    public ObservableCollection<BoostCandidate> Candidates { get; } = [];

    private BoostCandidate? _selected;
    public BoostCandidate? Selected { get => _selected; set => SetAndRequery(ref _selected, value); }

    private bool _active;
    public bool Active { get => _active; private set { if (SetAndRequery(ref _active, value)) Raise(nameof(Inactive)); } }
    public bool Inactive => !Active;

    private bool _busy;
    public bool Busy { get => _busy; private set => SetAndRequery(ref _busy, value); }

    private string _statusTitle = "", _statusDetail = "";
    public string StatusTitle { get => _statusTitle; private set => Set(ref _statusTitle, value); }
    public string StatusDetail { get => _statusDetail; private set => Set(ref _statusDetail, value); }

    public bool IsEmpty => Candidates.Count == 0;

    public RelayCommand RefreshCommand { get; }
    public RelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }

    public BoostViewModel(MainViewModel main)
    {
        _main = main;
        RefreshCommand = new RelayCommand(() => _ = Refresh(), () => !Busy);
        StartCommand = new RelayCommand(() => _ = Start(), () => Selected != null && !Active && !Busy);
        StopCommand = new RelayCommand(() => _ = Stop("Boost stopped."), () => Active && !Busy);
    }

    public async Task Refresh()
    {
        var procs = _main.Monitor.Last?.Procs ?? [];
        var list = await Task.Run(() => GameBoost.Candidates(procs));
        var keep = Selected?.Pid;
        Candidates.Clear();
        foreach (var c in list)
            Candidates.Add(new BoostCandidate { Pid = c.Pid, Image = c.Image, Title = c.Title, Icon = IconCache.GetOrAvatar(c.Path, c.Title), Gpu = c.Gpu });
        Selected = Candidates.FirstOrDefault(c => c.Pid == keep)
                ?? (_preferImage != null ? Candidates.FirstOrDefault(c => c.Image.Equals(_preferImage, StringComparison.OrdinalIgnoreCase)) : null)
                ?? Candidates.FirstOrDefault(c => c.Gpu >= 10);
        _preferImage = null;
        Raise(nameof(IsEmpty));
    }

    /// <summary>From a diagnosis fix button: open this page with the game preselected.</summary>
    public void OpenFor(string image)
    {
        _preferImage = image;
        _main.Page = Page.Boost;
        _ = Refresh();
    }

    private async Task Start()
    {
        if (Selected is not { } game) return;
        Busy = true;
        try
        {
            await _main.Dashboard.ReloadPlans();
            var plans = _main.Dashboard.Plans.ToList();
            var procs = _main.Monitor.Last?.Procs ?? [];
            var r = await Task.Run(() => _main.Boost.Start(game.Pid, procs, plans));
            Active = true;
            StatusTitle = $"Boosting {game.Title}";
            StatusDetail = $"{r.Throttled} background apps are in Efficiency mode"
                         + (r.FreedBytes > 0 ? $"  ·  {Fmt.Bytes(r.FreedBytes)} of RAM freed" : "")
                         + (r.PlanName != null ? $"  ·  power plan: {r.PlanName}" : "")
                         + (r.StandbyCleared ? "  ·  standby cache cleared" : "")
                         + ". Everything goes back to normal when you close the game.";
            _main.ShowToast($"Boost on for {game.Title}.");
            await _main.Dashboard.ReloadPlans();
        }
        catch (Exception ex) { _main.ShowToast("Couldn't start boost: " + ex.Message); }
        finally { Busy = false; }
    }

    public async Task Stop(string message)
    {
        Busy = true;
        try
        {
            int restored = await Task.Run(_main.Boost.Stop);
            Active = false;
            StatusTitle = StatusDetail = "";
            _main.ShowToast($"{message} Restored {restored} apps and your power plan.");
            await _main.Dashboard.ReloadPlans();
            await Refresh();
        }
        finally { Busy = false; }
    }
}
