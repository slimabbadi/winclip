namespace WinClip.Core;

public enum ClipKind
{
    Text = 0,
    Image = 1,
    Files = 2,
}

/// <summary>Names of the extra clipboard formats stored alongside an item's text.</summary>
public static class ClipFormat
{
    /// <summary>CF_HTML bytes ("HTML Format"), header included.</summary>
    public const string Html = "html";

    /// <summary>RTF bytes ("Rich Text Format").</summary>
    public const string Rtf = "rtf";

    /// <summary>The full-size image as PNG.</summary>
    public const string Png = "png";
}

/// <summary>Something copied, as captured from the clipboard.</summary>
/// <param name="Text">
/// Plain text for <see cref="ClipKind.Text"/>; newline-separated paths for <see cref="ClipKind.Files"/>;
/// a description such as "Image, 640 × 480" for <see cref="ClipKind.Image"/>. Search matches against it.
/// </param>
/// <param name="Formats">Extra formats keyed by <see cref="ClipFormat"/> names.</param>
/// <param name="Thumbnail">Small PNG preview for images.</param>
public sealed record ClipContent(
    ClipKind Kind,
    string Text,
    IReadOnlyDictionary<string, byte[]> Formats,
    byte[]? Thumbnail = null)
{
    public static ClipContent PlainText(string text) => new(ClipKind.Text, text, new Dictionary<string, byte[]>());
}

/// <summary>A history entry. Full formats are loaded on demand with <see cref="HistoryStore.GetFormats"/>.</summary>
/// <param name="Id">Database id.</param>
/// <param name="Kind">What was copied.</param>
/// <param name="Text">
/// The item's text, cut to the first <see cref="MaxTextLength"/> characters to bound memory. That is enough for
/// search and previews; use <see cref="HistoryStore.GetFullText"/> when the complete text is needed, such as pasting.
/// </param>
/// <param name="Thumbnail">Small PNG preview for images.</param>
/// <param name="IsRichText">Whether HTML or RTF formats are stored.</param>
/// <param name="SourceApp">Process that last copied it, if known.</param>
/// <param name="FirstCopiedAt">When it was first copied.</param>
/// <param name="LastCopiedAt">When it was last copied or pasted.</param>
/// <param name="CopyCount">How many times it was copied.</param>
/// <param name="Pinned">Whether it is exempt from the size cap and from clearing.</param>
public sealed record ClipItem(
    long Id,
    ClipKind Kind,
    string Text,
    byte[]? Thumbnail,
    bool IsRichText,
    string? SourceApp,
    DateTimeOffset FirstCopiedAt,
    DateTimeOffset LastCopiedAt,
    int CopyCount,
    bool Pinned)
{
    /// <summary>Longest <see cref="Text"/> kept on an item returned by <see cref="HistoryStore"/>.</summary>
    public const int MaxTextLength = 10_000;

    /// <summary>Cuts text to <see cref="MaxTextLength"/>, without splitting a surrogate pair.</summary>
    internal static string Shorten(string text)
    {
        if (text.Length <= MaxTextLength)
            return text;
        var length = char.IsHighSurrogate(text[MaxTextLength - 1]) ? MaxTextLength - 1 : MaxTextLength;
        return text[..length];
    }
}
