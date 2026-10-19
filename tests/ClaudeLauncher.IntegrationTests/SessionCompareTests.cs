using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using ClaudeLauncher.Core.Contracts;
using ClaudeLauncher.Infrastructure.Backends;
using ClaudeLauncher.Infrastructure.Changes;
using ClaudeLauncher.Infrastructure.Compare;
using ClaudeLauncher.Infrastructure.Usage;

namespace ClaudeLauncher.IntegrationTests;

/// <summary>
/// Two conversations side by side, read from their transcripts on demand: turns built from the
/// dialogue of each app's format, a copy's own part only, tokens and changed files per side, and
/// nothing kept once the comparison is let go.
/// </summary>
public sealed class SessionCompareTests : IDisposable
{
    private readonly string _dir;

    public SessionCompareTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "launcher-compare", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private string Write(string name, params object[] lines)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, string.Join('\n', lines.Select(l => l as string ?? JsonSerializer.Serialize(l))) + '\n',
            new UTF8Encoding(false));
        return path;
    }

    private static DateTimeOffset At(string iso) => DateTimeOffset.Parse(iso, System.Globalization.CultureInfo.InvariantCulture);

    // ------------------------------------------------------------- Claude lines

    private static object Ask(string text, string at, string session = "s1") => new
    {
        type = "user", sessionId = session, timestamp = at,
        message = new { role = "user", content = text },
    };

    private static object Say(string text, string at, string session = "s1", string? id = null, object? usage = null) => new
    {
        type = "assistant", sessionId = session, timestamp = at, requestId = id is null ? null : "req-" + id,
        message = new { role = "assistant", id, model = "claude-test", content = new object[] { new { type = "text", text } }, usage },
    };

    private static object Usage(long input, long output, long cacheRead = 0) =>
        new { input_tokens = input, cache_creation_input_tokens = 0, cache_read_input_tokens = cacheRead, output_tokens = output };

    private static object Tool(string id, string name, object input, string at, string session = "s1") => new
    {
        type = "assistant", sessionId = session, timestamp = at,
        message = new { role = "assistant", content = new object[] { new { type = "tool_use", id, name, input } } },
    };

    private static object Result(string id, string at, bool error = false, string session = "s1") => new
    {
        type = "user", sessionId = session, timestamp = at,
        message = new { role = "user", content = new object[] { new { tool_use_id = id, type = "tool_result", is_error = error, content = "工具输出" } } },
    };

    /// <summary>A Write call and its result: two lines added, the file new when <paramref name="created"/>.</summary>
    private static object[] Wrote(string id, string file, string at, bool created = false, bool error = false) =>
    [
        Tool(id, "Write", new { file_path = file, content = "one\ntwo\n" }, at),
        new
        {
            type = "user", sessionId = "s1", timestamp = at,
            message = new { role = "user", content = new object[] { new { tool_use_id = id, type = "tool_result", is_error = error, content = "ok" } } },
            toolUseResult = error ? null : new { type = created ? "create" : "update", filePath = file },
        },
    ];

    // ------------------------------------------------------------------ turns

    [Fact]
    public void AClaudeTurnIsAPromptWithEveryAnswerUpToTheNext()
    {
        var path = Write("claude.jsonl",
            Ask("第一个问题", "2026-10-01T08:00:00Z"),
            Say("先看看目录", "2026-10-01T08:00:10Z"),
            Tool("t1", "Bash", new { command = "ls" }, "2026-10-01T08:00:11Z"),
            Result("t1", "2026-10-01T08:00:12Z"),
            Say("看完了，是这样的", "2026-10-01T08:01:00Z"),
            Ask("第二个问题", "2026-10-01T08:02:00Z"),
            Say("好", "2026-10-01T08:03:00Z"));

        var turns = SessionComparer.ReadTurns(path, OwnPart.Whole);

        Assert.Equal(2, turns.Count);
        var first = turns[0];
        Assert.Equal((1, "第一个问题"), (first.Number, first.Prompt));
        Assert.Equal("先看看目录\n\n看完了，是这样的", first.Answer);
        Assert.Equal(At("2026-10-01T08:00:00Z"), first.At);
        Assert.Equal(At("2026-10-01T08:01:00Z"), first.AnsweredAt);
        Assert.Equal(first.Answer.Length, first.AnswerLength);
        Assert.Equal((2, "第二个问题", "好"), (turns[1].Number, turns[1].Prompt, turns[1].Answer));
        Assert.Equal(At("2026-10-01T08:03:00Z"), turns[1].AnsweredAt);
    }

    private static string Codex(string at, string type, object payload) =>
        JsonSerializer.Serialize(new { timestamp = at, type, payload });

    private static string CodexMessage(string at, string role, string text) =>
        Codex(at, "response_item", new { type = "message", role, content = new[] { new { type = role == "user" ? "input_text" : "output_text", text } } });

    [Fact]
    public void ACodexTurnLeavesOutContextToolsAndMirroredEvents()
    {
        var path = Write("rollout-codex.jsonl",
            Codex("2026-10-02T09:00:00Z", "session_meta", new { id = "11111111-0000-4000-8000-000000000001", cwd = @"D:\work\one" }),
            CodexMessage("2026-10-02T09:00:01Z", "user", "<environment_context>\n  <cwd>D:\\work\\one</cwd>\n</environment_context>"),
            Codex("2026-10-02T09:00:02Z", "event_msg", new { type = "user_message", message = "把测试跑一遍" }),
            CodexMessage("2026-10-02T09:00:02Z", "user", "把测试跑一遍"),
            Codex("2026-10-02T09:00:05Z", "response_item", new { type = "function_call", name = "shell", call_id = "c1", arguments = "{\"command\":[\"dotnet\",\"test\"]}" }),
            Codex("2026-10-02T09:00:30Z", "response_item", new { type = "function_call_output", call_id = "c1", output = "Passed!" }),
            CodexMessage("2026-10-02T09:00:40Z", "assistant", "全部通过。"),
            Codex("2026-10-02T09:00:40Z", "event_msg", new { type = "agent_message", message = "全部通过。" }),
            CodexMessage("2026-10-02T09:01:00Z", "assistant", "要我提交吗？"));

        var turn = Assert.Single(SessionComparer.ReadTurns(path, OwnPart.Whole));

        Assert.Equal((1, "把测试跑一遍"), (turn.Number, turn.Prompt));
        Assert.Equal("全部通过。\n\n要我提交吗？", turn.Answer);
        Assert.Equal(At("2026-10-02T09:00:02Z"), turn.At);
        Assert.Equal(At("2026-10-02T09:01:00Z"), turn.AnsweredAt);
    }

    private string Gemini(string name, params object[] messages)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            sessionId = "0a1b2c3d-0000-4000-8000-00000000000a", projectHash = "p", startTime = "2026-10-03T08:00:00Z",
            lastUpdated = "2026-10-03T08:10:00Z", kind = "main", messages,
        }), new UTF8Encoding(false));
        return path;
    }

    private static object GeminiAsk(string id, string at, string text) =>
        new { id, timestamp = at, type = "user", content = new[] { new { text } } };

    private static object GeminiSay(string id, string at, string text, long input = 100, long output = 10) =>
        new { id, timestamp = at, type = "gemini", content = text, model = "gemini-test",
              tokens = new { input, output, cached = 0, thoughts = 0, tool = 0, total = input + output } };

    [Fact]
    public void AGeminiSessionReadWholeGivesTurnsWithTheirTimes()
    {
        var path = Gemini("session-2026-10-03T08-00-0a1b2c3d.json",
            GeminiAsk("u0", "2026-10-03T08:00:00Z", "解释这个函数"),
            GeminiSay("m0", "2026-10-03T08:00:20Z", "它读取配置"),
            GeminiSay("m1", "2026-10-03T08:00:40Z", "然后返回结果"),
            GeminiAsk("u1", "2026-10-03T08:05:00Z", "谢谢"));

        var turns = SessionComparer.ReadTurns(path, OwnPart.Whole);

        Assert.Equal(2, turns.Count);
        Assert.Equal("它读取配置\n\n然后返回结果", turns[0].Answer);
        Assert.Equal(At("2026-10-03T08:00:00Z"), turns[0].At);
        Assert.Equal(At("2026-10-03T08:00:40Z"), turns[0].AnsweredAt);
        Assert.Equal(("谢谢", ""), (turns[1].Prompt, turns[1].Answer));
        Assert.Null(turns[1].AnsweredAt);
    }

    [Fact]
    public void WhatTheAssistantSaidBeforeAnyPromptIsATurnWithoutOne()
    {
        var turns = SessionComparer.BuildTurns(
        [
            new TranscriptEntry(0, MessageRole.Assistant, "你好，有什么可以帮你？"),
            new TranscriptEntry(1, MessageRole.Note, "系统提醒"),
            new TranscriptEntry(2, MessageRole.User, "写个脚本"),
            new TranscriptEntry(3, MessageRole.ToolCall, "Bash  ls"),
            new TranscriptEntry(4, MessageRole.Assistant, "写好了"),
        ]);

        Assert.Equal(2, turns.Count);
        Assert.False(turns[0].HasPrompt);
        Assert.Equal((0, "", "你好，有什么可以帮你？"), (turns[0].Number, turns[0].Prompt, turns[0].Answer));
        Assert.Equal((1, "写个脚本", "写好了"), (turns[1].Number, turns[1].Prompt, turns[1].Answer));
    }

    [Fact]
    public void AVeryLongPromptOrAnswerIsCutAndSaysHowMuchIsLeft()
    {
        var pasted = new string('文', SessionComparer.MaxTextChars + 5000);
        var turns = SessionComparer.BuildTurns(
        [
            new TranscriptEntry(0, MessageRole.User, pasted),
            new TranscriptEntry(1, MessageRole.Assistant, new string('答', SessionComparer.MaxTextChars - 10)),
            new TranscriptEntry(2, MessageRole.Assistant, new string('续', 300)),
        ]);

        var turn = Assert.Single(turns);
        Assert.Equal(pasted.Length, turn.PromptLength);
        Assert.StartsWith(new string('文', SessionComparer.MaxTextChars), turn.Prompt);
        Assert.EndsWith("（后面还有 5,000 字，完整内容在对话记录里）", turn.Prompt);
        Assert.Equal(SessionComparer.MaxTextChars - 10 + 2 + 300, turn.AnswerLength);
        Assert.EndsWith("（后面还有 292 字，完整内容在对话记录里）", turn.Answer);
        Assert.True(turn.Answer.Length < SessionComparer.MaxTextChars + 100);
    }

    // ------------------------------------------------------------- comparison

    [Fact]
    public void TwoConversationsLineUpTheirPromptsAndCountBothSides()
    {
        var a = Write("a.jsonl",
            Ask("写一个 CSV 解析器", "2026-10-01T08:00:00Z"),
            Say("写好了", "2026-10-01T08:05:00Z", id: "a1", usage: Usage(100, 20)),
            Ask("给它加单元测试", "2026-10-01T09:00:00Z"),
            Say("加好了", "2026-10-01T10:30:00Z", id: "a2", usage: Usage(50, 30, cacheRead: 100)));
        var b = Write("b.jsonl",
            Ask("写一个 CSV 解析器", "2026-10-02T08:00:00Z"),
            Say("这样写", "2026-10-02T08:01:00Z", id: "b1", usage: Usage(10, 1)),
            Ask("顺便解释一下正则表达式", "2026-10-02T08:02:00Z"),
            Say("正则是……", "2026-10-02T08:03:00Z"),
            Ask("给它加上单元测试", "2026-10-02T08:04:00Z"),
            Say("好", "2026-10-02T08:05:00Z"));

        var result = SessionComparer.Compare(new ConversationSide(a, ConversationKey.CliCode),
            new ConversationSide(b, ConversationKey.DesktopCode));

        Assert.Equal([TurnMatch.Identical, TurnMatch.OnlyB, TurnMatch.Similar], result.Rows.Select(r => r.Match));
        Assert.Equal((1, 1, 0, 1), (result.Identical, result.Similar, result.OnlyA, result.OnlyB));
        Assert.Equal(("加好了", "好"), (result.Rows[2].A!.Answer, result.Rows[2].B!.Answer));
        Assert.InRange(result.Rows[2].Similarity, PromptAligner.SimilarAt, 0.999);
        Assert.False(result.Approximate);

        Assert.Equal(2, result.A.Prompts);
        Assert.Equal(TimeSpan.FromMinutes(150), result.A.Span);
        Assert.Equal(TokenSource.Transcript, result.A.TokensFrom);
        Assert.Equal(new TokenCounts(2, 150, 0, 100, 50), result.A.Tokens);
        Assert.Equal(3, result.B.Prompts);
        Assert.Equal(new TokenCounts(1, 10, 0, 0, 1), result.B.Tokens);
        Assert.Equal(At("2026-10-02T08:00:00Z"), result.B.First);
        Assert.Equal(At("2026-10-02T08:05:00Z"), result.B.Last);
    }

    [Fact]
    public void AResumedCopyCountsOnlyWhatWasSaidAndSpentAfterTheResume()
    {
        // The copy starts with the old session's lines, under the old id, then goes on under its own.
        var path = Write("resumed.jsonl",
            Ask("旧问题", "2026-10-01T08:00:00Z", session: "old"),
            Say("旧回答", "2026-10-01T08:01:00Z", session: "old", id: "o1", usage: Usage(1000, 100)),
            Ask("新问题", "2026-10-05T08:00:00Z", session: "new"),
            // One response written over two lines, its output growing: counted once, at its largest.
            Say("新回答前半", "2026-10-05T08:00:30Z", session: "new", id: "n1", usage: Usage(40, 5)),
            Say("新回答后半", "2026-10-05T08:00:31Z", session: "new", id: "n1", usage: Usage(40, 9)));

        var copy = SessionComparer.Compare(new ConversationSide(path, ConversationKey.CliCode, new OwnPart("new", false)),
            new ConversationSide(path, ConversationKey.CliCode));

        var own = Assert.Single(copy.Rows, r => r.A is not null).A!;
        Assert.Equal("新问题", own.Prompt);
        Assert.Equal(1, copy.A.Prompts);
        Assert.Equal(At("2026-10-05T08:00:00Z"), copy.A.First);
        Assert.Equal(new TokenCounts(1, 40, 0, 0, 9), copy.A.Tokens);

        // Read whole, the same file has both prompts and both calls.
        Assert.Equal(2, copy.B.Prompts);
        Assert.Equal(new TokenCounts(2, 1040, 0, 0, 109), copy.B.Tokens);
    }

    [Fact]
    public void TokensTheLedgerCountedAreTakenAsTheyAre()
    {
        var path = Write("ledger.jsonl",
            Ask("问题", "2026-10-01T08:00:00Z"),
            Say("回答", "2026-10-01T08:01:00Z", id: "x", usage: Usage(1, 1)));
        var known = new TokenCounts(7, 700, 70, 7000, 77);

        var result = SessionComparer.Compare(new ConversationSide(path, ConversationKey.CliCode, KnownTokens: known),
            new ConversationSide(path, ConversationKey.CliCode));

        Assert.Equal((TokenSource.Ledger, known), (result.A.TokensFrom, result.A.Tokens!.Value));
        Assert.Equal((TokenSource.Transcript, new TokenCounts(1, 1, 0, 0, 1)), (result.B.TokensFrom, result.B.Tokens!.Value));
    }

    [Fact]
    public void AnImportedConversationHasNeitherTokensNorFiles()
    {
        var imported = Gemini("session-imported.json",
            GeminiAsk("u0", "2026-10-03T08:00:00Z", "网页上问的"),
            GeminiSay("m0", "2026-10-03T08:00:20Z", "网页上答的"));
        var gemini = Gemini("session-2026-10-03T08-00-0a1b2c3d.json",
            GeminiAsk("u0", "2026-10-03T08:00:00Z", "网页上问的"),
            GeminiSay("m0", "2026-10-03T08:00:20Z", "终端里答的", input: 300, output: 30));

        var result = SessionComparer.Compare(new ConversationSide(imported, ConversationKey.ClaudeWeb),
            new ConversationSide(gemini, ConversationKey.Gemini));

        Assert.Equal((null, TokenSource.NotRecorded), (result.A.Tokens, result.A.TokensFrom));
        Assert.Equal(new TokenCounts(1, 300, 0, 0, 30), result.B.Tokens);
        Assert.Null(result.A.Files);
        Assert.Null(result.B.Files);
        Assert.Null(result.Files);
        Assert.Equal(TurnMatch.Identical, Assert.Single(result.Rows).Match);
    }

    [Fact]
    public void ChangedFilesSplitIntoEitherSideAloneAndBoth()
    {
        // Two attempts at one task in two checkouts: the shared file is the same place in each.
        var a = Write("files-a.jsonl",
        [
            Ask("改代码", "2026-10-01T08:00:00Z"),
            .. Wrote("w1", @"D:\work\one\src\shared.cs", "2026-10-01T08:01:00Z", created: true),
            .. Wrote("w2", @"D:\work\one\docs\a-only.md", "2026-10-01T08:02:00Z"),
            .. Wrote("w3", @"D:\work\one\src\shared.cs", "2026-10-01T08:03:00Z"),
            .. Wrote("w4", @"D:\work\one\failed.txt", "2026-10-01T08:04:00Z", error: true),
            .. Wrote("w5", @"C:\elsewhere\notes.txt", "2026-10-01T08:05:00Z"),
        ]);
        var b = Write("files-b.jsonl",
        [
            Ask("改代码", "2026-10-02T08:00:00Z"),
            .. Wrote("v1", @"D:\work\two\src\Shared.cs", "2026-10-02T08:01:00Z"),
            .. Wrote("v2", @"D:\work\two\b-only.md", "2026-10-02T08:02:00Z"),
        ]);

        var files = SessionComparer.Compare(
            new ConversationSide(a, ConversationKey.CliCode, ProjectFolder: @"D:\work\one\"),
            new ConversationSide(b, ConversationKey.CliCode, ProjectFolder: @"D:\work\two")).Files!;

        Assert.Equal([@"C:\elsewhere\notes.txt", @"docs\a-only.md"], files.OnlyA.Select(f => f.Key));
        Assert.Equal([@"b-only.md"], files.OnlyB.Select(f => f.Key));
        var shared = Assert.Single(files.Both);
        Assert.Equal(@"src\shared.cs", shared.Key, ignoreCase: true);
        Assert.Equal((2, 4, ChangeKind.Added), (shared.A.Times, shared.A.Added, shared.A.Kind));
        Assert.Equal((1, 2, @"D:\work\two\src\Shared.cs"), (shared.B.Times, shared.B.Added, shared.B.FullPath));
        Assert.DoesNotContain(files.OnlyA, f => f.Key.Contains("failed", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(@"D:\work\one\src\a.cs", @"D:\work\one", @"src\a.cs")]
    [InlineData("D:/work/one/src/a.cs", @"D:\work\one", @"src\a.cs")]           // Codex joins with forward slashes
    [InlineData(@"d:\WORK\one\a.cs", @"D:\work\one", "a.cs")]                    // Windows paths ignore case
    [InlineData(@"D:\work\one2\a.cs", @"D:\work\one", @"D:\work\one2\a.cs")]     // a sibling folder is not inside
    [InlineData(@"D:\work\one", @"D:\work\one", @"D:\work\one")]
    [InlineData(@"C:\other\a.cs", null, @"C:\other\a.cs")]
    public void AChangedFileIsKnownByWhereItIsInsideTheWorkingDirectory(string path, string? project, string key) =>
        Assert.Equal(key, SessionComparer.KeyOf(path, project));

    [Fact]
    public void ASideWhoseSourceRecordsNoEditsLeavesTheOthersFilesStanding()
    {
        var claude = Write("edits.jsonl",
        [
            Ask("写文件", "2026-10-01T08:00:00Z"),
            .. Wrote("w1", @"D:\work\one\a.cs", "2026-10-01T08:01:00Z"),
        ]);
        var gemini = Gemini("session-2026-10-03T08-00-0a1b2c3d.json", GeminiAsk("u0", "2026-10-03T08:00:00Z", "写文件"));

        var result = SessionComparer.Compare(new ConversationSide(claude, ConversationKey.CliCode, ProjectFolder: @"D:\work\one"),
            new ConversationSide(gemini, ConversationKey.Gemini));

        Assert.Equal(["a.cs"], result.A.Files!.Select(f => f.Key));
        Assert.Null(result.B.Files);
        Assert.Equal(["a.cs"], result.Files!.OnlyA.Select(f => f.Key));
        Assert.Empty(result.Files.OnlyB);
        Assert.Empty(result.Files.Both);
    }

    [Fact]
    public void ACancelledComparisonStops()
    {
        var path = Write("cancel.jsonl", Ask("问题", "2026-10-01T08:00:00Z"));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => SessionComparer.Compare(
            new ConversationSide(path, ConversationKey.CliCode), new ConversationSide(path, ConversationKey.CliCode), cancel.Token));
    }

    [Fact]
    public void AFileThatCannotBeReadIsPutDownToItsSide()
    {
        var claude = Write("fine.jsonl", Ask("问题", "2026-10-01T08:00:00Z"), Say("回答", "2026-10-01T08:01:00Z"));
        // Gemini writes its file again whole: caught half written, it does not parse.
        var halfWritten = Path.Combine(_dir, "session-2026-10-03T08-00-0a1b2c3d.json");
        File.WriteAllText(halfWritten, """{ "sessionId": "0a1b2c3d", "messages": [ { "id": "u0", """);
        var gone = Path.Combine(_dir, "gone.jsonl");

        var right = Assert.Throws<UnreadableSideException>(() => SessionComparer.Compare(
            new ConversationSide(claude, ConversationKey.CliCode), new ConversationSide(halfWritten, ConversationKey.Gemini)));
        Assert.Null(right.A);
        Assert.Contains("正在写入或已损坏", right.B!.Message);

        var left = Assert.Throws<UnreadableSideException>(() => SessionComparer.Compare(
            new ConversationSide(gone, ConversationKey.CliCode), new ConversationSide(claude, ConversationKey.CliCode)));
        Assert.IsAssignableFrom<IOException>(left.A);
        Assert.Null(left.B);

        // Each side is read even when the other could not be.
        var both = Assert.Throws<UnreadableSideException>(() => SessionComparer.Compare(
            new ConversationSide(gone, ConversationKey.CliCode), new ConversationSide(halfWritten, ConversationKey.Gemini)));
        Assert.NotNull(both.A);
        Assert.NotNull(both.B);

        // Still an I/O failure to a caller that does not ask which side.
        Assert.IsAssignableFrom<IOException>(both);
    }

    [Theory]
    [InlineData(TurnMatch.Identical, 1.0, 100)]
    [InlineData(TurnMatch.Similar, 1.0, 99)]
    [InlineData(TurnMatch.Similar, 0.996, 99)]
    [InlineData(TurnMatch.Similar, 0.875, 87)]
    [InlineData(TurnMatch.Similar, 0.6, 60)]
    [InlineData(TurnMatch.OnlyA, 0.0, 0)]
    public void APairShowsHowAlikeItIsRoundedDownAndNeverAllUnlessIdentical(TurnMatch match, double similarity, int percent) =>
        Assert.Equal(percent, new AlignedTurn(null, null, match, similarity).Percent);

    [Fact]
    public void PromptsAlikeByTheSameLongOpeningAreNotShownAsTheSame()
    {
        var spec = string.Concat(Enumerable.Range(0, 100).Select(i => $"要求{i}：照规格实现。"));
        var a = Write("spec-a.jsonl", Ask(spec + "用 Python 实现", "2026-10-01T08:00:00Z"), Say("好", "2026-10-01T08:01:00Z"));
        var b = Write("spec-b.jsonl", Ask(spec + "用 Rust 实现", "2026-10-02T08:00:00Z"), Say("好", "2026-10-02T08:01:00Z"));

        var row = Assert.Single(SessionComparer.Compare(new ConversationSide(a, ConversationKey.CliCode),
            new ConversationSide(b, ConversationKey.CliCode)).Rows);

        Assert.Equal(TurnMatch.Similar, row.Match);
        Assert.Equal(1, row.Similarity, 9);
        Assert.True(row.OpeningOnly);
        Assert.Equal(99, row.Percent);
    }

    // ------------------------------------------------------------- releasing

    [Fact]
    public void TheCompareCodeKeepsNoStaticState()
    {
        // Constants only: a cache here would keep what a closed window read for the rest of the run.
        var types = typeof(SessionComparer).Assembly.GetTypes()
            .Where(t => t.Namespace == typeof(SessionComparer).Namespace && !IsGenerated(t))
            .ToList();
        Assert.Contains(typeof(PromptAligner), types);
        var stored = types
            .SelectMany(t => t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .Where(f => !f.IsLiteral && !IsGenerated(f))
            .Select(f => $"{f.DeclaringType!.Name}.{f.Name}")
            .ToList();
        Assert.Empty(stored);

        static bool IsGenerated(MemberInfo member) =>
            member.IsDefined(typeof(CompilerGeneratedAttribute), false)
            || member.DeclaringType is { } outer && IsGenerated(outer);
    }

    [Fact]
    public void NothingOfAComparisonOutlivesItsResult()
    {
        var a = Write("live-a.jsonl", Ask("问题", "2026-10-01T08:00:00Z"), Say(new string('答', 5000), "2026-10-01T08:01:00Z"));
        var b = Write("live-b.jsonl", Ask("问题", "2026-10-02T08:00:00Z"), Say("短", "2026-10-02T08:01:00Z"));

        var (result, answer) = CompareWeakly(a, b);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(result.IsAlive);
        Assert.False(answer.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Result, WeakReference Answer) CompareWeakly(string a, string b)
    {
        var result = SessionComparer.Compare(new ConversationSide(a, ConversationKey.CliCode),
            new ConversationSide(b, ConversationKey.CliCode));
        Assert.Equal(5000, result.Rows[0].A!.Answer.Length);
        return (new WeakReference(result), new WeakReference(result.Rows[0].A!.Answer));
    }
}
