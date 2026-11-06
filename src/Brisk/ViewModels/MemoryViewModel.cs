using System.Collections.ObjectModel;
using System.Windows.Media;
using Brisk.Services;

namespace Brisk.ViewModels;

/// <summary>All processes of one app (e.g. 40 chrome.exe) shown as a single row.</summary>
public sealed class AppGroup : ObservableObject
{
    public required string Key { get; init; }
    public required string DisplayName { get; init; }
    public ImageSource? Icon { get; init; }
    public string? Path { get; init; }
    public bool IsProtected { get; init; }
    public bool IsBackground { get; init; }

    private int[] _pids = [];
    public int[] Pids { get => _pids; set { _pids = value; Raise(); Raise(nameof(CountText)); } }

    private long _memory;
    public long Memory { get => _memory; set { if (Set(ref _memory, value)) Raise(nameof(MemoryText)); } }

    private double _cpu;
    public double Cpu { get => _cpu; set { if (Set(ref _cpu, value)) Raise(nameof(CpuText)); } }

    private double _share;
    /// <summary>0-1, this app's share of the biggest app's memory, for the bar.</summary>
    public double Share { get => _share; set => Set(ref _share, value); }

    public string MemoryText => Fmt.Bytes(Memory);
    public string CpuText => Cpu < 0.05 ? "0%" : Cpu < 10 ? $"{Cpu:0.0}%" : $"{Cpu:0}%";
    public string CountText => Pids.Length == 1 ? "1 process" : $"{Pids.Length} processes";
}

public sealed record GroupSnapshot(string Key, string DisplayName, string? Path, int[] Pids, long Memory, double Cpu, bool Background);

public sealed class MemoryViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly Dictionary<string, AppGroup> _byKey = new(StringComparer.OrdinalIgnoreCase);
    private List<AppGroup> _all = [];
    private static readonly int MySession = System.Diagnostics.Process.GetCurrentProcess().SessionId;

    public ObservableCollection<AppGroup> Apps { get; } = [];
    /// <summary>Every app, sorted by memory, ignoring the search filter.</summary>
    public IList<AppGroup> All => _all;

    private string _filter = "";
    public string Filter { get => _filter; set { if (Set(ref _filter, value)) ApplyFilter(); } }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; private set => SetAndRequery(ref _isBusy, value); }

    private string _summary = "";
    public string Summary { get => _summary; private set => Set(ref _summary, value); }

    public RelayCommand FreeRamCommand { get; }
    public RelayCommand EndTaskCommand { get; }
    public RelayCommand TrimCommand { get; }

    public MemoryViewModel(MainViewModel main)
    {
        _main = main;
        FreeRamCommand = new RelayCommand(FreeRam, () => !IsBusy);
        EndTaskCommand = new RelayCommand(p => { if (p is AppGroup g) _ = _main.EndAppAsync(g.Key, g.DisplayName); },
            p => p is AppGroup { IsProtected: false });
        TrimCommand = new RelayCommand(p => { if (p is AppGroup g) _ = _main.TrimAppAsync(g.Key, g.DisplayName); },
            p => p is AppGroup g && g.Key is not ("Memory Compression" or "System" or "Registry" or "Secure System"));
    }

    /// <summary>Runs on a worker thread: groups processes by exe and resolves names/paths.</summary>
    public static List<GroupSnapshot> BuildGroups(IReadOnlyList<ProcInfo> procs)
    {
        var groups = new List<GroupSnapshot>();
        foreach (var g in procs.GroupBy(p => p.ImageName, StringComparer.OrdinalIgnoreCase))
        {
            var pids = g.Select(p => p.Pid).ToArray();
            string? path = null;
            foreach (var pid in pids) { path = ProcessScanner.GetPath(pid); if (path != null) break; }
            var friendly = AppNames.KnownName(g.Key) ?? IconCache.FriendlyName(path);
            var name = !string.IsNullOrWhiteSpace(friendly) ? friendly : StripExe(g.Key);
            bool background = g.All(p => p.SessionId != MySession);
            groups.Add(new GroupSnapshot(g.Key, name, path, pids, g.Sum(p => p.PrivateWorkingSet), g.Sum(p => p.CpuPercent), background));
        }
        return groups;
    }

    private static string StripExe(string s) => s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? s[..^4] : s;

    /// <summary>UI thread: update rows in place so selection and scroll position survive refreshes.</summary>
    public void Apply(List<GroupSnapshot> snapshot)
    {
        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in snapshot)
        {
            live.Add(s.Key);
            if (!_byKey.TryGetValue(s.Key, out var g))
            {
                g = new AppGroup
                {
                    Key = s.Key, DisplayName = s.DisplayName, Path = s.Path, Icon = IconCache.GetOrAvatar(s.Path, s.DisplayName),
                    IsProtected = ProcessScanner.Protected.Contains(s.Key) || s.Path == null && s.Background,
                    IsBackground = s.Background,
                };
                _byKey[s.Key] = g;
            }
            g.Pids = s.Pids;
            g.Memory = s.Memory;
            g.Cpu = s.Cpu;
        }
        foreach (var dead in _byKey.Keys.Where(k => !live.Contains(k)).ToList()) _byKey.Remove(dead);

        _all = _byKey.Values.OrderByDescending(g => g.Memory).ToList();
        long max = _all.Count > 0 ? Math.Max(1, _all[0].Memory) : 1;
        foreach (var g in _all) g.Share = (double)g.Memory / max;

        long total = _all.Sum(g => g.Memory);
        int procCount = _all.Sum(g => g.Pids.Length);
        Summary = $"{_all.Count} apps · {procCount} processes · {Fmt.Bytes(total)} in use by apps";
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        IEnumerable<AppGroup> src = _all;
        if (!string.IsNullOrWhiteSpace(Filter))
            src = src.Where(g => g.DisplayName.Contains(Filter, StringComparison.OrdinalIgnoreCase)
                              || g.Key.Contains(Filter, StringComparison.OrdinalIgnoreCase));
        var target = src.ToList();

        // Minimal diff: remove missing, insert/move to target order.
        var targetSet = new HashSet<AppGroup>(target);
        for (int i = Apps.Count - 1; i >= 0; i--)
            if (!targetSet.Contains(Apps[i])) Apps.RemoveAt(i);
        for (int i = 0; i < target.Count; i++)
        {
            int cur = Apps.IndexOf(target[i]);
            if (cur == -1) Apps.Insert(i, target[i]);
            else if (cur != i) Apps.Move(cur, i);
        }
    }

    public async void FreeRam()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var r = await Task.Run(() => MemoryOptimizer.Optimize());
            var msg = r.TrimmedBytes > 0
                ? $"Freed {Fmt.Bytes(r.TrimmedBytes)} from {r.ProcessesTrimmed} apps"
                : "Apps were already lean, nothing to free";
            if (r.StandbyPurged) msg += " + cleared standby cache";
            _main.ShowToast(msg + ".");
        }
        finally { IsBusy = false; }
    }
}
