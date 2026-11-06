using System.Text.Json;
using Microsoft.Win32;

namespace Brisk.Services;

public sealed class Settings
{
    /// <summary>Closing the window keeps Brisk in the tray so the lag recorder keeps running.</summary>
    public bool RecordInBackground { get; set; } = true;
    public bool TrayHintShown { get; set; }

    private static readonly string FilePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Brisk", "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch { }
    }

    // "Start with Windows" lives in the registry (not the json) so Task Manager shows it like any other app.
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "Brisk";

    public static bool StartWithWindows
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(RunName) is string;
        }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (value) k.SetValue(RunName, $"\"{Environment.ProcessPath}\" --tray");
            else k.DeleteValue(RunName, false);
        }
    }
}
