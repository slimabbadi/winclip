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
}
