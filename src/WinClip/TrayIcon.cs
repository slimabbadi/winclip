using System.Drawing;
using System.Windows.Forms;

namespace WinClip;

internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Icon _image;
    private readonly ToolStripMenuItem _showHistory;
    private string? _hotkey;
    private bool _paused;

    /// <param name="setPaused">Called when the user turns "Pause recording" on or off.</param>
    public TrayIcon(Action showHistory, Action<bool> setPaused, Action openSettings, Action clearHistory, Action quit)
    {
        using (var stream = typeof(TrayIcon).Assembly.GetManifestResourceStream("WinClip.ico")!)
            _image = new Icon(stream, SystemInformation.SmallIconSize);

        _showHistory = new ToolStripMenuItem("Show history", null, (_, _) => showHistory());
        var pause = new ToolStripMenuItem("Pause recording") { CheckOnClick = true };
        pause.CheckedChanged += (_, _) =>
        {
            _paused = pause.Checked;
            setPaused(_paused);
            UpdateTooltip();
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_showHistory);
        menu.Items.Add(pause);
        menu.Items.Add("Settings…", null, (_, _) => openSettings());
        menu.Items.Add("Clear history…", null, (_, _) => clearHistory());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit WinClip", null, (_, _) => quit());

        _icon = new NotifyIcon { Icon = _image, Text = "WinClip", ContextMenuStrip = menu, Visible = true };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                showHistory();
        };
    }

    /// <param name="hotkey">Readable shortcut, or null when none is registered.</param>
    public void SetHotkey(string? hotkey)
    {
        _hotkey = hotkey;
        _showHistory.ShortcutKeyDisplayString = hotkey;
        UpdateTooltip();
    }

    private void UpdateTooltip() =>
        _icon.Text = "WinClip" + (_paused ? ", paused" : "") + (_hotkey is null ? "" : $" ({_hotkey})");

    public void Notify(string title, string message, bool warning = false) =>
        _icon.ShowBalloonTip(5000, title, message, warning ? ToolTipIcon.Warning : ToolTipIcon.Info);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _image.Dispose();
    }
}
