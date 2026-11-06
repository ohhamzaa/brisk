using System.Collections.ObjectModel;
using Brisk.Services;

namespace Brisk.ViewModels;

public enum DiagnoseState { Idle, Running, Done }

public sealed class FixVm(string label, RelayCommand command)
{
    public string Label { get; } = label;
    public RelayCommand Command { get; } = command;
}

public sealed class IssueVm
{
    public required Issue Issue { get; init; }
    public string Headline => Issue.Headline;
    public string Detail => Issue.Detail;
    public string Level => Issue.Severity >= 0.6 ? "danger" : "warn";
    public string KindLabel => Issue.Kind switch
    {
        IssueKind.Cpu => "CPU",
        IssueKind.Memory => "MEMORY",
        IssueKind.Disk => "DISK",
        IssueKind.Throttle => "CPU SPEED",
        IssueKind.Gpu => "GRAPHICS",
        IssueKind.Vram => "VIDEO MEMORY",
        IssueKind.Network => "NETWORK",
        _ => "STORAGE",
    };
    public System.Windows.Media.ImageSource? CulpritIcon { get; init; }
    public string? CulpritName => Issue.Culprit?.Name;
    public string? CulpritStats => Issue.Culprit?.Stats;
    public bool HasCulprit => Issue.Culprit != null;
    public List<FixVm> Fixes { get; init; } = [];
}

/// <summary>"Why is my PC slow?": watch for 10 seconds, then explain the cause with one-click fixes.</summary>
public sealed class DiagnoseViewModel : ObservableObject
{
    public const int Seconds = 10;
    private readonly MainViewModel _main;
    private readonly List<Sample> _samples = [];
    private readonly object _gate = new();
    private volatile bool _collecting;

    private DiagnoseState _state = DiagnoseState.Idle;
    public DiagnoseState State
    {
        get => _state;
        private set { if (SetAndRequery(ref _state, value)) { Raise(nameof(IsIdle)); Raise(nameof(IsRunning)); Raise(nameof(IsDone)); } }
    }
    public bool IsIdle => State == DiagnoseState.Idle;
    public bool IsRunning => State == DiagnoseState.Running;
    public bool IsDone => State == DiagnoseState.Done;

    private double _progress;
    public double Progress { get => _progress; private set => Set(ref _progress, value); }

    private string _countdown = "";
    public string Countdown { get => _countdown; private set => Set(ref _countdown, value); }

    private string _live = "";
    public string Live { get => _live; private set => Set(ref _live, value); }

    private string _verdict = "", _verdictDetail = "";
    public string Verdict { get => _verdict; private set => Set(ref _verdict, value); }
    public string VerdictDetail { get => _verdictDetail; private set => Set(ref _verdictDetail, value); }

    private bool _healthy;
    public bool Healthy { get => _healthy; private set => Set(ref _healthy, value); }

    private string _summary = "";
    public string Summary { get => _summary; private set => Set(ref _summary, value); }

    public ObservableCollection<IssueVm> Issues { get; } = [];

    public RelayCommand StartCommand { get; }
    public RelayCommand OpenHistoryCommand { get; }

    public DiagnoseViewModel(MainViewModel main)
    {
        _main = main;
        StartCommand = new RelayCommand(Start, () => State != DiagnoseState.Running);
        OpenHistoryCommand = new RelayCommand(() => _main.Page = Page.History);
    }

    public void Start()
    {
        if (State == DiagnoseState.Running) return;
        lock (_gate) _samples.Clear();
        Progress = 0;
        Countdown = $"{Seconds}s";
        Live = "";
        State = DiagnoseState.Running;
        _collecting = true;
    }

    /// <summary>Monitor thread, once a second.</summary>
    public void Feed(Sample s)
    {
        if (!_collecting) return;
        int n;
        lock (_gate) { _samples.Add(s); n = _samples.Count; }
        string disk = double.IsNaN(s.DiskBusy) ? "" : $"   ·   Disk {s.DiskBusy:0}%";
        string gpu = double.IsNaN(s.GpuPct) ? "" : $"   ·   GPU {s.GpuPct:0}%";
        string ping = s.Net.Probing && !double.IsNaN(s.Net.InternetMs) ? $"   ·   Ping {s.Net.InternetMs:0} ms" : "";
        string live = $"CPU {s.Cpu:0}%   ·   Memory {s.MemUsedPct:0}%{disk}{gpu}{ping}";
        MainViewModel.Ui(() =>
        {
            Progress = Math.Min(1, n / (double)Seconds);
            Countdown = $"{Math.Max(0, Seconds - n)}s";
            Live = live;
        });
        if (n >= Seconds)
        {
            _collecting = false;
            List<Sample> copy;
            lock (_gate) copy = _samples.ToList();
            var plan = _main.Dashboard.ActivePlan?.Name;
            var diag = Diagnoser.Analyze(copy, plan);
            MainViewModel.Ui(() => Show(diag));
        }
    }

    private void Show(Diagnosis d)
    {
        Issues.Clear();
        Healthy = d.Healthy;
        string disk = double.IsNaN(d.AvgDisk) ? "" : $"  ·  disk {d.AvgDisk:0}% busy";
        string speed = double.IsNaN(d.AvgPerf) ? "" : $"  ·  CPU speed {d.AvgPerf:0}%";
        string gpu = double.IsNaN(d.AvgGpu) ? "" : $"  ·  GPU {d.AvgGpu:0}%";
        string ping = double.IsNaN(d.InternetMs) ? "" : $"  ·  ping {d.InternetMs:0} ms";
        Summary = $"Average over {Seconds}s: CPU {d.AvgCpu:0}%  ·  memory {d.MemUsed:0}% used{disk}{gpu}{speed}{ping}";

        if (d.Healthy)
        {
            Verdict = "Nothing is slowing your PC down right now";
            VerdictDetail = "CPU, memory, disk, graphics and network all look fine. If it felt slow earlier, the lag history shows what happened. "
                          + "Tip: run this while the PC actually feels slow.";
        }
        else
        {
            Verdict = d.Issues.Count == 1 ? "1 thing is slowing your PC down" : $"{d.Issues.Count} things are slowing your PC down";
            VerdictDetail = d.Issues.Count == 1 ? "Here's what it is and how to fix it." : "Worst first. Each one comes with a fix you can apply right here.";
            foreach (var issue in d.Issues)
            {
                Issues.Add(new IssueVm
                {
                    Issue = issue,
                    CulpritIcon = issue.Culprit?.SamplePid is { } pid ? IconCache.Get(ProcessScanner.GetPath(pid)) : null,
                    Fixes = issue.Fixes.Select(f => new FixVm(f.Label, new RelayCommand(() => RunFix(f)))).ToList(),
                });
            }
        }
        State = DiagnoseState.Done;
    }

    private async void RunFix(FixAction f)
    {
        switch (f.Kind)
        {
            case FixKind.FreeRam: _main.Memory.FreeRam(); break;
            case FixKind.TrimApp when f.Target != null: await _main.TrimAppAsync(f.Target, f.TargetName ?? f.Target); break;
            case FixKind.EndApp when f.Target != null: await _main.EndAppAsync(f.Target, f.TargetName ?? f.Target); break;
            case FixKind.HighPerformance: await _main.Dashboard.SetHighPerformanceAsync(); break;
            case FixKind.OpenStartup: _main.Page = Page.Startup; break;
            case FixKind.OpenCleanup: _main.Page = Page.Cleanup; _main.Cleanup.ScanCommand.Execute(null); break;
            case FixKind.GameBoost: _main.BoostPage.OpenFor(f.Target ?? ""); break;
        }
    }
}
