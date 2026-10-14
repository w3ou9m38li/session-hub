using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClaudeLauncher.Core.Catalog;
using ClaudeLauncher.Core.Contracts;
using ClaudeLauncher.Infrastructure.Backends;
using ClaudeLauncher.Infrastructure.Catalog;
using ClaudeLauncher.Infrastructure.Discovery;
using ClaudeLauncher.Infrastructure.Search;
using ClaudeLauncher.Infrastructure.Settings;
using ClaudeLauncher.Infrastructure.Usage;

namespace ClaudeLauncher.IntegrationTests;

/// <summary>
/// DeepSeek Harness keeps each session as a log of events, compressed a few lines at a time:
/// a run of Zstandard frames under <c>sessions\&lt;workspace&gt;\session-&lt;id&gt;\</c>. These pin
/// what is listed and shown, that a frame still being written costs nothing before it, and that
/// the log is read whole everywhere — the transcript view, the search index, the token count.
/// </summary>
public sealed class DshSessionTests : IDisposable
{
    private const string IdA = "session-0a1b2c3d-0000-4000-8000-00000000000a";
    private const string IdB = "session-0b1b2c3d-0000-4000-8000-00000000000b";
    private const string IdC = "session-0c1b2c3d-0000-4000-8000-00000000000c";
    private const long T0 = 1_791_000_000_000;            // 2026-10-02, in dsh's milliseconds

    private readonly string _dir;
    private readonly string _home;

    public DshSessionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "launcher-dsh", Guid.NewGuid().ToString("n"));
        _home = Path.Combine(_dir, "deploy", "data");
        Directory.CreateDirectory(Path.Combine(_home, "sessions"));
    }

    // ------------------------------------------------------------------ building logs

    private static string Line(object value) => JsonSerializer.Serialize(value);

    private static object Head(string id, string cwd = @"D:\work\demo", int depth = 0) =>
        new { type = "session", version = 4, id, createdAt = T0, cwd, isSeeded = false, delegationDepth = depth, agentPreset = "standard" };

    private static object Event(string type, int seq, object data) => new { type, seq, time = T0 + seq * 1000L, data };

    private static object Said(int seq, string text, string kind = "user") => Event("user/message", seq, new
    {
        content = new object[] { new { type = "text", text } },
        source = new { kind },
        role = "user",
        id = $"u-{seq}",
    });

    private static object Answer(int seq, string id, object[] blocks, object? usage = null, string model = "deepseek-flash") =>
        Event("assistant/message", seq, new
        {
            turn = 1,
            step = 1,
            message = new { role = "assistant", content = blocks, source = new { kind = "model", provider = "deepseek-official", model }, id },
            usage,
        });

    private static object Text(string text) => new { type = "text", text };

    private static object Usage(long input, long output, long cacheRead = 0, long cacheWrite = 0, long reasoning = 0) =>
        new { inputTokens = input, outputTokens = output, cacheReadTokens = cacheRead, cacheWriteTokens = cacheWrite, reasoningTokens = reasoning };

    private static object Title(int seq, string title, string kind = "provider") =>
        Event("session/title", seq, new { title, messageSeqs = new[] { 1 }, source = new { kind } });

    /// <summary>Lines written as dsh writes them: each group of lines one compressed frame, appended.</summary>
    private static byte[] Frames(params object[][] groups)
    {
        using var compressor = new ZstdSharp.Compressor();
        var bytes = new List<byte>();
        foreach (var group in groups)
        {
            var text = string.Concat(group.Select(l => Line(l) + "\n"));
            bytes.AddRange(compressor.Wrap(Encoding.UTF8.GetBytes(text)).ToArray());
        }
        return [.. bytes];
    }

    private string Session(string id, params object[][] groups) => SessionIn(_home, "--D-work-demo--", id, groups);

    private static string SessionIn(string home, string workspace, string id, params object[][] groups)
    {
        var folder = Path.Combine(home, "sessions", workspace, id);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "session.v4.jsonl.zstd");
        File.WriteAllBytes(path, Frames(groups));
        return path;
    }

    private static object[][] Exchange(string id, string question, string answer, string? title = null) =>
    [
        [Head(id), Event("model/selection", 1, new { provider = "deepseek-official", model = "deepseek-flash" })],
        [Said(2, question), Said(3, "<sandbox>写入工作区</sandbox>", "runtime-context")],
        title is null
            ? [Answer(4, id + "-m1", [Text(answer)], Usage(100, 20))]
            : [Answer(4, id + "-m1", [Text(answer)], Usage(100, 20)), Title(5, title)],
    ];

    private SqliteCatalog Catalog()
    {
        var catalog = new SqliteCatalog(Path.Combine(_dir, "catalog.sqlite"));
        catalog.Initialize(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "CatalogSchema.sql")));
        return catalog;
    }

    private DshPaths Paths => new([_home]);

    // ------------------------------------------------------------------ reading

    [Fact]
    public void EverythingASessionHoldsIsShownInItsPlace()
    {
        var path = Session(IdA,
            [Head(IdA)],
            [Said(1, "帮我看看这个目录"), Said(2, "<sandbox>只读</sandbox>", "runtime-context"), Said(3, "技能列表……", "skill-catalog")],
            [
                Answer(4, "m1",
                [
                    new { type = "reasoning", text = "先列出文件\n再读第一个" },
                    Text("好的，我先看看。"),
                    new { type = "tool-call", id = "call-1", name = "pwsh", arguments = "{\"command\":\"Get-ChildItem\"}" },
                ], Usage(100, 20)),
                Event("tool/call", 5, new { callId = "call-1", name = "pwsh", arguments = "{\"command\":\"Get-ChildItem\"}" }),
                Event("tool/result", 6, new
                {
                    message = new { role = "tool", toolCallId = "call-1", content = new object[] { Text("a.txt\nb.txt") }, isError = false, id = "r1" },
                }),
            ],
            [
                Event("approval/asked", 7, new { id = "ap1", toolName = "pwsh", callId = "call-2", reason = "要删除文件" }),
                Event("approval/decided", 8, new { id = "ap1", outcome = "approved-once" }),
                Event("command/run", 9, new { commandId = "c1", name = "permission", args = "full", source = new { kind = "user" } }),
                Event("command/done", 10, new { commandId = "c1", kind = "success", text = "权限已切换" }),
                Event("system/message", 11, new { message = new { role = "system", content = new object[] { Text("系统提示，不显示") } } }),
                Event("turn/end", 12, new { turn = 1, reason = new { kind = "error", error = new { message = "额度用完了", code = "QUOTA" } } }),
            ]);

        var entries = DshSession.ReadEntries(path);
        var shown = entries.Select(e => (e.Role, e.Text)).ToList();
        Assert.Equal(
        [
            (MessageRole.User, "帮我看看这个目录"),
            (MessageRole.Note, "注入的上下文 · 运行环境与权限"),
            (MessageRole.Note, "注入的上下文 · 可用技能列表"),
            (MessageRole.Note, "思考 · 先列出文件 再读第一个"),
            (MessageRole.Assistant, "好的，我先看看。"),
            (MessageRole.ToolCall, "pwsh  Get-ChildItem"),         // once: the tool/call event repeats the block
            (MessageRole.ToolResult, "工具输出（11 字符）"),
            (MessageRole.Note, "请求批准 · pwsh"),
            (MessageRole.Note, "批准结果 · 允许（approved-once）"),
            (MessageRole.Note, "命令 · /permission full"),
            (MessageRole.Note, "命令结果 · 权限已切换"),
            (MessageRole.Note, "出错 · 额度用完了"),
        ], shown);
        Assert.Equal("a.txt\nb.txt", entries.Single(e => e.Role == MessageRole.ToolResult).Detail);
        Assert.Contains("QUOTA", entries.Last().Detail);
        Assert.All(entries, e => Assert.NotNull(e.Timestamp));

        // The search index and the side panel get what the two sides said, nothing else.
        Assert.Equal(["帮我看看这个目录", "好的，我先看看。"], DshSession.ReadDialogue(path).Select(e => e.Text).ToArray());
    }

    [Fact]
    public void EachSessionIsARowNamedByItsLatestTitleInTheFolderItRanIn()
    {
        Session(IdA,
            [Head(IdA, @"D:\work\alpha"), Said(1, "第一句话"), Title(2, "临时标题", "fallback")],
            [Answer(3, "m1", [Text("回答")]), Title(4, "整理实验数据")]);
        Session(IdB, [Head(IdB), Said(1, "<ctx>注入</ctx>", "runtime-context"), Said(2, "只有这一句")]);
        Session(IdC, [Head(IdC, depth: 1), Said(1, "子代理的任务")]);
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(_home, "storages")).FullName, "workspace.json"),
            Line(new { global = new { archivedSessionIds = new[] { IdB }, pinnedSessionIds = Array.Empty<string>() } }));

        var scan = new DshMetadataReader(Paths).Read();
        Assert.Equal(3, scan.Files);
        Assert.Equal(1, scan.Subagents);                                     // counted, not listed
        Assert.Equal(0, scan.Unreadable);
        var rows = scan.Rows.ToDictionary(r => r.Key.NativeSessionId);
        Assert.Equal([IdA, IdB], rows.Keys.Order().ToArray());

        var a = rows[IdA];
        Assert.Equal(new ConversationKey(ConversationKey.DeepSeek, _home, "default", IdA), a.Key);
        Assert.Equal("整理实验数据", a.Title);                                // the latest title dsh gave it
        Assert.Equal(@"D:\work\alpha", a.ProjectRef);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(T0 + 4000), a.LastActivityUtc);
        Assert.Equal(NativeArchiveState.Active, a.NativeArchiveState);
        Assert.Null(a.CliSessionId);
        Assert.False(a.TitleIsPlaceholder);

        var b = rows[IdB];
        Assert.Equal("只有这一句", b.Title);                                  // what the user typed, not what was put in
        Assert.Equal(NativeArchiveState.Archived, b.NativeArchiveState);     // as dsh's own list has it
        Assert.Equal(AppStanding.Archived, OrderingRules.Standing(b.Key.Backend, b.NativeArchiveState));
    }

    [Fact]
    public void AFrameStillBeingWrittenLeavesTheLinesBeforeItStanding()
    {
        var path = Session(IdA, [Head(IdA), Said(1, "问题")], [Answer(2, "m1", [Text("回答")], Usage(10, 5))]);
        var whole = File.ReadAllBytes(path);
        var next = Frames([Said(3, "还在写的这一句")]);
        File.WriteAllBytes(path, [.. whole, .. next.AsSpan(0, next.Length / 2)]);   // half of the frame being appended

        Assert.Equal(["问题", "回答"], DshSession.ReadEntries(path).Select(e => e.Text).ToArray());
        Assert.Equal(IdA, DshSession.ReadHeader(path)!.SessionId);
        Assert.Single(DshSession.ReadCalls(path));

        File.WriteAllBytes(path, [.. whole, .. next]);                     // and next time it is whole
        Assert.Equal("还在写的这一句", DshSession.ReadEntries(path).Last().Text);
    }

    [Fact]
    public void TheNewestLogGenerationIsTheSessionAndOnlyLogsAreTakenForOne()
    {
        var path = Session(IdA, [Head(IdA), Said(1, "新的")]);
        var folder = Path.GetDirectoryName(path)!;
        File.WriteAllText(Path.Combine(folder, "session.v3.jsonl"), Line(Head(IdA)) + "\n");
        File.WriteAllText(Path.Combine(folder, "session.v4.jsonl.tmp"), "half");
        Assert.Equal(path, DshPaths.LogOf(folder));
        Assert.Equal(path, Assert.Single(Paths.SessionFiles()).File);

        Assert.Equal(0, DshSession.GenerationOf("session.jsonl"));
        Assert.Equal(4, DshSession.GenerationOf("session.v4.jsonl.zstd"));
        Assert.Equal(12, DshSession.GenerationOf("session.v12.jsonl"));
        Assert.Null(DshSession.GenerationOf("session.v0.jsonl"));
        Assert.Null(DshSession.GenerationOf("session.v04.jsonl"));
        Assert.Null(DshSession.GenerationOf("session.v4.jsonl.tmp"));

        Assert.True(DshSession.IsSessionFile(path));
        Assert.True(DshSession.IsSessionFile(@"C:\data\archive\copies\dsh\" + IdA + DshSession.CopySuffix));
        Assert.True(DshSession.IsSessionFile(@"C:\data\archive\copies\dsh\" + IdA + DshSession.CopySuffix + PackedCopy.Suffix));
        Assert.False(DshSession.IsSessionFile(@"C:\p\0a1b2c3d-0000-4000-8000-00000000000a.jsonl"));   // Claude
        Assert.False(DshSession.IsSessionFile(@"C:\s\rollout-2026-10-01T08-00-00-0a1b.jsonl"));      // Codex
        Assert.False(DshSession.IsSessionFile(@"C:\g\chats\session-2026-10-01T08-00-0a1b2c3d.json")); // Gemini
    }

    [Fact]
    public void AHomeIsFoundAsItselfOrInADeploymentsDataFolder()
    {
        var deployment = Path.GetDirectoryName(_home)!;
        Assert.Equal(_home, DshPaths.HomeOf(_home));
        Assert.Equal(_home, DshPaths.HomeOf(deployment));                     // the folder the start script is in
        Assert.Equal(_home, DshPaths.HomeOf(" \"" + deployment + "\\\" "));
        Assert.Null(DshPaths.HomeOf(_dir));
        Assert.Null(DshPaths.HomeOf(Path.Combine(_dir, "nowhere")));
        Assert.Null(DshPaths.HomeOf(""));

        var found = DshPaths.Discover([deployment, _home, Path.Combine(_dir, "nowhere")]);
        Assert.Equal(_home, found.Homes[0]);                                  // once, first, as the user added it
        Assert.Equal(1, found.Homes.Count(h => string.Equals(h, _home, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void TokensAreCountedOnceWithTheCacheApartAndTheReasoningInTheOutput()
    {
        using var ledger = new UsageLedger(Path.Combine(_dir, "usage.sqlite"));
        var a = Session(IdA,
            [Head(IdA), Event("model/selection", 1, new { provider = "google-account", model = "gemini-3.8-flash-high" })],
            [Said(2, "问"), Answer(3, "shared-1", [Text("答")], Usage(input: 300, output: 50, cacheRead: 700, cacheWrite: 40, reasoning: 30), "gemini-3.8-flash-high")]);
        // A session that copied the first one's message (a fork) has it under the same id.
        var b = SessionIn(_home, "--D-other--", IdB,
            [Head(IdB), Answer(1, "shared-1", [Text("答")], Usage(300, 50, 700, 40), "gemini-3.8-flash-high")],
            [Answer(2, "own-1", [Text("再答")], Usage(1000, 100))]);

        ledger.Update([new("a", a, AppFamily.DeepSeek), new("b", b, AppFamily.DeepSeek)]);
        var total = new TokenCounts();
        foreach (var row in ledger.Rows()) total += row.Tokens;
        Assert.Equal(2, total.Calls);                                         // the shared message once
        Assert.Equal(1300, total.Input);                                      // the uncached prompt only
        Assert.Equal(700, total.CacheRead);
        Assert.Equal(40, total.CacheWrite);
        Assert.Equal(150, total.Output);                                      // reasoning is already in it
        Assert.All(ledger.Rows(), r => Assert.Equal(AppFamily.DeepSeek, r.App));
        Assert.Equal(["deepseek-flash", "gemini-3.8-flash-high"], ledger.Rows().Select(r => r.Model).Distinct().Order().ToArray());
    }

    [Fact]
    public void TheTranscriptViewAndTheSearchIndexReadTheCompressedLogWhole()
    {
        var path = Session(IdA, Exchange(IdA, "第一个问题", "第一个回答"));
        using (var reader = new TranscriptReader(path))
        {
            var page = reader.Read(0, 2);
            Assert.Equal(["第一个问题", "注入的上下文 · 运行环境与权限"], page.Entries.Select(e => e.Text).ToArray());
            Assert.False(page.ReachedEnd);
            var rest = reader.Read(page.NextLineOffset, 10);
            Assert.Equal(["第一个回答"], rest.Entries.Select(e => e.Text).ToArray());
            Assert.True(rest.ReachedEnd);
        }
        Assert.Equal("第一个回答", TranscriptReader.ReadTail(path, 1).Entries.Single().Text);

        using var catalog = Catalog();
        using var index = new ContentIndex(Path.Combine(_dir, "search-index.sqlite"));
        catalog.AttachContentIndex(index.DatabasePath);
        var row = Assert.Single(new DshMetadataReader(Paths).Read().Rows);
        catalog.UpsertRange([row]);
        List<string> Found(string search) =>
            catalog.Page(Visibility.Active, search, null, 100).Rows.Select(r => r.Key.NativeSessionId).ToList();

        Assert.Equal(2, index.Update([new ContentIndex.Source(ContentIndex.KeyOf(row.Key), path)]).Messages);
        catalog.ContentIndexChanged();
        Assert.Equal([IdA], Found("个回答"));
        Assert.Empty(Found("写入工作区"));                                     // what was put in is not what was said

        // The log grows by a frame: read whole again, not appended to what was indexed.
        File.WriteAllBytes(path, [.. File.ReadAllBytes(path), .. Frames([Said(6, "引物设计怎么做"), Answer(7, "m2", [Text("这样做")])])]);
        Assert.Equal(4, index.Update([new ContentIndex.Source(ContentIndex.KeyOf(row.Key), path)]).Messages);
        catalog.ContentIndexChanged();
        Assert.Equal([IdA], Found("引物设计"));
    }

    [Fact]
    public void TheArchiveKeepsACopyOfItsWholeLinesOnceTheLogIsLeftAlone()
    {
        var path = Session(IdA, Exchange(IdA, "要保存的问题", "要保存的回答"));
        using var archive = new TranscriptArchive(Path.Combine(_dir, "archive"));
        var source = new TranscriptArchive.Source(path, "dsh", IdA);

        Assert.Equal(0, archive.Update([source], TimeSpan.FromMinutes(1)).Copied);   // just written: it may be growing
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));
        Assert.Equal(1, archive.Update([source], TimeSpan.FromMinutes(1)).Copied);

        var copy = archive.CopyOf("dsh", IdA)!;
        Assert.EndsWith(IdA + DshSession.CopySuffix + PackedCopy.Suffix, copy);
        Assert.Equal(Encoding.UTF8.GetString(Unpack(path)), Encoding.UTF8.GetString(Unpack(copy)));   // the lines, every one whole
        Assert.Equal(DshSession.ReadEntries(path).Select(e => e.Text), DshSession.ReadEntries(copy).Select(e => e.Text));

        // The app deletes it: what was kept is read in its place.
        var row = Assert.Single(new DshMetadataReader(Paths).Read().Rows);
        Assert.Equal(("dsh", IdA), TranscriptArchive.IdentityOf(row));
        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        Assert.Equal(["要保存的问题", "注入的上下文 · 运行环境与权限", "要保存的回答"],
            DshSession.ReadEntries(copy).Select(e => e.Text).ToArray());
    }

    private static byte[] Unpack(string path)
    {
        using var input = TranscriptFiles.OpenRead(path);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    [Fact]
    public void ASyncListsDeepSeekOnlyWhenGivenAHomeAndJudgesOnlyTheHomesItRead()
    {
        using var catalog = Catalog();
        var config = Path.Combine(_dir, "claude");
        Directory.CreateDirectory(Path.Combine(config, "projects"));
        var claude = new ClaudeInstallation { Found = false };
        var codex = CodexPaths.FromHome(Path.Combine(_dir, "codex"));        // never this machine's own data
        var other = Path.Combine(_dir, "other-home");
        Directory.CreateDirectory(Path.Combine(other, "sessions"));
        IReadOnlyList<SessionMetadata> Listed() =>
            catalog.Page(Visibility.Active, null, null, 100, GroupFilter.All, BackendFilter.DeepSeek).Rows;

        Session(IdA, Exchange(IdA, "问 A", "答 A", "A 的标题"));
        var b = Session(IdB, Exchange(IdB, "问 B", "答 B"));
        SessionIn(other, "--D-x--", IdC, Exchange(IdC, "问 C", "答 C"));

        var without = new CatalogSyncService(catalog, config).Sync(claude, codex);
        Assert.DoesNotContain(without.Coverage.Sources, s => s.Backend == ConversationKey.DeepSeek);
        Assert.Empty(Listed());

        DshPaths homes = new([_home, other]);
        var sync = new CatalogSyncService(catalog, config, dsh: () => homes);
        var first = sync.Sync(claude, codex);
        var tally = Assert.Single(first.Coverage.Sources, s => s.Backend == ConversationKey.DeepSeek);
        Assert.Equal(3, tally.Records);
        Assert.Equal(SourceCoverage.Resolved, tally.Coverage);
        Assert.Equal(3, Listed().Count);
        Assert.False(sync.Sync(claude, codex).ChangedAnything);

        // A home no longer read says nothing about its sessions: they stay listed, not marked gone.
        homes = new([_home]);
        Assert.Equal(0, sync.Sync(claude, codex).RowsRemoved);
        Assert.All(Listed(), r => Assert.Null(r.SourceGoneUtc));

        // A session deleted from a home that is read: kept, marked gone.
        Directory.Delete(Path.GetDirectoryName(b)!, recursive: true);
        Assert.Equal(1, sync.Sync(claude, codex).RowsRemoved);
        Assert.Equal([IdB], Listed().Where(r => r.SourceGoneUtc is not null).Select(r => r.Key.NativeSessionId).ToArray());
        Assert.Equal(3, Listed().Count);

        var a = Listed().Single(r => r.Key.NativeSessionId == IdA);
        Assert.Equal(a.NativeOpenRef, TranscriptLocator.Find(a, config, codex));
        Assert.Equal(TranscriptLocator.Find(a, config, codex), TranscriptLocator.Find(a with { NativeOpenRef = null }, config, codex));
        Assert.Empty(catalog.Page(Visibility.Active, null, null, 100, GroupFilter.All, BackendFilter.Gemini).Rows);
    }

    [Fact]
    public void NothingInAHomeIsChangedByReadingIt()
    {
        Session(IdA, Exchange(IdA, "问", "答", "标题"));
        Directory.CreateDirectory(Path.Combine(_home, "storages"));
        File.WriteAllText(Path.Combine(_home, "storages", "workspace.json"), Line(new { global = new { archivedSessionIds = Array.Empty<string>() } }));
        File.WriteAllText(Path.Combine(_home, ".credentials.yaml"), "never: read");
        var before = Snapshot(_home);

        // The credentials held open exclusively: reading the sessions must not need them.
        using (new FileStream(Path.Combine(_home, ".credentials.yaml"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var row = Assert.Single(new DshMetadataReader(Paths).Read().Rows);
            var path = row.NativeOpenRef!;
            DshSession.ReadEntries(path);
            DshSession.ReadCalls(path);
            using var reader = new TranscriptReader(path);
            reader.Read();
        }
        Assert.Equal(before, Snapshot(_home));
    }

    private static Dictionary<string, (string Hash, DateTime Written)> Snapshot(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToDictionary(
            f => f, f => (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))), File.GetLastWriteTimeUtc(f)));

    [Fact]
    public void SettingsKeepTheAddedHomesTidyAndTheDeepSeekChoices()
    {
        var file = Path.Combine(_dir, "settings.json");
        Assert.True(SettingsStore.Save(file, new LauncherSettings
        {
            DshHomes = [@" D:\Tools\DSH Home ", @"d:\tools\dsh home", "", @"E:\dsh"],
            UsageApp = "DeepSeek",
            AppFilter = "DeepSeek",
        }));
        var loaded = SettingsStore.Load(file);
        Assert.Equal([@"D:\Tools\DSH Home", @"E:\dsh"], loaded.DshHomes);
        Assert.Equal("DeepSeek", loaded.UsageApp);
        Assert.Equal("DeepSeek", loaded.AppFilter);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
    }
}
