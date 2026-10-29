using System.Runtime.InteropServices;
using System.Text;
using WinClip.Core;
using static WinClip.Native.NativeMethods;

namespace WinClip.Native;

/// <summary>Clipboard formats as read, before conversion. Image formats are only read when there is no text or files.</summary>
internal sealed record RawClipboard(string[]? Files, string? Text, byte[]? Html, byte[]? Rtf, byte[]? Png, byte[]? Dib);

/// <summary>
/// Bounds enforced while the clipboard is open, so an oversized copy is dropped before it is copied out of
/// shared memory, decoded and encrypted.
/// </summary>
internal sealed record ClipboardLimits(int MaxTextChars, int MaxImageBytes)
{
    /// <summary>Clipboard text is UTF-16, so its size in shared memory is twice the character count.</summary>
    public long MaxTextBytes => (long)MaxTextChars * 2;
}

/// <summary>
/// Raw Win32 clipboard access. Other apps briefly hold the clipboard open after writing to it,
/// so opening retries with a short backoff instead of failing on the first attempt.
/// </summary>
internal static class ClipboardAccess
{
    // A folder tree dropped on the clipboard can hold far more paths than history should keep.
    private const int MaxFileCount = 10_000;
    private const int MaxPathLength = 32_767;

    private static readonly uint ExcludeFromMonitoring = RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
    private static readonly uint ViewerIgnore = RegisterClipboardFormat("Clipboard Viewer Ignore");
    private static readonly uint CanIncludeInHistory = RegisterClipboardFormat("CanIncludeInClipboardHistory");
    private static readonly uint HtmlFormat = RegisterClipboardFormat("HTML Format");
    private static readonly uint RtfFormat = RegisterClipboardFormat("Rich Text Format");
    private static readonly uint PngFormat = RegisterClipboardFormat("PNG");
    private static readonly uint PreferredDropEffect = RegisterClipboardFormat("Preferred DropEffect");

    private static long _ownWriteSequence;

    /// <summary>
    /// True when the current contents carry one of the markers password managers set to keep
    /// secrets out of clipboard history. Only a cheap check: the read repeats it once the clipboard is open.
    /// </summary>
    public static bool IsMarkedPrivate() =>
        IsClipboardFormatAvailable(ExcludeFromMonitoring) || IsClipboardFormatAvailable(ViewerIgnore);

    /// <summary>True when <paramref name="sequence"/> is the clipboard change WinClip itself last wrote.</summary>
    public static bool IsOwnWrite(uint sequence) =>
        sequence != 0 && sequence == (uint)Interlocked.Read(ref _ownWriteSequence);

    /// <summary>Reads the clipboard, or null when it can't be opened or the contents opted out of history.</summary>
    /// <remarks>
    /// Synchronous on purpose: Windows ties an open clipboard to the thread that opened it, so opening, reading
    /// and closing must not be split across an await. Call it off the UI thread, since opening may retry.
    /// </remarks>
    public static RawClipboard? TryRead(ClipboardLimits limits)
    {
        if (!TryOpen())
            return null;
        try
        {
            // The markers are checked again here: the pre-check runs before the clipboard is opened, and
            // another app can mark or replace the contents in between.
            if (IsMarkedPrivateWhileOpen())
                return null;

            string[]? files = null;
            if (IsClipboardFormatAvailable(CF_HDROP) && !TryReadFiles(limits, out files))
                return null;

            string? text = null;
            if (IsClipboardFormatAvailable(CF_UNICODETEXT))
            {
                // Over the limit or unreadable: skip the copy rather than store a truncated version of it.
                if (ReadBytes(CF_UNICODETEXT, limits.MaxTextBytes) is not { } textBytes)
                    return null;
                text = DecodeText(textBytes);
            }

            var hasText = !string.IsNullOrWhiteSpace(text);
            var wantImage = files is null && !hasText;
            return new RawClipboard(
                files,
                text,
                hasText ? TrimNulls(ReadBytes(HtmlFormat, limits.MaxTextBytes)) : null,
                hasText ? TrimNulls(ReadBytes(RtfFormat, limits.MaxTextBytes)) : null,
                wantImage ? ReadBytes(PngFormat, limits.MaxImageBytes) : null,
                wantImage && !IsClipboardFormatAvailable(PngFormat) ? ReadBytes(CF_DIB, limits.MaxImageBytes) : null);
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
            if (!EmptyClipboard() || !entries.All(entry => SetBytes(entry.Format, entry.Data)))
                return false;

            // Remember the clipboard state WinClip just produced. Its update notification looks exactly like
            // a real copy, so the monitor ignores the one whose sequence number matches this value.
            Interlocked.Exchange(ref _ownWriteSequence, GetClipboardSequenceNumber());
            return true;
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

    /// <summary>
    /// Opens the clipboard for reading on the calling thread. No owner window is passed: that is only needed to
    /// write, and the message window belongs to the UI thread.
    /// </summary>
    private static bool TryOpen()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
                return true;
            Thread.Sleep(15 * (attempt + 1));
        }
        return false;
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

    /// <summary>
    /// All three markers password managers set, read with the clipboard open. A marker whose value cannot be
    /// read counts as private: missing a copy beats recording a secret.
    /// </summary>
    private static bool IsMarkedPrivateWhileOpen()
    {
        if (IsClipboardFormatAvailable(ExcludeFromMonitoring) || IsClipboardFormatAvailable(ViewerIgnore))
            return true;
        if (!IsClipboardFormatAvailable(CanIncludeInHistory))
            return false;
        return ReadBytes(CanIncludeInHistory, sizeof(int)) is not { Length: >= 4 } flag
            || BitConverter.ToInt32(flag) == 0;
    }

    /// <summary>
    /// Copies one clipboard format out of shared memory, or returns null when it is unavailable, unreadable
    /// or larger than <paramref name="maxBytes"/>. The size is checked before anything is allocated.
    /// </summary>
    private static byte[]? ReadBytes(uint format, long maxBytes)
    {
        var handle = GetClipboardData(format);
        if (handle == IntPtr.Zero)
            return null;
        var size = (ulong)GlobalSize(handle);
        if (size > (ulong)Math.Min(maxBytes, int.MaxValue))
            return null;
        var pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero)
            return null;
        try
        {
            var bytes = new byte[(int)size];
            Marshal.Copy(pointer, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    private static string DecodeText(byte[] bytes)
    {
        // Bounded by the allocation, which may be larger than the text; stop at the terminator.
        var text = Encoding.Unicode.GetString(bytes, 0, bytes.Length & ~1);
        var terminator = text.IndexOf('\0');
        return terminator >= 0 ? text[..terminator] : text;
    }

    /// <summary>
    /// Reads the dropped paths, or returns false when the list is larger than history should hold: too many
    /// files, or more path text than the copy limit allows.
    /// </summary>
    private static bool TryReadFiles(ClipboardLimits limits, out string[]? files)
    {
        files = null;
        var drop = GetClipboardData(CF_HDROP);
        if (drop == IntPtr.Zero)
            return true;
        var count = DragQueryFile(drop, uint.MaxValue, null, 0);
        if (count > MaxFileCount)
            return false;

        var paths = new List<string>((int)count);
        long totalLength = 0;
        for (uint i = 0; i < count; i++)
        {
            var needed = DragQueryFile(drop, i, null, 0);
            // A path longer than the Windows limit is not a real file; skip it rather than grow the buffer blindly.
            if (needed == 0 || needed > MaxPathLength)
                continue;
            totalLength += needed;
            if (totalLength > limits.MaxTextChars)
                return false;
            var buffer = new char[needed + 1];
            var length = DragQueryFile(drop, i, buffer, (uint)buffer.Length);
            if (length > 0)
                paths.Add(new string(buffer, 0, (int)length));
        }

        files = paths.Count == 0 ? null : paths.ToArray();
        return true;
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
