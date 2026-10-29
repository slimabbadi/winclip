using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using WinClip.Core;
using WinClip.Native;

namespace WinClip;

internal sealed record ClipRow(
    ClipItem Item, string Shortcut, string Preview, string Expanded, string Origin, string Age, bool Pinned,
    ImageSource? Thumbnail, string? Glyph)
{
    public Visibility PinVisibility => Pinned ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ThumbnailVisibility => Thumbnail is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility GlyphVisibility => Glyph is null ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>
/// The history popup. Focus stays in the search box; arrow keys move the selection, Enter pastes into
/// the app that was focused when the popup opened.
/// </summary>
internal sealed partial class PopupWindow : Window
{
    private readonly HistoryStore _store;
    private const string FileGlyph = "";

    private readonly Dictionary<long, ImageSource> _thumbnails = [];
    private IReadOnlyList<ClipItem> _history = [];
    private IntPtr _target;

    public PopupWindow(HistoryStore store)
    {
        _store = store;
        InitializeComponent();
        Deactivated += (_, _) => Hide();
        PreviewKeyDown += OnKeyDown;
        SourceInitialized += (_, _) => ApplyWindowFrame();
    }

    /// <summary>
    /// Raised with the chosen item, the window to paste into (zero: copy only), and whether to drop formatting.
    /// </summary>
    public event Action<ClipItem, IntPtr, bool>? ItemChosen;

    /// <param name="target">Window to paste into; zero opens in copy-only mode.</param>
    public void ShowFor(IntPtr target)
    {
        _target = target;
        _history = _store.GetAll();
        var ids = _history.Select(item => item.Id).ToHashSet();
        foreach (var stale in _thumbnails.Keys.Where(id => !ids.Contains(id)).ToList())
            _thumbnails.Remove(stale);
        SearchBox.Text = "";
        Refresh();
        RenderHints();

        var handle = new WindowInteropHelper(this).EnsureHandle();
        var anchor = PopupPlacement.FindAnchor(target);
        PopupPlacement.Place(handle, anchor, Width, Height);
        Show();
        // The first Show() can move the window back to its default position, so place it again.
        PopupPlacement.Place(handle, anchor, Width, Height);
        Activate();
        WindowFocus.BringToForeground(handle);
        SearchBox.Focus();
    }

    private void OnSearchChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => Refresh();

    private void Refresh(long? keepSelectedId = null)
    {
        var now = DateTimeOffset.Now;
        var matches = ClipSearch.Filter(_history, SearchBox.Text);
        var rows = matches.Select((item, i) => ToRow(item, i, now)).ToList();
        Clips.ItemsSource = rows;

        var keep = rows.FindIndex(r => r.Item.Id == keepSelectedId);
        Clips.SelectedIndex = rows.Count == 0 ? -1 : Math.Max(keep, 0);
        if (Clips.SelectedItem is not null)
            Clips.ScrollIntoView(Clips.SelectedItem);

        Placeholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Text = _history.Count == 0
            ? "Copy something and it shows up here."
            : $"Nothing matches “{SearchBox.Text.Trim()}”.";
        EmptyState.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;
        e.Handled = true;

        switch (key)
        {
            case Key.Escape:
                Hide();
                break;
            case Key.Down:
                MoveSelection(+1);
                break;
            case Key.Up:
                MoveSelection(-1);
                break;
            case Key.Enter:
                Choose(Clips.SelectedItem as ClipRow, paste: modifiers != ModifierKeys.Control,
                    plainText: modifiers == ModifierKeys.Shift);
                break;
            case >= Key.D1 and <= Key.D9 when modifiers == ModifierKeys.Control:
                Choose(Clips.Items.Count > key - Key.D1 ? Clips.Items[key - Key.D1] as ClipRow : null, paste: true, plainText: false);
                break;
            case Key.P when modifiers == ModifierKeys.Alt:
                TogglePin();
                break;
            case Key.Delete when modifiers == ModifierKeys.Alt:
                DeleteSelected();
                break;
            default:
                e.Handled = false;
                break;
        }
    }

    private void OnClipClicked(object sender, MouseButtonEventArgs e)
    {
        if (Clips.ContainerFromElement((DependencyObject)e.OriginalSource) is FrameworkElement { DataContext: ClipRow row })
            Choose(row, paste: true, plainText: false);
    }

    private void MoveSelection(int delta)
    {
        if (Clips.Items.Count == 0)
            return;
        Clips.SelectedIndex = Math.Clamp(Clips.SelectedIndex + delta, 0, Clips.Items.Count - 1);
        Clips.ScrollIntoView(Clips.SelectedItem);
    }

    private void Choose(ClipRow? row, bool paste, bool plainText)
    {
        if (row is null)
            return;
        Hide();
        ItemChosen?.Invoke(row.Item, paste ? _target : IntPtr.Zero, plainText);
    }

    private void TogglePin()
    {
        if (Clips.SelectedItem is not ClipRow row)
            return;
        _store.SetPinned(row.Item.Id, !row.Item.Pinned);
        _history = _store.GetAll();
        Refresh(keepSelectedId: row.Item.Id);
    }

    private void DeleteSelected()
    {
        if (Clips.SelectedItem is not ClipRow row)
            return;
        var index = Clips.SelectedIndex;
        _store.Delete(row.Item.Id);
        _history = _store.GetAll();
        Refresh();
        if (Clips.Items.Count > 0)
            Clips.SelectedIndex = Math.Min(index, Clips.Items.Count - 1);
    }

    private void RenderHints()
    {
        Hints.Inlines.Clear();
        var canPaste = _target != IntPtr.Zero;
        (string Keys, string Action)[] hints = canPaste
            ? [("Enter", "paste"), ("Shift+Enter", "plain"), ("Ctrl+Enter", "copy"), ("Alt+P", "pin"), ("Alt+Del", "delete")]
            : [("Enter", "copy"), ("Shift+Enter", "plain"), ("Alt+P", "pin"), ("Alt+Del", "delete")];
        foreach (var (keys, action) in hints)
        {
            Hints.Inlines.Add(new Run(keys) { Foreground = (Brush)FindResource("Text") });
            Hints.Inlines.Add(new Run($" {action}    "));
        }
    }

    /// <summary>Windows 11 rounded corners and a hairline border; older Windows ignores both.</summary>
    private void ApplyWindowFrame()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var corners = NativeMethods.DWMWCP_ROUND;
        NativeMethods.DwmSetWindowAttribute(handle, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref corners, sizeof(int));
        var border = 0x00433732; // COLORREF is 0x00BBGGRR: #323743, the Hairline brush.
        NativeMethods.DwmSetWindowAttribute(handle, NativeMethods.DWMWA_BORDER_COLOR, ref border, sizeof(int));
    }

    private ClipRow ToRow(ClipItem item, int index, DateTimeOffset now)
    {
        var head = item.Text.Length > 600 ? item.Text[..600] : item.Text;
        string preview, expanded;
        if (item.Kind == ClipKind.Files)
        {
            var paths = item.Text.Split('\n');
            var first = Path.GetFileName(paths[0].TrimEnd('\\'));
            preview = paths.Length == 1 ? first : $"{first} and {paths.Length - 1} more";
            expanded = string.Join('\n', paths.Take(4)) + (paths.Length > 4 ? $"\nand {paths.Length - 4} more" : "");
        }
        else
        {
            preview = Whitespace().Replace(head.Length > 300 ? head[..300] : head, " ").Trim();
            expanded = head.Replace("\t", "    ").Trim('\r', '\n');
        }

        var origin = "Copied"
            + (item.SourceApp is { } app ? $" from {app}" : "")
            + (item.IsRichText ? " with formatting" : "")
            + (item.CopyCount > 1 ? $", {item.CopyCount} times" : "");

        return new ClipRow(
            item,
            Shortcut: index < 9 ? (index + 1).ToString() : "",
            Preview: preview,
            Expanded: expanded,
            Origin: origin,
            Age: FormatAge(now - item.LastCopiedAt, item.LastCopiedAt),
            Pinned: item.Pinned,
            Thumbnail: Thumbnail(item),
            Glyph: item.Kind == ClipKind.Files ? FileGlyph : null);
    }

    private ImageSource? Thumbnail(ClipItem item)
    {
        if (item.Thumbnail is null)
            return null;
        if (!_thumbnails.TryGetValue(item.Id, out var image))
        {
            try
            {
                image = ImageCodec.Decode(item.Thumbnail);
            }
            catch (Exception ex) when (ex is NotSupportedException or FileFormatException)
            {
                return null;
            }
            _thumbnails[item.Id] = image;
        }
        return image;
    }
    private static string FormatAge(TimeSpan age, DateTimeOffset at) => age switch
    {
        { TotalMinutes: < 1 } => "now",
        { TotalHours: < 1 } => $"{(int)age.TotalMinutes}m",
        { TotalDays: < 1 } => $"{(int)age.TotalHours}h",
        { TotalDays: < 7 } => $"{(int)age.TotalDays}d",
        _ => at.ToLocalTime().ToString("MMM d"),
    };

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
