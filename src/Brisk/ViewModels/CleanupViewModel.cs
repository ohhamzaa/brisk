using System.Collections.ObjectModel;
using Brisk.Services;

namespace Brisk.ViewModels;

public sealed class JunkItem(JunkCategory category) : ObservableObject
{
    public JunkCategory Category { get; } = category;
    public string Title => Category.Title;
    public string Description => Category.Description;
    public bool Locked => Category.NeedsAdmin && !SystemStats.IsAdmin;

    private bool _selected = category.DefaultSelected && !(category.NeedsAdmin && !SystemStats.IsAdmin);
    public bool Selected { get => _selected; set => Set(ref _selected, value); }

    private bool _scanned;
    public bool Scanned { get => _scanned; set { Set(ref _scanned, value); Raise(nameof(SizeText)); } }

    public string SizeText => Locked ? "Needs admin" : !Scanned ? "-" : Category.Bytes == 0 ? "Clean" : Fmt.Bytes(Category.Bytes);
    public void Refresh() { Raise(nameof(SizeText)); }
}

public sealed class CleanupViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    public ObservableCollection<JunkItem> Items { get; }

    private bool _busy;
    public bool Busy { get => _busy; private set => SetAndRequery(ref _busy, value); }

    private bool _hasScanned;
    public bool HasScanned { get => _hasScanned; private set => SetAndRequery(ref _hasScanned, value); }

    private string _status = "Scan to see how much space you can get back.";
    public string Status { get => _status; private set => Set(ref _status, value); }

    private string _totalText = "";
    public string TotalText { get => _totalText; private set => Set(ref _totalText, value); }

    public RelayCommand ScanCommand { get; }
    public RelayCommand CleanCommand { get; }

    public CleanupViewModel(MainViewModel main)
    {
        _main = main;
        Items = new(JunkCleaner.Categories().Select(c => new JunkItem(c)));
        ScanCommand = new RelayCommand(Scan, () => !Busy);
        CleanCommand = new RelayCommand(Clean, () => !Busy && HasScanned && Items.Any(i => i.Selected && i.Category.Bytes > 0));
        foreach (var i in Items) i.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(JunkItem.Selected)) UpdateTotal(); };
    }

    private async void Scan()
    {
        if (Busy) return;
        Busy = true;
        Status = "Scanning…";
        try
        {
            foreach (var item in Items)
            {
                Status = $"Scanning {item.Title.ToLowerInvariant()}…";
                await Task.Run(() => JunkCleaner.Scan(item.Category));
                item.Scanned = true;
            }
            HasScanned = true;
            UpdateTotal();
            long all = Items.Where(i => !i.Locked).Sum(i => i.Category.Bytes);
            Status = all == 0 ? "Nothing to clean. Your PC is tidy."
                : $"Found {Fmt.Bytes(all)} you can clean. Tick the categories you want, then press Clean.";
        }
        catch (Exception ex) { Status = "Scan failed: " + ex.Message; }
        finally { Busy = false; }
    }

    private async void Clean()
    {
        var targets = Items.Where(i => i.Selected && !i.Locked && i.Category.Bytes > 0).ToList();
        if (targets.Count == 0) return;
        if (targets.Any(t => t.Category.IsRecycleBin))
        {
            var ok = await _main.AskAsync("Empty the Recycle Bin?",
                "Files in the Recycle Bin will be deleted for good.", "Empty and clean", danger: true);
            if (!ok) return;
        }

        Busy = true;
        long freed = 0; int skipped = 0;
        try
        {
            foreach (var t in targets)
            {
                Status = $"Cleaning {t.Title.ToLowerInvariant()}…";
                var r = await Task.Run(() => JunkCleaner.Clean(t.Category));
                freed += r.FreedBytes; skipped += r.Skipped;
                await Task.Run(() => JunkCleaner.Scan(t.Category));
                t.Refresh();
            }
            Status = $"Cleaned {Fmt.Bytes(freed)}." + (skipped > 0 ? $" {skipped} files were in use and were skipped." : "");
            _main.ShowToast($"Freed {Fmt.Bytes(freed)} of disk space.");
            UpdateTotal();
        }
        finally { Busy = false; }
    }

    private void UpdateTotal()
    {
        long sel = Items.Where(i => i.Selected && !i.Locked).Sum(i => i.Category.Bytes);
        TotalText = HasScanned ? Fmt.Bytes(sel) : "";
        System.Windows.Input.CommandManager.InvalidateRequerySuggested(); // ticking a box can enable Clean
    }
}
