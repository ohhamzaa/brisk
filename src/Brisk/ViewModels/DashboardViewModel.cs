using System.Collections.ObjectModel;
using Brisk.Services;

namespace Brisk.ViewModels;

public sealed record Tip(string Level, string Title, string Body, string? ActionText, RelayCommand? Action);

public sealed class DashboardViewModel : ObservableObject
{
    private const int HistoryLength = 60;
    private readonly MainViewModel _main;
    private readonly Queue<double> _cpuHist = new(), _ramHist = new();
    private int _samples;

    public string CpuName { get; } = SystemStats.CpuName;
    public string OsName { get; } = SystemStats.OsName;
    public string CoresText { get; } = $"{Environment.ProcessorCount} threads";

    private double _cpu;
    public double Cpu { get => _cpu; private set { if (Set(ref _cpu, value)) Raise(nameof(CpuText)); } }
    public string CpuText => $"{Cpu:0}";

    private double[] _cpuHistory = [];
    public double[] CpuHistory { get => _cpuHistory; private set => Set(ref _cpuHistory, value); }

    private double _ram;
    public double Ram { get => _ram; private set { if (Set(ref _ram, value)) Raise(nameof(RamText)); } }
    public string RamText => $"{Ram:0}";

    private double[] _ramHistory = [];
    public double[] RamHistory { get => _ramHistory; private set => Set(ref _ramHistory, value); }

    private string _ramDetail = "";
    public string RamDetail { get => _ramDetail; private set => Set(ref _ramDetail, value); }

    private string _ramAvailable = "", _ramCached = "", _ramTotal = "";
    public string RamAvailable { get => _ramAvailable; private set => Set(ref _ramAvailable, value); }
    public string RamCached { get => _ramCached; private set => Set(ref _ramCached, value); }
    public string RamTotal { get => _ramTotal; private set => Set(ref _ramTotal, value); }

    private double _disk;
    public double Disk { get => _disk; private set => Set(ref _disk, value); }

    private string _diskText = "", _diskName = "C:";
    public string DiskText { get => _diskText; private set => Set(ref _diskText, value); }
    public string DiskName { get => _diskName; private set => Set(ref _diskName, value); }

    private string _diskTotal = "";
    public string DiskTotal { get => _diskTotal; private set => Set(ref _diskTotal, value); }

    private string _processCount = "";
    public string ProcessCount { get => _processCount; private set => Set(ref _processCount, value); }

    private string _topCpu = "-";
    public string TopCpu { get => _topCpu; private set => Set(ref _topCpu, value); }

    private string _uptime = "";
    public string Uptime { get => _uptime; private set => Set(ref _uptime, value); }

    public ObservableCollection<AppGroup> TopApps { get; } = [];
    public ObservableCollection<Tip> Tips { get; } = [];

    public ObservableCollection<PowerPlan> Plans { get; } = [];
    private PowerPlan? _activePlan;
    public PowerPlan? ActivePlan
    {
        get => _activePlan;
        set
        {
            if (value == null || value == _activePlan) return;
            var target = value;
            _activePlan = value;
            Raise();
            _ = SwitchPlan(target);
        }
    }

    public RelayCommand FreeRamCommand => _main.Memory.FreeRamCommand;
    public RelayCommand QuickCleanCommand { get; }
    public RelayCommand OpenMemoryCommand { get; }
    public RelayCommand DiagnoseCommand { get; }
    public RelayCommand OpenHistoryCommand { get; }

    private string? _lastLag;
    /// <summary>"Last slowdown: today 3:12 PM, 40 s: Microsoft Defender is ..." or null.</summary>
    public string? LastLag { get => _lastLag; set => Set(ref _lastLag, value); }

    public DashboardViewModel(MainViewModel main)
    {
        _main = main;
        QuickCleanCommand = new RelayCommand(() => { _main.Page = Page.Cleanup; _main.Cleanup.ScanCommand.Execute(null); });
        OpenMemoryCommand = new RelayCommand(() => _main.Page = Page.Memory);
        DiagnoseCommand = new RelayCommand(() => { _main.Page = Page.Diagnose; _main.Diagnose.Start(); });
        OpenHistoryCommand = new RelayCommand(() => _main.Page = Page.History);
        _ = LoadPlans();
    }

    public async Task<bool> SetHighPerformanceAsync()
    {
        if (Plans.Count == 0) await LoadPlans();
        var target = Plans.FirstOrDefault(p => p.Name.Contains("High performance", StringComparison.OrdinalIgnoreCase))
                  ?? Plans.FirstOrDefault(p => p.Name.Contains("Ultimate", StringComparison.OrdinalIgnoreCase))
                  ?? Plans.FirstOrDefault(p => p.Name.Contains("Balanced", StringComparison.OrdinalIgnoreCase));
        if (target == null) { _main.ShowToast("No high performance power plan found on this PC."); return false; }
        ActivePlan = target;
        return true;
    }

    public void OnSample(Sample s)
    {
        _samples++;
        var mem = s.Mem;
        double cpu = s.Cpu;
        Cpu = cpu;
        Ram = mem.Total == 0 ? 0 : mem.Used * 100.0 / mem.Total;
        RamDetail = $"{Fmt.Gb(mem.Used)} / {Fmt.Gb(mem.Total)} GB";
        RamAvailable = Fmt.Bytes(mem.Available);
        RamCached = Fmt.Bytes(mem.Cached);
        RamTotal = Fmt.Bytes(mem.Total);

        if (_samples == 1)
        {
            // Start with a flat line at the current value so the graph spans the card from the first second.
            for (int i = 0; i < HistoryLength - 1; i++) { _cpuHist.Enqueue(cpu); _ramHist.Enqueue(Ram); }
        }
        UpdateGpuNet(s);
        Push(_cpuHist, cpu); CpuHistory = _cpuHist.ToArray();
        Push(_ramHist, Ram); RamHistory = _ramHist.ToArray();

        if (_samples % 5 == 1 && s.DiskTotal > 0)
        {
            long total = s.DiskTotal, free = s.DiskFree;
            DiskName = (Path.GetPathRoot(Environment.SystemDirectory) ?? "C:").TrimEnd('\\');
            Disk = total == 0 ? 0 : (total - free) * 100.0 / total;
            DiskText = $"{Fmt.Bytes(free)} free";
            DiskTotal = $"of {Fmt.Bytes(total)}";
            Uptime = Fmt.Duration(SystemStats.Uptime);
        }
        if (_samples % 10 == 1) BuildTips(mem);
    }

    private static void Push(Queue<double> q, double v)
    {
        q.Enqueue(v);
        while (q.Count > HistoryLength) q.Dequeue();
    }

    public void UpdateTopApps(IList<AppGroup> apps)
    {
        ProcessCount = apps.Sum(a => a.Pids.Length).ToString();
        var busiest = apps.Where(a => a.Key is not ("System" or "Registry" or "Brisk.exe")).MaxBy(a => a.Cpu);
        TopCpu = busiest is { Cpu: >= 0.5 } ? $"{busiest.DisplayName} · {busiest.CpuText}" : "Idle";

        var top = apps.Take(5).ToList();
        for (int i = TopApps.Count - 1; i >= 0; i--) if (!top.Contains(TopApps[i])) TopApps.RemoveAt(i);
        for (int i = 0; i < top.Count; i++)
        {
            int cur = TopApps.IndexOf(top[i]);
            if (cur == -1) TopApps.Insert(i, top[i]);
            else if (cur != i) TopApps.Move(cur, i);
        }
    }

    private void BuildTips(MemoryInfo mem)
    {
        var tips = new List<Tip>();
        var up = SystemStats.Uptime;
        if (up.TotalDays >= 7)
            tips.Add(new Tip("warn", $"Restart your PC (on for {Fmt.Duration(up)})",
                "A restart clears leaked memory and finishes pending updates. It's the single best speed fix.", null, null));
        if (Ram >= 85)
            tips.Add(new Tip("warn", $"Memory is {Ram:0}% full",
                "Close the apps you aren't using, or free up RAM from background apps.", "Free up RAM", FreeRamCommand));
        if (Disk >= 90)
            tips.Add(new Tip("danger", $"{DiskName} drive is {Disk:0}% full",
                "Windows slows down when the system drive is almost full. Clean junk files to make room.", "Clean junk", QuickCleanCommand));
        int startupOn = _main.Startup.EnabledCount;
        if (startupOn >= 8)
            tips.Add(new Tip("info", $"{startupOn} apps start with Windows",
                "Each one slows boot and sits in memory. Turn off the ones you don't need right away.", "Review startup",
                new RelayCommand(() => _main.Page = Page.Startup)));
        if (ActivePlan != null && ActivePlan.Name.Contains("saver", StringComparison.OrdinalIgnoreCase))
            tips.Add(new Tip("info", "Power saver is on", "Your CPU is being held back. Switch to Balanced or High performance below.", null, null));
        if (tips.Count == 0)
            tips.Add(new Tip("ok", "Your PC looks healthy", "No problems found. Brisk keeps checking while it's open.", null, null));

        Tips.Clear();
        foreach (var t in tips) Tips.Add(t);
    }

    public Task ReloadPlans() => LoadPlans();

    // ---------- GPU + network strip ----------
    private string _gpuText = "-", _gpuSub = "", _vramText = "-", _vramSub = "", _pingText = "-", _pingSub = "", _trafficText = "-", _trafficSub = "";
    public string GpuText { get => _gpuText; private set => Set(ref _gpuText, value); }
    public string GpuSub { get => _gpuSub; private set => Set(ref _gpuSub, value); }
    public string VramText { get => _vramText; private set => Set(ref _vramText, value); }
    public string VramSub { get => _vramSub; private set => Set(ref _vramSub, value); }
    public string PingText { get => _pingText; private set => Set(ref _pingText, value); }
    public string PingSub { get => _pingSub; private set => Set(ref _pingSub, value); }
    public string TrafficText { get => _trafficText; private set => Set(ref _trafficText, value); }
    public string TrafficSub { get => _trafficSub; private set => Set(ref _trafficSub, value); }

    private string _pingLevel = "ok";
    /// <summary>ok / warn / danger, colours the ping tile.</summary>
    public string PingLevel { get => _pingLevel; private set => Set(ref _pingLevel, value); }

    private double _gpu;
    public double Gpu { get => _gpu; private set => Set(ref _gpu, value); }

    private void UpdateGpuNet(Sample s)
    {
        if (double.IsNaN(s.GpuPct)) { GpuText = "n/a"; GpuSub = "No GPU counters"; }
        else
        {
            Gpu = s.GpuPct;
            GpuText = $"{s.GpuPct:0}%";
            var top = s.Procs.Where(p => p.Pid != Environment.ProcessId).MaxBy(p => p.GpuPercent);
            GpuSub = top is { GpuPercent: >= 3 } ? $"{AppNames.Resolve(top.ImageName, top.Pid)} {top.GpuPercent:0}%" : s.Gpu.Name ?? "Idle";
        }
        if (s.Gpu.VramTotal > 0)
        {
            VramText = $"{Fmt.Gb(s.Gpu.VramUsed)} / {Fmt.Gb(s.Gpu.VramTotal)} GB";
            VramSub = $"{s.Gpu.VramPct:0}% of video memory";
        }
        var n = s.Net;
        if (!n.Probing) { PingText = "-"; PingSub = _main.Settings.NetworkChecks ? "Measuring…" : "Network checks off"; PingLevel = "ok"; }
        else if (double.IsNaN(n.InternetMs)) { PingText = "Offline"; PingSub = "Can't reach the internet"; PingLevel = "danger"; }
        else
        {
            PingText = $"{n.InternetMs:0} ms";
            PingSub = (double.IsNaN(n.RouterMs) ? "" : $"Router {n.RouterMs:0} ms  ·  ") + (n.InternetLoss > 0 ? $"{n.InternetLoss:P0} lost" : $"jitter {n.InternetJitter:0} ms");
            PingLevel = n.InternetLoss >= 0.05 || n.InternetMs >= 120 ? "danger" : n.InternetMs >= 60 || n.InternetJitter >= 20 ? "warn" : "ok";
        }
        TrafficText = $"↓ {Fmt.Bytes(n.DownBytesPerSec)}/s";
        TrafficSub = $"↑ {Fmt.Bytes(n.UpBytesPerSec)}/s";
    }

    private async Task LoadPlans()
    {
        var plans = await Task.Run(PowerPlans.List);
        Plans.Clear();
        foreach (var p in plans) Plans.Add(p);
        _activePlan = plans.FirstOrDefault(p => p.Active);
        Raise(nameof(ActivePlan));
    }

    private async Task SwitchPlan(PowerPlan plan)
    {
        bool ok = await Task.Run(() => PowerPlans.SetActive(plan.Id));
        _main.ShowToast(ok ? $"Power plan set to {plan.Name}." : $"Couldn't switch to {plan.Name}.");
        await LoadPlans();
    }
}
