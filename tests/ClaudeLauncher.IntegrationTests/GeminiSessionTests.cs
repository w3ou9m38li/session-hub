using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeLauncher.Core.Catalog;
using ClaudeLauncher.Core.Contracts;
using ClaudeLauncher.Infrastructure.Backends;
using ClaudeLauncher.Infrastructure.Catalog;
using ClaudeLauncher.Infrastructure.Discovery;
using ClaudeLauncher.Infrastructure.Search;
using ClaudeLauncher.Infrastructure.Usage;

namespace ClaudeLauncher.IntegrationTests;

/// <summary>
/// Gemini CLI keeps each session as one JSON document, written again whole as it grows. These
/// pin what is listed and shown, and that a rewritten session is read afresh everywhere — the
/// transcript view, the search index, the token count — rather than read on from an offset.
/// </summary>
public sealed class GeminiSessionTests : IDisposable
{
    private const string IdA = "0a1b2c3d-0000-4000-8000-00000000000a";
    private const string IdB = "0b1b2c3d-0000-4000-8000-00000000000b";
    private const string IdC = "0c1b2c3d-0000-4000-8000-00000000000c";

    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

    private readonly string _dir;
    private readonly GeminiPaths _paths;

    public GeminiSessionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "launcher-gemini", Guid.NewGuid().ToString("n"));
        _paths = GeminiPaths.FromHome(Path.Combine(_dir, ".gemini"));
        Directory.CreateDirectory(_paths.ProjectsRoot);
    }

    private static object User(string id, string at, params object[] parts) =>
        new { id, timestamp = at, type = "user", content = parts };

    private static object Words(string text) => new { text };

    private static object Tokens(long input, long cached, long output, long thoughts = 0, long tool = 0) =>
        new { input, output, cached, thoughts, tool, total = input + output + thoughts };

    private static object Model(string id, string at, string content, object? tokens = null,
        object[]? thoughts = null, object[]? toolCalls = null) =>
        new { id, timestamp = at, type = "gemini", content, thoughts, tokens, model = "gemini-3-pro", toolCalls };

    private static object[] Exchanges(int count, string extra = "") =>
    [
        .. Enumerable.Range(0, count).SelectMany(i => new[]
        {
            User($"u{i}", $"2026-10-01T08:0{i}:00Z", Words($"第 {i} 个问题{(i == count - 1 ? extra : "")}")),
            Model($"m{i}", $"2026-10-01T08:0{i}:30Z", $"第 {i} 个回答"),
        }),
    ];

    /// <summary>A session file where Gemini CLI puts it, its project folder naming the working directory.</summary>
    private string Session(string project, string id, object[] messages, string? summary = null,
        string lastUpdated = "2026-10-01T08:10:00Z", string? root = @"D:\work\demo", string? fileName = null)
    {
        var folder = Path.Combine(_paths.ProjectsRoot, project);
        Directory.CreateDirectory(Path.Combine(folder, "chats"));
        if (root is not null) File.WriteAllText(Path.Combine(folder, ".project_root"), root);
        var path = Path.Combine(folder, "chats", fileName ?? $"session-2026-10-01T08-00-{id[..8]}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            sessionId = id, projectHash = project, startTime = "2026-10-01T08:00:00Z", lastUpdated, summary,
            kind = "main", messages,
        }, Options), new UTF8Encoding(false));
        return path;
    }

    private SqliteCatalog Catalog()
    {
        var catalog = new SqliteCatalog(Path.Combine(_dir, "catalog.sqlite"));
        catalog.Initialize(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "CatalogSchema.sql")));
        return catalog;
    }

    [Fact]
    public void EverythingASessionHoldsIsShownInItsPlace()
    {
        var path = Session("p", IdA,
        [
            User("u1", "2026-10-01T08:00:00Z", Words("看一下这个文件"),
                new { inlineData = new { mimeType = "image/png", data = Convert.ToBase64String(Png) } },
                new { inlineData = new { mimeType = "application/pdf", data = "JVBERi0xLjQ=" } }),
            Model("m1", "2026-10-01T08:00:30Z", "这是一张图",
                thoughts: [new { subject = "Looking at the picture", description = "It shows a chart." }],
                toolCalls: [new { id = "c1", name = "read_file", displayName = "ReadFile",
                                  args = new { absolute_path = @"D:\work\demo\a.txt" }, resultDisplay = "file contents" }]),
            User("u2", "2026-10-01T08:01:00Z",
                new { functionResponse = new { name = "run_shell_command", response = new { output = "ok" } } }),
            new { id = "i1", timestamp = "2026-10-01T08:02:00Z", type = "info", content = "已切换模型" },
            new { id = "e1", timestamp = "2026-10-01T08:03:00Z", type = "error", content = "配额用完" },
        ]);

        var entries = GeminiSession.ReadEntries(path, keepImages: true);

        Assert.Equal(
        [
            MessageRole.Attachment, MessageRole.Attachment, MessageRole.User,
            MessageRole.Note, MessageRole.Assistant, MessageRole.ToolCall, MessageRole.ToolResult,
            MessageRole.ToolResult, MessageRole.Note, MessageRole.Note,
        ], entries.Select(e => e.Role).ToArray());
        Assert.Equal([0, 0, 0, 1, 1, 1, 1, 2, 3, 4], entries.Select(e => e.Index).ToArray());

        Assert.Equal(Png, entries[0].Image);
        Assert.Null(entries[1].Image);                                  // not a picture: named, never decoded
        Assert.Contains("application/pdf", entries[1].Text);
        Assert.Equal("看一下这个文件", entries[2].Text);
        Assert.Equal("思考 · Looking at the picture", entries[3].Text);
        Assert.Contains("It shows a chart.", entries[3].Detail);
        Assert.Equal("这是一张图", entries[4].Text);
        Assert.Equal(@"ReadFile  D:\work\demo\a.txt", entries[5].Text);
        Assert.Equal("file contents", entries[6].Detail);
        Assert.Equal("工具输出 · run_shell_command（2 字符）", entries[7].Text);
        Assert.Equal("ok", entries[7].Detail);
        Assert.Equal("提示 · 已切换模型", entries[8].Text);
        Assert.Equal("错误 · 配额用完", entries[9].Text);

        // Pictures are decoded only for the transcript view.
        Assert.Null(GeminiSession.ReadEntries(path)[0].Image);
        Assert.Equal(["看一下这个文件", "这是一张图"], GeminiSession.ReadDialogue(path).Select(e => e.Text).ToArray());
    }

    [Fact]
    public void EachSessionIsARowNamedByItsSummaryOrWhatWasFirstSaid()
    {
        var a = Session("one", IdA, Exchanges(1), summary: "整理实验数据", root: @"D:\work\one");
        Session("two", IdB, [User("u", "2026-10-01T08:00:00Z", Words("第一句话")), Model("m", "2026-10-01T08:00:30Z", "好")],
            lastUpdated: "2026-10-02T09:00:00Z", root: @"D:\work\two");
        Session("three", IdC, [Model("m", "2026-10-01T08:00:30Z", "只有回答")]);
        // Not sessions: another file in a chats folder, a document with no id, one caught half written.
        var other = Path.Combine(_paths.ProjectsRoot, "four", "chats");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "notes.json"), "not json");
        File.WriteAllText(Path.Combine(other, "session-empty.json"), "{}");
        File.WriteAllText(Path.Combine(other, "session-half.json"), "{\"sessionId\":\"0d");
        // The sign-in beside the sessions is never opened.
        File.WriteAllText(Path.Combine(_paths.Home, "oauth_creds.json"), "not json");

        var scan = new GeminiMetadataReader(_paths).Read();

        Assert.Equal(5, scan.Files);
        Assert.Equal(1, scan.Unreadable);
        var rows = scan.Rows.ToDictionary(r => r.Key.NativeSessionId);
        Assert.Equal([IdA, IdB, IdC], rows.Keys.Order().ToArray());

        var row = rows[IdA];
        Assert.Equal(new ConversationKey(ConversationKey.Gemini, _paths.Home, "default", IdA), row.Key);
        Assert.Equal(IdA, row.CliSessionId);
        Assert.Equal("整理实验数据", row.Title);
        Assert.False(row.TitleIsPlaceholder);
        Assert.Equal(@"D:\work\one", row.ProjectRef);
        Assert.Equal(a, row.NativeOpenRef);
        Assert.Equal(new FileInfo(a).Length, row.TranscriptBytes);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 8, 10, 0, TimeSpan.Zero), row.LastActivityUtc);
        Assert.Equal(NativeArchiveState.Unsupported, row.NativeArchiveState);

        Assert.Equal("第一句话", rows[IdB].Title);
        Assert.Equal(@"D:\work\two", rows[IdB].ProjectRef);
        Assert.Equal(IdC, rows[IdC].Title);                            // nothing said by the user: its id
        Assert.True(rows[IdC].TitleIsPlaceholder);
    }

    [Fact]
    public void ACopyLeftBehindIsListedOnceAsTheFileWrittenLast()
    {
        Session("old", IdA, Exchanges(1), lastUpdated: "2026-10-01T08:10:00Z", fileName: "session-old-0a1b2c3d.json");
        var newer = Session("new", IdA, Exchanges(2), lastUpdated: "2026-10-03T08:10:00Z", fileName: "session-new-0a1b2c3d.json");

        var row = Assert.Single(new GeminiMetadataReader(_paths).Read().Rows);
        Assert.Equal(newer, row.NativeOpenRef);
    }

    [Fact]
    public void AChangedSessionIsReadAgainAnUnchangedOneIsNot()
    {
        var cache = new GeminiProbeCache();
        var path = Session("p", IdA, Exchanges(1), summary: "旧的摘要");
        Assert.Equal("旧的摘要", Assert.Single(new GeminiMetadataReader(_paths, cache).Read().Rows).Title);

        Session("p", IdA, Exchanges(2), summary: "改过的摘要，长了一些");
        Assert.Equal("改过的摘要，长了一些", Assert.Single(new GeminiMetadataReader(_paths, cache).Read().Rows).Title);
        Assert.Same(cache.Header(new FileInfo(path)), cache.Header(new FileInfo(path)));
    }

    [Fact]
    public void TheTranscriptViewPagesThroughTheDocumentAndRereadsARewrite()
    {
        var path = Session("p", IdA, Exchanges(3));

        using (var reader = new TranscriptReader(path))
        {
            var first = reader.Read(0, 4);
            Assert.Equal(4, first.Entries.Count);
            Assert.Equal(4, first.NextLineOffset);
            Assert.False(first.ReachedEnd);
            var rest = reader.Read(first.NextLineOffset, 4);
            Assert.Equal(["第 2 个问题", "第 2 个回答"], rest.Entries.Select(e => e.Text).ToArray());
            Assert.True(rest.ReachedEnd);

            // Written again with more in it: reading from the top sees all of it.
            Session("p", IdA, Exchanges(4));
            Assert.Equal(8, reader.Read(0, 100).Entries.Count);
        }

        var tail = TranscriptReader.ReadTail(path, 3);
        Assert.True(tail.WholeFile);
        Assert.Equal(["第 2 个回答", "第 3 个问题", "第 3 个回答"], tail.Entries.Select(e => e.Text).ToArray());
        Assert.Equal(["第 3 个问题", "第 3 个回答"], TranscriptReader.LastExchange(path).Select(e => e.Text).ToArray());
        Assert.Equal(["第 3 个回答"], TranscriptReader.RecentDialogue(path, 1).Select(e => e.Text).ToArray());

        // Always whole, whatever offset the index last stopped at.
        var (entries, next) = TranscriptReader.ReadDialogueFrom(path, new FileInfo(path).Length / 2);
        Assert.Equal(8, entries.Count);
        Assert.Equal(new FileInfo(path).Length, next);
    }

    [Fact]
    public void AHalfWrittenSessionReadsAsAFileThatCannotBeReadYet()
    {
        var path = Session("p", IdA, Exchanges(1));
        File.WriteAllText(path, "{\"sessionId\":\"" + IdA + "\",\"messages\":[{\"id\":");

        Assert.Throws<IOException>(() => GeminiSession.ReadEntries(path));
        Assert.Throws<IOException>(() => TranscriptReader.ReadTail(path, 10));
    }

    [Fact]
    public void TheSearchIndexReadsARewrittenSessionWhole()
    {
        using var catalog = Catalog();
        using var index = new ContentIndex(Path.Combine(_dir, "search-index.sqlite"));
        catalog.AttachContentIndex(index.DatabasePath);
        var path = Session("p", IdA, Exchanges(1));
        var row = Assert.Single(new GeminiMetadataReader(_paths).Read().Rows);
        catalog.UpsertRange([row]);
        List<string> Found(string search) =>
            catalog.Page(Visibility.Active, search, null, 100).Rows.Select(r => r.Key.NativeSessionId).ToList();

        var first = index.Update([new ContentIndex.Source(ContentIndex.KeyOf(row.Key), path)]);
        catalog.ContentIndexChanged();
        Assert.Equal(2, first.Messages);
        Assert.Equal([IdA], Found("个回答"));                       // only in what was said

        Session("p", IdA, Exchanges(3, extra: "：引物设计"));
        var again = index.Update([new ContentIndex.Source(ContentIndex.KeyOf(row.Key), path)]);
        catalog.ContentIndexChanged();
        Assert.Equal(6, again.Messages);                                // all of it, once — not appended to what was there
        Assert.Equal([IdA], Found("引物设计"));
        Assert.Equal(0, index.Update([new ContentIndex.Source(ContentIndex.KeyOf(row.Key), path)]).Indexed);
    }

    [Fact]
    public void TokensAreCountedOnceEachThinkingAsOutputAndTheCacheApart()
    {
        using var ledger = new UsageLedger(Path.Combine(_dir, "usage.sqlite"));
        object[] once =
        [
            User("u1", "2026-10-01T08:00:00Z", Words("问题")),
            Model("m1", "2026-10-01T08:00:30Z", "回答", Tokens(input: 1000, cached: 600, output: 50, thoughts: 30, tool: 5)),
            new { id = "i1", timestamp = "2026-10-01T08:00:40Z", type = "info", content = "没有 tokens 的提示" },
        ];
        var path = Session("p", IdA, once);
        ledger.Update([new("g", path, UsageApp.Gemini)]);
        var counted = Total(ledger);
        Assert.Equal(1, counted.Calls);
        Assert.Equal(400 + 5, counted.Input);                           // input less what came from the cache, plus tools
        Assert.Equal(600, counted.CacheRead);
        Assert.Equal(0, counted.CacheWrite);
        Assert.Equal(80, counted.Output);                               // the thinking is output too

        // The session grows: the file is written again whole, and only the new call adds to the count.
        Session("p", IdA,
        [
            .. once,
            User("u2", "2026-10-01T08:01:00Z", Words("再问")),
            Model("m2", "2026-10-01T08:01:30Z", "再答", Tokens(input: 2000, cached: 1500, output: 100)),
        ]);
        ledger.Update([new("g", path, UsageApp.Gemini)]);
        counted = Total(ledger);
        Assert.Equal(2, counted.Calls);
        Assert.Equal(405 + 500, counted.Input);
        Assert.Equal(2100, counted.CacheRead);
        Assert.Equal(180, counted.Output);
        Assert.All(ledger.Rows(), r => Assert.Equal(UsageApp.Gemini, r.App));
        Assert.Equal(["gemini-3-pro"], ledger.Rows().Select(r => r.Model).Distinct().ToArray());
    }

    private static TokenCounts Total(UsageLedger ledger)
    {
        var total = new TokenCounts();
        foreach (var row in ledger.Rows()) total += row.Tokens;
        return total;
    }

    [Fact]
    public void ASyncListsGeminiOnlyWhenGivenItsFolderAndKeepsRowsAnIncompleteReadMissed()
    {
        using var catalog = Catalog();
        var config = Path.Combine(_dir, "claude");
        Directory.CreateDirectory(Path.Combine(config, "projects"));
        var claude = new ClaudeInstallation { Found = false };
        // An empty Codex home: the test must never read this machine's own Codex data.
        var codex = CodexPaths.FromHome(Path.Combine(_dir, "codex"));
        int Listed() => catalog.Page(Visibility.Active, null, null, 100, GroupFilter.All, BackendFilter.Gemini).Rows.Count;

        Session("one", IdA, Exchanges(1));
        var b = Session("two", IdB, Exchanges(1));

        var without = new CatalogSyncService(catalog, config).Sync(claude, codex);
        Assert.DoesNotContain(without.Coverage.Sources, s => s.Backend == ConversationKey.Gemini);
        Assert.Equal(0, Listed());

        var sync = new CatalogSyncService(catalog, config, _paths);
        var first = sync.Sync(claude, codex);
        var tally = Assert.Single(first.Coverage.Sources, s => s.Backend == ConversationKey.Gemini);
        Assert.Equal(2, tally.Records);
        Assert.Equal(SourceCoverage.Resolved, tally.Coverage);
        Assert.Equal(2, Listed());
        Assert.False(sync.Sync(claude, codex).ChangedAnything);

        // A session gone while another is caught half written: nothing is marked on that read.
        File.Delete(b);
        var half = Path.Combine(_paths.ProjectsRoot, "two", "chats", "session-half.json");
        File.WriteAllText(half, "{\"sessionId\":");
        var partial = sync.Sync(claude, codex);
        Assert.Equal(SourceCoverage.Partial, Assert.Single(partial.Coverage.Sources, s => s.Backend == ConversationKey.Gemini).Coverage);
        Assert.Equal(0, partial.RowsRemoved);
        Assert.Equal(2, Listed());

        // Read whole again: the session is gone from Gemini, and stays listed, marked so.
        File.Delete(half);
        var settled = sync.Sync(claude, codex);
        Assert.Equal(1, settled.RowsRemoved);
        Assert.Equal(2, Listed());
        var rows = catalog.Page(Visibility.Active, null, null, 100, GroupFilter.All, BackendFilter.Gemini).Rows;
        Assert.Equal([IdB], rows.Where(r => r.SourceGoneUtc is not null).Select(r => r.Key.NativeSessionId).ToArray());

        var row = rows.Single(r => r.SourceGoneUtc is null);
        Assert.Equal(row.NativeOpenRef, TranscriptLocator.Find(row, config, codex));
        Assert.Empty(catalog.Page(Visibility.Active, null, null, 100, GroupFilter.All, BackendFilter.Codex).Rows);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
    }
}
