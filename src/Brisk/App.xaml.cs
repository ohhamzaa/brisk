using System.Windows;
using System.Windows.Threading;

namespace Brisk;

public partial class App : Application
{
    private const string MutexName = @"Local\Brisk.SingleInstance";
    private const string ShowEventName = @"Local\Brisk.Show";
    private static Mutex? _mutex;
    private static bool _ownsMutex;
    private static EventWaitHandle? _showEvent;

    public static bool Exiting { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandled;
        base.OnStartup(e);

        bool restarted = e.Args.Contains("--restarted", StringComparer.OrdinalIgnoreCase);
        bool tray = e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase);
        Services.Demo.On = e.Args.Contains("--demo", StringComparer.OrdinalIgnoreCase);
        Services.Demo.ShowWelcome = e.Args.Contains("--welcome", StringComparer.OrdinalIgnoreCase);

        _mutex = new Mutex(false, MutexName);
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        if (!Acquire(restarted ? 8000 : 0))
        {
            // Already running (maybe in the tray): bring that one to the front instead.
            _showEvent.Set();
            Shutdown();
            return;
        }

        var listener = new Thread(() =>
        {
            while (_showEvent.WaitOne())
                Dispatcher.BeginInvoke(() => (MainWindow as MainWindow)?.ShowFromTray());
        }) { IsBackground = true, Name = "Brisk single-instance" };
        listener.Start();

        var window = new MainWindow();
        MainWindow = window;
        // For scripted screenshots/tests: open without stealing keyboard focus from whatever you're typing in.
        if (e.Args.Contains("--no-activate", StringComparer.OrdinalIgnoreCase)) window.ShowActivated = false;
        if (!tray) window.Show();
    }

    private static bool Acquire(int waitMs)
    {
        try { _ownsMutex = _mutex!.WaitOne(waitMs); }
        catch (AbandonedMutexException) { _ownsMutex = true; }
        return _ownsMutex;
    }

    public static void ReleaseInstance()
    {
        if (!_ownsMutex) return;
        try { _mutex?.ReleaseMutex(); } catch { }
        _ownsMutex = false;
    }

    public static void ReacquireInstance() => Acquire(0);

    public static void ExitApp()
    {
        Exiting = true;
        Current.Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ReleaseInstance();
        base.OnExit(e);
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Keep running: a failed refresh shouldn't take the whole app down.
        try
        {
            var log = Path.Combine(Path.GetTempPath(), "brisk-error.log");
            File.AppendAllText(log, $"[{DateTime.Now:u}] {e.Exception}\n\n");
        }
        catch { }
        e.Handled = true;
    }
}
