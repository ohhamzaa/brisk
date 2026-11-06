using System.Text.Json;

namespace Brisk.Services;

public sealed class LagEvent
{
    public DateTime Start { get; set; }
    public double Seconds { get; set; }
    public List<string> Kinds { get; set; } = [];
    public double PeakCpu { get; set; }
    public double PeakDisk { get; set; }
    public double PeakMemUsed { get; set; }
    public string Headline { get; set; } = "";
    public string Detail { get; set; } = "";
    public List<string> Culprits { get; set; } = [];
}

/// <summary>
/// The "black box": watches the sample stream and, whenever the PC is struggling for 5+ seconds,
/// writes down when, how long, and who caused it. Stored locally in %LOCALAPPDATA%\Brisk.
/// </summary>
public sealed class LagRecorder
{
    private const int MinSeconds = 5, CoolDownSamples = 3, MaxKeptSamples = 120, MaxEvents = 300;
    public static readonly string DefaultPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Brisk", "lag-history.json");

    private readonly string FilePath;

    private readonly List<Sample> _window = [];
    private readonly object _gate = new();
    private DateTime _start;
    private int _hotCount, _cool;
    private double _peakCpu, _peakDisk, _peakMem;
    private readonly List<LagEvent> _events;

    public bool Enabled { get; set; } = true;
    public Func<string?>? ActivePlanName { get; set; }
    public event Action<LagEvent>? Recorded;

    public LagRecorder(string? path = null)
    {
        FilePath = path ?? DefaultPath;
        _events = Load(FilePath);
    }

    public IReadOnlyList<LagEvent> Events
    {
        get { lock (_gate) return _events.ToList(); }
    }

    /// <summary>What counts as "the PC is struggling" for one second.</summary>
    public static bool IsHot(Sample s) =>
        s.Cpu >= 90
        || (!double.IsNaN(s.DiskBusy) && s.DiskBusy >= 90)
        || s.AvailPct <= 6
        || (!double.IsNaN(s.PagesInPerSec) && s.PagesInPerSec >= 1500 && s.AvailPct <= 20)
        || IsNetHot(s) || IsVramHot(s);

    // A GPU at 100% in a game is normal, so it isn't "lag" by itself; full VRAM and network spikes are.
    private static bool IsNetHot(Sample s) =>
        s.Net.Probing && (s.Net.InternetLoss >= 0.3 || (!double.IsNaN(s.Net.InternetMs) && s.Net.InternetMs >= 250)
                          || (!double.IsNaN(s.Net.RouterMs) && s.Net.RouterMs >= 100));
    private static bool IsVramHot(Sample s) => s.Gpu.VramTotal > 0 && s.Gpu.VramPct >= 97;

    /// <summary>Called by the monitor once per second (from its thread).</summary>
    public void Feed(Sample s)
    {
        if (!Enabled) { _window.Clear(); _hotCount = 0; return; }

        if (IsHot(s))
        {
            if (_hotCount == 0)
            {
                _start = s.Time; _peakCpu = _peakDisk = _peakMem = 0; _window.Clear();
            }
            _hotCount++;
            _cool = 0;
            _window.Add(s);
            if (_window.Count > MaxKeptSamples) _window.RemoveAt(0);
            _peakCpu = Math.Max(_peakCpu, s.Cpu);
            if (!double.IsNaN(s.DiskBusy)) _peakDisk = Math.Max(_peakDisk, s.DiskBusy);
            _peakMem = Math.Max(_peakMem, s.MemUsedPct);
            if ((s.Time - _start).TotalMinutes >= 10) Finish(s.Time); // split very long episodes
        }
        else if (_hotCount > 0 && ++_cool >= CoolDownSamples)
        {
            Finish(s.Time.AddSeconds(-CoolDownSamples));
        }
    }

    private void Finish(DateTime end)
    {
        int hot = _hotCount;
        _hotCount = 0; _cool = 0;
        if (hot < MinSeconds) { _window.Clear(); return; }

        var diag = Diagnoser.Analyze(_window, ActivePlanName?.Invoke());
        var top = diag.Issues.FirstOrDefault(i => i.Kind != IssueKind.Space);
        // Rank by whatever actually caused this slowdown (mixing CPU %, MB/s and VRAM into one score
        // let a big-but-idle app outrank the real culprit). The diagnosed culprit always comes first.
        var all = Diagnoser.Aggregate(_window);
        Func<Culprit, double> key = top?.Kind switch
        {
            IssueKind.Memory => c => c.FaultsPerSec * 1e6 + c.Memory,
            IssueKind.Disk => c => c.IoPerSec,
            IssueKind.Vram => c => c.Vram,
            IssueKind.Gpu => c => c.Gpu,
            _ => c => c.Cpu,
        };
        var culprits = all.OrderByDescending(key).Select(c => c.Name).ToList();
        if (top?.Culprit is { } first) { culprits.Remove(first.Name); culprits.Insert(0, first.Name); }
        culprits = culprits.Take(3).ToList();
        if (top?.Kind == IssueKind.Network) culprits = top.Culprit != null ? [top.Culprit.Name] : [];

        var kinds = new List<string>();
        if (_peakCpu >= 90) kinds.Add("CPU");
        if (_peakMem >= 94 || _window.Any(w => w.PagesInPerSec >= 1500)) kinds.Add("Memory");
        if (_peakDisk >= 90) kinds.Add("Disk");
        if (_window.Any(IsVramHot)) kinds.Add("GPU memory");
        if (_window.Any(IsNetHot)) kinds.Add("Network");

        var ev = new LagEvent
        {
            Start = _start,
            Seconds = Math.Max(hot, (end - _start).TotalSeconds),
            Kinds = kinds,
            PeakCpu = _peakCpu, PeakDisk = _peakDisk, PeakMemUsed = _peakMem,
            Headline = top?.Headline ?? $"Your PC was under heavy load (CPU peaked at {_peakCpu:0}%)",
            Detail = top?.Detail ?? "",
            Culprits = culprits,
        };
        _window.Clear();

        lock (_gate)
        {
            _events.Insert(0, ev);
            if (_events.Count > MaxEvents) _events.RemoveRange(MaxEvents, _events.Count - MaxEvents);
        }
        Save();
        Recorded?.Invoke(ev);
    }

    public void Clear()
    {
        lock (_gate) _events.Clear();
        Save();
    }

    private static List<LagEvent> Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<List<LagEvent>>(File.ReadAllText(path)) ?? [];
        }
        catch { }
        return [];
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string json;
            lock (_gate) json = JsonSerializer.Serialize(_events);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, FilePath, overwrite: true); // atomic: a crash never leaves half a file
        }
        catch { }
    }
}
