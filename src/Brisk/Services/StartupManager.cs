using System.Diagnostics;
using Microsoft.Win32;

namespace Brisk.Services;

public enum StartupSource { UserRun, MachineRun, MachineRun32, UserFolder, CommonFolder }

public sealed class StartupEntry
{
    public required string Name { get; init; }
    public required string Command { get; init; }
    public string? ExePath { get; init; }
    public string? Publisher { get; init; }
    public required StartupSource Source { get; init; }
    public bool Enabled { get; set; }
    public bool NeedsAdmin => Source is StartupSource.MachineRun or StartupSource.MachineRun32 or StartupSource.CommonFolder;

    public string SourceLabel => Source switch
    {
        StartupSource.UserRun => "Registry (you)",
        StartupSource.MachineRun or StartupSource.MachineRun32 => "Registry (all users)",
        StartupSource.UserFolder => "Startup folder (you)",
        _ => "Startup folder (all users)",
    };
}

/// <summary>
/// Enables/disables startup apps exactly like Task Manager does: via the
/// Explorer\StartupApproved keys. Nothing is deleted, so every change is reversible
/// and shows up the same in Task Manager and Settings.
/// </summary>
public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Run32Key = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedBase = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    public static List<StartupEntry> Load()
    {
        if (Demo.On) return Demo.Startup().OrderBy(e => !e.Enabled).ThenBy(e => e.Name).ToList();
        var list = new List<StartupEntry>();
        ReadRun(list, Registry.CurrentUser, RunKey, StartupSource.UserRun);
        ReadRun(list, Registry.LocalMachine, RunKey, StartupSource.MachineRun);
        ReadRun(list, Registry.LocalMachine, Run32Key, StartupSource.MachineRun32);
        ReadFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.Startup), StartupSource.UserFolder);
        ReadFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), StartupSource.CommonFolder);
        return list.OrderBy(e => !e.Enabled).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static void SetEnabled(StartupEntry e, bool enabled)
    {
        if (Demo.On) { e.Enabled = enabled; return; }
        var (hive, sub) = ApprovedLocation(e.Source);
        using var key = hive.CreateSubKey(ApprovedBase + sub, writable: true)
                        ?? throw new UnauthorizedAccessException();
        var data = new byte[12];
        data[0] = enabled ? (byte)0x02 : (byte)0x03;
        if (!enabled) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
        key.SetValue(e.Name, data, RegistryValueKind.Binary);
        e.Enabled = enabled;
    }

    private static (RegistryKey Hive, string Sub) ApprovedLocation(StartupSource s) => s switch
    {
        StartupSource.UserRun => (Registry.CurrentUser, "Run"),
        StartupSource.MachineRun => (Registry.LocalMachine, "Run"),
        StartupSource.MachineRun32 => (Registry.LocalMachine, "Run32"),
        StartupSource.UserFolder => (Registry.CurrentUser, "StartupFolder"),
        _ => (Registry.LocalMachine, "StartupFolder"),
    };

    private static bool IsApproved(StartupSource s, string name)
    {
        try
        {
            var (hive, sub) = ApprovedLocation(s);
            using var key = hive.OpenSubKey(ApprovedBase + sub);
            if (key?.GetValue(name) is byte[] { Length: > 0 } b) return (b[0] & 1) == 0; // odd first byte = disabled
        }
        catch { }
        return true;
    }

    private static void ReadRun(List<StartupEntry> list, RegistryKey hive, string path, StartupSource src)
    {
        try
        {
            using var key = hive.OpenSubKey(path);
            if (key == null) return;
            foreach (var name in key.GetValueNames())
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                var cmd = key.GetValue(name)?.ToString() ?? "";
                var exe = ExtractExe(cmd);
                list.Add(new StartupEntry
                {
                    Name = name, Command = cmd, ExePath = exe, Publisher = GetPublisher(exe),
                    Source = src, Enabled = IsApproved(src, name),
                });
            }
        }
        catch { }
    }

    private static void ReadFolder(List<StartupEntry> list, string folder, StartupSource src)
    {
        try
        {
            if (!Directory.Exists(folder)) return;
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                var fileName = Path.GetFileName(file);
                if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(new StartupEntry
                {
                    Name = fileName, Command = file, ExePath = file,
                    Publisher = GetPublisher(file), Source = src, Enabled = IsApproved(src, fileName),
                });
            }
        }
        catch { }
    }

    /// <summary>Pulls the executable path out of a command line like  "C:\x\app.exe" --min  or  C:\x\app.exe /s.</summary>
    public static string? ExtractExe(string cmd)
    {
        cmd = Environment.ExpandEnvironmentVariables(cmd.Trim());
        if (cmd.Length == 0) return null;
        string candidate;
        if (cmd[0] == '"')
        {
            int end = cmd.IndexOf('"', 1);
            candidate = end > 1 ? cmd[1..end] : cmd.Trim('"');
        }
        else
        {
            int exe = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            candidate = exe > 0 ? cmd[..(exe + 4)] : cmd.Split(' ')[0];
        }
        return File.Exists(candidate) ? candidate : null;
    }

    private static string? GetPublisher(string? path)
    {
        if (path == null || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            var company = FileVersionInfo.GetVersionInfo(path).CompanyName?.Trim();
            return string.IsNullOrEmpty(company) ? null : company;
        }
        catch { return null; }
    }
}
