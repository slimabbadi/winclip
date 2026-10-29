namespace WinClip.Core;

/// <summary>
/// Filters history for the search box. Substring matches come first in their original (recency) order;
/// fuzzy subsequence matches follow, tightest match first.
/// </summary>
public static class ClipSearch
{
    // Matching against megabyte-sized clips on every keystroke would stall the popup.
    // Fuzzy matching is quadratic in the worst case, so it gets a tighter window.
    private const int MaxSearchedChars = 10_000;
    private const int MaxFuzzySearchedChars = 2_000;

    public static IReadOnlyList<ClipItem> Filter(IReadOnlyList<ClipItem> items, string query)
    {
        query = query.Trim();
        if (query.Length == 0)
            return items;

        var substringMatches = new List<ClipItem>();
        var fuzzyMatches = new List<(ClipItem Item, int Span, int Order)>();

        for (var i = 0; i < items.Count; i++)
        {
            var text = items[i].Text.AsSpan(0, Math.Min(items[i].Text.Length, MaxSearchedChars));
            if (text.Contains(query, StringComparison.OrdinalIgnoreCase))
                substringMatches.Add(items[i]);
            else if (FuzzySpan(text[..Math.Min(text.Length, MaxFuzzySearchedChars)], query) is { } span)
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
    internal static int? FuzzySpan(ReadOnlySpan<char> text, string query)
    {
        var needle = query.Where(c => !char.IsWhiteSpace(c)).Select(char.ToLowerInvariant).ToArray();
        if (needle.Length == 0)
            return null;

        int? best = null;
        for (var start = 0; start < text.Length; start++)
        {
            if (char.ToLowerInvariant(text[start]) != needle[0])
                continue;

            var matched = 1;
            var end = start;
            for (var i = start + 1; i < text.Length && matched < needle.Length; i++)
            {
                if (char.ToLowerInvariant(text[i]) == needle[matched])
                {
                    matched++;
                    end = i;
                }
            }

            if (matched < needle.Length)
                break; // Later starts cannot match either.

            var span = end - start + 1;
            if (best is null || span < best)
                best = span;
        }
        return best;
    }
}
