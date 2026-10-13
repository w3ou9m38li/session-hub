using System.Text;
using ClaudeLauncher.Core.Contracts;
using ClaudeLauncher.Infrastructure.Backends;
using ClaudeLauncher.Infrastructure.Catalog;
using ClaudeLauncher.Infrastructure.Discovery;

namespace ClaudeLauncher.IntegrationTests;

/// <summary>
/// The list now refreshes itself in the background, so a sync that finds nothing new must
/// cost next to nothing and change nothing: no transcript reopened, no row rewritten, no
/// paging cursor invalidated.
/// </summary>
public sealed class IncrementalSyncTests : IDisposable
{
    private static readonly DateTime Written = new(2026, 9, 20, 8, 0, 0, DateTimeKind.Utc);

    private readonly string _dir;
    private readonly string _config;
    private readonly string _project;
    private readonly SqliteCatalog _catalog;

    public IncrementalSyncTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "launcher-incremental", Guid.NewGuid().ToString("n"));
        _config = Path.Combine(_dir, "claude");
        _project = Path.Combine(_config, "projects", "G--demo");
        Directory.CreateDirectory(_project);
        _catalog = new SqliteCatalog(Path.Combine(_dir, "catalog.sqlite"));
        _catalog.Initialize(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "CatalogSchema.sql")));
    }

    private string Transcript(string id, string title, DateTime? written = null)
    {
        var path = Path.Combine(_project, id + ".jsonl");
        File.WriteAllText(path,
            "{\"type\":\"custom-title\",\"customTitle\":\"" + title + "\",\"cwd\":\"G:\\\\demo\"}\n",
            new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(path, written ?? Written);
        return path;
    }

    private List<SessionMetadata> Read(CliProbeCache cache) =>
        new CliMetadataReader(_config, cache: cache).Read("acc", "org").ToList();

    [Fact]
    public void AnUnchangedTranscriptIsNotOpenedAgain()
    {
        var cache = new CliProbeCache();
        Transcript("11111111-0000-0000-0000-000000000001", "标题甲");
        Assert.Equal("标题甲", Read(cache).Single().Title);

        // Same length, same write time, different bytes: only reopening the file could notice.
        Transcript("11111111-0000-0000-0000-000000000001", "标题乙");
        Assert.Equal("标题甲", Read(cache).Single().Title);
        Assert.Equal("标题乙", Read(new CliProbeCache()).Single().Title);
    }

    [Fact]
    public void AChangedTranscriptIsReadAgain()
    {
        var cache = new CliProbeCache();
        Transcript("22222222-0000-0000-0000-000000000002", "旧标题");
        Assert.Equal("旧标题", Read(cache).Single().Title);

        Transcript("22222222-0000-0000-0000-000000000002", "新的标题", Written.AddMinutes(5));
        Assert.Equal("新的标题", Read(cache).Single().Title);
    }

    [Fact]
    public void ADeletedTranscriptIsForgotten()
    {
        var cache = new CliProbeCache();
        var gone = Transcript("33333333-0000-0000-0000-000000000003", "甲的标题");
        Transcript("44444444-0000-0000-0000-000000000004", "乙的标题");
        Read(cache);
        Assert.Equal(2, cache.Count);

        File.Delete(gone);
        Assert.Single(Read(cache));
        Assert.Equal(1, cache.Count);
    }

    private static SessionMetadata Row(int i) => new()
    {
        Key = new ConversationKey(ConversationKey.CliCode, "acc", "org", $"s-{i:D2}"),
        Title = $"标题 {i}",
        SearchKey = $"标题 {i}",
        ProjectRef = @"G:\demo",
        SourceRevision = $"{i}:1",
        LastActivityUtc = new DateTimeOffset(Written).AddMinutes(i),
        NativeArchiveState = NativeArchiveState.Unsupported,
        SourceCoverage = SourceCoverage.Resolved,
    };

    [Fact]
    public void RewritingIdenticalRowsChangesNothing()
    {
        var rows = Enumerable.Range(0, 30).Select(Row).ToList();
        Assert.Equal(30, _catalog.UpsertRange(rows));
        var revision = _catalog.CurrentRevision();
        var first = _catalog.Page(Visibility.Active, null, null, 10);

        Assert.Equal(0, _catalog.UpsertRange(rows));
        Assert.Equal(revision, _catalog.CurrentRevision());
        // A cursor handed out before an idle sync still pages on.
        Assert.Equal(PageOutcome.Ok, _catalog.Page(Visibility.Active, null, first.Next, 10).Outcome);

        Assert.Equal(1, _catalog.UpsertRange([rows[3] with { Title = "改过的标题" }]));
        Assert.Equal(revision + 1, _catalog.CurrentRevision());
    }

    [Fact]
    public void ASyncOfUnchangedSourcesReportsNoChange()
    {
        Transcript("55555555-0000-0000-0000-000000000005", "甲的标题");
        var stays = Transcript("66666666-0000-0000-0000-000000000006", "乙的标题");
        var sync = new CatalogSyncService(_catalog, _config);
        var claude = new ClaudeInstallation { Found = false };
        // An empty Codex home: the test must never read this machine's own Codex data.
        var codex = CodexPaths.FromHome(Path.Combine(_dir, "codex"));

        var first = sync.Sync(claude, codex);
        Assert.Equal(2, first.RowsChanged);

        var idle = sync.Sync(claude, codex);
        Assert.False(idle.ChangedAnything);

        // Deleted by Claude: still listed, now marked as gone from it.
        File.Delete(stays);
        var removed = sync.Sync(claude, codex);
        Assert.Equal(0, removed.RowsChanged);
        Assert.Equal(1, removed.RowsRemoved);
        var rows = _catalog.Page(Visibility.Active, null, null, 10).Rows;
        Assert.Equal(2, rows.Count);
        Assert.Single(rows, r => r.SourceGoneUtc is not null);
    }

    [Fact]
    public void ReadingTheSourcesLeavesTheCatalogAloneUntilTheReadIsApplied()
    {
        // The list goes on being paged and searched while the apps' files are read: only the writing waits for it.
        Transcript("77777777-0000-0000-0000-000000000007", "丙的标题");
        var sync = new CatalogSyncService(_catalog, _config);
        var revision = _catalog.CurrentRevision();

        var read = sync.Read(new ClaudeInstallation { Found = false }, CodexPaths.FromHome(Path.Combine(_dir, "codex")));

        Assert.Single(read.Rows);
        Assert.Equal(revision, _catalog.CurrentRevision());
        Assert.Empty(_catalog.Page(Visibility.Active, null, null, 10).Rows);

        Assert.Equal(1, sync.Apply(read).RowsChanged);
        Assert.Single(_catalog.Page(Visibility.Active, null, null, 10).Rows);
    }

    public void Dispose()
    {
        _catalog.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
    }
}
