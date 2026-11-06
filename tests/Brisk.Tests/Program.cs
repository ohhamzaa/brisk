// Self-contained checks for the parts of Brisk that change things on disk or in the registry.
// Run: dotnet run --project tests/Brisk.Tests
using System.Diagnostics;
using System.IO;
using Brisk;
using Brisk.Services;
using Microsoft.Win32;

GameBoost.RecoverIfNeeded(); // never start from a leftover state file

if (args.Contains("--bench"))
{
    // Cost of each piece of one monitor tick, in ms (averaged over 20 one-second ticks).
    var sc = new ProcessScanner(); var sys = new SystemCounters(); var gpuC = new GpuCounters(); var net = new NetworkProbe();
    var st = new SystemStats();
    var t = new Dictionary<string, double> { ["cpu+mem"] = 0, ["pdh system"] = 0, ["gpu"] = 0, ["process scan"] = 0, ["net stats"] = 0, ["group+names"] = 0 };
    for (int i = 0; i < 21; i++)
    {
        Thread.Sleep(1000);
        var sw = Stopwatch.StartNew(); st.SampleCpu(); SystemStats.GetMemory(); var a = sw.Elapsed.TotalMilliseconds;
        sw.Restart(); sys.Sample(); var b = sw.Elapsed.TotalMilliseconds;
        sw.Restart(); gpuC.Sample(); var c = sw.Elapsed.TotalMilliseconds;
        sw.Restart(); var benchProcs = sc.Scan(); var d = sw.Elapsed.TotalMilliseconds;
        sw.Restart(); net.Sample(sys.NetDown, sys.NetUp); var e = sw.Elapsed.TotalMilliseconds;
        sw.Restart(); Brisk.ViewModels.MemoryViewModel.BuildGroups(benchProcs); var f = sw.Elapsed.TotalMilliseconds;
        if (i == 0) continue; // warm-up
        t["cpu+mem"] += a / 20; t["pdh system"] += b / 20; t["gpu"] += c / 20; t["process scan"] += d / 20; t["net stats"] += e / 20; t["group+names"] += f / 20;
    }
    foreach (var (k, v) in t) Console.WriteLine($"{k,-14} {v,7:0.00} ms");
    return 0;
}

int failed = 0;
void Check(string name, bool ok)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}");
    if (!ok) failed++;
}

// ---------- 1. Cleanup never escapes its folder, respects age, removes empty dirs ----------
var sandbox = Path.Combine(Path.GetTempPath(), "brisk-test-" + Guid.NewGuid().ToString("N")[..8]);
var junk = Path.Combine(sandbox, "junk");
var outside = Path.Combine(sandbox, "precious");
Directory.CreateDirectory(Path.Combine(junk, "sub", "deeper"));
Directory.CreateDirectory(outside);

string oldFile = Path.Combine(junk, "old.tmp");
string newFile = Path.Combine(junk, "new.tmp");
string deepOld = Path.Combine(junk, "sub", "deeper", "old2.tmp");
string readOnlyOld = Path.Combine(junk, "ro.tmp");
string precious = Path.Combine(outside, "keep-me.txt");
foreach (var f in new[] { oldFile, newFile, deepOld, readOnlyOld, precious }) File.WriteAllText(f, new string('x', 1000));
foreach (var f in new[] { oldFile, deepOld, readOnlyOld }) File.SetLastWriteTime(f, DateTime.Now.AddDays(-3));
File.SetAttributes(readOnlyOld, FileAttributes.ReadOnly);
File.SetLastWriteTime(precious, DateTime.Now.AddDays(-30));

// Junction junk\link -> precious\ (mklink /J needs no admin).
var link = Path.Combine(junk, "link");
Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{outside}\"") { CreateNoWindow = true, UseShellExecute = false })!.WaitForExit();
Check("test setup: junction created", Directory.Exists(link) && new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint));

var r = JunkCleaner.CleanFolder(junk, null, TimeSpan.FromHours(24));
Check("old file deleted", !File.Exists(oldFile));
Check("nested old file deleted", !File.Exists(deepOld));
Check("read-only old file deleted", !File.Exists(readOnlyOld));
Check("new file (< 24h) kept", File.Exists(newFile));
Check("file behind junction NOT touched", File.Exists(precious));
Check("empty sub-folders removed", !Directory.Exists(Path.Combine(junk, "sub")));
Check("junction itself left in place", Directory.Exists(link));
Check($"reported 3 deleted / 3000 bytes (got {r.Deleted} / {r.FreedBytes})", r.Deleted == 3 && r.FreedBytes == 3000);

// Locked file is skipped, not forced.
string locked = Path.Combine(junk, "locked.tmp");
File.WriteAllText(locked, "x");
using (var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
{
    var r2 = JunkCleaner.CleanFolder(junk, null, TimeSpan.Zero);
    Check("locked file skipped and counted", File.Exists(locked) && r2.Skipped == 1);
}

// Pattern-only cleaning (thumbcache style) touches only matching files.
string match = Path.Combine(junk, "thumbcache_32.db"), other = Path.Combine(junk, "iconcache_32.db");
File.WriteAllText(match, "x"); File.WriteAllText(other, "x");
JunkCleaner.CleanFolder(junk, "thumbcache_*.db", TimeSpan.Zero);
Check("pattern: matching file deleted", !File.Exists(match));
Check("pattern: other file kept", File.Exists(other));

Directory.Delete(link); // removes the junction only
Directory.Delete(sandbox, true);

// ---------- 2. Startup toggle writes the same bytes Task Manager uses, and is reversible ----------
const string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
const string approvedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
const string testName = "BriskSelfTest_DeleteMe";
try
{
    // Points at a file that doesn't exist, so even if left behind it can never run anything.
    using (var k = Registry.CurrentUser.CreateSubKey(runKey)) k.SetValue(testName, @"C:\brisk-selftest\does-not-exist.exe");
    var entry = StartupManager.Load().Single(e => e.Name == testName);
    Check("startup: test entry loaded as enabled", entry.Enabled);

    StartupManager.SetEnabled(entry, false);
    using (var k = Registry.CurrentUser.OpenSubKey(approvedKey))
    {
        var b = k?.GetValue(testName) as byte[];
        Check("startup: disabled writes 12 bytes starting 0x03", b is { Length: 12 } && b[0] == 0x03);
    }
    Check("startup: reload sees disabled", !StartupManager.Load().Single(e => e.Name == testName).Enabled);

    StartupManager.SetEnabled(entry, true);
    Check("startup: re-enabled", StartupManager.Load().Single(e => e.Name == testName).Enabled);
}
finally
{
    using (var k = Registry.CurrentUser.OpenSubKey(runKey, true)) k?.DeleteValue(testName, false);
    using (var k = Registry.CurrentUser.OpenSubKey(approvedKey, true)) k?.DeleteValue(testName, false);
}
using (var k = Registry.CurrentUser.OpenSubKey(runKey)) Check("startup: test entry cleaned up", k?.GetValue(testName) == null);

// ---------- 3. Command-line parsing ----------
string notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe");
Check("exe from quoted command", StartupManager.ExtractExe($"\"{notepad}\" --minimized") == notepad);
Check("exe from unquoted command", StartupManager.ExtractExe($"{notepad} /background")?.Equals(notepad, StringComparison.OrdinalIgnoreCase) == true);
Check("exe from %env% command", StartupManager.ExtractExe(@"%windir%\notepad.exe -x")?.EndsWith("notepad.exe", StringComparison.OrdinalIgnoreCase) == true);

// ---------- 4. Process scanner sanity ----------
var scanner = new ProcessScanner();
scanner.Scan(); Thread.Sleep(300);
var procs = scanner.Scan();
var self = procs.SingleOrDefault(p => p.Pid == Environment.ProcessId);
Check($"scanner sees {procs.Count} processes including itself", procs.Count > 20 && self != null);
Check("scanner reports non-zero private memory for itself", self?.PrivateWorkingSet > 1_000_000);
Check("scanner names itself", self?.ImageName.StartsWith("Brisk.Tests", StringComparison.OrdinalIgnoreCase) == true);

// I/O counters: writing a file must show up as I/O for this process.
scanner.Scan();
var ioFile = Path.Combine(Path.GetTempPath(), "brisk-io-" + Guid.NewGuid().ToString("N")[..6]);
File.WriteAllBytes(ioFile, new byte[8 << 20]);
File.Delete(ioFile);
Thread.Sleep(200);
var selfIo = scanner.Scan().Single(p => p.Pid == Environment.ProcessId);
Check($"scanner sees this process's disk writes ({Fmt.Bytes(selfIo.IoBytesPerSec)}/s)", selfIo.IoBytesPerSec > 4 << 20);

// ---------- 5. Diagnosis rules on synthetic samples ----------
const long GB = 1L << 30;
Sample Synth(double cpu, double availPct, double disk, double perf, double pagesIn, params ProcInfo[] procs) =>
    new(DateTime.Now, cpu, new MemoryInfo(16 * GB, (ulong)(16 * GB * availPct / 100), 2 * GB, (uint)(100 - availPct)),
        disk, perf, pagesIn, 500 * GB, 1000 * GB, procs);
ProcInfo P(int pid, string name, double cpu = 0, long mem = 100 << 20, double faults = 0, double io = 0) =>
    new(pid, name, mem, cpu, 1, faults, io);

var calm = Enumerable.Range(0, 10).Select(_ => Synth(12, 60, 5, 100, 0, P(9001, "app.exe", 3))).ToList();
Check("diagnosis: calm PC is healthy", Diagnoser.Analyze(calm).Healthy);

var cpuHog = Enumerable.Range(0, 10).Select(_ => Synth(97, 50, 10, 100, 0, P(9001, "game.exe", 80), P(9002, "other.exe", 5))).ToList();
var dCpu = Diagnoser.Analyze(cpuHog);
Check($"diagnosis: CPU hog found ({dCpu.Issues.FirstOrDefault()?.Headline})",
    dCpu.Issues.FirstOrDefault() is { Kind: IssueKind.Cpu, Culprit.Image: "game.exe" } i1 && i1.Fixes.Any(f => f.Kind == FixKind.EndApp));

var defender = Enumerable.Range(0, 10).Select(_ => Synth(92, 50, 95, 100, 0, P(9003, "MsMpEng.exe", 60, io: 80 << 20))).ToList();
var dDef = Diagnoser.Analyze(defender);
Check("diagnosis: Defender scan explained, no End button offered",
    dDef.Issues.Any(i => i.Culprit?.Image == "MsMpEng.exe" && i.Detail.Contains("Defender") && i.Fixes.All(f => f.Kind != FixKind.EndApp)));
Check("diagnosis: disk-bound Defender scan flagged as disk issue", dDef.Issues.Any(i => i.Kind == IssueKind.Disk));

var swap = Enumerable.Range(0, 10).Select(_ => Synth(40, 7, 85, 100, 2500,
    P(9004, "chrome.exe", 10, 6 * GB, faults: 900), P(9005, "big.exe", 1, 7 * GB, faults: 10))).ToList();
var dSwap = Diagnoser.Analyze(swap);
Check($"diagnosis: swapping blames the app causing it, not the biggest ({dSwap.Issues.FirstOrDefault()?.Culprit?.Image})",
    dSwap.Issues.FirstOrDefault() is { Kind: IssueKind.Memory, Culprit.Image: "chrome.exe" } i2 && i2.Fixes.Any(f => f.Kind == FixKind.FreeRam));

var throttled = Enumerable.Range(0, 10).Select(_ => Synth(45, 60, 5, 48, 0, P(9006, "app.exe", 30))).ToList();
var dThr = Diagnoser.Analyze(throttled, "Power saver");
Check("diagnosis: power-saver throttling offers High performance",
    dThr.Issues.Any(i => i.Kind == IssueKind.Throttle && i.Fixes.Any(f => f.Kind == FixKind.HighPerformance)));
Check("diagnosis: no High performance fix when already on it",
    Diagnoser.Analyze(throttled, "High performance").Issues.Single(i => i.Kind == IssueKind.Throttle).Fixes.Count == 0);

// GPU / VRAM / network rules
GpuSnapshot Gpu(double util, long used, long total) => new(util, used, total, "Test GPU", new Dictionary<int, double>(), new Dictionary<int, long>());
NetSnapshot Net(double routerMs, double routerLoss, double inetMs, double inetLoss, double jitter = 2, double down = 0) =>
    new(routerMs, routerLoss, inetMs, inetLoss, jitter, down, 0, true);
Sample WithGpuNet(Sample s, GpuSnapshot g, NetSnapshot n) => s with { Gpu = g, Net = n };

var gpuGame = Enumerable.Range(0, 10).Select(_ => WithGpuNet(Synth(40, 50, 5, 100, 0, P(9101, "MyGame.exe", 30) with { GpuPercent = 98 }),
    Gpu(99, 4L * GB, 8L * GB), Net(2, 0, 20, 0))).ToList();
var dGpu = Diagnoser.Analyze(gpuGame);
Check("diagnosis: maxed GPU names the game and offers Game boost, not End",
    dGpu.Issues.SingleOrDefault(i => i.Kind == IssueKind.Gpu) is { Culprit.Image: "MyGame.exe" } gi
    && gi.Fixes.Any(f => f.Kind == FixKind.GameBoost) && gi.Fixes.All(f => f.Kind != FixKind.EndApp));

var vramFull = Enumerable.Range(0, 10).Select(_ => WithGpuNet(Synth(30, 50, 5, 100, 0,
        P(9101, "MyGame.exe", 20) with { VramBytes = 6L * GB }, P(9102, "chrome.exe", 5) with { VramBytes = 1L * GB }),
    Gpu(80, 7_800L << 20, 8L * GB), Net(2, 0, 20, 0))).ToList();
var dVram = Diagnoser.Analyze(vramFull);
Check("diagnosis: full VRAM offers to close the *other* app (Chrome), not the game",
    dVram.Issues.SingleOrDefault(i => i.Kind == IssueKind.Vram) is { } vi && vi.Fixes.Any(f => f.Target == "chrome.exe")
    && vi.Fixes.All(f => f.Target != "MyGame.exe"));

var wifi = Enumerable.Range(0, 10).Select(_ => WithGpuNet(Synth(10, 60, 5, 100, 0), Gpu(5, 1L * GB, 8L * GB), Net(85, 0.2, 140, 0.2))).ToList();
Check("diagnosis: slow router ping blames Wi-Fi, not the ISP",
    Diagnoser.Analyze(wifi).Issues.SingleOrDefault(i => i.Kind == IssueKind.Network)?.Headline.Contains("Wi-Fi") == true);

var isp = Enumerable.Range(0, 10).Select(_ => WithGpuNet(Synth(10, 60, 5, 100, 0), Gpu(5, 1L * GB, 8L * GB), Net(2, 0, 180, 0.08, 40))).ToList();
var dIsp = Diagnoser.Analyze(isp);
Check("diagnosis: fast router + slow internet = internet lag",
    dIsp.Issues.SingleOrDefault(i => i.Kind == IssueKind.Network) is { } ni && ni.Headline.Contains("internet") && ni.Detail.Contains("router responds fine"));

var download = Enumerable.Range(0, 10).Select(_ => WithGpuNet(Synth(10, 60, 5, 100, 0, P(9103, "steam.exe", 2, io: 30 << 20)),
    Gpu(5, 1L * GB, 8L * GB), Net(2, 0, 160, 0, 10, 25 << 20))).ToList();
Check("diagnosis: names the likely downloader (Steam) when the line is saturated",
    Diagnoser.Analyze(download).Issues.SingleOrDefault(i => i.Kind == IssueKind.Network)?.Detail.Contains("steam") == true);

var goodNet = Enumerable.Range(0, 10).Select(_ => WithGpuNet(Synth(10, 60, 5, 100, 0), Gpu(30, 2L * GB, 8L * GB), Net(2, 0, 18, 0))).ToList();
Check("diagnosis: healthy GPU + network = healthy", Diagnoser.Analyze(goodNet).Healthy);

// ---------- 6. Lag recorder (temp file, never the real history) ----------
var histPath = Path.Combine(Path.GetTempPath(), "brisk-hist-" + Guid.NewGuid().ToString("N")[..6] + ".json");
try
{
    var rec = new LagRecorder(histPath);
    LagEvent? got = null;
    rec.Recorded += e => got = e;
    foreach (var _ in Enumerable.Range(0, 3)) rec.Feed(Synth(97, 50, 10, 100, 0, P(9001, "game.exe", 80)));
    foreach (var _ in Enumerable.Range(0, 4)) rec.Feed(Synth(10, 60, 5, 100, 0));
    Check("recorder: a 3-second blip is ignored", got == null && rec.Events.Count == 0);

    foreach (var _ in Enumerable.Range(0, 8)) rec.Feed(Synth(97, 50, 10, 100, 0, P(9001, "game.exe", 80)));
    foreach (var _ in Enumerable.Range(0, 4)) rec.Feed(Synth(10, 60, 5, 100, 0));
    Check($"recorder: an 8-second freeze is recorded with its cause ({got?.Headline})",
        got != null && got.Seconds >= 8 && got.Culprits.FirstOrDefault() == "game" && got.Kinds.Contains("CPU"));
    Check("recorder: history persisted and reloads", new LagRecorder(histPath).Events.Count == 1);

    got = null;
    foreach (var _ in Enumerable.Range(0, 7)) rec.Feed(WithGpuNet(Synth(10, 60, 5, 100, 0), Gpu(5, 1L * GB, 8L * GB), Net(2, 0, 420, 0.4)));
    foreach (var _ in Enumerable.Range(0, 4)) rec.Feed(WithGpuNet(Synth(10, 60, 5, 100, 0), Gpu(5, 1L * GB, 8L * GB), Net(2, 0, 18, 0)));
    Check($"recorder: a 7-second network lag spike is recorded as Network ({got?.Headline})",
        got != null && got.Kinds.SequenceEqual(["Network"]));
}
finally { File.Delete(histPath); }

// ---------- 7. Game boost on two throwaway processes (never your apps, never your power plan) ----------
Process Dummy() => Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -Command Start-Sleep 60")
    { CreateNoWindow = true, UseShellExecute = false })!;
var game = Dummy();
var bg = Dummy();
try
{
    Thread.Sleep(800);
    // Start from Normal: some hosts (e.g. CI runners) launch children at BelowNormal, and boost
    // deliberately leaves already-lowered apps alone.
    game.PriorityClass = ProcessPriorityClass.Normal;
    bg.PriorityClass = ProcessPriorityClass.Normal;
    var myProcs = new List<ProcInfo>
    {
        new(game.Id, "game-test.exe", 50 << 20, 40, game.SessionId, 0, 0),
        new(bg.Id, "background-test.exe", 50 << 20, 2, bg.SessionId, 0, 0),
    };
    var boost = new GameBoost();
    var report = boost.Start(game.Id, myProcs, plans: []); // empty plan list: power plan is not touched
    game.Refresh(); bg.Refresh();
    Check($"boost: game raised to AboveNormal ({game.PriorityClass})", game.PriorityClass == ProcessPriorityClass.AboveNormal);
    Check($"boost: background lowered to BelowNormal ({bg.PriorityClass})", bg.PriorityClass == ProcessPriorityClass.BelowNormal);
    Check("boost: reports 1 throttled app and no plan change", report.Throttled == 1 && report.PlanName == null);
    Check("boost: game still running is detected", boost.GameStillRunning());

    int restored = boost.Stop();
    game.Refresh(); bg.Refresh();
    Check($"boost: stop restores both ({restored} apps, {game.PriorityClass}/{bg.PriorityClass})",
        restored == 1 && game.PriorityClass == ProcessPriorityClass.Normal && bg.PriorityClass == ProcessPriorityClass.Normal);

    // Crash recovery: start a boost, "lose" the object, recover from the state file.
    new GameBoost().Start(game.Id, myProcs, plans: []);
    bg.Refresh();
    bool lowered = bg.PriorityClass == ProcessPriorityClass.BelowNormal;
    int recovered = GameBoost.RecoverIfNeeded();
    bg.Refresh();
    Check($"boost: recovers after a crash from the saved state ({recovered})", lowered && recovered == 1 && bg.PriorityClass == ProcessPriorityClass.Normal);
    Check("boost: nothing left to recover afterwards", GameBoost.RecoverIfNeeded() == 0);

    game.Kill(); game.WaitForExit();
    var b2 = new GameBoost();
    b2.Start(bg.Id, myProcs.Where(p => p.Pid == bg.Id).ToList(), plans: []);
    bool before = b2.GameStillRunning();
    bg.Kill(); bg.WaitForExit();
    Check("boost: notices when the game is gone (true while running, false after exit)", before && !b2.GameStillRunning());
    b2.Stop();
}
finally
{
    foreach (var p in new[] { game, bg }) { try { if (!p.HasExited) p.Kill(); } catch { } }
    GameBoost.RecoverIfNeeded();
}

// ---------- 7b. Demo mode never touches the real PC ----------
var victim = Dummy();
const string runKeyDemo = @"Software\Microsoft\Windows\CurrentVersion\Run";
string? runBefore;
using (var k = Registry.CurrentUser.OpenSubKey(runKeyDemo)) runBefore = k?.GetValue("Brisk") as string;
try
{
    Thread.Sleep(500);
    victim.PriorityClass = ProcessPriorityClass.Normal;
    Demo.On = true;
    Check("demo: End task is a no-op on a real process", ProcessScanner.Kill(victim.Id) && !victim.HasExited);
    Settings.StartWithWindows = !Settings.StartWithWindows;
    using (var k = Registry.CurrentUser.OpenSubKey(runKeyDemo)) Check("demo: 'Start with Windows' doesn't touch the registry", (k?.GetValue("Brisk") as string) == runBefore);
    Check("demo: startup list is the sample one", StartupManager.Load().Any(e => e.Name == "Skyline Racers Launcher"));
    var demoBoost = new GameBoost();
    bool reported = demoBoost.Start(victim.Id, [], []).Throttled > 0;
    victim.Refresh();
    Check("demo: game boost changes nothing", reported && victim.PriorityClass == ProcessPriorityClass.Normal);
    demoBoost.Stop();
    var ds = Demo.NextSample(5);
    Check($"demo: sample PC is under memory pressure with a game on the GPU ({ds.MemUsedPct:0}% RAM, {ds.GpuPct:0}% GPU)",
        ds.MemUsedPct > 85 && ds.GpuPct > 85 && ds.Procs.Count > 100);
}
finally
{
    Demo.On = false;
    if (!victim.HasExited) victim.Kill();
}

// ---------- 8. GPU counters on this PC ----------
using (var gc = new GpuCounters())
{
    gc.Sample(); Thread.Sleep(1100);
    var gs = gc.Sample();
    Check($"gpu: counters readable ({gs.Name ?? "no name"}, {gs.Utilization:0}% load, {Fmt.Bytes(gs.VramUsed)} / {Fmt.Bytes(gs.VramTotal)} VRAM, {gs.PerPidUtil.Count} processes)",
        !gc.Available || (!double.IsNaN(gs.Utilization) && gs.VramTotal >= 0 && (gs.VramTotal == 0 || gs.VramUsed <= gs.VramTotal * 1.05)));
}

// ---------- 9. Power plan parsing ----------
var plans = PowerPlans.List();
Check($"power plans parsed ({plans.Count}), exactly one active", plans.Count > 0 && plans.Count(p => p.Active) == 1);
Check("no plan name has unbalanced parentheses", plans.All(p => p.Name.Count(c => c == '(') == p.Name.Count(c => c == ')')));

Console.WriteLine(failed == 0 ? "\nAll checks passed." : $"\n{failed} check(s) FAILED.");
return failed == 0 ? 0 : 1;
