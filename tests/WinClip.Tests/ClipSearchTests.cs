using WinClip.Core;

namespace WinClip.Tests;

public sealed class ClipSearchTests
{
    private static ClipItem Item(string text) =>
        new(0, ClipKind.Text, text, null, false, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 1, false);

    [Fact]
    public void Empty_query_returns_history_unchanged()
    {
        var items = new[] { Item("b"), Item("a") };

        Assert.Same(items, ClipSearch.Filter(items, "  "));
    }

    [Fact]
    public void Substring_matches_keep_recency_order_and_rank_above_fuzzy_matches()
    {
        var items = new[] { Item("git status"), Item("good stuff"), Item("GIT log") };

        var result = ClipSearch.Filter(items, "git");

        Assert.Equal(["git status", "GIT log"], result.Select(i => i.Text));
    }

    [Fact]
    public void Fuzzy_matches_rank_the_tightest_match_first()
    {
        var items = new[] { Item("h______e______l______o"), Item("help-o"), Item("nothing here") };

        var result = ClipSearch.Filter(items, "helo");

        Assert.Equal(["help-o", "h______e______l______o"], result.Select(i => i.Text));
    }

    [Theory]
    [InlineData("a__abc", "ac", 3)]
    [InlineData("a_b_c", "a c", 5)]
    [InlineData("abc", "abd", null)]
    public void FuzzySpan_finds_the_shortest_window(string text, string query, int? expected)
    {
        Assert.Equal(expected, ClipSearch.FuzzySpan(text, query));
    }

    [Fact]
    public void FuzzySpan_handles_long_adversarial_text()
    {
        var text = string.Concat(Enumerable.Repeat("aX", 1_000));

        Assert.Null(ClipSearch.FuzzySpan(text, "ab"));
        Assert.Equal(4, ClipSearch.FuzzySpan(text, "axax"));
        Assert.Equal(2_000, ClipSearch.FuzzySpan(text, string.Concat(Enumerable.Repeat("ax", 1_000))));
        Assert.Null(ClipSearch.FuzzySpan(text, string.Concat(Enumerable.Repeat("ax", 1_001))));
    }

    [Fact]
    public void FuzzySpan_matches_a_brute_force_reference_on_random_text()
    {
        var random = new Random(1234);
        const string alphabet = "abcAB_";
        for (var round = 0; round < 2_000; round++)
        {
            var text = new string(Enumerable.Range(0, random.Next(0, 40)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
            var query = new string(Enumerable.Range(0, random.Next(1, 5)).Select(_ => alphabet[random.Next(alphabet.Length - 1)]).ToArray());

            Assert.Equal(Reference(text, query), ClipSearch.FuzzySpan(text, query));
        }
    }

    [Fact]
    public void Filter_ignores_whitespace_in_a_fuzzy_query_and_is_case_insensitive()
    {
        var result = ClipSearch.Filter([Item("a_B_c"), Item("xyz")], "A b C");

        Assert.Equal(["a_B_c"], result.Select(i => i.Text));
    }

    private static int? Reference(string text, string query)
    {
        var needle = query.Where(c => !char.IsWhiteSpace(c)).Select(char.ToLowerInvariant).ToArray();
        int? best = null;
        for (var start = 0; start < text.Length; start++)
        {
            var matched = 0;
            var end = start;
            for (var i = start; i < text.Length && matched < needle.Length; i++)
            {
                if (char.ToLowerInvariant(text[i]) == needle[matched])
                {
                    matched++;
                    end = i;
                }
            }
            if (matched == needle.Length && (best is null || end - start + 1 < best))
                best = end - start + 1;
        }
        return best;
    }
}
