namespace Brisk.Services;

public enum IssueKind { Cpu, Memory, Disk, Throttle, Space, Gpu, Vram, Network }
public enum FixKind { TrimApp, EndApp, FreeRam, HighPerformance, OpenStartup, OpenCleanup, GameBoost }

public sealed record FixAction(string Label, FixKind Kind, string? Target = null, string? TargetName = null);

public sealed record Culprit(string Image, string Name, double Cpu, long Memory, double FaultsPerSec, double IoPerSec, int? SamplePid,
    double Gpu = 0, long Vram = 0)
{
    public string Stats => string.Join("  ·  ", new[]
    {
        Cpu >= 1 ? $"CPU {Cpu:0}%" : null,
        Gpu >= 1 ? $"GPU {Gpu:0}%" : null,
        Memory >= 50L << 20 ? $"RAM {Fmt.Bytes(Memory)}" : null,
        Vram >= 100L << 20 ? $"VRAM {Fmt.Bytes(Vram)}" : null,
        IoPerSec >= 1 << 20 ? $"Disk {Fmt.Bytes(IoPerSec)}/s" : null,
        FaultsPerSec >= 20 ? $"Swapping {FaultsPerSec:0}/s" : null,
    }.Where(s => s != null));
}

public sealed record Issue(IssueKind Kind, double Severity, string Headline, string Detail, Culprit? Culprit, IReadOnlyList<FixAction> Fixes);

public sealed record Diagnosis(DateTime Time, IReadOnlyList<Issue> Issues, double AvgCpu, double MemUsed, double AvgDisk, double AvgPerf,
    double AvgGpu = double.NaN, double InternetMs = double.NaN)
{
    public bool Healthy => Issues.Count == 0;
}

/// <summary>
/// Turns a few seconds of samples into "your PC is slow because X, do Y".
/// Pure function of the samples, so the live diagnosis and the lag recorder give the same answers.
/// </summary>
public static class Diagnoser
{
    public static Diagnosis Analyze(IReadOnlyList<Sample> samples, string? activePlanName = null)
    {
        if (samples.Count == 0) return new Diagnosis(DateTime.Now, [], 0, 0, 0, double.NaN);

        double avgCpu = samples.Average(s => s.Cpu);
        double avgDisk = Avg(samples.Select(s => s.DiskBusy));
        double avgPerf = Avg(samples.Select(s => s.CpuPerf));
        double avgPagesIn = Avg(samples.Select(s => s.PagesInPerSec));
        double avgGpu = Avg(samples.Select(s => s.GpuPct));
        double minAvail = samples.Min(s => s.AvailPct);
        var last = samples[^1];
        var apps = Aggregate(samples);
        var issues = new List<Issue>();

        // ---- CPU ----
        if (avgCpu >= 75)
        {
            var c = apps.MaxBy(a => a.Cpu);
            var fixes = new List<FixAction>();
            if (c != null && AppNames.CanOfferEnd(c.Image)) fixes.Add(new("End " + c.Name, FixKind.EndApp, c.Image, c.Name));
            issues.Add(new Issue(IssueKind.Cpu, Scale(avgCpu, 70, 100),
                c != null && c.Cpu >= 10 ? $"{c.Name} is maxing out your CPU" : $"Your CPU is maxed out at {avgCpu:0}%",
                c != null && c.Cpu >= 10
                    ? $"Your CPU averaged {avgCpu:0}% and {c.Name} alone used {c.Cpu:0}% of it. {AppNames.Explain(c.Image, c.Name)}"
                    : $"Lots of small things are adding up to {avgCpu:0}% CPU. Close apps you aren't using.",
                c, fixes));
        }

        // ---- memory ----
        bool swapping = avgPagesIn >= 600;
        if (minAvail <= 12 || (swapping && minAvail <= 25))
        {
            // The app making Windows swap most is the best culprit; fall back to the biggest app.
            var bySwap = apps.Where(a => a.Image != "Memory Compression").MaxBy(a => a.FaultsPerSec);
            var c = bySwap is { FaultsPerSec: >= 50 } ? bySwap
                  : apps.Where(a => a.Image is not ("Memory Compression" or "System")).MaxBy(a => a.Memory);
            var fixes = new List<FixAction> { new("Free up RAM", FixKind.FreeRam) };
            if (c != null) fixes.Add(new("Trim " + c.Name, FixKind.TrimApp, c.Image, c.Name));
            if (c != null && AppNames.CanOfferEnd(c.Image)) fixes.Add(new("End " + c.Name, FixKind.EndApp, c.Image, c.Name));
            double used = 100 - minAvail;
            issues.Add(new Issue(IssueKind.Memory, Math.Max(Scale(used, 85, 100), swapping ? 0.75 : 0),
                swapping ? "Your memory is full and Windows is swapping to disk" : $"Your memory is almost full ({used:0}%)",
                (swapping
                    ? $"Only {100 - used:0}% of RAM is free, so Windows is reading {avgPagesIn:0} pages a second back from disk. That's the stutter you feel. "
                    : $"{used:0}% of your RAM is in use. ")
                + (c != null ? $"{c.Name} is using {Fmt.Bytes(c.Memory)}. {AppNames.Explain(c.Image, c.Name)}" : ""),
                c, fixes));
        }

        // ---- disk activity ----
        if (!double.IsNaN(avgDisk) && avgDisk >= 70)
        {
            var c = apps.MaxBy(a => a.IoPerSec);
            if (c is { IoPerSec: < 256 * 1024 }) c = null; // nobody stands out
            var fixes = new List<FixAction>();
            if (c != null && AppNames.CanOfferEnd(c.Image)) fixes.Add(new("End " + c.Name, FixKind.EndApp, c.Image, c.Name));
            issues.Add(new Issue(IssueKind.Disk, Scale(avgDisk, 60, 100),
                c != null ? $"{c.Name} is keeping your disk busy" : $"Your disk is {avgDisk:0}% busy",
                c != null
                    ? $"Your disk was {avgDisk:0}% busy and {c.Name} is moving {Fmt.Bytes(c.IoPerSec)} a second. Apps that need the disk wait in line. {AppNames.Explain(c.Image, c.Name)}"
                    : $"Your disk was {avgDisk:0}% busy, which makes apps slow to open and save. "
                      + (swapping ? "Much of it is Windows swapping memory." : "Nothing single stands out; it's many small reads and writes."),
                c, fixes));
        }

        // ---- GPU load ----
        if (!double.IsNaN(avgGpu) && avgGpu >= 95)
        {
            var c = apps.MaxBy(a => a.Gpu);
            if (c is { Gpu: < 20 }) c = null;
            var fixes = new List<FixAction>();
            bool looksLikeGame = c != null && AppNames.Classify(c.Image) == AppNames.Category.App;
            if (c != null && !looksLikeGame && AppNames.CanOfferEnd(c.Image)) fixes.Add(new("End " + c.Name, FixKind.EndApp, c.Image, c.Name));
            if (looksLikeGame) fixes.Add(new("Game boost", FixKind.GameBoost, c!.Image, c.Name));
            issues.Add(new Issue(IssueKind.Gpu, Scale(avgGpu, 90, 100) * 0.8, // a maxed GPU in a game is often expected
                c != null ? $"{c.Name} is maxing out your graphics card" : $"Your graphics card is maxed out ({avgGpu:0}%)",
                (c != null ? $"Your GPU averaged {avgGpu:0}% and {c.Name} used {c.Gpu:0}% of it. " : $"Your GPU averaged {avgGpu:0}%. ")
                + (looksLikeGame
                    ? "If this is a game, that's normal at high settings. For smoother play, lower the graphics quality or cap the frame rate a little below your monitor's refresh rate."
                    : c != null ? $"{c.Name} is using your graphics card heavily. If you aren't watching or playing something in it, close it." : ""),
                c, fixes));
        }

        // ---- video memory ----
        double vramPct = last.Gpu.VramTotal > 0 ? last.Gpu.VramPct : double.NaN;
        if (!double.IsNaN(vramPct) && vramPct >= 92)
        {
            var byVram = apps.Where(a => a.Vram > 0).OrderByDescending(a => a.Vram).ToList();
            var top = byVram.FirstOrDefault();
            // Offer to close the *other* big VRAM user, not the game you're playing.
            var other = byVram.Skip(1).FirstOrDefault(a => a.Vram >= 200L << 20 && AppNames.CanOfferEnd(a.Image));
            var fixes = new List<FixAction>();
            if (other != null) fixes.Add(new("End " + other.Name, FixKind.EndApp, other.Image, other.Name));
            issues.Add(new Issue(IssueKind.Vram, Scale(vramPct, 88, 100),
                "Your graphics memory is full",
                $"{Fmt.Bytes(last.Gpu.VramUsed)} of {Fmt.Bytes(last.Gpu.VramTotal)} video memory is in use, so textures spill into normal RAM and games stutter. "
                + (top != null ? $"{top.Name} uses {Fmt.Bytes(top.Vram)}. " : "")
                + (other != null ? $"{other.Name} is also holding {Fmt.Bytes(other.Vram)}. " : "")
                + "Lower texture quality, or close other apps that use the GPU (browsers, Discord, wallpaper apps).",
                top, fixes));
        }

        // ---- network ----
        var net = last.Net;
        if (net.Probing)
        {
            double down = samples.Average(s => s.Net.DownBytesPerSec);
            var hog = apps.Where(a => AppNames.Classify(a.Image) is AppNames.Category.Launcher or AppNames.Category.Update or AppNames.Category.Sync
                                      && a.IoPerSec >= 1 << 20).MaxBy(a => a.IoPerSec);
            string busy = down >= 2 << 20
                ? $" Something is downloading at {Fmt.Bytes(down)}/s" + (hog != null ? $" (most likely {hog.Name})." : ".")
                : "";
            if (!double.IsNaN(net.RouterMs) && (net.RouterLoss >= 0.1 || net.RouterMs >= 30))
            {
                issues.Add(new Issue(IssueKind.Network, Math.Max(Scale(net.RouterMs, 20, 150), net.RouterLoss >= 0.1 ? 0.8 : 0),
                    "Your Wi-Fi / router connection is unstable",
                    $"Pings to your own router take {net.RouterMs:0} ms" + (net.RouterLoss > 0 ? $" and {net.RouterLoss:P0} are lost" : "")
                    + ". On a healthy home network that's under 10 ms. Move closer to the router, switch to 5 GHz Wi-Fi or an Ethernet cable, or restart the router." + busy,
                    hog, []));
            }
            else if (!double.IsNaN(net.InternetMs) && (net.InternetLoss >= 0.05 || net.InternetMs >= 120 || net.InternetJitter >= 30)
                     || double.IsNaN(net.InternetMs) && net.InternetLoss >= 0.5)
            {
                string router = double.IsNaN(net.RouterMs) ? "" : $"Your router responds fine ({net.RouterMs:0} ms), but ";
                issues.Add(new Issue(IssueKind.Network, Math.Max(Scale(net.InternetMs, 80, 300), net.InternetLoss >= 0.05 ? 0.7 : 0.3),
                    double.IsNaN(net.InternetMs) ? "Your internet connection is down" : "Your internet connection is lagging",
                    double.IsNaN(net.InternetMs)
                        ? "Brisk can't reach the internet at all right now."
                        : $"{router}the internet takes {net.InternetMs:0} ms" + (net.InternetLoss > 0 ? $" with {net.InternetLoss:P0} packet loss" : "")
                          + (net.InternetJitter >= 30 ? $" and jumps around by {net.InternetJitter:0} ms" : "")
                          + ". That's what makes games rubber-band and calls break up."
                          + (busy.Length > 0 ? busy + " Pause it while you play." : " The problem is most likely on your internet provider's side."),
                    hog, []));
            }
        }

        // ---- CPU held back (power plan / heat / battery) ----
        if (!double.IsNaN(avgPerf) && avgPerf < 70 && avgCpu >= 15)
        {
            bool saver = activePlanName?.Contains("saver", StringComparison.OrdinalIgnoreCase) == true;
            bool high = activePlanName != null && (activePlanName.Contains("High", StringComparison.OrdinalIgnoreCase)
                                                   || activePlanName.Contains("Ultimate", StringComparison.OrdinalIgnoreCase));
            var fixes = new List<FixAction>();
            if (!high) fixes.Add(new("Switch to High performance", FixKind.HighPerformance));
            issues.Add(new Issue(IssueKind.Throttle, Scale(100 - avgPerf, 30, 70),
                $"Your CPU is running at only {avgPerf:0}% of its normal speed",
                saver ? "Power saver mode is holding it back. Switch to Balanced or High performance."
                      : high ? "Your power plan is already on high, so this is likely heat (dusty fans, a laptop on a soft surface) or a laptop running on battery."
                             : "That's usually the power plan, a laptop on battery, or the CPU getting too hot.",
                null, fixes));
        }

        // ---- low disk space ----
        if (last.DiskTotal > 0)
        {
            double usedPct = (last.DiskTotal - last.DiskFree) * 100.0 / last.DiskTotal;
            if (usedPct >= 92)
                issues.Add(new Issue(IssueKind.Space, Scale(usedPct, 90, 100),
                    $"Your system drive is {usedPct:0}% full",
                    $"Only {Fmt.Bytes(last.DiskFree)} is left. Windows needs free space for updates, its page file and temp files, and slows down without it.",
                    null, [new("Clean junk files", FixKind.OpenCleanup)]));
        }

        return new Diagnosis(DateTime.Now, issues.OrderByDescending(i => i.Severity).ToList(),
            avgCpu, samples.Average(s => s.MemUsedPct), avgDisk, avgPerf, avgGpu, net.InternetMs);
    }

    /// <summary>Per-app averages across the window (CPU, GPU, swap, I/O) and peak memory / VRAM.</summary>
    public static List<Culprit> Aggregate(IReadOnlyList<Sample> samples)
    {
        int self = Environment.ProcessId;
        var acc = new Dictionary<string, (double Cpu, double Faults, double Io, long Mem, int Pid, double Gpu, long Vram)>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in samples)
        {
            foreach (var g in s.Procs.Where(p => p.Pid != self).GroupBy(p => p.ImageName, StringComparer.OrdinalIgnoreCase))
            {
                acc.TryGetValue(g.Key, out var a);
                acc[g.Key] = (a.Cpu + g.Sum(p => p.CpuPercent), a.Faults + g.Sum(p => p.HardFaultsPerSec),
                    a.Io + g.Sum(p => p.IoBytesPerSec), Math.Max(a.Mem, g.Sum(p => p.PrivateWorkingSet)), g.First().Pid,
                    a.Gpu + Math.Min(100, g.Sum(p => p.GpuPercent)), Math.Max(a.Vram, g.Sum(p => p.VramBytes)));
            }
        }
        int n = samples.Count;
        return acc.Select(kv => new Culprit(kv.Key, AppNames.Resolve(kv.Key, kv.Value.Pid),
            kv.Value.Cpu / n, kv.Value.Mem, kv.Value.Faults / n, kv.Value.Io / n, kv.Value.Pid,
            kv.Value.Gpu / n, kv.Value.Vram)).ToList();
    }

    private static double Avg(IEnumerable<double> xs)
    {
        var v = xs.Where(x => !double.IsNaN(x)).ToList();
        return v.Count == 0 ? double.NaN : v.Average();
    }

    private static double Scale(double v, double lo, double hi) => double.IsNaN(v) ? 0.3 : Math.Clamp((v - lo) / (hi - lo), 0.05, 1);
}
