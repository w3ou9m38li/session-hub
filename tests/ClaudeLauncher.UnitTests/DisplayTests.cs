using ClaudeLauncher.Core.Display;
using ClaudeLauncher.Infrastructure.Catalog;

namespace ClaudeLauncher.UnitTests;

public sealed class RelativeTimeTests
{
    // A Wednesday afternoon.
    private static readonly DateTime Now = new(2026, 9, 23, 15, 30, 0);

    [Theory]
    [InlineData(0, "刚刚")]
    [InlineData(-0.5, "刚刚")]
    [InlineData(5, "5 分钟前")]
    [InlineData(59, "59 分钟前")]
    public void RecentMomentsReadAsElapsedMinutes(double minutesAgo, string expected)
    {
        Assert.Equal(expected, RelativeTime.Format(Now.AddMinutes(-minutesAgo), Now));
    }

    [Fact]
    public void EarlierTodayShowsTheClock()
    {
        Assert.Equal("今天 09:05", RelativeTime.Format(new DateTime(2026, 9, 23, 9, 5, 0), Now));
    }

    [Fact]
    public void YesterdayIsCalledThatEvenLessThanADayAgo()
    {
        // 23:50 yesterday is under 16 hours ago but is still "yesterday" to a person.
        Assert.Equal("昨天 23:50", RelativeTime.Format(new DateTime(2026, 9, 22, 23, 50, 0), Now));
    }

    [Fact]
    public void ThisWeekUsesTheWeekday()
    {
        Assert.Equal("周五 10:00", RelativeTime.Format(new DateTime(2026, 9, 18, 10, 0, 0), Now));
    }

    [Fact]
    public void OlderThisYearDropsTheYear()
    {
        Assert.Equal("06-09 17:31", RelativeTime.Format(new DateTime(2026, 6, 9, 17, 31, 0), Now));
    }

    [Fact]
    public void AnotherYearShowsTheDateOnly()
    {
        Assert.Equal("2025-12-01", RelativeTime.Format(new DateTime(2025, 12, 1, 8, 0, 0), Now));
    }

    [Fact]
    public void AFarFutureStampIsShownAsItIsRatherThanAsJustNow()
    {
        // Bad data must look like bad data, not like fresh activity.
        Assert.Equal("2027-01-01 00:00", RelativeTime.Format(new DateTime(2027, 1, 1), Now));
    }

    [Fact]
    public void FullFormKeepsSecondsAndWeekday()
    {
        Assert.Equal("2026-09-23 15:30:00（周三）", RelativeTime.Full(Now));
    }
}

public sealed class PathDisplayTests
{
    [Fact]
    public void CandidatesKeepTheTailAndShrinkFromTheHead()
    {
        var forms = PathDisplay.Candidates(@"\\wsl.localhost\ubuntu\home\abc\ABC进阶一套");

        Assert.Equal(@"\\wsl.localhost\ubuntu\home\abc\ABC进阶一套", forms[0]);
        Assert.Equal(@"…\ABC进阶一套", forms[^1]);
        Assert.Contains(@"…\abc\ABC进阶一套", forms);
        // Longest first, so the first one that fits is the most informative.
        for (var i = 2; i < forms.Count; i++) Assert.True(forms[i - 1].Length >= forms[i].Length);
    }

    [Fact]
    public void AShortPathOffersItselfAndItsFolder()
    {
        Assert.Equal([@"G:\wheat GWAS 2", @"…\wheat GWAS 2"], PathDisplay.Candidates(@"G:\wheat GWAS 2"));
    }

    [Fact]
    public void ADriveRootIsNotCutToNothing()
    {
        Assert.Equal([@"D:\"], PathDisplay.Candidates(@"D:\"));
    }

    [Fact]
    public void ASlugWithoutSeparatorsStaysWhole()
    {
        Assert.Equal(["D--ClaudeLauncher"], PathDisplay.Candidates("D--ClaudeLauncher"));
    }

    [Fact]
    public void NothingIsOfferedForNoPath()
    {
        Assert.Empty(PathDisplay.Candidates(null));
        Assert.Empty(PathDisplay.Candidates("  "));
    }

    [Theory]
    [InlineData(@"G:\wheat GWAS 2\", "wheat GWAS 2")]
    [InlineData("/home/me/proj", "proj")]
    [InlineData("slug-only", "slug-only")]
    public void FolderNameIsTheLastSegment(string path, string expected)
    {
        Assert.Equal(expected, PathDisplay.FolderName(path));
    }
}

public sealed class SearchTermTests
{
    [Fact]
    public void WordsAreSplitNormalisedAndDeduplicated()
    {
        Assert.Equal(["wheat", "病级"], SearchKeys.Terms("  Wheat 病级 WHEAT "));
    }

    [Fact]
    public void AChinesePhraseIsOneTerm()
    {
        Assert.Equal(["提取病级数据"], SearchKeys.Terms("提取病级数据"));
    }

    [Fact]
    public void FullWidthSpaceSeparatesToo()
    {
        // NFKC folds the ideographic space an IME types onto a plain one.
        Assert.Equal(["gwas", "病级"], SearchKeys.Terms("ＧＷＡＳ　病级"));
    }

    [Fact]
    public void AnOverlongQueryIsCapped()
    {
        var terms = SearchKeys.Terms(string.Join(' ', Enumerable.Range(0, 20).Select(i => "w" + i)));
        Assert.Equal(SearchKeys.MaxTerms, terms.Count);
    }

    [Fact]
    public void AMinusLeavesAWordOut()
    {
        Assert.Equal([new SearchTerm("wheat", false), new SearchTerm("病级", true)], SearchKeys.Parse("Wheat -病级"));
        Assert.Equal(["wheat"], SearchKeys.Terms("wheat -病级"));           // only the words to find
    }

    [Fact]
    public void AMinusInsideAWordOrOnItsOwnIsJustACharacter()
    {
        Assert.Equal([new SearchTerm("0-5", false), new SearchTerm("-", false)], SearchKeys.Parse("0-5 -"));
        Assert.Equal([new SearchTerm("a", false), new SearchTerm("-", false), new SearchTerm("b", false)],
            SearchKeys.Parse("a - b"));
    }

    [Fact]
    public void QuotesKeepAPhraseWhole()
    {
        Assert.Equal([new SearchTerm("kasp 引物", false), new SearchTerm("wheat", false)],
            SearchKeys.Parse("\"KASP 引物\" wheat"));
        Assert.Equal([new SearchTerm("候选 基因", false)], SearchKeys.Parse("\u201C候选 基因\u201D"));   // Chinese quotes
        Assert.Equal([new SearchTerm("没有 收尾", false)], SearchKeys.Parse("\"没有 收尾"));             // unclosed: to the end
        Assert.Equal([new SearchTerm("整句 排除", true)], SearchKeys.Parse("-\"整句 排除\""));
        Assert.Empty(SearchKeys.Parse("\"\""));
    }

    [Fact]
    public void EmptySearchHasNoTerms()
    {
        Assert.Empty(SearchKeys.Terms(null));
        Assert.Empty(SearchKeys.Terms("   "));
    }

    [Theory]
    [InlineData("019eabb7", true)]
    [InlineData("3f3f2c8b-64ab", true)]
    [InlineData("local_3f3f", true)]
    [InlineData("de", false)]          // too short: would match most ids by accident
    [InlineData("gwas", false)]        // not hex
    [InlineData("abcdefg", false)]     // 'g' is not a hex digit
    public void OnlyIdShapedWordsSearchIds(string term, bool expected)
    {
        Assert.Equal(expected, SearchKeys.LooksLikeIdFragment(term));
    }
}
