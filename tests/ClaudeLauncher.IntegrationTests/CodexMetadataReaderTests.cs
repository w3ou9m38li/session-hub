using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClaudeLauncher.Core.Contracts;
using ClaudeLauncher.Infrastructure.Backends;
using Microsoft.Data.Sqlite;

namespace ClaudeLauncher.IntegrationTests;

/// <summary>
/// The Codex reader is the one place this tool touches ChatGPT's data, and it must only ever
/// read it. These tests pin what it lists, what it leaves out, and that the originals come
/// through byte-for-byte unchanged.
/// </summary>
public sealed class CodexMetadataReaderTests : IDisposable
{
    private readonly string _root;
    private readonly CodexPaths _paths;

    public CodexMetadataReaderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "launcher-codex", Guid.NewGuid().ToString("n"));
        _paths = CodexPaths.FromHome(_root);
        Directory.CreateDirectory(Path.Combine(_paths.SessionsRoot, "2026", "09", "22"));
        Directory.CreateDirectory(Path.Combine(_paths.ArchivedSessionsRoot, "2026", "08", "01"));
    }

    private sealed record Thread(
        string Id, string? RolloutPath, string Cwd = @"D:\proj", string Title = "", string? Name = null,
        string Preview = "", string FirstUserMessage = "", bool Archived = false,
        string Source = "vscode", string? ThreadSource = "user", long UpdatedAt = 1_790_000_000,
        long? UpdatedAtMs = null, string? SectionId = null);

    /// <summary>The columns Codex 0.153 writes, minus the ones this reader never looks at.</summary>
    private void CreateDatabase(IEnumerable<Thread> threads, IEnumerable<(string Id, string Name)>? sections = null)
    {
        SqliteConnection.ClearAllPools();
        using var connection = new SqliteConnection($"Data Source={_paths.StateDatabase};Pooling=False");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE threads (
              id TEXT PRIMARY KEY, rollout_path TEXT NOT NULL, created_at INTEGER NOT NULL,
              updated_at INTEGER NOT NULL, source TEXT NOT NULL, model_provider TEXT NOT NULL,
              cwd TEXT NOT NULL, title TEXT NOT NULL, archived INTEGER NOT NULL DEFAULT 0,
              first_user_message TEXT NOT NULL DEFAULT '', updated_at_ms INTEGER,
              thread_source TEXT, preview TEXT NOT NULL DEFAULT '', name TEXT,
              is_pinned INTEGER NOT NULL DEFAULT 0, thread_section_id TEXT);
            CREATE TABLE thread_sections (id TEXT PRIMARY KEY, name TEXT NOT NULL, appearance TEXT);
            """;
        cmd.ExecuteNonQuery();

        foreach (var t in threads)
        {
            cmd.CommandText = """
                INSERT INTO threads (id, rollout_path, created_at, updated_at, source, model_provider, cwd, title,
                                     archived, first_user_message, updated_at_ms, thread_source, preview, name, thread_section_id)
                VALUES ($id, $path, $created, $updated, $source, 'openai', $cwd, $title,
                        $archived, $first, $updatedMs, $threadSource, $preview, $name, $section);
                """;
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("$id", t.Id);
            cmd.Parameters.AddWithValue("$path", t.RolloutPath ?? Path.Combine(_paths.SessionsRoot, "missing.jsonl"));
            cmd.Parameters.AddWithValue("$created", t.UpdatedAt - 100);
            cmd.Parameters.AddWithValue("$updated", t.UpdatedAt);
            cmd.Parameters.AddWithValue("$source", t.Source);
            cmd.Parameters.AddWithValue("$cwd", t.Cwd);
            cmd.Parameters.AddWithValue("$title", t.Title);
            cmd.Parameters.AddWithValue("$archived", t.Archived ? 1 : 0);
            cmd.Parameters.AddWithValue("$first", t.FirstUserMessage);
            cmd.Parameters.AddWithValue("$updatedMs", (object?)t.UpdatedAtMs ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$threadSource", (object?)t.ThreadSource ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$preview", t.Preview);
            cmd.Parameters.AddWithValue("$name", (object?)t.Name ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$section", (object?)t.SectionId ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        foreach (var (id, name) in sections ?? [])
        {
            cmd.CommandText = "INSERT INTO thread_sections VALUES ($id, $name, NULL);";
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$name", name);
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
    }

    private string WriteRollout(string id, bool archived = false, string cwd = @"D:\proj", bool subagent = false)
    {
        var dir = archived
            ? Path.Combine(_paths.ArchivedSessionsRoot, "2026", "08", "01")
            : Path.Combine(_paths.SessionsRoot, "2026", "09", "22");
        var path = Path.Combine(dir, $"rollout-2026-09-22T10-00-00-{id}.jsonl");
        object source = subagent
            ? new { subagent = new { thread_spawn = new { parent_thread_id = Guid.NewGuid().ToString(), depth = 1 } } }
            : "vscode";
        var meta = JsonSerializer.Serialize(new
        {
            timestamp = "2026-09-22T02:00:00.000Z",
            type = "session_meta",
            payload = new { id, cwd, source, thread_source = subagent ? "subagent" : "user" },
        });
        var message = """{"type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"你好"}]}}""";
        File.WriteAllText(path, meta + "\n" + message + "\n", new UTF8Encoding(false));
        return path;
    }

    private static string NewId() => Guid.NewGuid().ToString();

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    public void RootThreadsAreListedAndSubagentsOnlyCounted()
    {
        var root = NewId(); var archived = NewId(); var childA = NewId(); var childB = NewId();
        var rootPath = WriteRollout(root);
        var archivedPath = WriteRollout(archived, archived: true);
        CreateDatabase([
            new(root, rootPath, Title: "主任务"),
            new(archived, archivedPath, Title: "旧任务", Archived: true),
            // Newer stores mark sub-agents in thread_source…
            new(childA, WriteRollout(childA, subagent: true), Title: "子 A", ThreadSource: "subagent"),
            // …older rows only carry the spawn source JSON, and must count the same way.
            new(childB, WriteRollout(childB, subagent: true), Title: "子 B", ThreadSource: null,
                Source: """{"subagent":{"thread_spawn":{"parent_thread_id":"x","depth":1}}}"""),
        ]);

        var scan = new CodexMetadataReader(_paths).Read();

        Assert.Equal(2, scan.Rows.Count);
        Assert.Equal(2, scan.Subagents);
        Assert.Equal(2, scan.RootThreads);
        Assert.Equal(1, scan.RootArchived);
        Assert.Equal(4, scan.ThreadsInDatabase);
        Assert.All(scan.Rows, r => Assert.Equal(ConversationKey.Codex, r.Key.Backend));
        Assert.Equal(NativeArchiveState.Archived, scan.Rows.Single(r => r.Key.NativeSessionId == archived).NativeArchiveState);
        Assert.Equal(NativeArchiveState.Active, scan.Rows.Single(r => r.Key.NativeSessionId == root).NativeArchiveState);
        Assert.Equal(rootPath, scan.Rows.Single(r => r.Key.NativeSessionId == root).NativeOpenRef);
    }

    [Fact]
    public void ARolloutOnlyThereCompressedIsStillTheTasksTranscript()
    {
        // A maintenance tool compressed archived rollouts in place: rollout-….jsonl.zst beside where Codex recorded it.
        var recorded = NewId(); var moved = NewId();
        var recordedPath = WriteRollout(recorded, archived: true);
        var movedPath = WriteRollout(moved, archived: true);
        var text = File.ReadAllText(recordedPath);
        foreach (var path in new[] { recordedPath, movedPath })
        {
            using (var output = File.Create(path + ".zst"))
            using (var zstd = new ZstdSharp.CompressionStream(output))
                zstd.Write(File.ReadAllBytes(path));
            File.Delete(path);
        }
        CreateDatabase([
            new(recorded, recordedPath, Title: "压缩过的", Archived: true),
            new(moved, Path.Combine(_paths.SessionsRoot, "elsewhere.jsonl"), Title: "按 ID 找到的", Archived: true),
        ]);

        var rows = new CodexMetadataReader(_paths).Read().Rows.ToDictionary(r => r.Key.NativeSessionId);

        Assert.Equal(recordedPath + ".zst", rows[recorded].NativeOpenRef);
        Assert.Equal(movedPath + ".zst", rows[moved].NativeOpenRef);
        Assert.All(rows.Values, r => Assert.Equal(SourceCoverage.Resolved, r.SourceCoverage));
        using var reader = new StreamReader(TranscriptFiles.OpenRead(rows[recorded].NativeOpenRef!));
        Assert.Equal(text, reader.ReadToEnd());
    }

    [Fact]
    public void TitlePrefersNameThenTitleThenPreviewThenFirstMessage()
    {
        var a = NewId(); var b = NewId(); var c = NewId(); var d = NewId(); var e = NewId();
        CreateDatabase([
            new(a, WriteRollout(a), Title: "标题", Name: "用户起的名字", Preview: "预览", FirstUserMessage: "第一句"),
            new(b, WriteRollout(b), Title: "标题", Preview: "预览", FirstUserMessage: "第一句"),
            new(c, WriteRollout(c), Preview: "预览", FirstUserMessage: "第一句"),
            new(d, WriteRollout(d), FirstUserMessage: "第一句"),
            new(e, WriteRollout(e)),
        ]);

        var rows = new CodexMetadataReader(_paths).Read().Rows.ToDictionary(r => r.Key.NativeSessionId);

        Assert.Equal("用户起的名字", rows[a].Title);
        Assert.Equal("标题", rows[b].Title);
        Assert.Equal("预览", rows[c].Title);
        Assert.Equal("第一句", rows[d].Title);
        Assert.True(rows[e].TitleIsPlaceholder);
        Assert.Equal(e, rows[e].Title);
    }

    [Fact]
    public void ExtendedLengthPrefixesAreStrippedFromPathsAndSectionsBecomeGroups()
    {
        var id = NewId();
        var path = WriteRollout(id);
        CreateDatabase(
            [new(id, @"\\?\" + path, Cwd: @"\\?\E:\课题组\论文", Title: "t", SectionId: "sec-1", UpdatedAt: 1, UpdatedAtMs: 1_790_000_000_123)],
            [("sec-1", "我的论文")]);

        var row = Assert.Single(new CodexMetadataReader(_paths).Read().Rows);

        Assert.Equal(@"E:\课题组\论文", row.ProjectRef);
        Assert.Equal(path, row.NativeOpenRef);
        Assert.Equal("我的论文", row.SidebarGroup);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_790_000_000_123), row.LastActivityUtc);
    }

    [Fact]
    public void ARolloutTheDatabaseDoesNotKnowIsStillListedFromItsFolder()
    {
        var known = NewId(); var orphanActive = NewId(); var orphanArchived = NewId(); var orphanChild = NewId();
        CreateDatabase([new(known, WriteRollout(known), Title: "已登记")]);
        WriteRollout(orphanActive, cwd: @"D:\elsewhere");
        WriteRollout(orphanArchived, archived: true);
        WriteRollout(orphanChild, subagent: true);

        var scan = new CodexMetadataReader(_paths).Read();

        Assert.Equal(3, scan.Rows.Count);
        Assert.Equal(2, scan.RolloutsNotInDatabase);
        Assert.Equal(1, scan.Subagents);
        var active = scan.Rows.Single(r => r.Key.NativeSessionId == orphanActive);
        Assert.True(active.TitleIsPlaceholder);
        Assert.Equal(@"D:\elsewhere", active.ProjectRef);
        Assert.Equal(NativeArchiveState.Active, active.NativeArchiveState);
        Assert.Equal(NativeArchiveState.Archived, scan.Rows.Single(r => r.Key.NativeSessionId == orphanArchived).NativeArchiveState);
    }

    [Fact]
    public void AThreadWhoseFileIsGoneStaysListedAsPartial()
    {
        var id = NewId();
        CreateDatabase([new(id, Path.Combine(_paths.SessionsRoot, "gone.jsonl"), Title: "文件没了")]);

        var scan = new CodexMetadataReader(_paths).Read();

        var row = Assert.Single(scan.Rows);
        Assert.Equal(SourceCoverage.Partial, row.SourceCoverage);
        Assert.Null(row.NativeOpenRef);
        Assert.Equal(1, scan.RowsWithoutFile);
    }

    [Fact]
    public void ReadingLeavesTheDatabaseAndRolloutsUntouched()
    {
        var id = NewId();
        var rollout = WriteRollout(id);
        CreateDatabase([new(id, rollout, Title: "只读")]);
        var dbHash = Hash(_paths.StateDatabase);
        var fileHash = Hash(rollout);
        var dbWrite = File.GetLastWriteTimeUtc(_paths.StateDatabase);
        var fileWrite = File.GetLastWriteTimeUtc(rollout);

        _ = new CodexMetadataReader(_paths).Read();
        _ = new CodexMetadataReader(_paths).Read();

        Assert.Equal(dbHash, Hash(_paths.StateDatabase));
        Assert.Equal(fileHash, Hash(rollout));
        Assert.Equal(dbWrite, File.GetLastWriteTimeUtc(_paths.StateDatabase));
        Assert.Equal(fileWrite, File.GetLastWriteTimeUtc(rollout));
        Assert.False(File.Exists(_paths.StateDatabase + "-journal"));
    }

    [Fact]
    public void LocatorFindsTheRolloutByIdWhenTheRecordedPathIsStale()
    {
        var id = NewId();
        var real = WriteRollout(id);
        var row = new SessionMetadata
        {
            Key = new ConversationKey(ConversationKey.Codex, _root, "default", id),
            CliSessionId = id, Title = "t", SearchKey = "t", SourceRevision = "1",
            NativeOpenRef = Path.Combine(_root, "moved-away.jsonl"),
        };

        Assert.Equal(real, TranscriptLocator.Find(row, claudeConfigDir: _root, _paths));
    }

    [Theory]
    [InlineData(@"C:\x\rollout-2026-09-22T23-41-55-01a0c9c7-e537-7a30-9e7b-b508ed70ed67.jsonl", "01a0c9c7-e537-7a30-9e7b-b508ed70ed67")]
    [InlineData(@"C:\x\notes.jsonl", null)]
    [InlineData(@"C:\x\rollout-short.jsonl", null)]
    public void ThreadIdComesFromTheFileName(string path, string? expected)
    {
        Assert.Equal(expected, CodexMetadataReader.ThreadIdFromFileName(path));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* temp dir */ }
    }
}
