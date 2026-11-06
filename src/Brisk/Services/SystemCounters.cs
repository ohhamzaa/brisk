using System.Runtime.InteropServices;

namespace Brisk.Services;

/// <summary>
/// The few whole-system numbers that aren't in the process snapshot, read through PDH
/// (the API behind Performance Monitor). English counter names work on every Windows language.
/// </summary>
public sealed class SystemCounters : IDisposable
{
    private IntPtr _query;
    private IntPtr _diskIdle, _cpuPerf, _pagesIn;

    public SystemCounters()
    {
        if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0) { _query = IntPtr.Zero; return; }
        PdhAddEnglishCounter(_query, @"\PhysicalDisk(_Total)\% Idle Time", IntPtr.Zero, out _diskIdle);
        PdhAddEnglishCounter(_query, @"\Processor Information(_Total)\% Processor Performance", IntPtr.Zero, out _cpuPerf);
        PdhAddEnglishCounter(_query, @"\Memory\Pages Input/sec", IntPtr.Zero, out _pagesIn);
        PdhCollectQueryData(_query); // rate counters need a first sample
    }

    /// <summary>Call once per second. Values are NaN if a counter isn't available on this PC.</summary>
    public (double DiskBusy, double CpuPerf, double PagesInPerSec) Sample()
    {
        if (_query == IntPtr.Zero || PdhCollectQueryData(_query) != 0) return (double.NaN, double.NaN, double.NaN);
        double idle = Read(_diskIdle);
        return (double.IsNaN(idle) ? double.NaN : Math.Clamp(100 - idle, 0, 100), Read(_cpuPerf), Read(_pagesIn));
    }

    private static double Read(IntPtr counter)
    {
        if (counter == IntPtr.Zero) return double.NaN;
        return PdhGetFormattedCounterValue(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, IntPtr.Zero, out var v) == 0 && v.CStatus == 0
            ? v.DoubleValue : double.NaN;
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero) { PdhCloseQuery(_query); _query = IntPtr.Zero; }
    }

    private const uint PDH_FMT_DOUBLE = 0x200, PDH_FMT_NOCAP100 = 0x8000;

    [StructLayout(LayoutKind.Explicit)]
    private struct PDH_FMT_COUNTERVALUE
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double DoubleValue;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhAddEnglishCounterW")]
    private static extern int PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern int PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    private static extern int PdhGetFormattedCounterValue(IntPtr counter, uint format, IntPtr type, out PDH_FMT_COUNTERVALUE value);

    [DllImport("pdh.dll")]
    private static extern int PdhCloseQuery(IntPtr query);
}
