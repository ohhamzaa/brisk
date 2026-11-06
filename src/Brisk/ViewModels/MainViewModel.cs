using System.Diagnostics;
using System.Windows;
using Brisk.Services;

namespace Brisk.ViewModels;

public enum Page { Dashboard, Diagnose, Boost, Memory, Startup, Cleanup, History }

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private int _tick;
    private int _groupBusy;
    private DateTime _lastAlert = DateTime.MinValue;
    private int _boostCheck;

    public SystemMonitor Monitor { get; } = new();
    public LagRecorder Recorder { get; } = new(Demo.On ? Demo.SeedHistory() : null);
    public GameBoost Boost { get; } = new();
    public Settings Settings { get; } = Settings.Load();

    public DashboardViewModel Dashboard { get; }
    public DiagnoseViewModel Diagnose { get; }
    public BoostViewModel BoostPage { get; }
    public MemoryViewModel Memory { get; }
    public StartupViewModel Startup { get; }
    public CleanupViewModel Cleanup { get; }
    public HistoryViewModel History { get; }
    public WelcomeViewModel? Welcome { get => _welcome; private set => Set(ref _welcome, value); }
    private WelcomeViewModel? _welcome;

    public bool IsAdmin { get; } = SystemStats.IsAdmin;
    public string Version { get; } = "v" + (typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.1.0");

    /// <summary>Set by the window. When false (minimised to tray) Brisk skips all UI work and only records.</summary>
    public volatile bool UiActive = true;

    /// <summary>Tray notification request: title, text, page to open when clicked.</summary>
    public event Action<string, string, Page>? Notify;

    private Page _page = Page.Dashboard;
    public Page Page
    {
        get => _page;
        set
        {
            if (!Set(ref _page, value)) return;
            Raise(nameof(CurrentView));
            if (value == Page.Startup && Startup.Entries.Count == 0) Startup.Reload();
            if (value == Page.Boost && !BoostPage.Active) _ = BoostPage.Refresh();
        }
    }

    public object CurrentView => Page switch
    {
        Page.Diagnose => Diagnose,
        Page.Boost => BoostPage,
        Page.Memory => Memory,
        Page.Startup => Startup,
        Page.Cleanup => Cleanup,
        Page.History => History,
        _ => Dashboard,
    };

    public RelayCommand NavigateCommand { get; }
    public RelayCommand RestartAsAdminCommand { get; }
    public event Action? RestartingAsAdmin;

    public MainViewModel(bool skipWelcome = false)
    {
        int recovered = GameBoost.RecoverIfNeeded(); // undo a boost left behind by a crash

        Memory = new MemoryViewModel(this);
        Startup = new StartupViewModel(this);
        Cleanup = new CleanupViewModel(this);
        Dashboard = new DashboardViewModel(this);
        Diagnose = new DiagnoseViewModel(this);
        BoostPage = new BoostViewModel(this);
        History = new HistoryViewModel(this);

        Recorder.Enabled = Settings.RecordInBackground;
        Recorder.ActivePlanName = () => Dashboard.ActivePlan?.Name;
        Recorder.Recorded += ev => Ui(() => OnLagRecorded(ev));
        Monitor.Network.Enabled = Settings.NetworkChecks && !Demo.On; // demo never pings

        NavigateCommand = new RelayCommand(p => { if (p is string s && Enum.TryParse<Page>(s, out var pg)) Page = pg; });
        RestartAsAdminCommand = new RelayCommand(RestartAsAdmin, () => !IsAdmin);

        if (!Settings.FirstRunDone && !skipWelcome) Welcome = new WelcomeViewModel(this);
        if (recovered > 0) ShowToast($"Restored {recovered} apps from a game boost that was still on.");

        Monitor.Sampled += OnSample;
    }

    internal void FinishWelcome(bool diagnose)
    {
        if (Welcome is { } w) History.ApplyWelcome(w.Record, w.StartWithWindows, w.Notify, w.NetworkChecks);
        Settings.FirstRunDone = true;
        Settings.Save();
        Welcome = null;
        if (diagnose) Dashboard.DiagnoseCommand.Execute(null);
    }

    /// <summary>Runs on the monitor's thread once a second.</summary>
    private void OnSample(Sample s)
    {
        Recorder.Feed(s);
        Diagnose.Feed(s);
        Monitor.LowPower = !UiActive && !Diagnose.IsRunning && !Boost.Active;
        if (Boost.Active && ++_boostCheck % 3 == 0 && !Boost.GameStillRunning())
            Ui(async () =>
            {
                if (!Boost.Active) return;
                await BoostPage.Stop("Your game closed.");
                Notify?.Invoke("Game boost finished", "Your game closed, so Brisk put every app and your power plan back to normal.", Page.Boost);
            });
        if (!UiActive) return;

        Ui(() => Dashboard.OnSample(s));
        if (_tick++ % 2 == 0 && Interlocked.Exchange(ref _groupBusy, 1) == 0)
        {
            try
            {
                var groups = MemoryViewModel.BuildGroups(s.Procs);
                Ui(() =>
                {
                    try
                    {
                        if (!Memory.IsBusy) Memory.Apply(groups);
                        Dashboard.UpdateTopApps(Memory.All);
                    }
                    finally { Volatile.Write(ref _groupBusy, 0); }
                });
            }
            catch { Volatile.Write(ref _groupBusy, 0); }
        }
    }

    private void OnLagRecorded(LagEvent ev)
    {
        History.Add(ev);
        // Alert once per 10 minutes at most, so a bad evening doesn't become notification spam.
        if (!Settings.NotifyOnLag || DateTime.Now - _lastAlert < TimeSpan.FromMinutes(10)) return;
        _lastAlert = DateTime.Now;
        var dur = ev.Seconds >= 60 ? $"{ev.Seconds / 60:0} min" : $"{ev.Seconds:0} s";
        string what = ev.Kinds.Contains("Network") && ev.Kinds.Count == 1 ? "Lag spike" : "Your PC struggled";
        Notify?.Invoke($"{what} for {dur}", ev.Headline + ". Click to see what happened.", Page.History);
    }

    public static void Ui(Action a) => Application.Current?.Dispatcher.BeginInvoke(a);

    // ---------- toast ----------
    private string? _toast;
    public string? Toast { get => _toast; private set => Set(ref _toast, value); }
    private CancellationTokenSource? _toastCts;

    public async void ShowToast(string message)
    {
        _toastCts?.Cancel();
        var cts = _toastCts = new CancellationTokenSource();
        Toast = message;
        try { await Task.Delay(4500, cts.Token); Toast = null; } catch (TaskCanceledException) { }
    }

    // ---------- confirm dialog ----------
    private ConfirmRequest? _confirm;
    public ConfirmRequest? Confirm { get => _confirm; private set => Set(ref _confirm, value); }

    public Task<bool> AskAsync(string title, string message, string okText, bool danger = false)
    {
        var req = new ConfirmRequest(title, message, okText, danger);
        req.Done += () => Confirm = null;
        Confirm = req;
        return req.Task;
    }

    // ---------- shared actions (used by Memory page and diagnosis fixes) ----------
    public int[] PidsOf(string image) =>
        Monitor.Last?.Procs.Where(p => p.ImageName.Equals(image, StringComparison.OrdinalIgnoreCase)).Select(p => p.Pid).ToArray() ?? [];

    public async Task<bool> EndAppAsync(string image, string name)
    {
        var pids = PidsOf(image);
        if (pids.Length == 0) { ShowToast($"{name} isn't running anymore."); return true; }
        var ok = await AskAsync($"End {name}?",
            $"This closes {(pids.Length == 1 ? "1 process" : $"{pids.Length} processes")}. Unsaved work in {name} will be lost.",
            "End task", danger: true);
        if (!ok) return false;
        int killed = await Task.Run(() => pids.Count(ProcessScanner.Kill));
        foreach (var pid in pids) ProcessScanner.ForgetPid(pid);
        ShowToast(killed > 0 ? $"Ended {name}." : $"Couldn't end {name}. It may need admin.");
        return killed > 0;
    }

    public async Task TrimAppAsync(string image, string name)
    {
        var pids = PidsOf(image);
        int ok = await Task.Run(() => pids.Count(ProcessScanner.TrimWorkingSet));
        ShowToast(ok > 0
            ? $"Trimmed {name}. Memory it actively uses will come back as needed."
            : $"Couldn't trim {name}. It may need admin.");
    }

    private void RestartAsAdmin()
    {
        try
        {
            RestartingAsAdmin?.Invoke(); // releases the single-instance lock
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--restarted") { UseShellExecute = true, Verb = "runas" });
            App.ExitApp();
        }
        catch { ShowToast("Admin restart was cancelled."); App.ReacquireInstance(); }
    }

    public void Dispose()
    {
        if (Boost.Active) Boost.Stop(); // never leave apps throttled behind us
        Monitor.Dispose();
    }
}

public sealed class ConfirmRequest
{
    private readonly TaskCompletionSource<bool> _tcs = new();
    public string Title { get; }
    public string Message { get; }
    public string OkText { get; }
    public bool Danger { get; }
    public Task<bool> Task => _tcs.Task;
    public event Action? Done;
    public RelayCommand OkCommand { get; }
    public RelayCommand CancelCommand { get; }

    public ConfirmRequest(string title, string message, string okText, bool danger)
    {
        Title = title; Message = message; OkText = okText; Danger = danger;
        OkCommand = new RelayCommand(() => Finish(true));
        CancelCommand = new RelayCommand(() => Finish(false));
    }

    private void Finish(bool ok)
    {
        if (_tcs.TrySetResult(ok)) Done?.Invoke();
    }
}

/// <summary>First launch: three switches and a nudge to try the diagnosis.</summary>
public sealed class WelcomeViewModel(MainViewModel main) : ObservableObject
{
    public bool Record { get; set; } = true;
    public bool StartWithWindows { get; set; } = true;
    public bool Notify { get; set; } = true;
    public bool NetworkChecks { get; set; } = true;

    public RelayCommand DoneCommand { get; } = new(() => main.FinishWelcome(false));
    public RelayCommand DiagnoseCommand { get; } = new(() => main.FinishWelcome(true));
}
