using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Brisk;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    /// <summary>
    /// For state that gates buttons (busy flags etc.). WPF only re-checks CanExecute after user input,
    /// so without this a button stays disabled after background work finishes until the mouse moves.
    /// </summary>
    protected bool SetAndRequery<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (!Set(ref field, value, name)) return false;
        CommandManager.InvalidateRequerySuggested();
        return true;
    }
}

public sealed class RelayCommand(Action<object?> run, Func<object?, bool>? canRun = null) : ICommand
{
    public RelayCommand(Action run, Func<bool>? canRun = null)
        : this(_ => run(), canRun == null ? null : _ => canRun()) { }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? p) => canRun?.Invoke(p) ?? true;
    public void Execute(object? p) => run(p);
}

public static class Fmt
{
    public static string Bytes(double b)
    {
        string[] u = ["B", "KB", "MB", "GB", "TB"];
        int i = 0;
        while (Math.Abs(b) >= 1024 && i < u.Length - 1) { b /= 1024; i++; }
        return i == 0 ? $"{b:0} {u[i]}" : b >= 100 ? $"{b:0} {u[i]}" : $"{b:0.0} {u[i]}";
    }

    public static string Gb(double b) => $"{b / (1024.0 * 1024 * 1024):0.0}";

    public static string Duration(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h" :
        t.TotalHours >= 1 ? $"{t.Hours}h {t.Minutes}m" : $"{t.Minutes}m";
}
