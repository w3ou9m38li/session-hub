using ClaudeLauncher.Core.Catalog;
using ClaudeLauncher.Core.Contracts;
using ClaudeLauncher.Infrastructure.Catalog;
using Microsoft.Data.Sqlite;

namespace ClaudeLauncher.IntegrationTests;

/// <summary>
/// Search that reaches working directories and ids, counts that agree with paging, and undo
/// that puts back exactly what an organising action changed — and nothing the user changed
/// after it.
/// </summary>
public sealed class CatalogSearchAndUndoTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;
    private readonly SqliteCatalog _catalog;

    public CatalogSearchAndUndoTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "launcher-search", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "catalog.sqlite");
        _catalog = new SqliteCatalog(_dbPath);
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

    private static SessionMetadata Row(int i, string title, string? project = null, string? id = null,
        string? sidebarGroup = null, string backend = ConversationKey.CliCode) => new()
    {
        Key = new ConversationKey(backend, "acc", "org", id ?? $"session-{i:D5}"),
        Title = title,
        SearchKey = SearchKeys.Normalize(title),
        ProjectRef = project,
        SidebarGroup = sidebarGroup,
        SourceRevision = "1:1",
        LastActivityUtc = new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero).AddMinutes(i),
        NativeArchiveState = NativeArchiveState.Unsupported,
        SourceCoverage = SourceCoverage.Resolved,
    };

    private SessionMetadata Reload(ConversationKey key) =>
        _catalog.Page(Visibility.Active).Rows.Concat(_catalog.Page(Visibility.ArchivedView).Rows)
                .Single(r => r.Key == key);

    private IReadOnlyList<string> Titles(string search) =>
        _catalog.Page(Visibility.Active, search).Rows.Select(r => r.Title).OrderBy(t => t, StringComparer.Ordinal).ToList();

    // ----------------------------------------------------------------- search

    [Fact]
    public void AWorkingDirectoryIsSearchableLikeATitle()
    {
        _catalog.UpsertRange([
            Row(1, "Fix typography", project: @"G:\wheat GWAS 2"),
            Row(2, "Unrelated", project: @"D:\ClaudeLauncher"),
        ]);

        Assert.Equal(["Fix typography"], Titles("wheat"));
        // Case and width folded the same way a title is.
        Assert.Equal(["Fix typography"], Titles("ＧＷＡＳ"));
    }

    [Fact]
    public void EveryWordMustMatchSomewhere()
    {
        _catalog.UpsertRange([
            Row(1, "提取病级数据", project: @"G:\wheat GWAS 2"),
            Row(2, "提取病级数据", project: @"D:\rice"),
            Row(3, "Manhattan plot", project: @"G:\wheat GWAS 2"),
        ]);

        // One word from the title, one from the folder: only the row that has both.
        var hits = _catalog.Page(Visibility.Active, "病级 wheat").Rows;
        Assert.Equal("session-00001", Assert.Single(hits).Key.NativeSessionId);
    }

    [Fact]
    public void AnIdFragmentFindsItsConversation()
    {
        _catalog.UpsertRange([
            Row(1, "One", id: "019eabb7-bfd3-74b1-b30a-31cd21afe5e0"),
            Row(2, "Two", id: "7a1c0e55-0000-4000-8000-000000000000"),
        ]);

        Assert.Equal(["One"], Titles("019eabb7"));
        Assert.Equal(["One"], Titles("019EABB7"));     // pasted in upper case
    }

    [Fact]
    public void ShortWordsDoNotMatchIdsByAccident()
    {
        // "b30" appears inside the id, but a three-letter word is not an id search.
        _catalog.UpsertRange([Row(1, "One", id: "019eabb7-bfd3-74b1-b30a-31cd21afe5e0")]);
        Assert.Empty(Titles("b30"));
    }

    [Fact]
    public void CountAgreesWithEveryPageTheFilterReaches()
    {
        _catalog.UpsertRange(Enumerable.Range(0, 230).Select(i =>
            Row(i, i % 3 == 0 ? $"病级 {i}" : $"其他 {i}", project: i % 2 == 0 ? @"G:\wheat" : @"D:\rice")));

        foreach (var search in new[] { null, "病级", "wheat", "病级 wheat" })
        {
            var paged = 0;
            PageCursor? cursor = null;
            do
            {
                var page = _catalog.Page(Visibility.Active, search, cursor, 50);
                paged += page.Rows.Count;
                cursor = page.Next;
            } while (cursor is not null);

            Assert.Equal(paged, _catalog.Count(Visibility.Active, search));
        }
    }

    [Fact]
    public void ViewCountsSplitActiveFromArchivedPerApp()
    {
        var claude = Row(1, "a");
        var codex = Row(2, "b", backend: ConversationKey.Codex);
        _catalog.UpsertRange([claude, codex, Row(3, "c")]);
        _catalog.SetVisibility(claude.Key, Visibility.ArchivedView, 0, "op-1");

        Assert.Equal(new ViewCounts(2, 1), _catalog.CountViews());
        Assert.Equal(new ViewCounts(1, 1), _catalog.CountViews(BackendFilter.Claude));
        Assert.Equal(new ViewCounts(1, 0), _catalog.CountViews(BackendFilter.Codex));
    }

    [Fact]
    public void RowsWrittenBeforeTheColumnExistedBecomeSearchableOnOpen()
    {
        _catalog.UpsertRange([Row(1, "Fix typography", project: @"G:\Wheat GWAS 2")]);

        // Simulate a row an older build wrote: no project key at all.
        using (var raw = new SqliteConnection($"Data Source={_dbPath};Pooling=False"))
        {
            raw.Open();
            using var cmd = raw.CreateCommand();
            cmd.CommandText = "UPDATE sessions SET project_key=NULL;";
            cmd.ExecuteNonQuery();
        }
        Assert.Empty(Titles("wheat"));

        _catalog.Initialize(Schema());       // what every start does

        Assert.Equal(["Fix typography"], Titles("wheat"));
    }

    // ------------------------------------------------------------------- undo

    [Fact]
    public void AGroupWriteReportsWhatEachRowHeldBefore()
    {
        var mine = Row(1, "a");
        var fresh = Row(2, "b");
        _catalog.UpsertRange([mine, fresh]);
        _catalog.AssignGroup([mine], "旧组");

        var result = _catalog.AssignGroup([Reload(mine.Key), Reload(fresh.Key)], "新组");

        Assert.Equal(2, result.Applied);
        Assert.Equal("旧组", result.Changes.Single(c => c.Key == mine.Key).Before);
        Assert.Null(result.Changes.Single(c => c.Key == fresh.Key).Before);
        Assert.All(result.Changes, c => Assert.Equal("新组", c.After));
    }

    [Fact]
    public void RevertingPutsBackTheExactPreviousStateIncludingNoPreference()
    {
        // No preference means Claude's own group shows through; after an undo it must again.
        var row = Row(1, "a", sidebarGroup: "Claude 组");
        _catalog.UpsertRange([row]);
        var assigned = _catalog.AssignGroup([Reload(row.Key)], "我的");

        var undone = _catalog.RevertGroups(assigned.Changes);

        Assert.Equal(1, undone.Applied);
        var after = Reload(row.Key);
        Assert.Null(after.LocalGroup);
        Assert.Equal("Claude 组", GroupRules.Effective(after.LocalGroup, after.SidebarGroup));
    }

    [Fact]
    public void RevertingAnExplicitRemovalRestoresTheGroup()
    {
        var row = Row(1, "a");
        _catalog.UpsertRange([row]);
        _catalog.AssignGroup([row], "小麦");
        var cleared = _catalog.ClearGroup([Reload(row.Key)]);

        _catalog.RevertGroups(cleared.Changes);

        Assert.Equal("小麦", Reload(row.Key).LocalGroup);
    }

    [Fact]
    public void AnUndoNeverOverwritesAChoiceMadeAfterIt()
    {
        var row = Row(1, "a");
        _catalog.UpsertRange([row]);
        var first = _catalog.AssignGroup([row], "先");
        _catalog.AssignGroup([Reload(row.Key)], "后");       // the user changed it again

        var undone = _catalog.RevertGroups(first.Changes);

        Assert.Equal(0, undone.Applied);
        Assert.Equal(1, undone.Conflict);
        Assert.Equal("后", Reload(row.Key).LocalGroup);
    }

    [Fact]
    public void AutoGroupingCanBeUndoneInOneStep()
    {
        _catalog.UpsertRange([
            Row(1, "a", project: @"G:\wheat GWAS 2"),
            Row(2, "b", project: @"D:\ClaudeLauncher"),
        ]);

        var grouped = _catalog.GroupByProject(Visibility.Active);
        Assert.Equal(2, grouped.Changes.Count);

        _catalog.RevertGroups(grouped.Changes);

        Assert.All(_catalog.Page(Visibility.Active).Rows, r => Assert.Null(r.LocalGroup));
        Assert.Equal(2, _catalog.Groups(Visibility.Active).Single(t => t.Name is null).Count);
    }

    [Fact]
    public void ArchivingIsUndoneWithTheRevisionItProduced()
    {
        var row = Row(1, "a");
        _catalog.UpsertRange([row]);
        var archived = _catalog.SetVisibility(row.Key, Visibility.ArchivedView, 0, "op-archive");

        var undone = _catalog.SetVisibility(row.Key, Visibility.Active, archived.Revision, "op-undo");

        Assert.Equal(ViewArchiveOutcome.Applied, undone.Outcome);
        Assert.Single(_catalog.Page(Visibility.Active).Rows);
    }

    public void Dispose()
    {
        _catalog.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
    }
}
