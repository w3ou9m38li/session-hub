using ClaudeLauncher.Core.Catalog;

namespace ClaudeLauncher.UnitTests;

/// <summary>
/// The group name is the only user-typed value this tool stores, and every failure mode is
/// silent: two groups that look identical, a name that cannot be read in the column, a
/// Claude-side group quietly overwritten. Each is pinned here.
/// </summary>
public sealed class GroupRulesTests
{
    [Theory]
    [InlineData("  小麦 GWAS  ", "小麦 GWAS")]
    [InlineData("小麦\tGWAS", "小麦 GWAS")]
    [InlineData("小麦  GWAS", "小麦 GWAS")]
    [InlineData("\n 论文 \r\n", "论文")]
    public void NormalizeCollapsesWhitespaceSoLookalikesCannotBothExist(string input, string expected)
    {
        Assert.Equal(expected, GroupRules.Normalize(input));
    }

    [Fact]
    public void NormalizeDropsControlCharacters()
    {
        Assert.Equal("论文", GroupRules.Normalize("论\u0007文"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void NormalizeRefusesAnEmptyName(string? input)
    {
        Assert.Null(GroupRules.Normalize(input));
    }

    [Fact]
    public void NormalizeTruncatesRatherThanRejectingALongName()
    {
        var name = GroupRules.Normalize(new string('长', GroupRules.MaxLength + 20));
        Assert.Equal(GroupRules.MaxLength, name!.Length);
    }

    [Fact]
    public void OwnGroupWinsOverClaudes()
    {
        Assert.Equal("我的", GroupRules.Effective("我的", "Claude 的"));
    }

    [Fact]
    public void ClaudesGroupShowsThroughWhenTheUserHasNoPreference()
    {
        Assert.Equal("Claude 的", GroupRules.Effective(null, "Claude 的"));
    }

    [Fact]
    public void ExplicitlyUngroupedBeatsAClaudeGroup()
    {
        // Distinct from null: the user said "no group", and a Claude group must not reappear.
        Assert.Null(GroupRules.Effective(GroupRules.ExplicitlyUngrouped, "Claude 的"));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    public void NoPreferenceAndNoClaudeGroupIsUngrouped(string? sidebar, string? expected)
    {
        Assert.Equal(expected, GroupRules.Effective(null, sidebar));
    }

    [Theory]
    [InlineData(@"G:\wheat GWAS 2", "wheat GWAS 2")]
    [InlineData(@"D:\ClaudeLauncher\", "ClaudeLauncher")]
    [InlineData(@"\\wsl.localhost\ubuntu\home\me\proj", "proj")]
    [InlineData("/home/me/proj", "proj")]
    [InlineData("G--wheat-GWAS-2", "G--wheat-GWAS-2")]
    public void GroupNameComesFromTheLastPathSegment(string project, string expected)
    {
        Assert.Equal(expected, GroupRules.FromProjectPath(project));
    }

    [Fact]
    public void ADriveRootStillYieldsAName()
    {
        // Nothing follows the separator, so the drive itself is the only label available.
        Assert.Equal("D:", GroupRules.FromProjectPath(@"D:\"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"\")]
    public void NoWorkingDirectoryMeansNoGroup(string? project)
    {
        Assert.Null(GroupRules.FromProjectPath(project));
    }
}
