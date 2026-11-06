using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using Brisk.Native;
using Brisk.ViewModels;

namespace Brisk;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly TrayIcon _tray;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;

        // brisk.exe --page diagnose|memory|startup|cleanup|history  opens straight on that page.
        var args = Environment.GetCommandLineArgs();
        int i = Array.FindIndex(args, a => a.Equals("--page", StringComparison.OrdinalIgnoreCase));
        if (i >= 0 && i + 1 < args.Length && Enum.TryParse<Page>(args[i + 1], ignoreCase: true, out var page))
            _vm.Page = page;

        _tray = new TrayIcon(ShowFromTray, () => { ShowFromTray(); _vm.Dashboard.DiagnoseCommand.Execute(null); }, App.ExitApp);
        _vm.RestartingAsAdmin += () => { App.ReleaseInstance(); _tray.Dispose(); };

        StateChanged += (_, _) => OnStateChanged();
        IsVisibleChanged += (_, _) => UpdateUiActive();
        SourceInitialized += (_, _) => ApplyDarkFrame();
        _vm.UiActive = false; // until shown (may start hidden with --tray)
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void UpdateUiActive() => _vm.UiActive = IsVisible && WindowState != WindowState.Minimized;

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!App.Exiting && _vm.Settings.RecordInBackground)
        {
            e.Cancel = true; // keep recording from the tray
            Hide();
            if (!_vm.Settings.TrayHintShown)
            {
                _tray.Notify("Brisk is still watching", "It records slowdowns in the background. Right-click the tray icon to exit.");
                _vm.Settings.TrayHintShown = true;
                _vm.Settings.Save();
            }
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _tray.Dispose();
        _vm.Dispose();
        base.OnClosed(e);
        if (!App.Exiting) App.ExitApp();
    }

    private void ApplyDarkFrame()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int on = 1, round = 2; // DWMWCP_ROUND
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
    }

    private void OnStateChanged()
    {
        // A maximized borderless window overhangs the screen by the resize border; pad it back in.
        Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
        UpdateUiActive();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
