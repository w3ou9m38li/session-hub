using System.Text;
using System.Text.Json;
using ClaudeLauncher.Core.Contracts;
using ClaudeLauncher.Infrastructure.Backends;
using ClaudeLauncher.Infrastructure.Usage;
using static ClaudeLauncher.IntegrationTests.AnalysisFixtures;

namespace ClaudeLauncher.IntegrationTests;

/// <summary>
/// One conversation's use of its context, read on demand from its transcripts: the calls in
/// order, the cache that expired over a pause, the compactions, the tools that filled it — for
/// each app's format, with subagents apart and a copy's history read but not counted.
/// </summary>
public sealed class SessionAnalyzerTests : IDisposable
{
    private readonly string _dir;

    public SessionAnalyzerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "launcher-analysis", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
    }

    private string ClaudeWithSubagent(out string subagent)
    {
        var id = "0f0e0d0c-aaaa-bbbb-cccc-111122223333";
        var main = Write(Path.Combine(_dir, "projects", "p", id + ".jsonl"), ClaudeSession());
        subagent = Write(Path.Combine(_dir, "projects", "p", id, "subagents", "agent-a1b2c3.jsonl"),
        [
            Assistant("s1", 2, 1, 25_000, 0, block: ToolUse("toolu_s1", "Grep"), side: true),
            Result("toolu_s1", 2.1, new string('g', 100), side: true),
            Assistant("s2", 3, 1, 100, 25_001, side: true),
        ], createdMinutes: 2);
        return main;
    }

    [Fact]
    public void AClaudeConversationIsFollowedCallByCall()
    {
        var main = ClaudeWithSubagent(out var subagent);
        var files = SessionAnalyzer.FilesFor(main, AppFamily.Claude);
        Assert.Equal([main, subagent], files.Select(f => f.Path));
        Assert.Equal(SessionAnalyzer.MainLabel, files[0].Label);
        Assert.StartsWith("子代理", files[1].Label, StringComparison.Ordinal);

        var analysis = SessionAnalyzer.Analyze(files);

        // The conversation itself, the side chain written into it, the subagent: three caches.
        Assert.Equal(3, analysis.Series.Count);
        var line = analysis.Series[0];
        Assert.Equal(8, line.Calls.Count);                              // a response on two lines is one call
        Assert.Single(analysis.Series[1].Calls);
        Assert.Equal(2, analysis.Series[2].Calls.Count);
        Assert.Equal(11, analysis.Totals.Calls);
        Assert.Equal(39_910, analysis.Totals.MaxContext);
        Assert.Equal(TimeSpan.FromMinutes(32), analysis.Totals.Duration);

        // 19 minutes away: the cache had expired, and the whole context was written again.
        var rewrite = Assert.Single(analysis.Rewrites);
        Assert.Equal(31_000, rewrite.Tokens);
        Assert.Equal(30_515, rewrite.ContextBefore);
        Assert.Equal(TimeSpan.FromMinutes(19), rewrite.Gap);
        Assert.Equal(TimeSpan.FromMinutes(5), rewrite.Kept);
        Assert.Equal(SessionAnalyzer.MainLabel, rewrite.Series);
        Assert.True(line.Calls[2].LongGap);
        Assert.Equal(31_000, line.Calls[2].Rewritten);

        // One compaction the app marked (six minutes later too: not taken for a rewrite), one seen in the context halving.
        Assert.Equal(2, analysis.Compactions.Count);
        Assert.True(analysis.Compactions[0].Marked);
        Assert.Equal((31_005L, (long?)3_005), (analysis.Compactions[0].Before, analysis.Compactions[0].After));
        Assert.False(analysis.Compactions[1].Marked);
        Assert.Equal((39_910L, (long?)10_000), (analysis.Compactions[1].Before, analysis.Compactions[1].After));
        Assert.Equal(0, line.Calls[3].Rewritten);

        // What each tool's results came to, without the copy Claude Code keeps beside each.
        var lines = ClaudeSession();
        var read = analysis.Tools.Single(t => t.Name == "Read");
        Assert.Equal((1, Seen(lines[2])), (read.Results, read.Bytes));
        var bash = analysis.Tools.Single(t => t.Name == "Bash");
        Assert.Equal(Seen(lines[4]), bash.Bytes);
        Assert.InRange(bash.Bytes, 40_000, 40_500);                     // passed over unread, cut where the copy begins
        Assert.Equal(bash.Bytes, bash.Largest);
        Assert.Equal("Bash", analysis.Tools[0].Name);                   // the largest first
        Assert.Contains(analysis.Tools, t => t is { Name: "Grep", Results: 1 });
        Assert.Equal((long)Math.Round(bash.Bytes / 3.5), bash.EstimatedTokens);

        Assert.Contains(analysis.Tips, t => t.StartsWith("离开超过 5 分钟后继续", StringComparison.Ordinal)
                                            && t.Contains("重来了 1 次", StringComparison.Ordinal) && t.Contains("3.1 万", StringComparison.Ordinal));
        Assert.Contains(analysis.Tips, t => t.StartsWith("上下文压缩了 2 次（其中 1 次按上下文突然减半判断）", StringComparison.Ordinal));
        Assert.Contains(analysis.Tips, t => t.StartsWith("Bash 工具放进了 4", StringComparison.Ordinal));
        Assert.Contains(analysis.Tips, t => t.StartsWith("子代理、子任务共 3 次调用", StringComparison.Ordinal));
    }

    [Fact]
    public void AnHourCacheOutlastsAShortPause()
    {
        // Written for an hour, the cache is still there after nineteen minutes: a miss then is not an expiry.
        var path = Write(Path.Combine(_dir, "hour.jsonl"),
        [
            Assistant("1", 0, 10, 30_000, 0, longCache: true),
            Assistant("2", 1, 5, 500, 30_010, longCache: true),
            Assistant("3", 20, 5, 31_000, 0, longCache: true),
            Assistant("4", 90, 5, 31_100, 0, longCache: true),          // an hour and ten minutes later: expired
        ]);
        var analysis = SessionAnalyzer.Analyze([new AnalysisFile(path, AppFamily.Claude, SessionAnalyzer.MainLabel)]);
        var rewrite = Assert.Single(analysis.Rewrites);
        Assert.Equal(31_100, rewrite.Tokens);
        Assert.Equal(TimeSpan.FromHours(1), rewrite.Kept);
        // The pause it names is the hour the cache was kept, not the five minutes the nineteen outlasted.
        var tip = Assert.Single(analysis.Tips, t => t.Contains("缓存已经过期", StringComparison.Ordinal));
        Assert.StartsWith("离开超过 1 小时后继续，", tip, StringComparison.Ordinal);
    }

    [Fact]
    public void RewritesAfterAShortAndAnHourCacheNameBothTimes()
    {
        var path = Write(Path.Combine(_dir, "both.jsonl"),
        [
            Assistant("1", 0, 10, 30_000, 0),
            Assistant("2", 1, 5, 500, 30_010),
            Assistant("3", 20, 5, 31_000, 0, longCache: true),          // five minutes outlasted; from here an hour
            Assistant("4", 21, 5, 500, 31_005, longCache: true),
            Assistant("5", 100, 5, 32_000, 0, longCache: true),         // the hour outlasted too
        ]);
        var analysis = SessionAnalyzer.Analyze([new AnalysisFile(path, AppFamily.Claude, SessionAnalyzer.MainLabel)]);
        Assert.Equal([TimeSpan.FromMinutes(5), TimeSpan.FromHours(1)], analysis.Rewrites.Select(r => r.Kept));
        Assert.Contains(analysis.Tips, t => t.StartsWith("离开超过缓存保留的时间（5 分钟或 1 小时）后继续，缓存已经过期：整段上下文按缓存写入价重来了 2 次",
            StringComparison.Ordinal));
    }

    [Fact]
    public void AMarkerSplitBetweenTwoReadBlocksIsStillFound()
    {
        // The kept copy's key straddles the scanner's 1 MB blocks.
        var first = Assistant("1", 0, 5, 1_000, 0, block: ToolUse("toolu_big", "Read"));
        var opening = "{\"type\":\"user\",\"sessionId\":\"s-1\",\"message\":{\"role\":\"user\",\"content\":[{\"tool_use_id\":\"toolu_big\",\"type\":\"tool_result\",\"content\":\"";
        var block = 1024 * 1024;
        var fill = block - 7 - (Encoding.UTF8.GetByteCount(first) + 1) - opening.Length - 4;
        var result = opening + new string('z', fill) + "\"}]},\"toolUseResult\":{\"stdout\":\"" + new string('z', 5_000) + "\"},\"timestamp\":\"" + At(0.5) + "\"}";
        var path = Write(Path.Combine(_dir, "seam.jsonl"), [first, result, Assistant("2", 1, 5, 100, 1_005)]);
        Assert.Equal(block - 7, Encoding.UTF8.GetByteCount(first) + 1 + result.IndexOf(",\"toolUseResult\":", StringComparison.Ordinal));

        var analysis = SessionAnalyzer.Analyze([new AnalysisFile(path, AppFamily.Claude, SessionAnalyzer.MainLabel)]);

        var read = Assert.Single(analysis.Tools);
        Assert.Equal(Seen(result), read.Bytes);
    }

    [Fact]
    public void AResumedCopysHistoryStandsBeforeItButIsNotCounted()
    {
        // Resumed 40 minutes after the original stopped: its first call finds the cache expired.
        var path = Write(Path.Combine(_dir, "resumed.jsonl"),
        [
            Assistant("1", 0, 10, 30_000, 0, block: ToolUse("toolu_1", "Read"), session: "old"),
            Result("toolu_1", 0.1, new string('a', 500), "old"),
            Assistant("2", 1, 5, 500, 30_010, session: "old"),
            Assistant("9", 41, 5, 31_000, 0, block: ToolUse("toolu_9", "Bash"), session: "new"),
            Result("toolu_9", 41.1, new string('b', 300), "new"),
            Assistant("10", 42, 5, 100, 31_005, session: "new"),
        ]);

        var analysis = SessionAnalyzer.Analyze([new AnalysisFile(path, AppFamily.Claude, SessionAnalyzer.MainLabel)],
            new OwnPart("new", false));

        Assert.Equal(2, analysis.Totals.Calls);
        var rewrite = Assert.Single(analysis.Rewrites);
        Assert.Equal(31_000, rewrite.Tokens);
        Assert.Equal(TimeSpan.FromMinutes(40), rewrite.Gap);
        Assert.Equal(["Bash"], analysis.Tools.Select(t => t.Name));

        // Read whole, the history is the conversation's too.
        var whole = SessionAnalyzer.Analyze([new AnalysisFile(path, AppFamily.Claude, SessionAnalyzer.MainLabel)]);
        Assert.Equal(4, whole.Totals.Calls);
        Assert.Equal(2, whole.Tools.Count);
    }

    [Fact]
    public void ACodexRolloutsToolsCompactionAndPauseAreRead()
    {
        var path = Write(Path.Combine(_dir, "rollout-2026-09-01T08-00-00-0199aaaa-0000-7000-8000-000000000001.jsonl"),
            CodexSession(out var shellBytes, out var patchBytes));

        var analysis = SessionAnalyzer.Analyze(SessionAnalyzer.FilesFor(path, AppFamily.Codex));

        Assert.Equal(5, analysis.Totals.Calls);
        var rewrite = Assert.Single(analysis.Rewrites);
        Assert.Equal(31_000, rewrite.Tokens);                           // read again at full price
        Assert.Equal(TimeSpan.FromMinutes(12), rewrite.Gap);
        var compaction = Assert.Single(analysis.Compactions);           // its record and its note: one compaction
        Assert.True(compaction.Marked);
        Assert.Equal((31_000L, (long?)5_000), (compaction.Before, compaction.After));

        // Each output once — not again as the command's own event — and nothing from the compacted history.
        var shell = analysis.Tools.Single(t => t.Name == "shell");
        Assert.Equal((2, shellBytes), (shell.Results, shell.Bytes));
        var patch = analysis.Tools.Single(t => t.Name == "apply_patch");
        Assert.Equal((1, patchBytes), (patch.Results, patch.Bytes));
        Assert.Equal(2, analysis.Tools.Count);
        Assert.Equal(TimeSpan.FromMinutes(5), rewrite.Kept);
        Assert.Contains(analysis.Tips, t => t.StartsWith("离开超过 5 分钟后继续", StringComparison.Ordinal)
                                            && t.Contains("按全价重新读入了 1 次", StringComparison.Ordinal));
    }

    [Fact]
    public void TheNoteACodexCompactionLeavesMarksItOnItsOwn()
    {
        // Without the compacted record: the token count whose totals did not move says it.
        var lines = CodexSession(out _, out _).Where(l => !l.Contains("\"type\":\"compacted\"", StringComparison.Ordinal)).ToList();
        var path = Write(Path.Combine(_dir, "rollout-2026-09-01T08-00-00-0199aaaa-0000-7000-8000-000000000002.jsonl"), lines);

        var compaction = Assert.Single(SessionAnalyzer.Analyze(SessionAnalyzer.FilesFor(path, AppFamily.Codex)).Compactions);
        Assert.True(compaction.Marked);
    }

    [Fact]
    public void ALongCompactionSummaryPassedOverStillMarksTheCompaction()
    {
        // A summary longer than the scanner looks into, and a context too small for a halving to say anything.
        var path = Write(Path.Combine(_dir, "summary.jsonl"),
        [
            Assistant("1", 0, 10, 25_000, 0),
            CompactSummary(2, "This session is being continued from a previous conversation. " + new string('s', 40_000)),
            Assistant("2", 3, 5, 6_000, 0),
        ]);
        var analysis = SessionAnalyzer.Analyze([new AnalysisFile(path, AppFamily.Claude, SessionAnalyzer.MainLabel)]);
        var compaction = Assert.Single(analysis.Compactions);
        Assert.True(compaction.Marked);
        Assert.Equal((25_010L, (long?)6_005), (compaction.Before, compaction.After));
    }

    [Fact]
    public void AGeminiSessionIsReadWholeInOneParse()
    {
        object result = new object[] { new { functionResponse = new { id = "c1", name = "read_file", response = new { output = new string('r', 5_000) } } } };
        object shell = new { output = "ok" };
        var path = WriteGemini(Path.Combine(_dir, "gemini", "chats", "session-2026-09-01T08-00-0a1b2c3d.json"),
        [
            new { id = "u1", timestamp = At(0), type = "user", content = new object[] { new { text = "看一下" } } },
            GeminiModel("m1", 0.5, GeminiTokens(40_000, 0),
                [new { id = "c1", name = "read_file", displayName = "ReadFile", args = new { absolute_path = "D:\\a" }, result, resultDisplay = "…" }]),
            new { id = "u2", timestamp = At(1), type = "user", content = new object[] { new { functionResponse = new { name = "run_shell_command", response = shell } } } },
            GeminiModel("m2", 11, GeminiTokens(42_000, 0)),             // a pause, but no cache was ever read: nothing to lose
            GeminiModel("m3", 12, GeminiTokens(10_000, 0)),             // halved: compacted
            GeminiModel("m4", 13, GeminiTokens(30_000, 25_000)),
            GeminiModel("m5", 25, GeminiTokens(31_000, 0)),             // twelve minutes: the cache had expired
        ]);

        var analysis = SessionAnalyzer.Analyze(SessionAnalyzer.FilesFor(path, AppFamily.Gemini));

        Assert.Equal(5, analysis.Totals.Calls);
        Assert.Equal(31_000, Assert.Single(analysis.Rewrites).Tokens);
        var compaction = Assert.Single(analysis.Compactions);
        Assert.False(compaction.Marked);
        Assert.Equal(42_000, compaction.Before);
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(result).Length, analysis.Tools.Single(t => t.Name == "read_file").Bytes);
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(shell).Length, analysis.Tools.Single(t => t.Name == "run_shell_command").Bytes);
    }

    [Fact]
    public void ADshSessionIsReadWholeInOneRead()
    {
        var toolResult = DshEvent("tool/result", 4, 0.2, new
        {
            message = new { role = "tool", toolCallId = "call-1", content = new object[] { new { type = "text", text = new string('x', 3_000) } }, isError = false, id = "r1" },
        });
        var path = WriteDsh(Path.Combine(_dir, "dsh", "sessions", "--D-w--", "session-0a1b2c3d-0000-4000-8000-00000000000a"),
            [
                new { type = "session", version = 4, id = "session-0a1b2c3d-0000-4000-8000-00000000000a", createdAt = DshStart, cwd = "D:\\w", delegationDepth = 0 },
                DshEvent("model/selection", 1, 0, new { provider = "deepseek-official", model = "deepseek-flash" }),
            ],
            [
                DshAnswer(3, 0.1, "m1", 40_000, 0,
                    [new { type = "text", text = "先看看" }, new { type = "tool-call", id = "call-1", name = "pwsh", arguments = "{\"command\":\"ls\"}" }]),
                toolResult,
                DshAnswer(5, 10, "m2", 2_000, 39_000),
                DshAnswer(6, 25, "m3", 42_000, 0),                      // fifteen minutes: read again in full
                DshEvent("command/run", 7, 25.5, new { commandId = "c1", name = "compact", args = "", source = new { kind = "user" } }),
                DshAnswer(8, 26, "m4", 8_000, 0),
            ]);

        var analysis = SessionAnalyzer.Analyze(SessionAnalyzer.FilesFor(path, AppFamily.DeepSeek));

        Assert.Equal(4, analysis.Totals.Calls);
        Assert.Equal(42_000, Assert.Single(analysis.Rewrites).Tokens);
        var compaction = Assert.Single(analysis.Compactions);
        Assert.True(compaction.Marked);
        Assert.Equal((42_000L, (long?)8_000), (compaction.Before, compaction.After));
        var pwsh = Assert.Single(analysis.Tools);
        Assert.Equal(("pwsh", 1, (long)Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(toolResult))), (pwsh.Name, pwsh.Results, pwsh.Bytes));
    }

    [Fact]
    public void ACompactionJustDoneIsShownBeforeAnyCallFollows()
    {
        var path = Write(Path.Combine(_dir, "just.jsonl"),
        [
            Assistant("1", 0, 10, 40_000, 0),
            CompactBoundary(2),
        ]);
        var analysis = SessionAnalyzer.Analyze([new AnalysisFile(path, AppFamily.Claude, SessionAnalyzer.MainLabel)]);
        var compaction = Assert.Single(analysis.Compactions);
        Assert.Null(compaction.After);
        Assert.Equal(40_010, compaction.Before);
    }

    [Fact]
    public void AnUnreadableSubagentLeavesTheRestAndACancelStopsAll()
    {
        var main = ClaudeWithSubagent(out _);
        var files = SessionAnalyzer.FilesFor(main, AppFamily.Claude);
        files.Add(new AnalysisFile(Path.Combine(_dir, "gone.jsonl"), AppFamily.Claude, "子代理 · gone"));
        var analysis = SessionAnalyzer.Analyze(files);
        Assert.Equal(["子代理 · gone"], analysis.Unread);
        Assert.Equal(11, analysis.Totals.Calls);

        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => SessionAnalyzer.Analyze(files, default, cancel.Token));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
