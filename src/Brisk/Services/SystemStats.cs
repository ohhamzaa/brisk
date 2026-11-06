using Brisk.Native;
using Microsoft.Win32;

namespace Brisk.Services;

public readonly record struct MemoryInfo(ulong Total, ulong Available, ulong Cached, uint LoadPercent)
{
    public ulong Used => Total - Available;
}

/// <summary>Cheap whole-system counters. Call <see cref="SampleCpu"/> on a fixed interval.</summary>
public sealed class SystemStats
{
    private long _lastIdle, _lastKernel, _lastUser;

    public SystemStats() => SampleCpu();

    /// <summary>CPU busy % since the previous call (0-100).</summary>
    public double SampleCpu()
    {
        if (!NativeMethods.GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
        long dIdle = idle - _lastIdle, dKernel = kernel - _lastKernel, dUser = user - _lastUser;
        _lastIdle = idle; _lastKernel = kernel; _lastUser = user;
        long total = dKernel + dUser; // kernel time includes idle time
        if (total <= 0) return 0;
        return Math.Clamp((total - dIdle) * 100.0 / total, 0, 100);
    }

    public static MemoryInfo GetMemory()
    {
        var m = new NativeMethods.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>() };
        NativeMethods.GlobalMemoryStatusEx(ref m);
        ulong cached = 0;
        if (NativeMethods.GetPerformanceInfo(out var p, (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.PERFORMANCE_INFORMATION>()))
            cached = (ulong)p.SystemCache * (ulong)p.PageSize;
        return new MemoryInfo(m.ullTotalPhys, m.ullAvailPhys, cached, m.dwMemoryLoad);
    }

    public static (long Total, long Free, string Name) GetSystemDrive()
    {
        try
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            var d = new DriveInfo(root);
            return (d.TotalSize, d.AvailableFreeSpace, root.TrimEnd('\\'));
        }
        catch { return (0, 0, "C:"); }
    }

    public static TimeSpan Uptime => TimeSpan.FromMilliseconds(Environment.TickCount64);

    private static string? _cpuName;
    public static string CpuName
    {
        get
        {
            if (_cpuName != null) return _cpuName;
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                _cpuName = (k?.GetValue("ProcessorNameString") as string)?.Trim();
            }
            catch { }
            return _cpuName ??= "Unknown CPU";
        }
    }

    public static string OsName
    {
        get
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                var name = k?.GetValue("ProductName") as string ?? "Windows";
                var build = k?.GetValue("CurrentBuildNumber") as string;
                // ProductName still says "Windows 10" on Windows 11 builds.
                if (int.TryParse(build, out var b) && b >= 22000) name = name.Replace("Windows 10", "Windows 11");
                var display = k?.GetValue("DisplayVersion") as string;
                return display != null ? $"{name} {display}" : name;
            }
            catch { return "Windows"; }
        }
    }

    public static bool IsAdmin
    {
        get
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(id)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
    }
}
