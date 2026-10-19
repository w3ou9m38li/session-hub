using ClaudeLauncher.Core.Display;

namespace ClaudeLauncher.UnitTests;

public class ContinuationNamesTests
{
    [Fact]
    public void OneEarlierPartAndTheSessionNameEachOther()
    {
        // The chain: [earlier part, desktop session].
        Assert.Equal("续接前的部分", ContinuationNames.Of(0, at: 1, count: 2));
        Assert.Equal("续接后的会话", ContinuationNames.Of(1, at: 0, count: 2));
        Assert.Equal("这条会话在 Claude 里续接过：更早的对话在另一份记录里", ContinuationNames.Where(1, 2));
        Assert.Equal("这是一条会话续接前的部分：后来的对话在另一份记录里", ContinuationNames.Where(0, 2));
    }

    [Fact]
    public void ResumesAreNamedFromBothEnds()
    {
        Assert.Equal("续接自", ContinuationNames.ResumedFrom(fork: false));
        Assert.Equal("分叉自", ContinuationNames.ResumedFrom(fork: true));
        Assert.Equal("从这里分叉的第 2 个任务", ContinuationNames.ResumedInto(1, 2, fork: true));
        Assert.Equal("这条任务后来被分叉过 2 次", ContinuationNames.ResumedIntoWhere(2, fork: true));
        Assert.Equal("从这里续接的会话", ContinuationNames.ResumedInto(0, 1));
        Assert.Equal("从这里续接的第 2 个会话", ContinuationNames.ResumedInto(1, 3));
        Assert.Equal("另有 3 次续接后没有说话", ContinuationNames.Folded(3));
        Assert.Equal("这条会话后来在另一条会话里续接", ContinuationNames.ResumedIntoWhere(1));
        Assert.Equal("这条会话后来被续接过 2 次", ContinuationNames.ResumedIntoWhere(2));
    }

    [Fact]
    public void LongerChainsNumberTheirPartsFromWhereTheyAreSeen()
    {
        // The chain: [part 1, part 2, part 3, desktop session].
        Assert.Equal("续接前 第 1 段", ContinuationNames.Of(0, at: 3, count: 4));
        Assert.Equal("续接前 第 2 段", ContinuationNames.Of(1, at: 2, count: 4));
        Assert.Equal("续接后 第 3 段", ContinuationNames.Of(2, at: 1, count: 4));
        Assert.Equal("续接后的会话", ContinuationNames.Of(3, at: 0, count: 4));
        Assert.Equal("这条会话在 Claude 里续接过 3 次：更早的对话在另外 3 份记录里", ContinuationNames.Where(3, 4));
        Assert.Equal("这是一条会话续接前的第 2 段（共 3 段）", ContinuationNames.Where(1, 4));
    }
}
