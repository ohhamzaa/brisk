using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Brisk.Native;

namespace Brisk.Services;

public sealed record BoostReport(int Throttled, long FreedBytes, string? PlanName, bool StandbyCleared);

/// <summary>
/// Game boost: background apps go into Windows "Efficiency mode" (EcoQoS) at below-normal priority,
/// the game gets above-normal priority, idle memory is trimmed, and the power plan goes to high.
/// Nothing is closed or killed. Everything is written to disk first and restored when you stop,
/// when the game exits, or on the next launch if Brisk was closed mid-boost.
/// </summary>
public sealed class GameBoost
{
    private sealed record Saved(int Pid, long StartTicks, int Priority);
    private sealed record State(Guid? PreviousPlan, List<Saved> Apps, int GamePid, long GameStart, int GamePriority);

    private static readonly string StatePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Brisk", "boost-state.json");

    private State? _state;
    public bool Active => _state != null;
    public int GamePid => _state?.GamePid ?? 0;

    /// <summary>Never throttled: Windows itself, anything audio/input related, and Brisk.</summary>
    private static readonly HashSet<string> Never = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "dwm.exe", "audiodg.exe", "ctfmon.exe", "TextInputHost.exe", "ShellExperienceHost.exe",
        "StartMenuExperienceHost.exe", "SearchHost.exe", "sihost.exe", "taskhostw.exe", "RuntimeBroker.exe",
        "Brisk.exe", "conhost.exe", "WindowsTerminal.exe", "nvcontainer.exe", "NVIDIA Overlay.exe", "obs64.exe",
        "Discord.exe", // voice chat must not crackle mid-game
    };

    public BoostReport Start(int gamePid, IReadOnlyList<ProcInfo> procs, IReadOnlyList<PowerPlan> plans)
    {
        if (Active) Stop();
        int session = Process.GetCurrentProcess().SessionId;
        var saved = new List<Saved>();
        long freed = 0;

        int gamePrio = 0; long gameStart = 0;
        try
        {
            using var g = Process.GetProcessById(gamePid);
            gameStart = g.StartTime.Ticks;
            gamePrio = (int)g.PriorityClass;
            if (g.PriorityClass is ProcessPriorityClass.Normal or ProcessPriorityClass.BelowNormal)
                g.PriorityClass = ProcessPriorityClass.AboveNormal;
        }
        catch { }

        foreach (var p in procs)
        {
            if (p.Pid == gamePid || p.SessionId != session || ProcessScanner.Protected.Contains(p.ImageName) || Never.Contains(p.ImageName)) continue;
            if (p.ImageName.Equals(procs.FirstOrDefault(x => x.Pid == gamePid)?.ImageName, StringComparison.OrdinalIgnoreCase)) continue; // game helpers
            try
            {
                using var proc = Process.GetProcessById(p.Pid);
                var prio = proc.PriorityClass;
                if (prio is not (ProcessPriorityClass.Normal or ProcessPriorityClass.AboveNormal)) continue;
                var start = proc.StartTime.Ticks;
                proc.PriorityClass = ProcessPriorityClass.BelowNormal;
                SetEfficiencyMode(p.Pid, true);
                saved.Add(new Saved(p.Pid, start, (int)prio));
                long before = p.PrivateWorkingSet;
                if (ProcessScanner.TrimWorkingSet(p.Pid)) freed += before;
            }
            catch { /* exited, or another user's process */ }
        }

        Guid? previous = plans.FirstOrDefault(pl => pl.Active)?.Id;
        var target = plans.FirstOrDefault(pl => pl.Name.Contains("Ultimate", StringComparison.OrdinalIgnoreCase))
                  ?? plans.FirstOrDefault(pl => pl.Name.Contains("High performance", StringComparison.OrdinalIgnoreCase));
        string? planName = null;
        if (target != null && target.Id != previous && PowerPlans.SetActive(target.Id)) planName = target.Name;
        else if (target != null && target.Id == previous) { planName = target.Name; previous = null; }
        else previous = null;

        bool standby = false;
        if (SystemStats.IsAdmin && NativeMethods.EnablePrivilege("SeProfileSingleProcessPrivilege"))
        {
            int cmd = NativeMethods.MemoryPurgeLowPriorityStandbyList;
            standby = NativeMethods.NtSetSystemInformation(NativeMethods.SystemMemoryListInformation, ref cmd, sizeof(int)) == 0;
        }

        _state = new State(previous, saved, gamePid, gameStart, gamePrio);
        Persist();
        return new BoostReport(saved.Count, freed, planName, standby);
    }

    /// <summary>Undo everything. Safe to call twice. Returns how many apps were restored.</summary>
    public int Stop()
    {
        var s = _state ?? LoadPersisted();
        _state = null;
        if (s == null) return 0;
        int restored = 0;
        foreach (var a in s.Apps)
        {
            try
            {
                using var p = Process.GetProcessById(a.Pid);
                if (p.StartTime.Ticks != a.StartTicks) continue; // pid was reused by a different process
                p.PriorityClass = (ProcessPriorityClass)a.Priority;
                SetEfficiencyMode(a.Pid, false);
                restored++;
            }
            catch { }
        }
        try
        {
            using var g = Process.GetProcessById(s.GamePid);
            if (g.StartTime.Ticks == s.GameStart && s.GamePriority != 0) g.PriorityClass = (ProcessPriorityClass)s.GamePriority;
        }
        catch { }
        if (s.PreviousPlan is { } plan) PowerPlans.SetActive(plan);
        try { File.Delete(StatePath); } catch { }
        return restored;
    }

    /// <summary>If Brisk was closed or crashed while boosting, put things back now.</summary>
    public static int RecoverIfNeeded() => File.Exists(StatePath) ? new GameBoost().Stop() : 0;

    public bool GameStillRunning()
    {
        if (_state == null) return false;
        try
        {
            using var g = Process.GetProcessById(_state.GamePid);
            return !g.HasExited && g.StartTime.Ticks == _state.GameStart;
        }
        catch { return false; }
    }

    private void Persist()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            File.WriteAllText(StatePath, JsonSerializer.Serialize(_state));
        }
        catch { }
    }

    private static State? LoadPersisted()
    {
        try { return File.Exists(StatePath) ? JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath)) : null; }
        catch { return null; }
    }

    // ---------- EcoQoS / Efficiency mode (Windows 11; harmless no-op on older Windows) ----------
    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_POWER_THROTTLING_STATE { public uint Version, ControlMask, StateMask; }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(IntPtr h, int infoClass, ref PROCESS_POWER_THROTTLING_STATE info, int size);

    private const int ProcessPowerThrottling = 4;
    private const uint PROCESS_SET_INFORMATION = 0x0200;
    private const uint EXECUTION_SPEED = 0x1;

    private static void SetEfficiencyMode(int pid, bool on)
    {
        IntPtr h = NativeMethods.OpenProcess(PROCESS_SET_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return;
        try
        {
            var st = new PROCESS_POWER_THROTTLING_STATE { Version = 1, ControlMask = on ? EXECUTION_SPEED : 0, StateMask = on ? EXECUTION_SPEED : 0 };
            SetProcessInformation(h, ProcessPowerThrottling, ref st, Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
        }
        finally { NativeMethods.CloseHandle(h); }
    }

    /// <summary>Apps with a visible window, the likely games, busiest GPU first.</summary>
    public static List<(int Pid, string Image, string Title, string? Path, double Gpu)> Candidates(IReadOnlyList<ProcInfo> procs)
    {
        int session = Process.GetCurrentProcess().SessionId;
        var gpu = procs.GroupBy(p => p.Pid).ToDictionary(g => g.Key, g => g.First());
        var list = new List<(int, string, string, string?, double)>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.SessionId != session || p.MainWindowHandle == IntPtr.Zero || string.IsNullOrWhiteSpace(p.MainWindowTitle)) continue;
                    string image = p.ProcessName + ".exe";
                    if (Never.Contains(image) || ProcessScanner.Protected.Contains(image) || image is "ApplicationFrameHost.exe" or "SystemSettings.exe") continue;
                    double g = gpu.TryGetValue(p.Id, out var info) ? info.GpuPercent : 0;
                    list.Add((p.Id, image, p.MainWindowTitle, ProcessScanner.GetPath(p.Id), g));
                }
                catch { }
            }
        }
        return list.OrderByDescending(x => x.Item5).ThenBy(x => x.Item3).ToList();
    }
}
