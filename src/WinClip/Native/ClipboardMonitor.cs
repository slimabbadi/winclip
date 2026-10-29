using System.ComponentModel;
using System.Diagnostics;
using WinClip.Core;
using static WinClip.Native.NativeMethods;

namespace WinClip.Native;

/// <param name="SourceApp">Process name of the copying app, or null when unknown.</param>
internal sealed record CapturedClip(ClipContent Content, string? SourceApp);

/// <summary>Raises <see cref="Captured"/> for each copy that is allowed into history.</summary>
/// <remarks>
/// Windows posts one WM_CLIPBOARDUPDATE per clipboard change, so a burst of copies would otherwise run
/// overlapping captures that finish out of order and double-count. Captures run one at a time on the thread
/// pool, and updates that arrive while one is running collapse into the latest clipboard state.
/// </remarks>
internal sealed class ClipboardMonitor : IDisposable
{
    private readonly MessageWindow _window;
    private readonly Lock _gate = new();
    private bool _busy;
    private bool _pending;
    private uint _pendingSequence;
    private uint _handledSequence;
    private volatile bool _paused;
    private volatile bool _disposed;

    public ClipboardMonitor(MessageWindow window, ClipboardLimits limits)
    {
        _window = window;
        Limits = limits;
        if (!AddClipboardFormatListener(window.Handle))
            throw new Win32Exception();
        window.MessageReceived += OnMessage;
    }

    public event Action<CapturedClip>? Captured;

    /// <summary>When true, copies are ignored until it is set back to false.</summary>
    public bool Paused
    {
        get => _paused;
        set => _paused = value;
    }

    /// <summary>Bounds applied while reading, so an oversized copy is skipped before it is decoded.</summary>
    public ClipboardLimits Limits { get; set; }

    public void Dispose()
    {
        _disposed = true;
        _window.MessageReceived -= OnMessage;
        RemoveClipboardFormatListener(_window.Handle);
    }

    private void OnMessage(int message, IntPtr wParam)
    {
        if (message != WM_CLIPBOARDUPDATE)
            return;

        // Cheap pre-check; TryReadAsync repeats every marker check once the clipboard is open.
        if (Paused || ClipboardAccess.IsMarkedPrivate())
            return;

        var sequence = GetClipboardSequenceNumber();
        // WinClip's own writes (pasting from history) are not new copies; the store moves those up itself.
        if (ClipboardAccess.IsOwnWrite(sequence))
            return;

        bool start;
        lock (_gate)
        {
            if (sequence != 0 && sequence == _handledSequence)
                return; // The same clipboard state again; there is nothing new to read.
            _pending = true;
            _pendingSequence = sequence;
            start = !_busy;
            _busy = true;
        }

        if (start)
            _ = Task.Run(CapturePendingAsync);
    }

    /// <summary>Drains pending updates one at a time until none is left, then goes idle.</summary>
    private async Task CapturePendingAsync()
    {
        while (true)
        {
            lock (_gate)
            {
                if (!_pending)
                {
                    _busy = false;
                    return;
                }
                _pending = false;
                _handledSequence = _pendingSequence;
            }

            try
            {
                await CaptureAsync();
            }
            catch (Exception ex)
            {
                // A bad payload must not stop later captures, and an escaping exception here would be unobserved.
                Trace.TraceError($"Clipboard capture failed: {ex.GetType().FullName}{Environment.NewLine}{ex.StackTrace}");
            }

            if (_disposed)
            {
                lock (_gate)
                {
                    _pending = false;
                    _busy = false;
                }
                return;
            }
        }
    }

    private async Task CaptureAsync()
    {
        var ownerProcessId = ClipboardAccess.GetOwnerProcessId();
        if (await ClipboardAccess.TryReadAsync(_window.Handle, Limits) is not { } raw)
            return;
        if (ToContent(raw) is not { } content)
            return;
        // The user may have paused while the copy was being read and decoded.
        if (Paused || _disposed)
            return;
        Captured?.Invoke(new CapturedClip(content, ProcessName(ownerProcessId)));
    }

    private static ClipContent? ToContent(RawClipboard raw)
    {
        if (raw.Files is { Length: > 0 } files)
            return new ClipContent(ClipKind.Files, string.Join('\n', files), new Dictionary<string, byte[]>());

        if (!string.IsNullOrWhiteSpace(raw.Text))
        {
            var formats = new Dictionary<string, byte[]>();
            if (raw.Html is { Length: > 0 } html)
                formats[ClipFormat.Html] = html;
            if (raw.Rtf is { Length: > 0 } rtf)
                formats[ClipFormat.Rtf] = rtf;
            return new ClipContent(ClipKind.Text, raw.Text, formats);
        }

        var png = raw.Png ?? (raw.Dib is { } dib ? ImageCodec.DibToPng(dib) : null);
        if (png is null)
            return null;
        var (width, height, thumbnail) = ImageCodec.Describe(png);
        return new ClipContent(ClipKind.Image, $"Image, {width} × {height}",
            new Dictionary<string, byte[]> { [ClipFormat.Png] = png }, thumbnail);
    }

    private static string? ProcessName(int? processId)
    {
        if (processId is null)
            return null;
        try
        {
            using var process = Process.GetProcessById(processId.Value);
            return process.ProcessName;
        }
        catch (Exception)
        {
            // The process can exit while its name is read, and .NET reports that in several exception types.
            return null;
        }
    }
}
