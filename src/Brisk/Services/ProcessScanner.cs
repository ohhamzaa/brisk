using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Brisk.Native;

namespace Brisk.Services;

/// <param name="PrivateWorkingSet">Bytes of RAM only this process uses (Task Manager's "Memory").</param>
/// <param name="CpuPercent">Share of total CPU since the previous scan.</param>
/// <param name="HardFaultsPerSec">Pages Windows had to read back from disk for this process: swapping.</param>
/// <param name="IoBytesPerSec">Bytes read + written per second (files, disk, devices).</param>
public sealed record ProcInfo(int Pid, string ImageName, long PrivateWorkingSet, double CpuPercent, int SessionId,
    double HardFaultsPerSec, double IoBytesPerSec);

/// <summary>
/// Reads every process in one NtQuerySystemInformation call (what Task Manager does).
/// Works for protected processes too and costs ~1 ms instead of one syscall per process.
/// </summary>
public sealed class ProcessScanner
{
    // Offsets into SYSTEM_PROCESS_INFORMATION (64-bit).
    private const int OffNext = 0, OffPrivateWs = 8, OffHardFaults = 16, OffUserTime = 40, OffKernelTime = 48,
        OffNameLen = 56, OffNameBuf = 64, OffPid = 80, OffSession = 100,
        OffReadBytes = 232, OffWriteBytes = 240, OffOtherBytes = 248;

    private readonly record struct Prev(long Cpu, uint Faults, long Io);

    private readonly Dictionary<int, Prev> _prev = new();
    private readonly object _gate = new();
    private long _lastTimestamp;
    private IntPtr _buf = IntPtr.Zero;
    private int _bufSize = 1 << 20;
    private static readonly int Cores = Environment.ProcessorCount;

    public ProcessScanner()
    {
        if (!Environment.Is64BitProcess) throw new PlatformNotSupportedException("Brisk is a 64-bit app.");
    }

    /// <summary>Thread-safe: the native buffer and the per-process deltas are shared state.</summary>
    public List<ProcInfo> Scan()
    {
        lock (_gate) return ScanCore();
    }

    private List<ProcInfo> ScanCore()
    {
        var result = new List<ProcInfo>(400);
        if (!Query()) return result;

        long now = Stopwatch.GetTimestamp();
        double seconds = _lastTimestamp == 0 ? 0 : (now - _lastTimestamp) / (double)Stopwatch.Frequency;
        _lastTimestamp = now;

        var seen = new HashSet<int>();
        IntPtr p = _buf;
        while (true)
        {
            int next = Marshal.ReadInt32(p, OffNext);
            int pid = (int)Marshal.ReadInt64(p, OffPid);
            if (pid != 0) // skip System Idle Process
            {
                long ws = Marshal.ReadInt64(p, OffPrivateWs);
                uint faults = (uint)Marshal.ReadInt32(p, OffHardFaults);
                long cpuTime = Marshal.ReadInt64(p, OffUserTime) + Marshal.ReadInt64(p, OffKernelTime);
                long io = Marshal.ReadInt64(p, OffReadBytes) + Marshal.ReadInt64(p, OffWriteBytes) + Marshal.ReadInt64(p, OffOtherBytes);
                int session = Marshal.ReadInt32(p, OffSession);
                ushort nameLen = (ushort)Marshal.ReadInt16(p, OffNameLen);
                IntPtr nameBuf = Marshal.ReadIntPtr(p, OffNameBuf);
                string name = nameLen > 0 && nameBuf != IntPtr.Zero
                    ? Marshal.PtrToStringUni(nameBuf, nameLen / 2)
                    : (pid == 4 ? "System" : "Unknown");

                double cpu = 0, faultRate = 0, ioRate = 0;
                if (seconds > 0 && _prev.TryGetValue(pid, out var prev))
                {
                    cpu = Math.Clamp((cpuTime - prev.Cpu) / (seconds * 1e7 * Cores) * 100.0, 0, 100);
                    faultRate = Math.Max(0, (long)faults - prev.Faults) / seconds;
                    ioRate = Math.Max(0, io - prev.Io) / seconds;
                }
                _prev[pid] = new Prev(cpuTime, faults, io);
                seen.Add(pid);
                result.Add(new ProcInfo(pid, name, ws, cpu, session, faultRate, ioRate));
            }
            if (next == 0) break;
            p += next;
        }

        foreach (var dead in _prev.Keys.Where(k => !seen.Contains(k)).ToList()) _prev.Remove(dead);
        return result;
    }

    private bool Query()
    {
        for (int attempt = 0; attempt < 6; attempt++)
        {
            if (_buf == IntPtr.Zero) _buf = Marshal.AllocHGlobal(_bufSize);
            int status = NativeMethods.NtQuerySystemInformation(NativeMethods.SystemProcessInformation, _buf, _bufSize, out int needed);
            if (status == 0) return true;
            if (status != NativeMethods.STATUS_INFO_LENGTH_MISMATCH) return false;
            Marshal.FreeHGlobal(_buf);
            _buf = IntPtr.Zero;
            _bufSize = Math.Max(needed, _bufSize) + (256 << 10);
        }
        return false;
    }

    // ---------- per-process helpers ----------

    private static readonly Dictionary<int, string?> PathCache = new();

    public static string? GetPath(int pid)
    {
        lock (PathCache)
        {
            if (PathCache.TryGetValue(pid, out var cached)) return cached;
        }
        string? path = null;
        IntPtr h = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h != IntPtr.Zero)
        {
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                if (NativeMethods.QueryFullProcessImageName(h, 0, sb, ref size)) path = sb.ToString();
            }
            finally { NativeMethods.CloseHandle(h); }
        }
        lock (PathCache)
        {
            if (PathCache.Count > 4000) PathCache.Clear(); // pids get reused; don't grow forever
            PathCache[pid] = path;
        }
        return path;
    }

    public static void ForgetPid(int pid)
    {
        lock (PathCache) PathCache.Remove(pid);
    }

    /// <summary>Moves a process's pages out of RAM (they come back on demand). Returns false if access denied.</summary>
    public static bool TrimWorkingSet(int pid)
    {
        IntPtr h = NativeMethods.OpenProcess(NativeMethods.PROCESS_SET_QUOTA | NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return false;
        try { return NativeMethods.EmptyWorkingSet(h); }
        finally { NativeMethods.CloseHandle(h); }
    }

    public static bool Kill(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill();
            return true;
        }
        catch { return false; }
    }

    /// <summary>Processes Windows needs to stay alive. Brisk never offers to end these.</summary>
    public static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Registry", "Memory Compression", "Secure System", "smss.exe", "csrss.exe", "wininit.exe",
        "winlogon.exe", "services.exe", "lsass.exe", "lsaiso.exe", "svchost.exe", "dwm.exe", "fontdrvhost.exe",
        "sihost.exe", "ctfmon.exe", "explorer.exe", "spoolsv.exe", "audiodg.exe", "MsMpEng.exe",
        "SecurityHealthService.exe", "WUDFHost.exe", "conhost.exe", "Brisk.exe",
    };
}
