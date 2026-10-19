using ClaudeLauncher.Core.Contracts;
using ClaudeLauncher.Infrastructure.Backends;
using ClaudeLauncher.Infrastructure.Catalog;
using ClaudeLauncher.Infrastructure.Discovery;

namespace ClaudeLauncher.IntegrationTests;

/// <summary>What is read from Claude's own record of a desktop session, file by file.</summary>
public sealed class DesktopRecordTests : IDisposable
{
    private readonly string _profile = Path.Combine(Path.GetTempPath(), "launcher-desktop", Guid.NewGuid().ToString("n"));
    private readonly string _store;

    public DesktopRecordTests()
    {
        _store = Directory.CreateDirectory(Path.Combine(_profile, "claude-code-sessions", "acc", "org")).FullName;
    }

    private void Write(string sessionId, string json) =>
        File.WriteAllText(Path.Combine(_store, sessionId + ".json"), json);

    [Fact]
    public void TheTranscriptsASessionRanOnBeforeAreRead()
    {
        Write("local_1", """
            { "sessionId": "local_1", "cliSessionId": "cli-new", "title": "项目完善",
              "priorCliSessionIds": ["cli-old", "", 7, "cli-older"] }
            """);
        Write("local_2", """{ "sessionId": "local_2", "cliSessionId": "cli-2", "title": "没续接过" }""");
        Write("local_3", """{ "sessionId": "local_3", "title": "形状不对", "priorCliSessionIds": "cli-x" }""");

        var rows = new DesktopCodeMetadataReader(_profile).Read().ToDictionary(r => r.Key.NativeSessionId);

        // Strings only: an empty entry or one of another type is not a transcript id.
        Assert.Equal(["cli-old", "cli-older"], rows["local_1"].PriorCliSessionIds);
        Assert.Empty(rows["local_2"].PriorCliSessionIds);
        Assert.Empty(rows["local_3"].PriorCliSessionIds);
        Assert.Equal(ConversationKey.DesktopCode, rows["local_1"].Key.Backend);
    }

    [Fact]
    public void ARecordThatCannotBeReadIsCountedButOneThatIsNoConversationIsNot()
    {
        Write("local_1", """{ "sessionId": "local_1", "title": "读得到" }""");
        Write("local_2", """{ "sessionId": "local_2", "title": "写到一""");      // caught mid-rewrite
        Write("local_3", """{ "title": "没有会话 id" }""");

        var reader = new DesktopCodeMetadataReader(_profile);
        Assert.Equal(["local_1"], reader.Read().Select(r => r.Key.NativeSessionId));
        Assert.Equal(1, reader.Unreadable);
    }

    [Fact]
    public void ARecordClaudeIsReplacingIsStillRead()
    {
        Write("local_1", """{ "sessionId": "local_1", "title": "甲" }""");

        // Held the way Claude holds it while it renames a new copy over it or deletes it: reading
        // must not stand in its way, nor fail because of it.
        using (new FileStream(Path.Combine(_store, "local_1.json"), FileMode.Open, FileAccess.ReadWrite,
                   FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.DeleteOnClose))
        {
            var reader = new DesktopCodeMetadataReader(_profile);
            Assert.Equal(["local_1"], reader.Read().Select(r => r.Key.NativeSessionId));
            Assert.Equal(0, reader.Unreadable);
        }
    }

    [Fact]
    public void ASessionWhoseRecordCannotBeReadThisTimeIsNotMarkedGone()
    {
        Write("local_1", """{ "sessionId": "local_1", "cliSessionId": "cli-1", "title": "甲" }""");
        Write("local_2", """{ "sessionId": "local_2", "cliSessionId": "cli-2", "title": "乙" }""");
        using (var catalog = new SqliteCatalog(Path.Combine(_profile, "catalog.sqlite")))
        {
            catalog.Initialize(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "CatalogSchema.sql")));
            var sync = new CatalogSyncService(catalog, Path.Combine(_profile, "config"));
            var claude = new ClaudeInstallation { Found = true, ProfileRoot = _profile };
            // An empty Codex home: the test must never read this machine's own Codex data.
            var codex = CodexPaths.FromHome(Path.Combine(_profile, "codex"));
            Assert.Equal(2, sync.Sync(claude, codex).RowsChanged);

            // Claude is rewriting the record of the session in use.
            Write("local_2", """{ "sessionId": "local_2", "cliSess""");
            var result = sync.Sync(claude, codex);

            Assert.Equal(0, result.RowsRemoved);
            Assert.All(catalog.Page(Visibility.Active, null, null, 10).Rows, r => Assert.Null(r.SourceGoneUtc));
            Assert.Equal(SourceCoverage.Partial,
                result.Coverage.Sources.Single(s => s.Backend == ConversationKey.DesktopCode).Coverage);
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    public void Dispose()
    {
        try { Directory.Delete(_profile, recursive: true); } catch { /* temp dir */ }
    }
}
