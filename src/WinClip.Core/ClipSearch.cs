namespace WinClip.Core;

/// <summary>
/// Filters history for the search box. Substring matches come first in their original (recency) order;
/// fuzzy subsequence matches follow, tightest match first.
/// </summary>
public static class ClipSearch
{
    // Matching against megabyte-sized clips on every keystroke would stall the popup.
    // Fuzzy matching can still approach quadratic time on adversarial text, so it gets a tighter window.
    private const int MaxSearchedChars = ClipItem.MaxTextLength;
    private const int MaxFuzzySearchedChars = 2_000;

    public static IReadOnlyList<ClipItem> Filter(IReadOnlyList<ClipItem> items, string query)
    {
        query = query.Trim();
        if (query.Length == 0)
            return items;

        var needle = Needle(query);
        var substringMatches = new List<ClipItem>();
        var fuzzyMatches = new List<(ClipItem Item, int Span, int Order)>();

        for (var i = 0; i < items.Count; i++)
        {
            var text = items[i].Text.AsSpan(0, Math.Min(items[i].Text.Length, MaxSearchedChars));
            if (text.Contains(query, StringComparison.OrdinalIgnoreCase))
                substringMatches.Add(items[i]);
            else if (FuzzySpan(text[..Math.Min(text.Length, MaxFuzzySearchedChars)], needle) is { } span)
                fuzzyMatches.Add((items[i], span, i));
        }

        fuzzyMatches.Sort((a, b) => a.Span != b.Span ? a.Span.CompareTo(b.Span) : a.Order.CompareTo(b.Order));
        substringMatches.AddRange(fuzzyMatches.Select(match => match.Item));
        return substringMatches;
    }

    /// <summary>
    /// Length of the shortest window of <paramref name="text"/> containing every non-space character of
    /// <paramref name="query"/> in order, or null when there is none.
    /// </summary>
    internal static int? FuzzySpan(ReadOnlySpan<char> text, string query) => FuzzySpan(text, Needle(query));

    private static char[] Needle(string query) =>
        query.Where(c => !char.IsWhiteSpace(c)).Select(char.ToLowerInvariant).ToArray();

    /// <summary>
    /// Minimal-window subsequence search: scan forward until the needle is complete, scan backward from there
    /// to find the latest start that still completes it, then resume just after that start.
    /// </summary>
    private static int? FuzzySpan(ReadOnlySpan<char> text, char[] needle)
    {
        if (needle.Length == 0)
            return null;

        int? best = null;
        var matched = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (char.ToLowerInvariant(text[i]) != needle[matched])
                continue;
            if (++matched < needle.Length)
                continue;

            var start = i;
            for (var k = needle.Length - 1; k >= 0; start--)
            {
                if (char.ToLowerInvariant(text[start]) == needle[k])
                    k--;
            }
            start++; // The loop stepped past the first matched character.

            var span = i - start + 1;
            if (best is null || span < best)
                best = span;
            if (span == needle.Length)
                break; // Cannot get tighter.

            i = start; // Windows starting at or before start are covered; resume from start + 1.
            matched = 0;
        }
        return best;
    }
}
