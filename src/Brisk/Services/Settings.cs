using System.Text.Json;
using Microsoft.Win32;

namespace Brisk.Services;

public sealed class Settings
{
    /// <summary>Closing the window keeps Brisk in the tray so the lag recorder keeps running.</summary>
    public bool RecordInBackground { get; set; } = true;
    public bool TrayHintShown { get; set; }
    /// <summary>Pop a notification the moment a slowdown is recorded.</summary>
    public bool NotifyOnLag { get; set; } = true;
    /// <summary>Ping the router and 1.1.1.1 to measure network lag (Brisk's only network traffic).</summary>
    public bool NetworkChecks { get; set; } = true;
    public bool FirstRunDone { get; set; }

    private static readonly string FilePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Brisk", "settings.json");

    public static Settings Load()
    {
        if (Demo.On) return new Settings { FirstRunDone = !Demo.ShowWelcome, TrayHintShown = true };
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { }
        return new();
    }

    public void Save()
    {
        if (Demo.On) return;
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
    private static bool _demoStart = true;

    public static bool StartWithWindows
    {
        get
        {
            if (Demo.On) return _demoStart;
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(RunName) is string;
        }
        set
        {
            if (Demo.On) { _demoStart = value; return; }
            using var k = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (value) k.SetValue(RunName, $"\"{Environment.ProcessPath}\" --tray");
            else k.DeleteValue(RunName, false);
        }
    }
}
