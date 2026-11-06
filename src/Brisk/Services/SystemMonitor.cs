namespace Brisk.Services;

/// <summary>One second of everything Brisk knows about the PC.</summary>
public sealed record Sample(
    DateTime Time, double Cpu, MemoryInfo Mem, double DiskBusy, double CpuPerf, double PagesInPerSec,
    long DiskFree, long DiskTotal, IReadOnlyList<ProcInfo> Procs)
{
    public double AvailPct => Mem.Total == 0 ? 100 : Mem.Available * 100.0 / Mem.Total;
    public double MemUsedPct => 100 - AvailPct;
}

/// <summary>
/// Background sampler (1 Hz, on a thread-pool timer, never the UI thread). Feeds the dashboard,
/// the "why is my PC slow" diagnosis and the lag recorder from a single stream, so the tray
/// recorder keeps working when the window is hidden.
/// </summary>
public sealed class SystemMonitor : IDisposable
{
    private readonly SystemStats _stats = new();
    private readonly SystemCounters _counters = new();
    private readonly Timer _timer;
    private int _busy;
    private long _diskFree, _diskTotal;
    private int _tick;

    public ProcessScanner Scanner { get; } = new();
    public Sample? Last { get; private set; }
    public event Action<Sample>? Sampled;

    public SystemMonitor()
    {
        Scanner.Scan(); // prime per-process deltas
        _timer = new Timer(_ => Tick(), null, 1000, 1000);
    }

    private void Tick()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return; // never overlap
        try
        {
            if (_tick++ % 10 == 0) (_diskTotal, _diskFree, _) = SystemStats.GetSystemDrive();
            var (disk, perf, pagesIn) = _counters.Sample();
            var s = new Sample(DateTime.Now, _stats.SampleCpu(), SystemStats.GetMemory(), disk, perf, pagesIn,
                _diskFree, _diskTotal, Scanner.Scan());
            Last = s;
            Sampled?.Invoke(s);
        }
        catch (Exception ex)
        {
            try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "brisk-error.log"), $"[{DateTime.Now:u}] monitor: {ex}\n"); } catch { }
        }
        finally { Volatile.Write(ref _busy, 0); }
    }

    public void Dispose()
    {
        _timer.Dispose();
        _counters.Dispose();
    }
}
