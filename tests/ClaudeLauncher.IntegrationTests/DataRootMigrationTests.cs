using System.Text;
using ClaudeLauncher.Core.Contracts;
using ClaudeLauncher.Infrastructure;
using ClaudeLauncher.Infrastructure.Catalog;

namespace ClaudeLauncher.IntegrationTests;

/// <summary>
/// The move carries the user's own catalog — their groups and archive choices. A half-done
/// move that deleted the original would lose all of it, so each guard is pinned here.
/// </summary>
public sealed class DataRootMigrationTests : IDisposable
{
    private readonly string _dir;
    private readonly string _legacy;
    private readonly string _target;

    public DataRootMigrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "launcher-migrate", Guid.NewGuid().ToString("n"));
        _legacy = Path.Combine(_dir, "old");
        _target = Path.Combine(_dir, "new");
        Directory.CreateDirectory(_legacy);
    }

    private void WriteLegacy(string relative, string content)
    {
        var path = Path.Combine(_legacy, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    [Fact]
    public void EverythingIsCarriedOverAndTheOldFolderGoes()
    {
        WriteLegacy("catalog.sqlite", "not really a database");
        WriteLegacy("export/全部对话.csv", "标题\n会话");
        WriteLegacy("memory-drafts/abc.md", "# 草稿");

        var report = DataRootMigrator.Migrate(_legacy, _target);

        Assert.Equal(MigrationOutcome.Moved, report.Outcome);
        Assert.Equal(3, report.Files);
        Assert.True(File.Exists(Path.Combine(_target, "catalog.sqlite")));
        Assert.Equal("标题\n会话", File.ReadAllText(Path.Combine(_target, "export", "全部对话.csv")));
        Assert.False(Directory.Exists(_legacy));
    }

    [Fact]
    public void AnExistingCatalogAtTheDestinationIsNeverMergedInto()
    {
        WriteLegacy("catalog.sqlite", "old");
        Directory.CreateDirectory(_target);
        File.WriteAllText(Path.Combine(_target, "catalog.sqlite"), "already in use");

        var report = DataRootMigrator.Migrate(_legacy, _target);

        Assert.Equal(MigrationOutcome.NothingToDo, report.Outcome);
        Assert.Equal("already in use", File.ReadAllText(Path.Combine(_target, "catalog.sqlite")));
        Assert.True(Directory.Exists(_legacy));          // left for the user to look at
        Assert.NotNull(report.Message);
    }

    [Fact]
    public void ARealCatalogArrivesWithEveryCommitAndNoStaleSidecar()
    {
        // The catalog is written ahead of the database file, so a move that copied only
        // catalog.sqlite could land at the far end missing the user's most recent groups.
        var row = new SessionMetadata
        {
            Key = new ConversationKey(ConversationKey.CliCode, "acc", "org", "session-1"),
            Title = "会话", SearchKey = "会话", SourceRevision = "1:1",
            LastActivityUtc = DateTimeOffset.UtcNow,
            NativeArchiveState = NativeArchiveState.Unsupported,
            SourceCoverage = SourceCoverage.Resolved,
        };
        using (var catalog = new SqliteCatalog(Path.Combine(_legacy, "catalog.sqlite")))
        {
            catalog.Initialize(CatalogSchema());
            catalog.UpsertRange([row]);
            catalog.AssignGroup([row], "小麦");
        }

        var report = DataRootMigrator.Migrate(_legacy, _target);

        Assert.Equal(MigrationOutcome.Moved, report.Outcome);
        Assert.False(File.Exists(Path.Combine(_target, "catalog.sqlite-shm")));

        using var moved = new SqliteCatalog(Path.Combine(_target, "catalog.sqlite"));
        moved.Initialize(CatalogSchema());
        var carried = moved.Page(Visibility.Active).Rows;
        Assert.Single(carried);
        Assert.Equal("小麦", carried[0].LocalGroup);
    }

    private static string CatalogSchema()
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

    [Fact]
    public void NoOldFolderIsNotAFailure()
    {
        var report = DataRootMigrator.Migrate(Path.Combine(_dir, "missing"), _target);
        Assert.Equal(MigrationOutcome.NothingToDo, report.Outcome);
    }

    [Fact]
    public void MigratingOntoItselfDoesNothing()
    {
        WriteLegacy("catalog.sqlite", "db");

        var report = DataRootMigrator.Migrate(_legacy, _legacy + Path.DirectorySeparatorChar);

        Assert.Equal(MigrationOutcome.NothingToDo, report.Outcome);
        Assert.True(File.Exists(Path.Combine(_legacy, "catalog.sqlite")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
    }
}
