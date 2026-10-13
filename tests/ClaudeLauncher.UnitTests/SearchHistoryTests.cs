using ClaudeLauncher.Infrastructure.Settings;

namespace ClaudeLauncher.UnitTests;

/// <summary>
/// The list under the search box: newest first, each search once, and not every stage of a
/// search typed slowly.
/// </summary>
public sealed class SearchHistoryTests
{
    [Theory]
    [InlineData("  kasp   primer ", "kasp primer")]
    [InlineData("病级", "病级")]
    [InlineData("-综合分析", "-综合分析")]
    [InlineData("k", null)]                 // one character is not worth offering again
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void SearchesAreKeptTrimmedAndWorthKeeping(string? typed, string? kept) =>
        Assert.Equal(kept, SearchHistory.Normalize(typed));

    [Fact]
    public void APastedParagraphIsNotKept() =>
        Assert.Null(SearchHistory.Normalize(new string('字', SearchHistory.MaxLength + 1)));

    [Fact]
    public void TheNewestComesFirstAndARepeatMovesUp()
    {
        var history = SearchHistory.Add([], "kasp");
        history = SearchHistory.Add(history, "病级");
        history = SearchHistory.Add(history, "KASP");       // finds the same: moves up, newest spelling

        Assert.Equal(["KASP", "病级"], history);
    }

    [Fact]
    public void FullAndHalfWidthAreTheSameSearch()
    {
        var history = SearchHistory.Add(["ＧＷＡＳ"], "gwas");

        Assert.Equal(["gwas"], history);
    }

    [Fact]
    public void TypingASearchSlowlyLeavesOneEntry()
    {
        // Kept as 「ka」 after a pause, then refined: each stage replaces the one before.
        var history = SearchHistory.Add(["病级"], "ka");
        history = SearchHistory.Add(history, "kasp", replacing: "ka");
        history = SearchHistory.Add(history, "kasp primer", replacing: "kasp");
        history = SearchHistory.Add(history, "kasp prim", replacing: "kasp primer");      // cut back

        Assert.Equal(["kasp prim", "病级"], history);
    }

    [Fact]
    public void ADifferentSearchInTheSameBoxIsKeptBeside()
    {
        var history = SearchHistory.Add(["kasp"], "病级", replacing: "kasp");

        Assert.Equal(["病级", "kasp"], history);
    }

    [Fact]
    public void AnEntryNotWorthKeepingLeavesTheListAsItWas()
    {
        var history = SearchHistory.Add(["kasp"], "k", replacing: "kasp");

        Assert.Equal(["kasp"], history);
    }

    [Fact]
    public void TheOldestFallOffAtCapacity()
    {
        var history = new List<string>();
        for (var i = 0; i < SearchHistory.Capacity + 5; i++) history = SearchHistory.Add(history, $"search {i}");

        Assert.Equal(SearchHistory.Capacity, history.Count);
        Assert.Equal($"search {SearchHistory.Capacity + 4}", history[0]);
        Assert.Equal("search 5", history[^1]);
    }

    [Fact]
    public void RemovingTakesEveryFormOfTheSearch()
    {
        Assert.Equal(["病级"], SearchHistory.Remove(["KASP", "病级"], "kasp"));
        Assert.True(SearchHistory.Contains(["KASP"], "ｋａｓｐ"));
        Assert.False(SearchHistory.Contains(["KASP"], "kas"));
    }

    [Fact]
    public void TheBoxKeepsOneEntryForASearchTypedOn()
    {
        var typing = new SearchHistory.Typing();
        var history = typing.Keep(["病级"], "ka");
        history = typing.Keep(history, "kasp");
        history = typing.Keep(history, "kasp");                 // stood a moment, then the box lost focus
        history = typing.Keep(history, "kasp primer");

        Assert.Equal(["kasp primer", "病级"], history);
    }

    [Fact]
    public void ASearchAlreadyKeptStaysWhenTypedOnIntoANewOne()
    {
        var typing = new SearchHistory.Typing();
        var history = typing.Keep(["病级", "kasp"], "kasp");
        history = typing.Keep(history, "kasp primer");

        Assert.Equal(["kasp primer", "kasp", "病级"], history);
    }

    [Fact]
    public void AStageTypedOnIntoAKnownSearchGivesWay()
    {
        var typing = new SearchHistory.Typing();
        var history = typing.Keep(["kasp"], "ka");
        history = typing.Keep(history, "kasp");

        Assert.Equal(["kasp"], history);
    }

    [Fact]
    public void AnEmptiedBoxStartsANewSearch()
    {
        var typing = new SearchHistory.Typing();
        var history = typing.Keep([], "kasp");
        typing.StartOver();
        history = typing.Keep(history, "kasp primer");
        history = typing.Keep(history, "病级");

        Assert.Equal(["病级", "kasp primer", "kasp"], history);
    }

    [Fact]
    public void AStoredListIsCleanedBeforeItIsShown()
    {
        var stored = new List<string?> { " kasp ", null, "KASP", "x", "病级", new string('长', 500), "  " };

        Assert.Equal(["kasp", "病级"], SearchHistory.Clean(stored));
        Assert.Empty(SearchHistory.Clean(null));
    }
}
