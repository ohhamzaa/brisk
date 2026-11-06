// Self-contained checks for the parts of Brisk that change things on disk or in the registry.
// Run: dotnet run --project tests/Brisk.Tests
using System.Diagnostics;
using System.IO;
using Brisk;
using Brisk.Services;
using Microsoft.Win32;

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
}
finally { File.Delete(histPath); }

// ---------- 7. Power plan parsing ----------
var plans = PowerPlans.List();
Check($"power plans parsed ({plans.Count}), exactly one active", plans.Count > 0 && plans.Count(p => p.Active) == 1);
Check("no plan name has unbalanced parentheses", plans.All(p => p.Name.Count(c => c == '(') == p.Name.Count(c => c == ')')));

Console.WriteLine(failed == 0 ? "\nAll checks passed." : $"\n{failed} check(s) FAILED.");
return failed == 0 ? 0 : 1;
