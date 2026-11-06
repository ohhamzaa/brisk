namespace Brisk.Services;

/// <summary>One second of everything Brisk knows about the PC.</summary>
public sealed record Sample(
    DateTime Time, double Cpu, MemoryInfo Mem, double DiskBusy, double CpuPerf, double PagesInPerSec,
    long DiskFree, long DiskTotal, IReadOnlyList<ProcInfo> Procs,
    GpuSnapshot Gpu = default, NetSnapshot Net = default)
{
    public double AvailPct => Mem.Total == 0 ? 100 : Mem.Available * 100.0 / Mem.Total;
    public double MemUsedPct => 100 - AvailPct;
    /// <summary>NaN when this PC exposes no GPU counters.</summary>
    public double GpuPct => Gpu.PerPidUtil == null ? double.NaN : Gpu.Utilization;
}

/// <summary>
/// Background sampler (1 Hz, on a thread-pool timer, never the UI thread). Feeds the dashboard,
/// the "why is my PC slow" diagnosis, the lag recorder and game boost from a single stream, so
/// the tray recorder keeps working when the window is hidden.
/// </summary>
public sealed class SystemMonitor : IDisposable
{
    private readonly SystemStats _stats = new();
    private readonly SystemCounters _counters = new();
    private readonly GpuCounters _gpu = new();
    private readonly Timer _timer;
    private int _busy;
    private long _diskFree, _diskTotal;
    private int _tick;

    public ProcessScanner Scanner { get; } = new();
    public NetworkProbe Network { get; } = new();
    public Sample? Last { get; private set; }
    public string? GpuName => _gpu.Available ? Last?.Gpu.Name : null;
    public event Action<Sample>? Sampled;

    /// <summary>True while hidden in the tray with nothing on screen needing per-second GPU data.</summary>
    public volatile bool LowPower;

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
            if (Demo.On)
            {
                var demo = Demo.NextSample(_tick++);
                Last = demo;
                Sampled?.Invoke(demo);
                return;
            }
            if (_tick++ % 10 == 0) (_diskTotal, _diskFree, _) = SystemStats.GetSystemDrive();
            var (disk, perf, pagesIn) = _counters.Sample();
            // GPU counters are the priciest read (Windows enumerates every engine of every process).
            // In the tray the black box only needs VRAM pressure, so read them every 5 s there.
            var gpu = !_gpu.Available ? GpuSnapshot.Empty
                    : !LowPower || _tick % 5 == 0 || Last == null ? _gpu.Sample()
                    : Last.Gpu;
            IReadOnlyList<ProcInfo> procs = Scanner.Scan();
            if (gpu.PerPidUtil.Count > 0 || gpu.PerPidVram.Count > 0)
            {
                procs = procs.Select(p =>
                {
                    double u = gpu.PerPidUtil.GetValueOrDefault(p.Pid);
                    long v = gpu.PerPidVram.GetValueOrDefault(p.Pid);
                    return u == 0 && v == 0 ? p : p with { GpuPercent = u, VramBytes = v };
                }).ToList();
            }
            var s = new Sample(DateTime.Now, _stats.SampleCpu(), SystemStats.GetMemory(), disk, perf, pagesIn,
                _diskFree, _diskTotal, procs, gpu, Network.Sample(_counters.NetDown, _counters.NetUp));
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
        Network.Dispose();
        _counters.Dispose();
        _gpu.Dispose();
    }
}
