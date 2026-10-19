using System.Diagnostics;
using System.Text;
using ClaudeLauncher.Core.Contracts;
using ClaudeLauncher.Infrastructure.Catalog;
using ClaudeLauncher.Infrastructure.Search;
using Microsoft.Data.Sqlite;

namespace ClaudeLauncher.IntegrationTests;

/// <summary>
/// A process beside the window — an agent's server, a worker — reads the catalog the window keeps
/// writing. Everything the list reads works on a read-only connection, nothing it does can write,
/// and the window never waits for it.
/// </summary>
public sealed class CatalogReadOnlyTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;
    private readonly string _schema;
    private readonly SqliteCatalog _window;

    public CatalogReadOnlyTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "launcher-readonly", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "catalog.sqlite");
        _schema = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "CatalogSchema.sql"));
        _window = SqliteCatalog.OpenOrRebuild(_path, _schema, out _);
    }

    private static SessionMetadata Row(string id, string title, int hoursAgo = 0, string backend = ConversationKey.CliCode,
        string? cli = null) => new()
    {
        Key = new ConversationKey(backend, "acc", "org", id),
        CliSessionId = cli,
        Title = title,
        SearchKey = SearchKeys.Normalize(title),
        SourceRevision = "1:1",
        LastActivityUtc = DateTimeOffset.UtcNow.AddHours(-hoursAgo),
        NativeArchiveState = NativeArchiveState.Unsupported,
        SourceCoverage = SourceCoverage.Resolved,
    };

    private static string User(string text) =>
        "{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"" + text + "\"}]}}";

    private string Transcript(string name, params string[] lines)
    {
        var path = Path.Combine(_dir, name + ".jsonl");
        File.WriteAllText(path, string.Join('\n', lines) + '\n', new UTF8Encoding(false));
        return path;
    }

    private static string[] Ids(CatalogPage page) => [.. page.Rows.Select(r => r.Key.NativeSessionId)];

    [Fact]
    public void EverythingTheListReadsWorksBesideTheWindow()
    {
        _window.UpsertRange([Row("a", "甲 GWAS", 1), Row("b", "乙", 2), Row("c", "丙", 3), Row("d", "丁", 4)]);
        var d = _window.ListedRows().Single(r => r.Key.NativeSessionId == "d");
        _window.SetVisibility(d.Key, Visibility.ArchivedView, d.PreferenceRevision, "op-1");
        _window.AssignGroup(_window.ListedRows().Where(r => r.Key.NativeSessionId is "a" or "b"), "课题");

        using var reader = SqliteCatalog.OpenReadOnly(_path);

        Assert.Equal(Ids(_window.Page(Visibility.Active)), Ids(reader.Page(Visibility.Active)));
        Assert.Equal(["a", "b", "c"], Ids(reader.Page(Visibility.Active)));
        Assert.Equal(["d"], Ids(reader.Page(Visibility.ArchivedView)));
        Assert.Equal(["a"], Ids(reader.Page(Visibility.Active, "gwas")));
        var byTitle = new SortOrder(SortField.Title, false);                                  // through the UI collation
        Assert.Equal(Ids(_window.Page(Visibility.Active, sort: byTitle)), Ids(reader.Page(Visibility.Active, sort: byTitle)));

        // Page after page, as the list pages.
        var first = reader.Page(Visibility.Active, limit: 2);
        Assert.Equal(["a", "b"], Ids(first));
        var second = reader.Page(Visibility.Active, cursor: first.Next, limit: 2);
        Assert.Equal(PageOutcome.Ok, second.Outcome);
        Assert.Equal(["c"], Ids(second));

        Assert.Equal(3, reader.Count(Visibility.Active));
        Assert.Equal(new ViewCounts(3, 1), reader.CountViews());
        Assert.Equal(2, reader.SectionCounts(Visibility.Active, null, GroupFilter.All, BackendFilter.All, SortField.Group)["课题"]);
        Assert.Contains(reader.Groups(Visibility.Active), g => g is { Name: "课题", Count: 2 });
        Assert.Equal(4, reader.ListedRows().Count);
        Assert.False(reader.IsEmpty());
        Assert.Equal(_window.CurrentRevision(), reader.CurrentRevision());
        Assert.Empty(reader.ContinuationChain(d));
        Assert.False(reader.Resumes(d).Any);

        // A write the window makes is seen, and a cursor from before it expires, as in the window.
        _window.UpsertRange([Row("e", "戊")]);
        Assert.Equal(PageOutcome.CursorExpired, reader.Page(Visibility.Active, cursor: first.Next, limit: 2).Outcome);
        Assert.Equal(["e", "a", "b", "c"], Ids(reader.Page(Visibility.Active)));
    }

    [Fact]
    public void WhatWasSaidIsSearchedReadOnlyToo()
    {
        var a = Row("a", "会话一");
        var b = Row("b", "会话二", 1);
        _window.UpsertRange([a, b]);
        var indexPath = Path.Combine(_dir, "search-index.sqlite");
        using var index = new ContentIndex(indexPath);
        index.Update([
            new(ContentIndex.KeyOf(a.Key), Transcript("a", User("KASP 引物怎么设计"))),
            new(ContentIndex.KeyOf(b.Key), Transcript("b", User("病级提取的表格"))),
        ]);

        using var reader = SqliteCatalog.OpenReadOnly(_path);
        reader.AttachContentIndex(indexPath);
        Assert.Equal(["a"], Ids(reader.Page(Visibility.Active, "引物怎么")));
        Assert.Equal(["b"], Ids(reader.Page(Visibility.Active, "病级")));               // two characters: the scan
        Assert.Equal(1, reader.Count(Visibility.Active, "kasp"));
        Assert.Equal(1, reader.ContentHitCounts("kasp")["kasp"][ContentIndex.KeyOf(a.Key)]);
        Assert.Equal(["a"], Ids(reader.Page(Visibility.Active, "kasp", sort: new SortOrder(SortField.Relevance, false))));

        // The window indexes more; told so, the reader looks again.
        index.Update([
            new(ContentIndex.KeyOf(a.Key), Transcript("a", User("KASP 引物怎么设计"))),
            new(ContentIndex.KeyOf(b.Key), Transcript("b", User("病级提取的表格"), User("后来才说的词"))),
        ]);
        reader.ContentIndexChanged();
        Assert.Equal(["b"], Ids(reader.Page(Visibility.Active, "后来才说")));
    }

    [Fact]
    public void AConversationIsFoundByItsKeyOrItsId()
    {
        var older = Row("3f2a9c10", "短的那个", 9);
        var recent = Row("3f2a9c10-aaaa-4bbb-8ccc-000000000001", "最近的", 1, cli: "3f2a9c10-aaaa-4bbb-8ccc-000000000001");
        var earlier = Row("3f2a9c10-aaaa-4bbb-8ccc-000000000002", "早一点的", 2);
        var archived = Row("3f2a9c10-bbbb-4bbb-8ccc-000000000003", "归档的", 3);
        var desktop = Row("local_7e6d", "桌面会话", 4, ConversationKey.DesktopCode, cli: "9d8c7b6a-0000-4000-8000-000000000003");
        var folded = Row("9d8c7b6a-0000-4000-8000-000000000003", "折进去的", 4, cli: "9d8c7b6a-0000-4000-8000-000000000003")
            with { MergedInto = "local_7e6d" };
        _window.UpsertRange([older, recent, earlier, archived, desktop, folded]);
        _window.SetVisibility(archived.Key, Visibility.ArchivedView, 0, "op-1");

        using var reader = SqliteCatalog.OpenReadOnly(_path);
        foreach (var catalog in new[] { _window, reader })
        {
            Assert.Equal("最近的", catalog.Get(recent.Key)?.Title);
            Assert.Equal(Visibility.ArchivedView, catalog.Get(archived.Key)?.Visibility);
            Assert.Equal("local_7e6d", catalog.Get(folded.Key)?.MergedInto);
            Assert.Null(catalog.Get(Row("nowhere", "").Key));

            string[] Found(string id, int limit = 10) => [.. catalog.FindById(id, limit).Select(r => r.Key.NativeSessionId)];

            // The id itself first, then those it begins, latest first — the archived view included.
            Assert.Equal([older.Key.NativeSessionId, recent.Key.NativeSessionId, earlier.Key.NativeSessionId,
                archived.Key.NativeSessionId], Found("3f2a9c10"));
            Assert.Equal([older.Key.NativeSessionId, recent.Key.NativeSessionId], Found("3f2a9c10", limit: 2));
            Assert.Equal([earlier.Key.NativeSessionId], Found(" 3F2A9C10-AAAA-4BBB-8CCC-000000000002 "));
            // A folded transcript is found as the row it is folded into.
            Assert.Equal(["local_7e6d"], Found("9d8c7b6a-0000-4000-8000-000000000003"));
            Assert.Equal(["local_7e6d"], Found("9d8c7b6a"));
            Assert.Equal(["local_7e6d"], Found("local_7e"));
            Assert.Empty(Found("ffff"));
            Assert.Empty(Found("   "));
        }
    }

    [Fact]
    public void NothingCanBeWrittenThroughIt()
    {
        _window.UpsertRange([Row("a", "甲")]);
        var before = _window.CurrentRevision();
        using var reader = SqliteCatalog.OpenReadOnly(_path);
        var row = reader.ListedRows().Single();

        Assert.Equal(8, Assert.Throws<SqliteException>(() => reader.UpsertRange([Row("b", "乙")])).SqliteErrorCode);
        Assert.Throws<SqliteException>(() => reader.SetPinned([row], true));
        Assert.Throws<SqliteException>(() => reader.Delete([row.Key]));
        Assert.Throws<SqliteException>(() => reader.Initialize(_schema));
        Assert.Equal(before, _window.CurrentRevision());
        Assert.Equal(["a"], Ids(_window.Page(Visibility.Active)));
    }

    [Fact]
    public async Task TheWindowWritesOnWhileItReads()
    {
        _window.UpsertRange([.. Enumerable.Range(0, 200).Select(i => Row($"r{i}", $"会话 {i}", i))]);
        using var reader = SqliteCatalog.OpenReadOnly(_path);

        // A window started while the reader is open migrates and checks the catalog as always.
        using (var another = SqliteCatalog.OpenOrRebuild(_path, _schema, out var rebuilt))
            Assert.Null(rebuilt);

        using var stop = new CancellationTokenSource();
        using var started = new SemaphoreSlim(0);
        // A thread of its own, reading the whole time the window writes.
        var reading = Task.Factory.StartNew(() =>
        {
            var reads = 0;
            while (!stop.IsCancellationRequested)
            {
                reader.Page(Visibility.Active, limit: SqliteCatalog.MaxPageSize);
                reader.Count(Visibility.Active, "会话");
                if (reads++ == 0) started.Release();
            }
            return reads;
        }, TaskCreationOptions.LongRunning);
        try
        {
            // Reading before the first write; a reader that cannot read says why at once.
            var up = started.WaitAsync(TimeSpan.FromSeconds(30));
            if (await Task.WhenAny(up, reading) == reading) await reading;
            Assert.True(await up);

            // Each write lands, none refused as busy. No clock: in WAL a read-only reader holds no
            // lock a writer waits on, so how long a write took says only how busy the machine was.
            for (var i = 0; i < 30; i++)
                _window.UpsertRange([Row($"w{i}", $"新会话 {i}")]);
        }
        finally
        {
            stop.Cancel();
        }
        Assert.True(await reading.WaitAsync(TimeSpan.FromSeconds(30)) > 0);

        Assert.Equal(_window.CurrentRevision(), reader.CurrentRevision());
        Assert.Equal(230, reader.Count(Visibility.Active));
        Assert.Equal(230, _window.Count(Visibility.Active));
    }

    [Fact]
    public void AListIsPagedWhileAnotherProcessHoldsTheWriteLock()
    {
        _window.UpsertRange([Row("a", "甲")]);
        using var reader = SqliteCatalog.OpenReadOnly(_path);

        // Another writer in the middle of a write — the window, for the reader; an export run, for the window.
        using var other = new SqliteConnection($"Data Source={_path};Pooling=False");
        other.Open();
        using var write = other.BeginTransaction(deferred: false);
        using (var cmd = other.CreateCommand())
        {
            cmd.CommandText = "UPDATE catalog_version SET revision=revision+1 WHERE singleton_id=1;";
            cmd.ExecuteNonQuery();
        }

        var clock = Stopwatch.StartNew();
        Assert.Equal(["a"], Ids(_window.Page(Visibility.Active)));
        Assert.Equal(["a"], Ids(reader.Page(Visibility.Active)));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"paging waited {clock.Elapsed.TotalMilliseconds:0} ms");
        write.Rollback();
    }

    [Fact]
    public void ItReadsACatalogTheWindowClosedAndNeverMakesOne()
    {
        _window.UpsertRange([Row("a", "甲")]);
        CloseWindow();
        Assert.False(File.Exists(_path + "-wal"));          // the last connection folded its log in

        using (var reader = SqliteCatalog.OpenReadOnly(_path))
            Assert.Equal(["a"], Ids(reader.Page(Visibility.Active)));

        var missing = Path.Combine(_dir, "missing.sqlite");
        Assert.Throws<SqliteException>(() => SqliteCatalog.OpenReadOnly(missing));
        Assert.False(File.Exists(missing));
    }

    /// <summary>
    /// What closing the window does to the file: its connection really closed, not kept in the pool.
    /// Only this catalog's pool — clearing every pool would close other tests' connections under them.
    /// </summary>
    private void CloseWindow()
    {
        _window.Dispose();
        using var samePool = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
        }.ToString());
        SqliteConnection.ClearPool(samePool);
    }

    public void Dispose()
    {
        CloseWindow();
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
    }
}
