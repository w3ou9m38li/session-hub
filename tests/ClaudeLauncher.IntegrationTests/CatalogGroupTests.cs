using ClaudeLauncher.Core.Catalog;
using ClaudeLauncher.Core.Contracts;
using ClaudeLauncher.Infrastructure.Catalog;

namespace ClaudeLauncher.IntegrationTests;

/// <summary>
/// Grouping is the one thing in the catalog the user builds by hand, so the failures that
/// matter are losing it — to a sync, to a stale write, to a filter that quietly drops rows.
/// </summary>
public sealed class CatalogGroupTests : IDisposable
{
    private readonly string _dir;
    private readonly SqliteCatalog _catalog;

    public CatalogGroupTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "launcher-groups", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
        _catalog = new SqliteCatalog(Path.Combine(_dir, "catalog.sqlite"));
        _catalog.Initialize(Schema());
    }

    private static string Schema()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "CatalogSchema.sql"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                         "src", "ClaudeLauncher.Infrastructure", "Catalog", "CatalogSchema.sql"),
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return File.ReadAllText(c);
        throw new FileNotFoundException("CatalogSchema.sql not found next to the tests.");
    }

    private static SessionMetadata Row(int i, string? project = null, string? sidebarGroup = null) => new()
    {
        Key = new ConversationKey(ConversationKey.CliCode, "acc", "org", $"session-{i:D5}"),
        Title = $"会话 {i}",
        SearchKey = $"会话 {i}",
        SourceRevision = "1:1",
        ProjectRef = project,
        SidebarGroup = sidebarGroup,
        LastActivityUtc = new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero).AddMinutes(i),
        NativeArchiveState = NativeArchiveState.Unsupported,
        SourceCoverage = SourceCoverage.Resolved,
    };

    private SessionMetadata Reload(SessionMetadata row) =>
        _catalog.Page(Visibility.Active).Rows.Single(r => r.Key == row.Key);

    [Fact]
    public void AGroupSurvivesTheNextSync()
    {
        // The whole point of the column: re-reading Claude's files must not undo the user's work.
        var row = Row(1);
        _catalog.UpsertRange([row]);
        _catalog.AssignGroup([row], "小麦");

        _catalog.UpsertRange([row with { Title = "改过的标题" }]);

        var after = Reload(row);
        Assert.Equal("小麦", after.LocalGroup);
        Assert.Equal("改过的标题", after.Title);
    }

    [Fact]
    public void FilteringByGroupReturnsOnlyThatGroup()
    {
        var a = Row(1);
        var b = Row(2);
        _catalog.UpsertRange([a, b]);
        _catalog.AssignGroup([a], "小麦");

        var hits = _catalog.Page(Visibility.Active, null, null, 100, GroupFilter.Named("小麦")).Rows;
        Assert.Single(hits);
        Assert.Equal(a.Key, hits[0].Key);
    }

    [Fact]
    public void ClaudesOwnGroupShowsThroughUntilTheUserOverridesIt()
    {
        var row = Row(1, sidebarGroup: "claude 与 ch");
        _catalog.UpsertRange([row]);

        Assert.Single(_catalog.Page(Visibility.Active, null, null, 100, GroupFilter.Named("claude 与 ch")).Rows);

        _catalog.AssignGroup([Reload(row)], "我的");
        Assert.Empty(_catalog.Page(Visibility.Active, null, null, 100, GroupFilter.Named("claude 与 ch")).Rows);
        Assert.Single(_catalog.Page(Visibility.Active, null, null, 100, GroupFilter.Named("我的")).Rows);
    }

    [Fact]
    public void ClearingAGroupAlsoRemovesAClaudeOne()
    {
        var row = Row(1, sidebarGroup: "claude 与 ch");
        _catalog.UpsertRange([row]);

        _catalog.ClearGroup([row]);

        Assert.Single(_catalog.Page(Visibility.Active, null, null, 100, GroupFilter.Ungrouped).Rows);
        Assert.Equal(GroupRules.ExplicitlyUngrouped, Reload(row).LocalGroup);
    }

    [Fact]
    public void GroupsTallyEveryBucketIncludingTheUngroupedOne()
    {
        _catalog.UpsertRange([Row(1), Row(2), Row(3, sidebarGroup: "Claude 组")]);
        _catalog.AssignGroup([Row(1)], "小麦");

        var tallies = _catalog.Groups(Visibility.Active);

        Assert.Equal(1, tallies.Single(t => t.Name == "小麦").Count);
        Assert.Equal(1, tallies.Single(t => t.Name == "Claude 组").Count);
        Assert.Equal(1, tallies.Single(t => t.Name is null).Count);
    }

    [Fact]
    public void AutoGroupingNamesEachConversationAfterItsWorkingDirectory()
    {
        _catalog.UpsertRange([
            Row(1, project: @"G:\wheat GWAS 2"),
            Row(2, project: @"G:\wheat GWAS 2"),
            Row(3, project: @"D:\ClaudeLauncher"),
        ]);

        var result = _catalog.GroupByProject(Visibility.Active);

        Assert.Equal(3, result.Applied);
        Assert.Equal(2, _catalog.Groups(Visibility.Active).Single(t => t.Name == "wheat GWAS 2").Count);
        Assert.Equal(1, _catalog.Groups(Visibility.Active).Single(t => t.Name == "ClaudeLauncher").Count);
    }

    [Fact]
    public void AutoGroupingLeavesEveryChoiceTheUserAlreadyMade()
    {
        var mine = Row(1, project: @"G:\wheat GWAS 2");
        var removed = Row(2, project: @"G:\wheat GWAS 2");
        var claudes = Row(3, project: @"G:\wheat GWAS 2", sidebarGroup: "Claude 组");
        _catalog.UpsertRange([mine, removed, claudes]);
        _catalog.AssignGroup([mine], "我的");
        _catalog.ClearGroup([removed]);

        var result = _catalog.GroupByProject(Visibility.Active);

        Assert.Equal(0, result.Applied);
        Assert.Equal("我的", Reload(mine).LocalGroup);
        Assert.Equal(GroupRules.ExplicitlyUngrouped, Reload(removed).LocalGroup);
        Assert.Null(Reload(claudes).LocalGroup);
    }

    [Fact]
    public void AConversationWithNoWorkingDirectoryIsCountedRatherThanGroupedBlank()
    {
        _catalog.UpsertRange([Row(1, project: null)]);

        var result = _catalog.GroupByProject(Visibility.Active);

        Assert.Equal(0, result.Applied);
        Assert.Equal(1, result.Unchanged);
        Assert.Null(Reload(Row(1)).LocalGroup);
    }

    [Fact]
    public void AStaleWriteDoesNotOverwriteANewerGroup()
    {
        var row = Row(1);
        _catalog.UpsertRange([row]);
        _catalog.AssignGroup([row], "先写的");

        // Same stale copy of the row, which no longer carries the current revision.
        var stale = _catalog.AssignGroup([row], "后写的");

        Assert.Equal(1, stale.Conflict);
        Assert.Equal("先写的", Reload(row).LocalGroup);
    }

    [Fact]
    public void RegroupingIntoTheSameGroupIsANoOp()
    {
        var row = Row(1);
        _catalog.UpsertRange([row]);
        _catalog.AssignGroup([row], "小麦");

        var again = _catalog.AssignGroup([Reload(row)], "小麦");

        Assert.Equal(0, again.Applied);
        Assert.Equal(1, again.Unchanged);
    }

    [Fact]
    public void ACursorFromOneGroupCannotBeUsedUnderAnother()
    {
        _catalog.UpsertRange(Enumerable.Range(0, 30).Select(i => Row(i)));
        _catalog.AssignGroup(_catalog.Page(Visibility.Active).Rows.Take(20), "小麦");

        var first = _catalog.Page(Visibility.Active, null, null, 10, GroupFilter.Named("小麦"));
        Assert.NotNull(first.Next);

        var crossed = _catalog.Page(Visibility.Active, null, first.Next, 10, GroupFilter.All);
        Assert.Equal(PageOutcome.CursorExpired, crossed.Outcome);
        Assert.Empty(crossed.Rows);
    }

    [Fact]
    public void AppFilterRestrictsRowsAndGroupCounts()
    {
        var claude = Row(1);
        var codex = Row(2) with { Key = new ConversationKey(ConversationKey.Codex, "home", "default", "thread-1") };
        _catalog.UpsertRange([claude, codex]);
        _catalog.AssignGroup([codex], "Codex 的组");

        Assert.Single(_catalog.Page(Visibility.Active, null, null, 100, GroupFilter.All, BackendFilter.Codex).Rows);
        Assert.Single(_catalog.Page(Visibility.Active, null, null, 100, GroupFilter.All, BackendFilter.Claude).Rows);
        Assert.Equal(2, _catalog.Page(Visibility.Active, null, null, 100, GroupFilter.All, BackendFilter.All).Rows.Count);

        var codexGroups = _catalog.Groups(Visibility.Active, BackendFilter.Codex);
        Assert.Equal(1, codexGroups.Single(t => t.Name == "Codex 的组").Count);
        Assert.DoesNotContain(codexGroups, t => t.Name is null);

        // A cursor taken under one app must not be stitched onto another.
        _catalog.UpsertRange(Enumerable.Range(10, 30).Select(i => Row(i)));
        var first = _catalog.Page(Visibility.Active, null, null, 10, GroupFilter.All, BackendFilter.Claude);
        Assert.NotNull(first.Next);
        Assert.Equal(PageOutcome.CursorExpired,
            _catalog.Page(Visibility.Active, null, first.Next, 10, GroupFilter.All, BackendFilter.All).Outcome);
    }

    [Fact]
    public void PagingInsideOneGroupReachesEveryRowExactlyOnce()
    {
        _catalog.UpsertRange(Enumerable.Range(0, 25).Select(i => Row(i)));
        _catalog.AssignGroup(_catalog.Page(Visibility.Active).Rows, "小麦");

        var seen = new List<string>();
        PageCursor? cursor = null;
        do
        {
            var page = _catalog.Page(Visibility.Active, null, cursor, 10, GroupFilter.Named("小麦"));
            Assert.Equal(PageOutcome.Ok, page.Outcome);
            seen.AddRange(page.Rows.Select(r => r.Key.NativeSessionId));
            cursor = page.Next;
        } while (cursor is not null);

        Assert.Equal(25, seen.Distinct(StringComparer.Ordinal).Count());
    }

    public void Dispose()
    {
        _catalog.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
    }
}
