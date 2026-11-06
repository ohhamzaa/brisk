using WinForms = System.Windows.Forms;

namespace Brisk;

/// <summary>Notification-area icon so the lag recorder can keep running with the window closed.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;

    public TrayIcon(Action open, Action diagnose, Action exit)
    {
        var res = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/brisk.ico"));
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Open Brisk", null, (_, _) => open());
        menu.Items.Add("Why is my PC slow?", null, (_, _) => diagnose());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Exit Brisk", null, (_, _) => exit());

        _icon = new WinForms.NotifyIcon
        {
            Icon = res != null ? new System.Drawing.Icon(res.Stream, WinForms.SystemInformation.SmallIconSize) : null,
            Text = "Brisk: watching for slowdowns",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) open(); };
    }

    public void Notify(string title, string text) => _icon.ShowBalloonTip(5000, title, text, WinForms.ToolTipIcon.None);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
