namespace Brisk.Services;

/// <summary>Human names and plain-English explanations for processes people actually ask about.</summary>
public static class AppNames
{
    /// <summary>Protected system processes whose file details Windows won't let us read.</summary>
    private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MsMpEng.exe"] = "Microsoft Defender Antivirus",
        ["NisSrv.exe"] = "Microsoft Defender Network Inspection",
        ["MpDefenderCoreService.exe"] = "Microsoft Defender Core Service",
        ["Memory Compression"] = "Memory Compression (Windows)",
        ["System"] = "System (Windows kernel)",
        ["Registry"] = "Registry (Windows)",
        ["csrss.exe"] = "Client Server Runtime",
        ["smss.exe"] = "Session Manager",
        ["wininit.exe"] = "Windows Start-Up Application",
        ["services.exe"] = "Services and Controller app",
        ["lsass.exe"] = "Local Security Authority",
        ["svchost.exe"] = "Service Host (Windows services)",
        ["dwm.exe"] = "Desktop Window Manager",
        ["audiodg.exe"] = "Windows Audio Device Graph",
        ["SecurityHealthService.exe"] = "Windows Security Health Service",
        ["TiWorker.exe"] = "Windows Modules Installer",
        ["TrustedInstaller.exe"] = "Windows Modules Installer",
        ["MoUsoCoreWorker.exe"] = "Windows Update",
        ["usocoreworker.exe"] = "Windows Update",
        ["wuauclt.exe"] = "Windows Update",
        ["SearchIndexer.exe"] = "Windows Search Indexer",
        ["CompatTelRunner.exe"] = "Windows Compatibility Telemetry",
    };

    public static string? KnownName(string image) => Known.TryGetValue(image, out var n) ? n : null;

    public static string Resolve(string image, int? pid)
    {
        if (KnownName(image) is { } k) return k;
        if (pid is { } p && IconCache.FriendlyName(ProcessScanner.GetPath(p)) is { Length: > 0 } f) return f;
        return image.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? image[..^4] : image;
    }

    public enum Category { App, Browser, Windows, Defender, Update, Search, Sync, Launcher, Kernel }

    public static Category Classify(string image) => image.ToLowerInvariant() switch
    {
        "chrome.exe" or "msedge.exe" or "firefox.exe" or "brave.exe" or "opera.exe" or "vivaldi.exe" or "arc.exe" => Category.Browser,
        "msmpeng.exe" or "mpdefendercoreservice.exe" or "nissrv.exe" => Category.Defender,
        "tiworker.exe" or "trustedinstaller.exe" or "mousocoreworker.exe" or "usocoreworker.exe" or "wuauclt.exe"
            or "waasmedicagent.exe" or "msiexec.exe" => Category.Update,
        "searchindexer.exe" or "searchprotocolhost.exe" or "searchfilterhost.exe" => Category.Search,
        "onedrive.exe" or "dropbox.exe" or "googledrivefs.exe" => Category.Sync,
        "steam.exe" or "steamwebhelper.exe" or "epicgameslauncher.exe" or "battle.net.exe" or "riotclientservices.exe" => Category.Launcher,
        "system" or "registry" or "memory compression" => Category.Kernel,
        "svchost.exe" or "dwm.exe" or "explorer.exe" or "csrss.exe" or "compattelrunner.exe" or "wmiprvse.exe" => Category.Windows,
        _ => Category.App,
    };

    /// <summary>One or two sentences on what the culprit is doing and what to do about it.</summary>
    public static string Explain(string image, string name) => Classify(image) switch
    {
        Category.Browser => $"Every {name} tab is its own process. Close tabs you aren't using, or press Shift+Esc in {name} to see which tab is the heavy one.",
        Category.Defender => "Microsoft Defender is scanning your files. It finishes on its own. You can schedule scans for when you're away in Windows Security.",
        Category.Update => "Windows Update is installing updates in the background. Let it finish, or restart your PC to complete it faster.",
        Category.Search => "Windows Search is indexing your files. It backs off by itself while you use the PC, so this passes.",
        Category.Sync => $"{name} is syncing files. Pause syncing from its tray icon if you need full speed right now.",
        Category.Launcher => $"{name} is updating or downloading in the background. Pause downloads in {name} while you play.",
        Category.Kernel when image == "System" => "This is Windows itself, usually a driver. If it stays high, update your graphics, chipset and network drivers.",
        Category.Kernel => "This is Windows managing memory, which means RAM is under pressure. Closing a big app helps most.",
        Category.Windows when image.Equals("dwm.exe", StringComparison.OrdinalIgnoreCase) =>
            "This draws everything on your screen. It gets busy with games, video, or several high-refresh monitors.",
        Category.Windows => $"{name} is part of Windows. It usually settles down on its own within a few minutes.",
        _ => $"If you aren't using {name} right now, closing it fixes this. If you are, it's working hard on something and will settle down when it finishes.",
    };

    /// <summary>Things it makes sense to offer an "End" button for.</summary>
    public static bool CanOfferEnd(string image) =>
        !ProcessScanner.Protected.Contains(image) && Classify(image) is Category.App or Category.Browser or Category.Launcher or Category.Sync;
}
