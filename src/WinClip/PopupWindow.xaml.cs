using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using WinClip.Core;
using WinClip.Native;

namespace WinClip;

/// <summary>
/// One list row. Strings and the thumbnail are computed on first access, so only rows the list actually
/// realizes pay for them.
/// </summary>
internal sealed partial class ClipRow
{
    private const string FileGlyph = "";

    private readonly int _index;
    private readonly DateTimeOffset _now;
    private readonly Func<ClipItem, ImageSource?> _thumbnailSource;
    private readonly Lazy<(string Preview, string Expanded)> _text;
    private readonly Lazy<string> _origin;
    private readonly Lazy<string> _age;
    private readonly Lazy<string> _accessibleName;

    public ClipRow(ClipItem item, int index, DateTimeOffset now, Func<ClipItem, ImageSource?> thumbnailSource)
    {
        Item = item;
        _index = index;
        _now = now;
        _thumbnailSource = thumbnailSource;
        _text = new Lazy<(string, string)>(BuildText);
        _origin = new Lazy<string>(BuildOrigin);
        _age = new Lazy<string>(() => FormatAge(_now - Item.LastCopiedAt, Item.LastCopiedAt));
        _accessibleName = new Lazy<string>(BuildAccessibleName);
    }

    public ClipItem Item { get; }
    public bool Pinned => Item.Pinned;
    public string Shortcut => _index < 9 ? (_index + 1).ToString() : "";
    public string Preview => _text.Value.Preview;
    public string Expanded => _text.Value.Expanded;
    public string Origin => _origin.Value;
    public string Age => _age.Value;
    public string AccessibleName => _accessibleName.Value;
    public ImageSource? Thumbnail => _thumbnailSource(Item);
    public string? Glyph => Item.Kind == ClipKind.Files ? FileGlyph : null;
    public Visibility PinVisibility => Pinned ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ThumbnailVisibility => Item.Thumbnail is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility GlyphVisibility => Glyph is null ? Visibility.Collapsed : Visibility.Visible;

    private (string, string) BuildText()
    {
        var text = Item.Text;
        var head = text.Length > 600 ? text[..600] : text;
        if (Item.Kind == ClipKind.Files)
        {
            var paths = text.Split('\n');
            var first = Path.GetFileName(paths[0].TrimEnd('\\'));
            return (paths.Length == 1 ? first : $"{first} and {paths.Length - 1} more",
                string.Join('\n', paths.Take(4)) + (paths.Length > 4 ? $"\nand {paths.Length - 4} more" : ""));
        }
        return (Whitespace().Replace(head.Length > 300 ? head[..300] : head, " ").Trim(),
            head.Replace("\t", "    ").Trim('\r', '\n'));
    }

    private string BuildOrigin() => "Copied"
        + (Item.SourceApp is { } app ? $" from {app}" : "")
        + (Item.IsRichText ? " with formatting" : "")
        + (Item.CopyCount > 1 ? $", {Item.CopyCount} times" : "");

    private string BuildAccessibleName()
    {
        var preview = Preview;
        if (preview.Length == 0 && Item.Kind == ClipKind.Image)
            preview = "Image";
        var parts = new List<string>(4);
        if (Pinned)
            parts.Add("Pinned");
        if (Shortcut.Length > 0)
            parts.Add(Shortcut);
        if (preview.Length > 0)
            parts.Add(preview);
        parts.Add($"{Origin}, {Age}");
        return string.Join(". ", parts);
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

/// <summary>Small least-recently-used cache of decoded thumbnails; failed decodes are cached as null.</summary>
internal sealed class ThumbnailCache(int capacity)
{
    private readonly Dictionary<long, LinkedListNode<(long Id, ImageSource? Image)>> _map = [];
    private readonly LinkedList<(long Id, ImageSource? Image)> _order = new();

    public ImageSource? Get(ClipItem item)
    {
        if (item.Thumbnail is null)
            return null;
        if (_map.TryGetValue(item.Id, out var node))
        {
            _order.Remove(node);
            _order.AddFirst(node);
            return node.Value.Image;
        }

        ImageSource? image = null;
        try
        {
            image = ImageCodec.Decode(item.Thumbnail);
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException)
        {
        }

        _map[item.Id] = _order.AddFirst((item.Id, image));
        if (_map.Count > capacity)
        {
            _map.Remove(_order.Last!.Value.Id);
            _order.RemoveLast();
        }
        return image;
    }

    public void Clear()
    {
        _map.Clear();
        _order.Clear();
    }
}

/// <summary>
/// The history popup. Focus stays in the search box; arrow keys move the selection, Enter pastes into
/// the app that was focused when the popup opened.
/// </summary>
internal sealed partial class PopupWindow : Window
{
    private const int DebounceThreshold = 1000;

    private readonly HistoryStore _store;
    private readonly ThumbnailCache _thumbnails = new(120);
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private IReadOnlyList<ClipItem> _history = [];
    private IntPtr _target;
    private ClipRow? _pressedRow;
    private bool _suppressSearch;
    private readonly SmoothScroll _scroll;

    public PopupWindow(HistoryStore store)
    {
        _store = store;
        InitializeComponent();
        _scroll = new SmoothScroll(Clips);
        Deactivated += (_, _) => Hide();
        PreviewKeyDown += OnKeyDown;
        SourceInitialized += (_, _) => ApplyWindowFrame();
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is false)
                ReleaseHistory();
        };
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            Refresh();
        };
    }

    /// <summary>True while clipboard recording is paused; shown in the empty state and footer.</summary>
    public bool RecordingPaused { get; set; }

    /// <summary>
    /// Raised with the chosen item, the window to paste into (zero: copy only), and whether to drop formatting.
    /// </summary>
    public event Action<ClipItem, IntPtr, bool>? ItemChosen;

    /// <param name="target">Window to paste into; zero opens in copy-only mode.</param>
    public void ShowFor(IntPtr target)
    {
        _target = target;
        _history = _store.GetAll();
        SetSearchText("");
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

    private void OnSearchChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _scroll?.Stop();
        if (_suppressSearch)
            return;
        // Large histories get a short debounce so fast typing doesn't re-filter on every key.
        if (_history.Count > DebounceThreshold)
        {
            Placeholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            _searchTimer.Stop();
            _searchTimer.Start();
        }
        else
            Refresh();
    }

    /// <summary>Sets the search text without triggering a refresh; callers refresh themselves.</summary>
    private void SetSearchText(string text)
    {
        _suppressSearch = true;
        try
        {
            SearchBox.Text = text;
        }
        finally
        {
            _suppressSearch = false;
        }
    }

    /// <summary>Drops decrypted history, rows and decoded images once the popup is hidden.</summary>
    private void ReleaseHistory()
    {
        _searchTimer.Stop();
        _pressedRow = null;
        Clips.ItemsSource = null;
        _history = [];
        _thumbnails.Clear();
        SetSearchText("");
    }

    private void Refresh(long? keepSelectedId = null)
    {
        var now = DateTimeOffset.Now;
        var matches = ClipSearch.Filter(_history, SearchBox.Text);
        var rows = matches.Select((item, i) => new ClipRow(item, i, now, _thumbnails.Get)).ToList();
        Clips.ItemsSource = rows;

        var keep = rows.FindIndex(r => r.Item.Id == keepSelectedId);
        Clips.SelectedIndex = rows.Count == 0 ? -1 : Math.Max(keep, 0);
        if (Clips.SelectedItem is not null)
            Clips.ScrollIntoView(Clips.SelectedItem);

        Placeholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Text = _history.Count > 0
            ? $"Nothing matches “{SearchBox.Text.Trim()}”."
            : RecordingPaused
                ? "Recording is paused. Turn it back on from the tray menu."
                : "Copy something and it shows up here.";
        EmptyState.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        _scroll.Stop();
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

    private ClipRow? RowAt(object originalSource) =>
        originalSource is DependencyObject element
        && Clips.ContainerFromElement(element) is FrameworkElement { DataContext: ClipRow row }
            ? row
            : null;

    private void OnClipPressed(object sender, MouseButtonEventArgs e) => _pressedRow = RowAt(e.OriginalSource);

    private void OnClipClicked(object sender, MouseButtonEventArgs e)
    {
        var pressed = _pressedRow;
        _pressedRow = null;
        // Ignore releases from drags or scrolls that started elsewhere.
        if (RowAt(e.OriginalSource) is { } row && row == pressed)
            Choose(row, paste: true, plainText: Keyboard.Modifiers == ModifierKeys.Shift);
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
        {
            Clips.SelectedIndex = Math.Min(index, Clips.Items.Count - 1);
            Clips.ScrollIntoView(Clips.SelectedItem);
        }
    }

    private void RenderHints()
    {
        Hints.Inlines.Clear();
        if (RecordingPaused)
        {
            Hints.Inlines.Add(new Run("Recording paused") { Foreground = (Brush)FindResource("Pin") });
            Hints.Inlines.Add(new LineBreak());
        }
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
}
