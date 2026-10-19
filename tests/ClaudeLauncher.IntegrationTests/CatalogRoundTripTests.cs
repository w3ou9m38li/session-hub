using ClaudeLauncher.Core.Contracts;
using ClaudeLauncher.Infrastructure.Catalog;

namespace ClaudeLauncher.IntegrationTests;

public sealed class CatalogRoundTripTests : IDisposable
{
    private readonly string _dir;
    private readonly SqliteCatalog _catalog;

    public CatalogRoundTripTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "launcher-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
        _catalog = new SqliteCatalog(Path.Combine(_dir, "catalog.sqlite"));
        _catalog.Initialize(Schema());
    }

    private static string Schema()
    {
        // Ships next to the test assembly via the Infrastructure project's content item.
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

    private static SessionMetadata Row(int i, DateTimeOffset? when, Visibility visibility = Visibility.Active) => new()
    {
        Key = new ConversationKey(ConversationKey.CliCode, "acc", "org", $"session-{i:D5}"),
        Title = $"会话 {i}",
        SearchKey = $"会话 {i}",
        SourceRevision = "1:1",
        LastActivityUtc = when,
        Visibility = visibility,
        NativeArchiveState = NativeArchiveState.Unsupported,
        SourceCoverage = SourceCoverage.Resolved,
    };

    [Fact]
    public void PagingCoversEveryRowExactlyOnceWithIdenticalTimestamps()
    {
        // Identical timestamps are the case where a naive time-only cursor loses or repeats rows.
        var when = new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
        _catalog.UpsertRange(Enumerable.Range(0, 1001).Select(i => Row(i, when)));

        var seen = new List<string>();
        PageCursor? cursor = null;
        do
        {
            var page = _catalog.Page(Visibility.Active, null, cursor, limit: 100);
            Assert.Equal(PageOutcome.Ok, page.Outcome);
            seen.AddRange(page.Rows.Select(r => r.Key.NativeSessionId));
            cursor = page.Next;
        } while (cursor is not null);

        Assert.Equal(1001, seen.Count);
        Assert.Equal(1001, seen.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void RowsWithNoTimestampAreStillReachable()
    {
        _catalog.UpsertRange([
            Row(1, new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero)),
            Row(2, null),
        ]);

        var all = new List<SessionMetadata>();
        PageCursor? cursor = null;
        do
        {
            var page = _catalog.Page(Visibility.Active, null, cursor, limit: 1);
            all.AddRange(page.Rows);
            cursor = page.Next;
        } while (cursor is not null);

        Assert.Equal(2, all.Count);
        Assert.Contains(all, r => r.LastActivityUtc is null);
    }

    [Fact]
    public void ConcurrentSyncExpiresAnOutstandingCursor()
    {
        var when = DateTimeOffset.UtcNow;
        _catalog.UpsertRange(Enumerable.Range(0, 150).Select(i => Row(i, when)));

        var first = _catalog.Page(Visibility.Active, null, null, limit: 100);
        Assert.NotNull(first.Next);

        // A sync lands between pages.
        _catalog.UpsertRange([Row(999, when)]);

        var second = _catalog.Page(Visibility.Active, null, first.Next, limit: 100);
        Assert.Equal(PageOutcome.CursorExpired, second.Outcome);
        Assert.Empty(second.Rows);   // never a stitched page
    }

    [Fact]
    public void ArchiveMovesTheRowBetweenViewsAndSurvivesAReread()
    {
        var row = Row(1, DateTimeOffset.UtcNow);
        _catalog.UpsertRange([row]);

        var result = _catalog.SetVisibility(row.Key, Visibility.ArchivedView, 0, "op-1");
        Assert.Equal(Core.Catalog.ViewArchiveOutcome.Applied, result.Outcome);

        Assert.Empty(_catalog.Page(Visibility.Active).Rows);
        var archived = _catalog.Page(Visibility.ArchivedView).Rows;
        Assert.Single(archived);
        Assert.Equal(NativeArchiveState.Unsupported, archived[0].NativeArchiveState);
    }

    [Fact]
    public void ASelectionIsArchivedInOneWriteWithEachRowJudgedOnItsOwn()
    {
        var rows = Enumerable.Range(1, 4).Select(i => Row(i, DateTimeOffset.UtcNow)).ToList();
        _catalog.UpsertRange(rows);
        _catalog.SetVisibility(rows[1].Key, Visibility.ArchivedView, 0, "op-earlier");
        var before = _catalog.CurrentRevision();
        var removed = Row(99, null).Key;                    // a sync removed it since it was listed

        var changes = _catalog.SetVisibility([
            (rows[0].Key, 0L, "op-a"),
            (rows[1].Key, 1L, "op-b"),                      // already archived
            (rows[2].Key, 5L, "op-c"),                      // changed since the caller read it
            (removed, 0L, "op-d"),
            (rows[3].Key, 0L, "op-e"),
        ], Visibility.ArchivedView);

        Assert.Equal(
            [Core.Catalog.ViewArchiveOutcome.Applied, Core.Catalog.ViewArchiveOutcome.AlreadyApplied,
             Core.Catalog.ViewArchiveOutcome.Conflict, Core.Catalog.ViewArchiveOutcome.Conflict,
             Core.Catalog.ViewArchiveOutcome.Applied],
            changes.Select(c => c.Outcome));
        Assert.Equal(before + 1, _catalog.CurrentRevision());     // one write for the whole selection
        Assert.Equal(["session-00001", "session-00002", "session-00004"],
            _catalog.Page(Visibility.ArchivedView).Rows.Select(r => r.Key.NativeSessionId).Order());

        // The undo goes by the revisions the write produced, again in one write.
        var applied = changes.Where(c => c.Outcome == Core.Catalog.ViewArchiveOutcome.Applied).ToList();
        var undone = _catalog.SetVisibility([.. applied.Select(c => (c.Key, c.Revision, "undo-" + c.Key.NativeSessionId))],
            Visibility.Active);
        Assert.All(undone, c => Assert.Equal(Core.Catalog.ViewArchiveOutcome.Applied, c.Outcome));
        Assert.Equal(before + 2, _catalog.CurrentRevision());
        Assert.Equal(["session-00002"], _catalog.Page(Visibility.ArchivedView).Rows.Select(r => r.Key.NativeSessionId));

        // An operation id seen before is not acted on twice, in a batch as alone.
        Assert.Equal(Core.Catalog.ViewArchiveOutcome.AlreadyApplied,
            Assert.Single(_catalog.SetVisibility([(rows[0].Key, 2L, "op-a")], Visibility.ArchivedView)).Outcome);
        Assert.Equal(before + 2, _catalog.CurrentRevision());
    }

    [Fact]
    public void ReplayingAnOperationIdDoesNotActTwice()
    {
        var row = Row(1, DateTimeOffset.UtcNow);
        _catalog.UpsertRange([row]);

        _catalog.SetVisibility(row.Key, Visibility.ArchivedView, 0, "op-same");
        var replay = _catalog.SetVisibility(row.Key, Visibility.Active, 1, "op-same");

        Assert.Equal(Core.Catalog.ViewArchiveOutcome.AlreadyApplied, replay.Outcome);
        Assert.Single(_catalog.Page(Visibility.ArchivedView).Rows);   // still archived
    }

    [Fact]
    public void StaleRevisionDoesNotOverwrite()
    {
        var row = Row(1, DateTimeOffset.UtcNow);
        _catalog.UpsertRange([row]);
        _catalog.SetVisibility(row.Key, Visibility.ArchivedView, 0, "op-1");

        var stale = _catalog.SetVisibility(row.Key, Visibility.Active, 0, "op-2");
        Assert.Equal(Core.Catalog.ViewArchiveOutcome.Conflict, stale.Outcome);
    }

    [Fact]
    public void SearchTreatsWildcardsAsLiterals()
    {
        _catalog.UpsertRange([
            Row(1, DateTimeOffset.UtcNow) with { Title = "100% 覆盖", SearchKey = "100% 覆盖" },
            Row(2, DateTimeOffset.UtcNow) with { Title = "其他", SearchKey = "其他" },
        ]);

        // '%' must match itself, not "anything".
        var hits = _catalog.Page(Visibility.Active, "100%").Rows;
        Assert.Single(hits);
        Assert.Equal("100% 覆盖", hits[0].Title);
    }

    [Fact]
    public void ChineseSubstringSearchWorks()
    {
        _catalog.UpsertRange([
            Row(1, DateTimeOffset.UtcNow) with { Title = "提取该pdf中的病级", SearchKey = "提取该pdf中的病级" },
            Row(2, DateTimeOffset.UtcNow) with { Title = "无关会话", SearchKey = "无关会话" },
        ]);

        Assert.Single(_catalog.Page(Visibility.Active, "病级").Rows);
    }

    // ------------------------------------------------------------ opening

    private static readonly byte[] NotADatabase = System.Text.Encoding.ASCII.GetBytes(new string('x', 8192));

    private static string[] Beside(string path) =>
        [.. Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*").Select(f => Path.GetFileName(f)).Order()];

    [Fact]
    public void ADamagedCatalogIsMovedAsideAndRebuilt()
    {
        var path = Path.Combine(Directory.CreateDirectory(Path.Combine(_dir, "damaged")).FullName, "catalog.sqlite");
        File.WriteAllBytes(path, NotADatabase);

        using (var rebuilt = SqliteCatalog.OpenOrRebuild(path, Schema(), out var rebuiltFrom))
        {
            Assert.NotNull(rebuiltFrom);
            Assert.Equal(NotADatabase, File.ReadAllBytes(rebuiltFrom));
            Assert.True(rebuilt.IsEmpty());
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Fact]
    public void ACatalogThatCannotBeWrittenIsLeftAsItIsNotRebuilt()
    {
        // Read-only here; a full disk or a file held by another program fails the same way.
        var path = Path.Combine(Directory.CreateDirectory(Path.Combine(_dir, "readonly")).FullName, "catalog.sqlite");
        using (var catalog = SqliteCatalog.OpenOrRebuild(path, Schema(), out _))
            catalog.UpsertRange([Row(1, DateTimeOffset.UtcNow)]);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var bytes = File.ReadAllBytes(path);

        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            Assert.Throws<CatalogUnavailableException>(() => SqliteCatalog.OpenOrRebuild(path, Schema(), out _));
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Assert.DoesNotContain(Beside(path), f => f.Contains(".damaged-", StringComparison.Ordinal));   // nothing moved aside
            Assert.Equal(bytes, File.ReadAllBytes(path));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }

        // Once it can be written again, it opens with everything in it.
        using (var again = SqliteCatalog.OpenOrRebuild(path, Schema(), out var rebuiltFrom))
        {
            Assert.Null(rebuiltFrom);
            Assert.Single(again.Page(Visibility.Active).Rows);
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Fact]
    public void ARebuildThatFailsPutsTheDamagedFileBack()
    {
        var path = Path.Combine(Directory.CreateDirectory(Path.Combine(_dir, "rebuild")).FullName, "catalog.sqlite");
        File.WriteAllBytes(path, NotADatabase);

        // The rebuild cannot finish — on a full disk it could not even write an empty catalog.
        Assert.Throws<CatalogUnavailableException>(() => SqliteCatalog.OpenOrRebuild(path, "NOT SQL AT ALL;", out _));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        // Back where it was, alone: no empty catalog for the next start to open unannounced.
        Assert.Equal(["catalog.sqlite"], Beside(path));
        Assert.Equal(NotADatabase, File.ReadAllBytes(path));
    }

    public void Dispose()
    {
        _catalog.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
    }
}
