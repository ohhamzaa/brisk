using Brisk.Native;

namespace Brisk.Services;

public sealed record OptimizeResult(long TrimmedBytes, int ProcessesTrimmed, bool StandbyPurged, string? Note);

/// <summary>
/// Honest RAM tuning:
///  1. Trim working sets of background apps (pages go to the standby list and are paged back on demand).
///  2. Admin only: purge the low-priority standby cache, which helps games and big apps that need free RAM *now*.
/// Never touches the foreground app or Brisk itself.
/// </summary>
public static class MemoryOptimizer
{
    public static OptimizeResult Optimize(int? skipPid = null)
    {
        var scanner = new ProcessScanner(); // own instance: don't disturb the monitor's CPU deltas
        var before = scanner.Scan();
        long beforeTotal = before.Sum(p => p.PrivateWorkingSet);
        int selfPid = Environment.ProcessId;
        int trimmed = 0;

        foreach (var p in before)
        {
            if (p.Pid == selfPid || p.Pid == skipPid || p.Pid == 4) continue;
            if (p.ImageName is "Memory Compression" or "Registry" or "Secure System") continue;
            if (ProcessScanner.TrimWorkingSet(p.Pid)) trimmed++;
        }

        bool purged = false;
        string? note = null;
        if (SystemStats.IsAdmin)
        {
            if (NativeMethods.EnablePrivilege("SeProfileSingleProcessPrivilege"))
            {
                int cmd = NativeMethods.MemoryPurgeLowPriorityStandbyList;
                purged = NativeMethods.NtSetSystemInformation(NativeMethods.SystemMemoryListInformation, ref cmd, sizeof(int)) == 0;
            }
            if (!purged) note = "Standby cache could not be purged.";
        }
        else
        {
            note = "Run as admin to also clear the standby cache.";
        }

        Thread.Sleep(400); // let the counters settle
        long afterTotal = scanner.Scan().Sum(p => p.PrivateWorkingSet);
        return new OptimizeResult(Math.Max(0, beforeTotal - afterTotal), trimmed, purged, note);
    }
}
