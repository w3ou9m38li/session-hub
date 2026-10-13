using ClaudeLauncher.Core.Catalog;
using ClaudeLauncher.Core.Contracts;

namespace ClaudeLauncher.UnitTests;

public sealed class SourceCoverageRulesTests
{
    [Fact]
    public void UnresolvedSourceSuppressesTheGlobalTotal()
    {
        var report = SourceCoverageRules.Build(
            [
                new SourceTally(ConversationKey.DesktopCode, 7, 1, SourceCoverage.Resolved),
                new SourceTally(ConversationKey.Chat, 0, 0, SourceCoverage.UnknownSource),
            ],
            verifiedLinks: 4,
            unknownSourceIds: [],
            nonRootTranscripts: 1550);

        // A number here would be a claim we cannot support.
        Assert.Null(report.GlobalUniqueConversationCount);
        Assert.Contains("source-coverage-incomplete", report.GlobalCountReason!, StringComparison.Ordinal);
    }

    [Fact]
    public void UnresolvedLinksAlsoSuppressTheGlobalTotal()
    {
        var report = SourceCoverageRules.Build(
            [
                new SourceTally(ConversationKey.DesktopCode, 7, 1, SourceCoverage.Resolved),
                new SourceTally(ConversationKey.CliCode, 133, 0, SourceCoverage.Resolved),
            ],
            verifiedLinks: 4,
            unknownSourceIds: ["a", "b", "c"],
            nonRootTranscripts: 1550);

        Assert.Null(report.GlobalUniqueConversationCount);
        Assert.Equal(3, report.UnknownSourceIds.Count);
    }

    [Fact]
    public void LinkedPairsAreCountedOnceNotAdded()
    {
        var report = SourceCoverageRules.Build(
            [
                new SourceTally(ConversationKey.DesktopCode, 7, 1, SourceCoverage.Resolved),
                new SourceTally(ConversationKey.CliCode, 40, 0, SourceCoverage.Resolved),
            ],
            verifiedLinks: 7,
            unknownSourceIds: [],
            nonRootTranscripts: 1550);

        // 7 + 40 = 47 rows, but 7 of them are two views of the same conversation.
        Assert.Equal(40, report.GlobalUniqueConversationCount);
        Assert.NotEqual(47, report.GlobalUniqueConversationCount);
    }

    [Fact]
    public void SubagentTranscriptsAreReportedSeparatelyFromConversations()
    {
        var report = SourceCoverageRules.Build(
            [new SourceTally(ConversationKey.CliCode, 42, 0, SourceCoverage.Resolved)],
            verifiedLinks: 0, unknownSourceIds: [], nonRootTranscripts: 1550);

        Assert.Equal(42, report.GlobalUniqueConversationCount);
        Assert.Equal(1550, report.NonRootTranscripts);
    }

    [Fact]
    public void UnreachableSourceMayNotReplaceCachedRows()
    {
        Assert.False(SourceCoverageRules.MayReplaceCachedRows(SourceCoverage.Unreachable, 0, 42));
    }

    [Fact]
    public void EmptyReadMayNotWipeAPopulatedCatalog()
    {
        Assert.False(SourceCoverageRules.MayReplaceCachedRows(SourceCoverage.Resolved, 0, 42));
    }

    [Fact]
    public void GenuinelyEmptySourceIsAllowedWhenCacheIsAlsoEmpty()
    {
        Assert.True(SourceCoverageRules.MayReplaceCachedRows(SourceCoverage.Resolved, 0, 0));
    }
}
