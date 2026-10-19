using ClaudeLauncher.Infrastructure.Catalog;
using ClaudeLauncher.Infrastructure.Safety;

namespace ClaudeLauncher.UnitTests;

public sealed class SearchKeyTests
{
    [Fact]
    public void ChineseSubstringFindsTitle()
    {
        var key = SearchKeys.Normalize("提取该pdf中的病级，病级分0-5级");
        Assert.Contains(SearchKeys.Normalize("病级"), key, StringComparison.Ordinal);
    }

    [Fact]
    public void WidthVariantsMatchNormalizedKey()
    {
        // Full-width Latin folds onto ASCII under NFKC.
        Assert.Equal(SearchKeys.Normalize("GWAS"), SearchKeys.Normalize("ＧＷＡＳ"));
    }

    [Fact]
    public void CaseIsFoldedInvariantly()
    {
        Assert.Equal(SearchKeys.Normalize("gwas"), SearchKeys.Normalize("GWAS"));
    }

    [Fact]
    public void NormalizationKeepsOriginalDisplayTitleUntouched()
    {
        const string original = "ＧＷＡＳ  分析";
        var normalized = SearchKeys.Normalize(original);

        Assert.NotEqual(original, normalized);
        Assert.Equal("ＧＷＡＳ  分析", original);   // caller's string is not mutated
    }

    [Fact]
    public void WhitespaceIsCollapsedNotStripped()
    {
        Assert.Equal("a b", SearchKeys.Normalize("  a   b  "));
    }
}

public sealed class LikeEscapingTests
{
    [Theory]
    [InlineData("100%", "100\\%")]
    [InlineData("a_b", "a\\_b")]
    [InlineData(@"C:\path", @"C:\\path")]
    public void WildcardsAreEscapedAsLiterals(string input, string expected)
    {
        Assert.Equal(expected, SqliteCatalog.EscapeLike(input));
    }

    [Fact]
    public void EscapeCharacterIsEscapedFirst()
    {
        // If '%' were escaped before '\', the result would be the broken "\\%".
        Assert.Equal(@"\\\%", SqliteCatalog.EscapeLike(@"\%"));
    }
}

public sealed class PathPolicyTests
{
    private static string Root => Path.Combine(Path.GetTempPath(), "launcher-root");

    [Fact]
    public void TraversalOutsideTheRootIsRejected()
    {
        Assert.False(PathPolicy.IsWithin(Root, Path.Combine(Root, "..", "elsewhere")));
    }

    [Fact]
    public void SiblingWithSharedPrefixIsNotInsideTheRoot()
    {
        Assert.False(PathPolicy.IsWithin(Root, Root + "-backup"));
    }

    [Fact]
    public void ChildOfTheRootIsAccepted()
    {
        Assert.True(PathPolicy.IsWithin(Root, Path.Combine(Root, "catalog.sqlite")));
    }

    [Fact]
    public void ClaudeConfigDirectoryIsNeverWritable()
    {
        var target = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects", "x.jsonl");

        Assert.False(PathPolicy.MayWrite(Root, target, out var reason));
        Assert.Contains("官方数据", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageInstallDirectoryIsNeverWritable()
    {
        Assert.False(PathPolicy.MayWrite(Root, @"C:\Program Files\WindowsApps\Claude\app.exe", out _));
    }

    [Fact]
    public void DriveRootIsRejected()
    {
        Assert.False(PathPolicy.MayWrite(Root, @"C:\", out var reason));
        Assert.Contains("驱动器根", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void OwnDataRootIsWritable()
    {
        Assert.True(PathPolicy.MayWrite(Root, Path.Combine(Root, "diagnostics", "report.json"), out _));
    }
}
