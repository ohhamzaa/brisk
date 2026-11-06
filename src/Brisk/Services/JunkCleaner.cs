using Brisk.Native;

namespace Brisk.Services;

public sealed class JunkCategory
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public bool DefaultSelected { get; init; } = true;
    public bool NeedsAdmin { get; init; }
    /// <summary>Only delete files older than this. Protects files that running installers/apps still use.</summary>
    public TimeSpan MinAge { get; init; } = TimeSpan.Zero;
    public Func<IEnumerable<string>> Folders { get; init; } = () => [];
    /// <summary>Optional file filter (e.g. thumbcache_*.db). Null = all files.</summary>
    public string? Pattern { get; init; }
    public bool IsRecycleBin { get; init; }

    public long Bytes { get; set; }
    public int Files { get; set; }
}

public sealed record CleanResult(long FreedBytes, int Deleted, int Skipped);

/// <summary>
/// Finds and removes junk. Safety rules:
///  * never follows symlinks/junctions (can't escape the folder),
///  * skips files in use (locked) instead of forcing,
///  * temp files must be older than 24h,
///  * only well-known cache/temp folders, never user documents.
/// </summary>
public static class JunkCleaner
{
    private static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string Roaming => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static string ProgramData => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    private static string WinDir => Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    public static List<JunkCategory> Categories() =>
    [
        new()
        {
            Id = "temp", Title = "Temporary files",
            Description = "Leftovers from installers and apps (older than 24 hours).",
            MinAge = TimeSpan.FromHours(24),
            Folders = () => [Path.GetTempPath()],
        },
        new()
        {
            Id = "wintemp", Title = "Windows temporary files",
            Description = "System temp folder (older than 24 hours).",
            NeedsAdmin = true, MinAge = TimeSpan.FromHours(24),
            Folders = () => [Path.Combine(WinDir, "Temp")],
        },
        new()
        {
            Id = "browser", Title = "Browser cache",
            Description = "Cached web files from Chrome, Edge, Brave, Opera, Vivaldi and Firefox. Logins and history are kept.",
            Folders = BrowserCaches,
        },
        new()
        {
            Id = "thumbs", Title = "Thumbnail cache",
            Description = "Explorer rebuilds thumbnails when needed.",
            Pattern = "thumbcache_*.db",
            Folders = () => [Path.Combine(Local, @"Microsoft\Windows\Explorer")],
        },
        new()
        {
            Id = "crash", Title = "Crash dumps & error reports",
            Description = "Memory dumps and reports left behind after app crashes.",
            Folders = () =>
            [
                Path.Combine(Local, "CrashDumps"),
                Path.Combine(Local, @"Microsoft\Windows\WER"),
                Path.Combine(ProgramData, @"Microsoft\Windows\WER\ReportArchive"),
                Path.Combine(ProgramData, @"Microsoft\Windows\WER\ReportQueue"),
            ],
        },
        new()
        {
            Id = "wupdate", Title = "Windows Update downloads",
            Description = "Already-installed update files. Windows re-downloads them if needed.",
            NeedsAdmin = true, MinAge = TimeSpan.FromDays(3),
            Folders = () => [Path.Combine(WinDir, @"SoftwareDistribution\Download")],
        },
        new()
        {
            Id = "dev", Title = "Developer caches",
            Description = "npm, Yarn, pnpm, pip and NuGet download caches. Safe, but the next install re-downloads packages.",
            DefaultSelected = false,
            Folders = () =>
            [
                Path.Combine(Local, "npm-cache"),
                Path.Combine(Local, @"Yarn\Cache"),
                Path.Combine(Local, @"pnpm-cache"),
                Path.Combine(Local, @"pip\Cache"),
                Path.Combine(Local, @"NuGet\v3-cache"),
            ],
        },
        new()
        {
            Id = "recycle", Title = "Recycle Bin",
            Description = "Permanently deletes what's in the Recycle Bin.",
            DefaultSelected = false, IsRecycleBin = true,
        },
    ];

    private static IEnumerable<string> BrowserCaches()
    {
        string[] chromium =
        [
            @"Google\Chrome\User Data", @"Microsoft\Edge\User Data", @"BraveSoftware\Brave-Browser\User Data",
            @"Vivaldi\User Data", @"Chromium\User Data",
        ];
        string[] cacheDirs = [@"Cache\Cache_Data", "Code Cache", "GPUCache", @"Service Worker\CacheStorage"];
        foreach (var b in chromium)
        {
            var root = Path.Combine(Local, b);
            if (!Directory.Exists(root)) continue;
            foreach (var profile in SafeDirs(root))
            {
                var n = Path.GetFileName(profile);
                if (n != "Default" && !n.StartsWith("Profile ", StringComparison.Ordinal)) continue;
                foreach (var c in cacheDirs) yield return Path.Combine(profile, c);
            }
            yield return Path.Combine(root, "ShaderCache");
            yield return Path.Combine(root, "GrShaderCache");
        }
        // Opera keeps its cache under Local\Opera Software\<channel>
        var opera = Path.Combine(Local, "Opera Software");
        if (Directory.Exists(opera))
            foreach (var ch in SafeDirs(opera)) yield return Path.Combine(ch, @"Cache\Cache_Data");
        // Firefox
        var ff = Path.Combine(Local, @"Mozilla\Firefox\Profiles");
        if (Directory.Exists(ff))
            foreach (var profile in SafeDirs(ff)) yield return Path.Combine(profile, "cache2");
    }

    public static void Scan(JunkCategory c, CancellationToken ct = default)
    {
        c.Bytes = 0; c.Files = 0;
        if (Demo.On) { Thread.Sleep(350); c.Bytes = Demo.JunkBytes(c.Id); c.Files = (int)(c.Bytes / 40_000); return; }
        if (c.IsRecycleBin)
        {
            var info = new NativeMethods.SHQUERYRBINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.SHQUERYRBINFO>() };
            if (NativeMethods.SHQueryRecycleBin(null, ref info) == 0) { c.Bytes = info.i64Size; c.Files = (int)info.i64NumItems; }
            return;
        }
        if (c.NeedsAdmin && !SystemStats.IsAdmin) return;
        var cutoff = DateTime.Now - c.MinAge;
        foreach (var folder in c.Folders())
        foreach (var f in EnumerateFiles(folder, c.Pattern, ct))
        {
            if (c.MinAge > TimeSpan.Zero && f.LastWriteTime > cutoff) continue;
            c.Bytes += f.Length; c.Files++;
        }
    }

    public static CleanResult Clean(JunkCategory c, CancellationToken ct = default)
    {
        if (Demo.On) { var r = new CleanResult(c.Bytes, c.Files, 0); c.Bytes = 0; Demo.ClearJunk(c.Id); return r; }
        if (c.IsRecycleBin)
        {
            long size = c.Bytes;
            int hr = NativeMethods.SHEmptyRecycleBin(IntPtr.Zero, null,
                NativeMethods.SHERB_NOCONFIRMATION | NativeMethods.SHERB_NOPROGRESSUI | NativeMethods.SHERB_NOSOUND);
            return hr == 0 ? new CleanResult(size, c.Files, 0) : new CleanResult(0, 0, c.Files);
        }
        if (c.NeedsAdmin && !SystemStats.IsAdmin) return new CleanResult(0, 0, 0);

        long freed = 0; int deleted = 0, skipped = 0;
        foreach (var folder in c.Folders())
        {
            var r = CleanFolder(folder, c.Pattern, c.MinAge, ct);
            freed += r.FreedBytes; deleted += r.Deleted; skipped += r.Skipped;
        }
        return new CleanResult(freed, deleted, skipped);
    }

    /// <summary>Deletes files under <paramref name="folder"/> (never through links), then prunes empty sub-folders.</summary>
    public static CleanResult CleanFolder(string folder, string? pattern, TimeSpan minAge, CancellationToken ct = default)
    {
        long freed = 0; int deleted = 0, skipped = 0;
        var cutoff = DateTime.Now - minAge;
        foreach (var f in EnumerateFiles(folder, pattern, ct).ToList())
        {
            if (minAge > TimeSpan.Zero && f.LastWriteTime > cutoff) continue;
            try
            {
                if (f.IsReadOnly) f.IsReadOnly = false;
                long len = f.Length;
                f.Delete();
                freed += len; deleted++;
            }
            catch { skipped++; } // in use or no access: leave it alone
        }
        if (pattern == null) RemoveEmptyDirs(folder);
        return new CleanResult(freed, deleted, skipped);
    }

    // ---------- safe file walking ----------

    private static IEnumerable<string> SafeDirs(string path)
    {
        try { return Directory.GetDirectories(path); } catch { return []; }
    }

    private static IEnumerable<FileInfo> EnumerateFiles(string root, string? pattern, CancellationToken ct)
    {
        if (!Directory.Exists(root)) yield break;
        var rootInfo = new DirectoryInfo(root);
        if (rootInfo.Attributes.HasFlag(FileAttributes.ReparsePoint)) yield break;

        var stack = new Stack<DirectoryInfo>();
        stack.Push(rootInfo);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            FileSystemInfo[] entries;
            try { entries = pattern == null ? dir.GetFileSystemInfos() : dir.GetFileSystemInfos(pattern); }
            catch { continue; }
            foreach (var e in entries)
            {
                if (e.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue; // never follow links
                if (e is DirectoryInfo d) { if (pattern == null) stack.Push(d); }
                else if (e is FileInfo f) yield return f;
            }
        }
    }

    private static void RemoveEmptyDirs(string root)
    {
        try
        {
            var rootInfo = new DirectoryInfo(root);
            if (!rootInfo.Exists || rootInfo.Attributes.HasFlag(FileAttributes.ReparsePoint)) return;
            foreach (var d in rootInfo.GetDirectories())
            {
                // Leave links alone entirely: deleting a junction is harmless, but we never touch what's behind it.
                if (d.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                RemoveEmptyDirs(d.FullName);
                try { if (!d.EnumerateFileSystemInfos().Any()) d.Delete(); } catch { }
            }
        }
        catch { }
    }
}
