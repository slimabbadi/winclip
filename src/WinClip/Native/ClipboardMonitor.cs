using System.ComponentModel;
using System.Diagnostics;
using WinClip.Core;
using static WinClip.Native.NativeMethods;

namespace WinClip.Native;

/// <param name="SourceApp">Process name of the copying app, or null when unknown.</param>
internal sealed record CapturedClip(ClipContent Content, string? SourceApp);

/// <summary>Raises <see cref="Captured"/> for each copy that is allowed into history.</summary>
internal sealed class ClipboardMonitor : IDisposable
{
    private readonly MessageWindow _window;

    public ClipboardMonitor(MessageWindow window)
    {
        _window = window;
        if (!AddClipboardFormatListener(window.Handle))
            throw new Win32Exception();
        window.MessageReceived += OnMessage;
    }

    public event Action<CapturedClip>? Captured;

    /// <summary>When true, copies are ignored until it is set back to false.</summary>
    public bool Paused { get; set; }

    public void Dispose()
    {
        _window.MessageReceived -= OnMessage;
        RemoveClipboardFormatListener(_window.Handle);
    }

    private async void OnMessage(int message, IntPtr wParam)
    {
        if (message != WM_CLIPBOARDUPDATE || Paused || ClipboardAccess.IsMarkedPrivate())
            return;

        try
        {
            // WinClip's own writes (pasting from history) are not new copies; the store moves those up itself.
            var ownerProcessId = ClipboardAccess.GetOwnerProcessId();
            if (ownerProcessId == Environment.ProcessId)
                return;

            if (await ClipboardAccess.TryReadAsync(_window.Handle) is not { } raw)
                return;
            if (await Task.Run(() => ToContent(raw)) is not { } content)
                return;
            Captured?.Invoke(new CapturedClip(content, ProcessName(ownerProcessId)));
        }
        catch (Exception ex)
        {
            // async void: an escaping exception would take down the app over one bad clipboard read.
            Trace.TraceError($"Clipboard capture failed: {ex}");
        }
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
        catch (ArgumentException)
        {
            return null; // The process exited after copying.
        }
    }
}
