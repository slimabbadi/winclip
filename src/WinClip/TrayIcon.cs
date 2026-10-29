using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace WinClip;

internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Icon _image;
    private readonly Bitmap _coffee = DrawCoffeeCup();
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
        menu.Items.Add("Buy me a coffee…", _coffee, (_, _) => SupportLink.Open());
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
        _coffee.Dispose();
    }

    /// <summary>A mug in Buy Me a Coffee yellow, drawn at 32 px so the menu can scale it down per DPI.</summary>
    private static Bitmap DrawCoffeeCup()
    {
        var bitmap = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var outline = new Pen(Color.FromArgb(0x1F, 0x22, 0x29), 2.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var fill = new SolidBrush(Color.FromArgb(0xFF, 0xDD, 0x00));
        using var body = new GraphicsPath();
        body.AddLine(5, 13, 21, 13);
        body.AddLine(21, 13, 21, 21);
        body.AddArc(13, 15, 8, 12, 0, 90);
        body.AddLine(17, 27, 9, 27);
        body.AddArc(5, 15, 8, 12, 90, 90);
        body.CloseFigure();
        g.FillPath(fill, body);
        g.DrawPath(outline, body);
        g.DrawArc(outline, 18, 15, 8, 7, -90, 180);
        g.DrawLine(outline, 10, 4, 10, 8);
        g.DrawLine(outline, 16, 4, 16, 8);
        return bitmap;
    }
}
