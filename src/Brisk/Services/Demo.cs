using System.Text.Json;

namespace Brisk.Services;

/// <summary>
/// <c>Brisk.exe --demo</c>: a realistic sample PC (a gaming setup under memory pressure) for screenshots,
/// videos and trying Brisk without touching anything. In demo mode every action is a no-op, and nothing
/// is written to your real settings, history or registry.
/// </summary>
public static class Demo
{
    public static bool On { get; set; }
    /// <summary><c>--demo --welcome</c> shows the first-run screen.</summary>
    public static bool ShowWelcome { get; set; }

    private static readonly HashSet<string> Cleared = [];
    public static void ClearJunk(string id) => Cleared.Add(id);

    private sealed record App(string Image, string Name, string?[] Paths, int Procs, long Mem, double Cpu,
        double Gpu = 0, long Vram = 0, double Faults = 0, double Io = 0, bool Window = false, string? Title = null);

    private const long MB = 1L << 20, GB = 1L << 30;
    private static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static readonly string Roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static readonly string Pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    private static readonly string Pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
    private static readonly string Win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    private static readonly App[] Apps =
    [
        new("SkylineRacers.exe", "Skyline Racers", [], 1, 5_900 * MB, 21, Gpu: 97, Vram: 6_800 * MB, Faults: 40, Window: true, Title: "Skyline Racers"),
        new("chrome.exe", "Google Chrome", [Path.Combine(Pf, @"Google\Chrome\Application\chrome.exe"), Path.Combine(Pf86, @"Google\Chrome\Application\chrome.exe")],
            19, 4_700 * MB, 6.5, Gpu: 3, Vram: 900 * MB, Faults: 780, Window: true, Title: "Lo-fi beats to relax to - YouTube - Google Chrome"),
        new("Memory Compression", "Memory Compression (Windows)", [], 1, 1_400 * MB, 0.4),
        new("Discord.exe", "Discord", [FindDiscord()], 6, 640 * MB, 1.8, Gpu: 2, Vram: 260 * MB, Window: true, Title: "#general - Discord"),
        new("steam.exe", "Steam", [Path.Combine(Pf86, @"Steam\steam.exe")], 1, 310 * MB, 0.6, Io: 2 * MB, Window: true, Title: "Steam"),
        new("steamwebhelper.exe", "Steam Client WebHelper", [Path.Combine(Pf86, @"Steam\bin\cef\cef.win7x64\steamwebhelper.exe")], 7, 420 * MB, 0.9),
        new("MsMpEng.exe", "Microsoft Defender Antivirus", [], 1, 330 * MB, 0.7),
        new("svchost.exe", "Service Host (Windows services)", [Path.Combine(Win, @"System32\svchost.exe")], 84, 290 * MB, 0.8),
        new("Spotify.exe", "Spotify", [Path.Combine(Roaming, @"Spotify\Spotify.exe")], 7, 280 * MB, 0.5, Window: true, Title: "Spotify Premium"),
        new("explorer.exe", "Windows Explorer", [Path.Combine(Win, "explorer.exe")], 1, 190 * MB, 0.3),
        new("OneDrive.exe", "Microsoft OneDrive", [Path.Combine(Local, @"Microsoft\OneDrive\OneDrive.exe"), Path.Combine(Pf, @"Microsoft OneDrive\OneDrive.exe")], 1, 150 * MB, 0.2),
        new("dwm.exe", "Desktop Window Manager", [Path.Combine(Win, @"System32\dwm.exe")], 1, 130 * MB, 1.6, Gpu: 4, Vram: 180 * MB),
        new("msedgewebview2.exe", "Microsoft Edge WebView2", [Path.Combine(Pf86, @"Microsoft\EdgeWebView\Application\msedgewebview2.exe")], 9, 240 * MB, 0.2),
        new("SearchHost.exe", "Search", [Path.Combine(Win, @"SystemApps\MicrosoftWindows.Client.CBS_cw5n1h2txyewy\SearchHost.exe")], 1, 95 * MB, 0),
        new("RuntimeBroker.exe", "Runtime Broker", [Path.Combine(Win, @"System32\RuntimeBroker.exe")], 9, 60 * MB, 0),
        new("ctfmon.exe", "CTF Loader", [Path.Combine(Win, @"System32\ctfmon.exe")], 1, 18 * MB, 0),
        new("audiodg.exe", "Windows Audio Device Graph", [], 1, 22 * MB, 0.4),
        new("System", "System (Windows kernel)", [], 1, 2 * MB, 0.6),
    ];

    private static readonly Dictionary<int, App> ByPid = new();
    private static readonly Dictionary<string, string?> PathByImage = new(StringComparer.OrdinalIgnoreCase);

    static Demo()
    {
        int pid = 100_000;
        foreach (var a in Apps)
        {
            PathByImage[a.Image] = a.Paths.FirstOrDefault(p => p != null && File.Exists(p));
            for (int i = 0; i < a.Procs; i++) ByPid[pid++] = a;
        }
    }

    private static string? FindDiscord()
    {
        try
        {
            var root = Path.Combine(Local, "Discord");
            return Directory.Exists(root)
                ? Directory.GetDirectories(root, "app-*").OrderDescending().Select(d => Path.Combine(d, "Discord.exe")).FirstOrDefault(File.Exists)
                : null;
        }
        catch { return null; }
    }

    public static string? PathFor(int pid) => ByPid.TryGetValue(pid, out var a) ? PathByImage[a.Image] : null;
    public static string? NameFor(string image) => Apps.FirstOrDefault(a => a.Image.Equals(image, StringComparison.OrdinalIgnoreCase))?.Name;

    // ---------- the live stream ----------
    private static readonly Random Rng = new(7);
    private static double Wobble(int t, double period, double amp) => Math.Sin(t / period) * amp + (Rng.NextDouble() - 0.5) * amp * 0.6;

    public static Sample NextSample(int t)
    {
        var procs = new List<ProcInfo>(ByPid.Count);
        foreach (var (pid, a) in ByPid)
        {
            double share = 1.0 / a.Procs;
            double cpu = Math.Max(0, a.Cpu * share * (1 + Wobble(t + pid, 3, 0.35)));
            procs.Add(new ProcInfo(pid, a.Image, (long)(a.Mem * share * (1 + Wobble(t, 11, 0.01))), cpu, 1,
                a.Faults * share * (1 + Wobble(t, 4, 0.3)), a.Io * share, a.Gpu * share, (long)(a.Vram * share)));
        }
        // Two overlapping waves + noise reads like a real busy PC rather than a sine.
        double totalCpu = Math.Clamp(procs.Sum(p => p.CpuPercent) + 2 + Math.Sin(t / 4.0) * 9 + Math.Sin(t / 1.7) * 4 + (Rng.NextDouble() - 0.5) * 6, 3, 100);
        ulong total = 32UL * (ulong)GB;
        double usedPct = 87.5 + Math.Sin(t / 9.0) * 1.8 + Math.Max(0, Math.Sin(t / 23.0)) * 2.5 + (Rng.NextDouble() - 0.5);
        ulong avail = (ulong)(total * (1 - usedPct / 100));
        var gpu = new GpuSnapshot(Math.Clamp(93 + Math.Sin(t / 3.0) * 5 + (Rng.NextDouble() - 0.5) * 4, 70, 100), 7_850 * MB + (long)(Wobble(t, 9, 40) * MB), 8 * GB, "Demo GPU",
            procs.Where(p => p.GpuPercent > 0).ToDictionary(p => p.Pid, p => p.GpuPercent),
            procs.Where(p => p.VramBytes > 0).ToDictionary(p => p.Pid, p => p.VramBytes));
        var net = new NetSnapshot(2 + Wobble(t, 4, 0.8), 0, 19 + Wobble(t, 5, 3), 0, 3 + Rng.NextDouble() * 2,
            1.4 * MB * (1 + Wobble(t, 3, 0.5)), 180_000 * (1 + Wobble(t, 4, 0.5)), true);
        return new Sample(DateTime.Now, totalCpu, new MemoryInfo(total, avail, 6UL * (ulong)GB, (uint)usedPct),
            Math.Clamp(14 + Wobble(t, 4, 6), 1, 100), 104 + Wobble(t, 8, 2), 820 + Wobble(t, 3, 120),
            412 * GB, 931 * GB, procs, gpu, net);
    }

    // ---------- static pages ----------
    public static List<StartupEntry> Startup() =>
    [
        Entry("Discord", PathByImage["Discord.exe"], "Discord Inc.", true),
        Entry("Steam", PathByImage["steam.exe"], "Valve Corporation", true),
        Entry("Spotify", PathByImage["Spotify.exe"], "Spotify Ltd", true),
        Entry("OneDrive", PathByImage["OneDrive.exe"], "Microsoft Corporation", true),
        Entry("SecurityHealth", Path.Combine(Win, @"System32\SecurityHealthSystray.exe"), "Microsoft Corporation", true, StartupSource.MachineRun),
        Entry("Skyline Racers Launcher", null, "Northlight Games", true),
        Entry("Microsoft Edge Update", PathByImage["msedgewebview2.exe"], "Microsoft Corporation", false),
        Entry("Adobe Updater", null, "Adobe Inc.", false),
        Entry("Logitech G HUB", null, "Logitech", false),
    ];

    private static StartupEntry Entry(string name, string? exe, string publisher, bool on, StartupSource src = StartupSource.UserRun) => new()
    {
        Name = name, Command = exe != null ? $"\"{exe}\"" : $"\"C:\\Program Files\\{name}\\{name.Replace(" ", "")}.exe\" --background",
        ExePath = exe != null && File.Exists(exe) ? exe : null, Publisher = publisher, Source = src, Enabled = on,
    };

    public static long JunkBytes(string id) => Cleared.Contains(id) ? 0 : id switch
    {
        "temp" => 3_180 * MB, "browser" => 1_460 * MB, "thumbs" => 214 * MB, "crash" => 655 * MB,
        "dev" => 4_900 * MB, "recycle" => 1_230 * MB, _ => 0,
    };

    private static readonly List<PowerPlan> Plans =
    [
        new(Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e"), "Balanced", false),
        new(Guid.Parse("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"), "High performance", true),
        new(Guid.Parse("a1841308-3541-4fab-bc81-f71556f20b4a"), "Power saver", false),
        new(Guid.Parse("e9a42b02-d5df-448d-aa00-03f14749eb61"), "Ultimate Performance", false),
    ];

    public static List<PowerPlan> PowerPlans() => Plans.ToList();

    public static void SetPlan(Guid id)
    {
        for (int i = 0; i < Plans.Count; i++) Plans[i] = Plans[i] with { Active = Plans[i].Id == id };
    }

    public static List<(int Pid, string Image, string Title, string? Path, double Gpu)> BoostCandidates() =>
        Apps.Where(a => a.Window).Select(a => (ByPid.First(kv => kv.Value == a).Key, a.Image, a.Title ?? a.Name, PathByImage[a.Image], a.Gpu))
            .OrderByDescending(x => x.Gpu).ToList();

    /// <summary>A believable week of slowdowns, written to a throwaway file.</summary>
    public static string SeedHistory()
    {
        var path = Path.Combine(Path.GetTempPath(), "brisk-demo-history.json");
        DateTime D(int daysAgo, int h, int m) => DateTime.Today.AddDays(-daysAgo).AddHours(h).AddMinutes(m);
        var events = new List<LagEvent>
        {
            Ev(D(0, 21, 14), 42, ["Memory"], "Your memory is full and Windows is swapping to disk", ["Google Chrome", "Skyline Racers", "Discord"]),
            Ev(D(0, 16, 2), 12, ["Disk"], "Microsoft Defender Antivirus is keeping your disk busy", ["Microsoft Defender Antivirus"]),
            Ev(D(1, 22, 40), 95, ["Network"], "Your internet connection is lagging", ["Steam"]),
            Ev(D(1, 13, 5), 8, ["CPU"], "Windows Modules Installer is maxing out your CPU", ["Windows Modules Installer", "Service Host (Windows services)"]),
            Ev(D(3, 20, 31), 26, ["CPU"], "Skyline Racers is maxing out your CPU", ["Skyline Racers", "Discord", "Google Chrome"]),
            Ev(D(4, 23, 10), 61, ["Memory", "GPU memory"], "Your graphics memory is full", ["Skyline Racers", "Google Chrome"]),
            Ev(D(5, 9, 45), 14, ["Disk"], "Microsoft OneDrive is keeping your disk busy", ["Microsoft OneDrive"]),
            Ev(D(6, 19, 20), 33, ["Network"], "Your Wi-Fi / router connection is unstable", []),
        };
        File.WriteAllText(path, JsonSerializer.Serialize(events));
        return path;
    }

    private static LagEvent Ev(DateTime start, double secs, List<string> kinds, string headline, List<string> culprits) => new()
    {
        Start = start, Seconds = secs, Kinds = kinds, Headline = headline, Culprits = culprits,
        PeakCpu = kinds.Contains("CPU") ? 99 : 45, PeakDisk = kinds.Contains("Disk") ? 98 : 20, PeakMemUsed = kinds.Contains("Memory") ? 95 : 70,
        Detail = "Recorded in demo mode.",
    };
}
