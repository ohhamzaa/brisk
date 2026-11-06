using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Brisk.Services;

public readonly record struct GpuSnapshot(
    double Utilization, long VramUsed, long VramTotal, string? Name,
    IReadOnlyDictionary<int, double> PerPidUtil, IReadOnlyDictionary<int, long> PerPidVram)
{
    public static readonly GpuSnapshot Empty = new(double.NaN, 0, 0, null,
        new Dictionary<int, double>(), new Dictionary<int, long>());
    public double VramPct => VramTotal > 0 ? VramUsed * 100.0 / VramTotal : double.NaN;
}

/// <summary>
/// GPU load and video memory, overall and per process, from the same WDDM performance counters
/// Task Manager reads ("GPU Engine", "GPU Process Memory", "GPU Adapter Memory").
/// </summary>
public sealed class GpuCounters : IDisposable
{
    private IntPtr _query, _engine, _procMem, _adapterMem;
    private readonly long _vramTotal;
    private readonly string? _name;

    public bool Available => _engine != IntPtr.Zero;

    public GpuCounters()
    {
        (_vramTotal, _name) = ReadAdapterInfo();
        if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0) { _query = IntPtr.Zero; return; }
        if (PdhAddEnglishCounter(_query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _engine) != 0) _engine = IntPtr.Zero;
        if (PdhAddEnglishCounter(_query, @"\GPU Process Memory(*)\Dedicated Usage", IntPtr.Zero, out _procMem) != 0) _procMem = IntPtr.Zero;
        if (PdhAddEnglishCounter(_query, @"\GPU Adapter Memory(*)\Dedicated Usage", IntPtr.Zero, out _adapterMem) != 0) _adapterMem = IntPtr.Zero;
        PdhCollectQueryData(_query);
    }

    public GpuSnapshot Sample()
    {
        if (_query == IntPtr.Zero || _engine == IntPtr.Zero || PdhCollectQueryData(_query) != 0) return GpuSnapshot.Empty;

        // Instance names look like  pid_1234_luid_0x0_0xD1F3_phys_0_eng_0_engtype_3D
        // Task Manager's rule: an engine's load = sum over processes; GPU load = busiest engine;
        // a process's load = its busiest engine type.
        var perEngine = new Dictionary<string, double>();
        var perPidEngine = new Dictionary<(int, string), double>();
        foreach (var (name, v) in ReadArray(_engine))
        {
            int pid = ParsePid(name);
            int luid = name.IndexOf("_luid_", StringComparison.Ordinal);
            int et = name.IndexOf("_engtype_", StringComparison.Ordinal);
            int eng = name.IndexOf("_eng_", StringComparison.Ordinal);
            if (pid < 0 || luid < 0 || et < 0 || eng < 0) continue;
            string engineKey = name[luid..et];          // luid + phys + engine index
            string type = name[(et + 9)..];
            perEngine[engineKey] = perEngine.GetValueOrDefault(engineKey) + v;
            perPidEngine[(pid, type)] = perPidEngine.GetValueOrDefault((pid, type)) + v;
        }
        var perPid = new Dictionary<int, double>();
        foreach (var ((pid, _), v) in perPidEngine) perPid[pid] = Math.Max(perPid.GetValueOrDefault(pid), Math.Min(100, v));
        double util = perEngine.Count == 0 ? 0 : Math.Min(100, perEngine.Values.Max());

        var perPidVram = new Dictionary<int, long>();
        foreach (var (name, v) in ReadArray(_procMem))
        {
            int pid = ParsePid(name);
            if (pid >= 0) perPidVram[pid] = perPidVram.GetValueOrDefault(pid) + (long)v;
        }
        long used = 0;
        foreach (var (_, v) in ReadArray(_adapterMem)) used = Math.Max(used, (long)v); // main GPU = the busiest adapter

        return new GpuSnapshot(util, used, _vramTotal, _name, perPid, perPidVram);
    }

    private static int ParsePid(string instance)
    {
        if (!instance.StartsWith("pid_", StringComparison.Ordinal)) return -1;
        int end = instance.IndexOf('_', 4);
        return end > 4 && int.TryParse(instance.AsSpan(4, end - 4), out var pid) ? pid : -1;
    }

    /// <summary>Dedicated VRAM and name of the biggest display adapter, from the driver's registry entry.</summary>
    private static (long, string?) ReadAdapterInfo()
    {
        long best = 0; string? name = null;
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls == null) return (0, null);
            foreach (var sub in cls.GetSubKeyNames().Where(n => n.Length == 4 && char.IsDigit(n[0])))
            {
                using var k = cls.OpenSubKey(sub);
                long size = k?.GetValue("HardwareInformation.qwMemorySize") switch
                {
                    long l => l,
                    byte[] b when b.Length >= 8 => BitConverter.ToInt64(b, 0),
                    _ => k?.GetValue("HardwareInformation.MemorySize") switch
                    {
                        int i => (uint)i,
                        byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b, 0),
                        _ => 0,
                    },
                };
                if (size > best) { best = size; name = k?.GetValue("DriverDesc") as string; }
            }
        }
        catch { }
        return (best, name);
    }

    /// <summary>All instances of a wildcard PDH counter, e.g. \Network Interface(*)\Bytes Received/sec.</summary>
    internal static List<(string Name, double Value)> ReadArray(IntPtr counter)
    {
        var list = new List<(string, double)>();
        if (counter == IntPtr.Zero) return list;
        uint size = 0;
        int st = PdhGetFormattedCounterArray(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out uint count, IntPtr.Zero);
        if (st != PDH_MORE_DATA || size == 0) return list;
        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArray(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out count, buf) != 0) return list;
            const int itemSize = 24; // { wchar* name; { DWORD status; [pad]; double value } }
            for (int i = 0; i < count; i++)
            {
                IntPtr item = buf + i * itemSize;
                if (Marshal.ReadInt32(item, 8) != 0) continue; // CStatus != PDH_CSTATUS_VALID_DATA
                string? name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item, 0));
                double v = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, 16));
                if (name != null && !double.IsNaN(v)) list.Add((name, v));
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return list;
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero) { PdhCloseQuery(_query); _query = IntPtr.Zero; }
    }

    private const uint PDH_FMT_DOUBLE = 0x200, PDH_FMT_NOCAP100 = 0x8000;
    private const int PDH_MORE_DATA = unchecked((int)0x800007D2);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhAddEnglishCounterW")]
    private static extern int PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern int PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhGetFormattedCounterArrayW")]
    private static extern int PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr buffer);

    [DllImport("pdh.dll")]
    private static extern int PdhCloseQuery(IntPtr query);
}
