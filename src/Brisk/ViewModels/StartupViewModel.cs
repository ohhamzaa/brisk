using System.Collections.ObjectModel;
using System.Windows.Media;
using Brisk.Services;

namespace Brisk.ViewModels;

public sealed class StartupItem : ObservableObject
{
    private readonly StartupViewModel _owner;
    public StartupEntry Entry { get; }
    public ImageSource? Icon { get; }
    public string Name => Entry.Name;
    public string Detail => Entry.Publisher is { } p ? $"{p} · {Entry.SourceLabel}" : Entry.SourceLabel;
    public string Command => Entry.Command;

    public StartupItem(StartupViewModel owner, StartupEntry entry)
    {
        _owner = owner;
        Entry = entry;
        Icon = IconCache.Get(entry.ExePath);
    }

    public bool Enabled
    {
        get => Entry.Enabled;
        set
        {
            if (value == Entry.Enabled) return;
            _owner.Toggle(this, value);
            Raise();
        }
    }
}

public sealed class StartupViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    public ObservableCollection<StartupItem> Entries { get; } = [];

    private string _summary = "";
    public string Summary { get => _summary; private set => Set(ref _summary, value); }

    public int EnabledCount { get; private set; }
    public RelayCommand ReloadCommand { get; }

    public StartupViewModel(MainViewModel main)
    {
        _main = main;
        ReloadCommand = new RelayCommand(Reload);
        Reload();
    }

    public void Reload()
    {
        Entries.Clear();
        foreach (var e in StartupManager.Load()) Entries.Add(new StartupItem(this, e));
        UpdateSummary();
    }

    internal void Toggle(StartupItem item, bool enabled)
    {
        try
        {
            StartupManager.SetEnabled(item.Entry, enabled);
            _main.ShowToast($"{item.Name} will {(enabled ? "start" : "no longer start")} with Windows.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            _main.ShowToast($"{item.Name} is set for all users. Restart Brisk as admin to change it.");
        }
        catch (Exception ex)
        {
            _main.ShowToast($"Couldn't change {item.Name}: {ex.Message}");
        }
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        EnabledCount = Entries.Count(e => e.Entry.Enabled);
        Summary = $"{EnabledCount} of {Entries.Count} apps start with Windows";
        Raise(nameof(EnabledCount));
    }
}
