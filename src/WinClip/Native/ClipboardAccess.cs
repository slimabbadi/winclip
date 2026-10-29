using System.Runtime.InteropServices;
using System.Text;
using WinClip.Core;
using static WinClip.Native.NativeMethods;

namespace WinClip.Native;

/// <summary>Clipboard formats as read, before conversion. Image formats are only read when there is no text or files.</summary>
internal sealed record RawClipboard(string[]? Files, string? Text, byte[]? Html, byte[]? Rtf, byte[]? Png, byte[]? Dib);

/// <summary>
/// Raw Win32 clipboard access. Other apps briefly hold the clipboard open after writing to it,
/// so opening retries with a short backoff instead of failing on the first attempt.
/// </summary>
internal static class ClipboardAccess
{
    private static readonly uint ExcludeFromMonitoring = RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
    private static readonly uint ViewerIgnore = RegisterClipboardFormat("Clipboard Viewer Ignore");
    private static readonly uint CanIncludeInHistory = RegisterClipboardFormat("CanIncludeInClipboardHistory");
    private static readonly uint HtmlFormat = RegisterClipboardFormat("HTML Format");
    private static readonly uint RtfFormat = RegisterClipboardFormat("Rich Text Format");
    private static readonly uint PngFormat = RegisterClipboardFormat("PNG");
    private static readonly uint PreferredDropEffect = RegisterClipboardFormat("Preferred DropEffect");

    /// <summary>
    /// True when the current contents carry one of the markers password managers set to keep
    /// secrets out of clipboard history.
    /// </summary>
    public static bool IsMarkedPrivate() =>
        IsClipboardFormatAvailable(ExcludeFromMonitoring) || IsClipboardFormatAvailable(ViewerIgnore);

    /// <summary>Reads the clipboard, or null when it can't be opened or the contents opted out of history.</summary>
    public static async Task<RawClipboard?> TryReadAsync(IntPtr owner)
    {
        if (!await TryOpenAsync(owner))
            return null;
        try
        {
            if (IsClipboardFormatAvailable(CanIncludeInHistory) && ReadBytes(CanIncludeInHistory) is { Length: >= 4 } flag
                && BitConverter.ToInt32(flag) == 0)
                return null;

            var files = IsClipboardFormatAvailable(CF_HDROP) ? ReadFiles() : null;
            var text = IsClipboardFormatAvailable(CF_UNICODETEXT) ? ReadText() : null;
            var hasText = !string.IsNullOrWhiteSpace(text);
            var wantImage = files is null && !hasText;
            return new RawClipboard(
                files,
                text,
                hasText ? TrimNulls(ReadBytes(HtmlFormat)) : null,
                hasText ? TrimNulls(ReadBytes(RtfFormat)) : null,
                wantImage ? ReadBytes(PngFormat) : null,
                wantImage && !IsClipboardFormatAvailable(PngFormat) ? ReadBytes(CF_DIB) : null);
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>
    /// Puts a history item back on the clipboard. <paramref name="plainText"/> writes only text, dropping
    /// formatting; for files that is their paths.
    /// </summary>
    public static async Task<bool> TryWriteAsync(IntPtr owner, ClipItem item, IReadOnlyDictionary<string, byte[]> formats, bool plainText)
    {
        // Convert before opening: the clipboard stays locked for every app while it is open.
        var entries = new List<(uint Format, byte[] Data)>();
        switch (item.Kind)
        {
            case ClipKind.Files when !plainText:
                var paths = item.Text.Split('\n');
                entries.Add((CF_HDROP, DropFiles(paths)));
                entries.Add((PreferredDropEffect, BitConverter.GetBytes(1))); // DROPEFFECT_COPY: paste copies, not moves.
                entries.Add((CF_UNICODETEXT, Utf16(string.Join(Environment.NewLine, paths))));
                break;
            case ClipKind.Image when formats.TryGetValue(ClipFormat.Png, out var png):
                entries.Add((PngFormat, png));
                entries.Add((CF_DIB, await Task.Run(() => ImageCodec.PngToDib(png))));
                break;
            default:
                entries.Add((CF_UNICODETEXT, Utf16(item.Kind == ClipKind.Files ? item.Text.Replace("\n", Environment.NewLine) : item.Text)));
                if (!plainText && formats.TryGetValue(ClipFormat.Html, out var html))
                    entries.Add((HtmlFormat, [.. html, 0]));
                if (!plainText && formats.TryGetValue(ClipFormat.Rtf, out var rtf))
                    entries.Add((RtfFormat, [.. rtf, 0]));
                break;
        }

        if (!await TryOpenAsync(owner))
            return false;
        try
        {
            return EmptyClipboard() && entries.All(entry => SetBytes(entry.Format, entry.Data));
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>Process that last wrote the clipboard, or null when it cannot be determined.</summary>
    public static int? GetOwnerProcessId()
    {
        var owner = GetClipboardOwner();
        if (owner == IntPtr.Zero)
            return null;
        GetWindowThreadProcessId(owner, out var processId);
        return processId == 0 ? null : (int)processId;
    }

    private static async Task<bool> TryOpenAsync(IntPtr owner)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (OpenClipboard(owner))
                return true;
            await Task.Delay(15 * (attempt + 1));
        }
        return false;
    }

    private static byte[]? ReadBytes(uint format)
    {
        var handle = GetClipboardData(format);
        if (handle == IntPtr.Zero)
            return null;
        var pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero)
            return null;
        try
        {
            var bytes = new byte[(int)Math.Min((ulong)GlobalSize(handle), int.MaxValue)];
            Marshal.Copy(pointer, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    private static string? ReadText()
    {
        if (ReadBytes(CF_UNICODETEXT) is not { } bytes)
            return null;
        // Bounded by the allocation, which may be larger than the text; stop at the terminator.
        var text = Encoding.Unicode.GetString(bytes, 0, bytes.Length & ~1);
        var terminator = text.IndexOf('\0');
        return terminator >= 0 ? text[..terminator] : text;
    }

    private static string[]? ReadFiles()
    {
        var drop = GetClipboardData(CF_HDROP);
        if (drop == IntPtr.Zero)
            return null;
        var count = DragQueryFile(drop, uint.MaxValue, null, 0);
        var files = new string[count];
        for (uint i = 0; i < count; i++)
        {
            var buffer = new char[DragQueryFile(drop, i, null, 0) + 1];
            var length = DragQueryFile(drop, i, buffer, (uint)buffer.Length);
            files[i] = new string(buffer, 0, (int)length);
        }
        return files.Length == 0 ? null : files;
    }

    private static bool SetBytes(uint format, byte[] data)
    {
        var handle = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)Math.Max(data.Length, 1));
        if (handle == IntPtr.Zero)
            return false;
        var pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero)
        {
            GlobalFree(handle);
            return false;
        }
        Marshal.Copy(data, 0, pointer, data.Length);
        GlobalUnlock(handle);

        // On success the system owns the memory; on failure it is still ours to free.
        if (SetClipboardData(format, handle) != IntPtr.Zero)
            return true;
        GlobalFree(handle);
        return false;
    }

    /// <summary>DROPFILES header followed by null-separated UTF-16 paths and a final extra null.</summary>
    private static byte[] DropFiles(string[] paths)
    {
        const int headerSize = 20;
        var list = Encoding.Unicode.GetBytes(string.Join('\0', paths) + "\0\0");
        var data = new byte[headerSize + list.Length];
        BitConverter.GetBytes(headerSize).CopyTo(data, 0); // pFiles: offset of the path list
        BitConverter.GetBytes(1).CopyTo(data, 16);         // fWide: paths are UTF-16
        list.CopyTo(data, headerSize);
        return data;
    }

    private static byte[] Utf16(string text) => Encoding.Unicode.GetBytes(text + "\0");

    private static byte[]? TrimNulls(byte[]? data)
    {
        if (data is null)
            return null;
        var end = Array.IndexOf(data, (byte)0);
        return end >= 0 ? data[..end] : data;
    }
}
