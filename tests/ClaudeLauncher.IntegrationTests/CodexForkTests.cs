using System.Text;
using System.Text.Json;
using ClaudeLauncher.Core.Contracts;
using ClaudeLauncher.Infrastructure.Backends;
using ClaudeLauncher.Infrastructure.Catalog;
using ClaudeLauncher.Infrastructure.Changes;
using ClaudeLauncher.Infrastructure.Search;

namespace ClaudeLauncher.IntegrationTests;

/// <summary>
/// A Codex task forked from another starts with the other's history, written all at once at
/// the moment of the fork. It stays a task of its own, as in ChatGPT; only what came after the
/// fork is its own — for search, for its changed files, for memory drafts.
/// </summary>
public sealed class CodexForkTests : IDisposable
{
    private const string Parent = "019dc2cf-0000-7000-8000-000000000001";
    private const string Fork = "019dc787-0000-7000-8000-000000000002";
    private const string Moment = "2026-04-26T02:03:39.000Z";

    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "launcher-fork", Guid.NewGuid().ToString("n"))).FullName;

    private static string Line(string at, string type, object payload) =>
        JsonSerializer.Serialize(new { timestamp = at, type, payload });

    private static string Said(string at, string who, string text) =>
        Line(at, "event_msg", new { type = who == "user" ? "user_message" : "agent_message", message = text });

    private static string Meta(string? forkedFrom) =>
        Line(Moment, "session_meta", new { id = Fork, forked_from_id = forkedFrom, timestamp = Moment, cwd = @"G:\proj" });

    private string Write(params string?[] lines)
    {
        var path = Path.Combine(_dir, $"rollout-2026-04-26T10-03-34-{Guid.NewGuid()}.jsonl");
        File.WriteAllText(path, string.Join('\n', lines.Where(l => l is not null)) + '\n', new UTF8Encoding(false));
        return path;
    }

    /// <summary>
    /// A fork: the copied turn written at the fork's moment, then a new one. Newer ChatGPT marks
    /// where the copy ends; older rollouts only tell it by time.
    /// </summary>
    private string ForkRollout(bool marked) => Write(
        Meta(Parent),
        Said("2026-04-26T02:03:39.010Z", "user", "第一问 KASP"),
        Said("2026-04-26T02:03:39.020Z", "agent", "第一答"),
        marked ? Line("2026-04-26T02:03:39.030Z", "event_msg", new { type = "thread_settings_applied" }) : null,
        Said(marked ? "2026-04-26T02:03:39.500Z" : "2026-04-26T02:04:10.000Z", "user", "分叉后的新问题 引物"),
        Said("2026-04-26T02:04:20.000Z", "agent", "新回答"));

    private static readonly OwnPart AfterFork = new(null, AfterFork: true);

    [Fact]
    public void WhatATaskWasForkedFromIsReadFromItsFirstLine()
    {
        Assert.Equal(Parent, OwnPart.ForkedFrom(ForkRollout(marked: true)));
        Assert.Null(OwnPart.ForkedFrom(Write(Meta(null), Said(Moment, "user", "一个普通任务"))));
        Assert.Null(OwnPart.ForkedFrom(Path.Combine(_dir, "no-such-rollout.jsonl")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OnlyWhatCameAfterTheForkIsTheTasksOwn(bool marked)
    {
        var path = ForkRollout(marked);

        Assert.Equal(4, TranscriptReader.ReadDialogueFrom(path, 0).Entries.Count);
        Assert.Equal(["分叉后的新问题 引物", "新回答"],
            TranscriptReader.ReadDialogueFrom(path, 0, part: AfterFork).Entries.Select(e => e.Text));
    }

    [Fact]
    public void ReadingOnFromTheMiddleIsPastTheCopyAlready()
    {
        var path = ForkRollout(marked: false);
        var lines = File.ReadAllLines(path);
        var offset = lines.Take(3).Sum(l => Encoding.UTF8.GetByteCount(l) + 1);     // up to the new question

        Assert.Equal(["分叉后的新问题 引物", "新回答"],
            TranscriptReader.ReadDialogueFrom(path, offset, part: AfterFork).Entries.Select(e => e.Text));
    }

    [Fact]
    public void TheIndexHoldsOnlyWhatWasSaidAfterTheFork()
    {
        using var index = new ContentIndex(Path.Combine(_dir, "index.sqlite"));
        index.Update([new ContentIndex.Source("fork", ForkRollout(marked: false), AfterFork)]);

        Assert.Empty(index.Hits("fork", "KASP"));
        Assert.Single(index.Hits("fork", "引物"));
    }

    [Fact]
    public void ChangesCopiedIntoAForkWereMadeInTheTaskItCameFrom()
    {
        static IEnumerable<string> Applied(string at, string call, string file) =>
        [
            Line(at, "response_item", new { type = "custom_tool_call", call_id = call, name = "apply_patch",
                input = $"*** Begin Patch\n*** Update File: {file}\n@@\n-old\n+new\n*** End Patch" }),
            Line(at, "event_msg", new
            {
                type = "patch_apply_end",
                call_id = call,
                success = true,
                changes = new Dictionary<string, object> { [@"G:\proj\" + file] = new { type = "update", unified_diff = "@@ -1 +1 @@\n-old\n+new\n" } },
            }),
        ];
        var path = Write([Meta(Parent), .. Applied("2026-04-26T02:03:39.100Z", "c1", "copied.py"),
            .. Applied("2026-04-26T02:05:00.000Z", "c2", "mine.py")]);

        Assert.Equal(2, ChangeScanner.Read(path, codex: true).Count);
        var mine = Assert.Single(ChangeScanner.Read(path, codex: true, part: AfterFork));
        Assert.Equal(@"G:\proj\mine.py", mine.Path);
    }

    private static SessionMetadata CodexTask(string id, string? forkedFrom = null, bool onDisk = true) => new()
    {
        Key = new ConversationKey(ConversationKey.Codex, @"C:\Users\x\.codex", "default", id),
        CliSessionId = id,
        Title = "对比海淀24-25气象差异",
        SearchKey = "对比海淀24-25气象差异",
        SourceRevision = "1",
        ResumedFrom = forkedFrom,
        TranscriptBytes = onDisk ? 1000 : null,
        NativeArchiveState = NativeArchiveState.Active,
    };

    [Fact]
    public void AForkStaysATaskOfItsOwnAndIsLinkedWhileTheTaskItCameFromIsThere()
    {
        var rows = new List<SessionMetadata> { CodexTask(Parent), CodexTask(Fork, Parent), CodexTask("019d0000-0000-7000-8000-000000000009", "gone") };
        CatalogSyncService.FoldResumedCopies(rows);

        Assert.All(rows, r => Assert.Null(r.MergedInto));
        Assert.Equal(Parent, rows[1].ResumedFrom);
        Assert.Null(rows[2].ResumedFrom);

        // A task that is only a line in the database, its record gone: the fork holds all there is.
        var orphaned = new List<SessionMetadata> { CodexTask(Parent, onDisk: false), CodexTask(Fork, Parent) };
        CatalogSyncService.FoldResumedCopies(orphaned);
        Assert.Null(orphaned[1].ResumedFrom);

        using var catalog = new SqliteCatalog(Path.Combine(_dir, "catalog.sqlite"));
        catalog.Initialize(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "CatalogSchema.sql")));
        catalog.UpsertRange(rows);
        var listed = catalog.Page(Visibility.Active, null, null, 10).Rows.ToDictionary(r => r.Key.NativeSessionId);

        Assert.Equal(Parent, catalog.Resumes(listed[Fork]).From?.Key.NativeSessionId);
        Assert.Equal([Fork], catalog.Resumes(listed[Parent]).Into.Select(r => r.Key.NativeSessionId));
        Assert.Equal(0, catalog.Resumes(listed[Parent]).Folded);
        Assert.Equal(AfterFork, OwnPart.For(listed[Fork]));
        Assert.Equal(OwnPart.Whole, OwnPart.For(listed[Parent]));
    }

    [Fact]
    public void AMemoryDraftOfACopyTakesOnlyWhatWasSaidInIt()
    {
        // A resumed Claude session: the copied instruction is the original's, the new one its own.
        var path = Path.Combine(_dir, "cccccccc-0000-4000-8000-000000000003.jsonl");
        static string User(string session, string text) => JsonSerializer.Serialize(new
        {
            type = "user", sessionId = session, message = new { role = "user", content = new object[] { new { type = "text", text } } },
        });
        File.WriteAllLines(path, [User("aaaaaaaa-0000-4000-8000-000000000001", "以后一律用中文回答"),
            User("cccccccc-0000-4000-8000-000000000003", "以后每次都先写测试")], new UTF8Encoding(false));

        Assert.Equal(2, MemoryCandidateExtractor.Extract(path, "c", "t", null, null).Candidates.Count);
        var draft = MemoryCandidateExtractor.Extract(path, "c", "t", null, null,
            part: new OwnPart("cccccccc-0000-4000-8000-000000000003", false));
        Assert.Equal("以后每次都先写测试", Assert.Single(draft.Candidates).Quote);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
    }
}
