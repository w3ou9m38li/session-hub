using System.Text;
using ClaudeLauncher.Core.Contracts;
using ClaudeLauncher.Infrastructure.Usage;
using Microsoft.Data.Sqlite;
using static ClaudeLauncher.IntegrationTests.AnalysisFixtures;

namespace ClaudeLauncher.IntegrationTests;

/// <summary>
/// The usage ledger's per-conversation summary — largest context, compactions, cache rewrites
/// after a pause, tool results — kept by the same incremental pass as the tokens: the same
/// whether a file is read whole or as it grows, never counted again for a copy, and in step
/// with what the analysis window finds.
/// </summary>
public sealed class UsageMetricsTests : IDisposable
{
    private readonly string _dir;
    private readonly List<UsageLedger> _ledgers = [];

    public UsageMetricsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "launcher-metrics", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
    }

    private UsageLedger Ledger(string name = "usage.sqlite")
    {
        var ledger = new UsageLedger(Path.Combine(_dir, name));
        _ledgers.Add(ledger);
        return ledger;
    }

    private string ClaudeWithSubagent(string folder = "p")
    {
        var main = Write(Path.Combine(_dir, folder, "0f0e0d0c.jsonl"), ClaudeSession());
        Write(Path.Combine(_dir, folder, "0f0e0d0c", "subagents", "agent-1.jsonl"),
        [
            Assistant("s1", 2, 1, 25_000, 0, block: ToolUse("toolu_s1", "Grep"), side: true),
            Result("toolu_s1", 2.1, new string('g', 100), side: true),
            Assistant("s2", 3, 1, 100, 25_001, side: true),
        ], createdMinutes: 2);
        return main;
    }

    [Fact]
    public void TheLedgerKeepsWhatTheAnalysisFinds()
    {
        var main = ClaudeWithSubagent();
        var subagent = Path.Combine(_dir, "p", "0f0e0d0c", "subagents", "agent-1.jsonl");
        var ledger = Ledger();
        ledger.Update([new("a", main, AppFamily.Claude), new("a", subagent, AppFamily.Claude)]);

        var metrics = ledger.Metrics("a");
        Assert.NotNull(metrics);
        var analysis = SessionAnalyzer.Analyze(SessionAnalyzer.FilesFor(main, AppFamily.Claude));
        Assert.Equal(analysis.Totals.MaxContext, metrics.MaxContext);
        Assert.Equal(analysis.Compactions.Count, metrics.Compactions);
        Assert.Equal(analysis.Rewrites.Count, metrics.Rewrites);
        Assert.Equal(analysis.RewrittenTokens, metrics.RewrittenTokens);
        Assert.Equal(analysis.Tools.Sum(t => t.Results), metrics.ToolResults);
        Assert.Equal(analysis.ToolBytes, metrics.ToolBytes);
        Assert.Equal(new SessionMetrics(39_910, 2, 1, 31_000, 3, metrics.ToolBytes), metrics);

        Assert.Null(ledger.Metrics("b"));
        Assert.Equal(metrics, Assert.Single(ledger.AllMetrics()).Value);
    }

    [Fact]
    public void AFileReadAsItGrowsKeepsWhatOneReadWould()
    {
        var lines = ClaudeSession();
        var whole = Write(Path.Combine(_dir, "whole.jsonl"), lines);
        var once = Ledger("once.sqlite");
        once.Update([new("a", whole, AppFamily.Claude)]);

        // Split mid-response, between a tool call and its result, between a compaction's mark and
        // its summary and the call after it, and in the middle of a line still being written.
        var growing = Path.Combine(_dir, "growing.jsonl");
        File.WriteAllText(growing, "");
        var piecemeal = Ledger("piecemeal.sqlite");
        var written = 0;
        foreach (var upTo in new[] { 1, 4, 7, 8, 10, lines.Count })
        {
            var text = string.Concat(lines.Skip(written).Take(upTo - written).Select(l => l + "\n"));
            written = upTo;
            if (upTo == 10)
            {
                // Half of the next line too: it waits for the next read.
                File.AppendAllText(growing, text + lines[10][..40], new UTF8Encoding(false));
                piecemeal.Update([new("a", growing, AppFamily.Claude)]);
                File.AppendAllText(growing, lines[10][40..] + "\n", new UTF8Encoding(false));
                written = 11;
                continue;
            }
            File.AppendAllText(growing, text, new UTF8Encoding(false));
            piecemeal.Update([new("a", growing, AppFamily.Claude)]);
        }
        Assert.Equal(lines.Count, written);
        Assert.Equal(File.ReadAllText(whole), File.ReadAllText(growing));

        Assert.Equal(once.Metrics("a"), piecemeal.Metrics("a"));
        Assert.Equal(new SessionMetrics(39_910, 2, 1, 31_000, 2, Seen(lines[2]) + Seen(lines[4])), piecemeal.Metrics("a"));
    }

    [Fact]
    public void AResponseWhoseLinesComeInTwoReadsIsOneCallAtItsFirstLinesTime()
    {
        // A long response's last block written six minutes after its first: the pause before the
        // next call is counted from the response's start, as one read of the whole file does.
        string[] lines =
        [
            Assistant("1", 0, 10, 30_000, 0),
            Assistant("2", 1, 5, 500, 30_010, block: new { type = "thinking", thinking = "…" }),
            Assistant("2", 7, 5, 500, 30_010, block: ToolUse("toolu_2", "Bash")),
            Assistant("3", 11.5, 5, 31_000, 0),
        ];
        var once = Ledger("once.sqlite");
        once.Update([new("a", Write(Path.Combine(_dir, "whole.jsonl"), lines), AppFamily.Claude)]);
        Assert.Equal(1, once.Metrics("a")?.Rewrites);

        var growing = Write(Path.Combine(_dir, "growing.jsonl"), lines[..2]);
        var piecemeal = Ledger("piecemeal.sqlite");
        piecemeal.Update([new("a", growing, AppFamily.Claude)]);
        File.AppendAllText(growing, string.Concat(lines[2..].Select(l => l + "\n")), new UTF8Encoding(false));
        piecemeal.Update([new("a", growing, AppFamily.Claude)]);
        Assert.Equal(once.Metrics("a"), piecemeal.Metrics("a"));
    }

    [Fact]
    public void ARolloutReadAsItGrowsKeepsWhatOneReadWould()
    {
        var lines = CodexSession(out var shellBytes, out var patchBytes);
        var whole = Write(Path.Combine(_dir, "rollout-whole.jsonl"), lines);
        var once = Ledger("once.sqlite");
        once.Update([new("x", whole, AppFamily.Codex)]);
        Assert.Equal(new SessionMetrics(31_000, 1, 1, 31_000, 3, shellBytes + patchBytes), once.Metrics("x"));

        // Between a call and its output, between the compaction's record, its note and the next response.
        var growing = Path.Combine(_dir, "rollout-growing.jsonl");
        File.WriteAllText(growing, "");
        var piecemeal = Ledger("piecemeal.sqlite");
        var written = 0;
        foreach (var upTo in new[] { 3, 7, 11, 12, lines.Count })
        {
            File.AppendAllText(growing, string.Concat(lines.Skip(written).Take(upTo - written).Select(l => l + "\n")),
                new UTF8Encoding(false));
            written = upTo;
            piecemeal.Update([new("x", growing, AppFamily.Codex)]);
        }
        Assert.Equal(once.Metrics("x"), piecemeal.Metrics("x"));
    }

    [Fact]
    public void ACopyNeverCountsAgainWhatItsOriginalHas()
    {
        var lines = ClaudeSession();
        var original = Write(Path.Combine(_dir, "projects", "p", "original.jsonl"), lines);
        // Resumed 38 minutes after the original's last call: the history under the old session,
        // then its own — the cache expired meanwhile, so its first call writes everything again.
        var resumed = Write(Path.Combine(_dir, "projects", "p", "resumed.jsonl"),
        [
            .. lines,
            Assistant("r1", 70, 5, 30_100, 0, block: ToolUse("toolu_r1", "Read"), session: "s-2"),
            Result("toolu_r1", 70.1, new string('c', 700), "s-2"),
            Assistant("r2", 71, 5, 100, 30_105, session: "s-2"),
        ], createdMinutes: 70);
        // This tool's copy of the original, read as a file of its own after the app deleted it.
        var copy = Write(Path.Combine(_dir, "archive", "copies", "claude", "original.jsonl"), lines, createdMinutes: 90);

        var alone = Ledger("alone.sqlite");
        alone.Update([new("a", original, AppFamily.Claude)]);
        var ledger = Ledger();
        ledger.Update([new("b", resumed, AppFamily.Claude), new("a", copy, AppFamily.Claude), new("a", original, AppFamily.Claude)]);

        Assert.Equal(alone.Metrics("a"), ledger.Metrics("a"));
        var own = ledger.Metrics("b");
        Assert.NotNull(own);
        Assert.Equal((0, 1, 30_100L, 1, 30_210L), (own.Compactions, own.Rewrites, own.RewrittenTokens, own.ToolResults, own.MaxContext));
        Assert.Equal(Seen(Result("toolu_r1", 70.1, new string('c', 700), "s-2")), own.ToolBytes);
    }

    [Fact]
    public void ACopiedCallJudgedOtherwiseInTheCopyCountsForNeither()
    {
        // The copy left out a call of the original, so there the next one comes after a longer
        // pause and looks like a rewrite. It is the original's call: judged where it was made.
        string[] original =
        [
            Assistant("1", 0, 10, 30_000, 0),
            Assistant("2", 4, 5, 500, 30_010),
            Assistant("3", 8, 5, 31_000, 0),
        ];
        Write(Path.Combine(_dir, "p", "original.jsonl"), original);
        Write(Path.Combine(_dir, "p", "resumed.jsonl"),
            [original[0], original[2], Assistant("r1", 9, 5, 100, 31_005, session: "s-2")], createdMinutes: 9);

        var ledger = Ledger();
        ledger.Update([new("a", Path.Combine(_dir, "p", "original.jsonl"), AppFamily.Claude),
            new("b", Path.Combine(_dir, "p", "resumed.jsonl"), AppFamily.Claude)]);

        Assert.Equal(0, ledger.Metrics("a")?.Rewrites);
        Assert.Equal(0, ledger.Metrics("b")?.Rewrites);
        Assert.Empty(ledger.RewriteRows());
    }

    [Fact]
    public void ACompactionOnlyTheAppMarkedIsKeptAcrossReads()
    {
        // A context too small for a halving to say anything: only the mark tells — and it comes in
        // one read, the call after it in the next.
        string[] lines =
        [
            Assistant("1", 0, 10, 25_000, 0),
            CompactBoundary(2),
            CompactSummary(2, "This session is being continued from a previous conversation."),
            Assistant("2", 3, 5, 6_000, 0),
        ];
        var once = Ledger("once.sqlite");
        once.Update([new("a", Write(Path.Combine(_dir, "whole.jsonl"), lines), AppFamily.Claude)]);
        Assert.Equal(1, once.Metrics("a")?.Compactions);

        var growing = Write(Path.Combine(_dir, "growing.jsonl"), lines[..3]);
        var piecemeal = Ledger("piecemeal.sqlite");
        piecemeal.Update([new("a", growing, AppFamily.Claude)]);
        Assert.Equal(0, piecemeal.Metrics("a")?.Compactions);           // no call has followed it yet
        File.AppendAllText(growing, lines[3] + "\n", new UTF8Encoding(false));
        piecemeal.Update([new("a", growing, AppFamily.Claude)]);
        Assert.Equal(once.Metrics("a"), piecemeal.Metrics("a"));
    }

    [Fact]
    public void ARewrittenFileLeavesNoFactsOfWhatItHeldBefore()
    {
        var path = Write(Path.Combine(_dir, "rewritten.jsonl"), ClaudeSession());
        var ledger = Ledger();
        ledger.Update([new("a", path, AppFamily.Claude)]);
        Assert.Equal(1, ledger.Metrics("a")?.Rewrites);

        Write(path, [Assistant("x1", 0, 5, 1_000, 0), Assistant("x2", 1, 5, 10, 1_005)]);
        ledger.Update([new("a", path, AppFamily.Claude)]);
        Assert.Equal(new SessionMetrics(1_020, 0, 0, 0, 0, 0), ledger.Metrics("a"));
    }

    [Fact]
    public void AGeminiSessionWrittenAgainWholeCountsItsFactsOnce()
    {
        object result = new object[] { new { functionResponse = new { id = "c1", name = "read_file", response = new { output = "x" } } } };
        object[] first =
        [
            GeminiModel("m1", 0.5, GeminiTokens(30_000, 25_000), [new { id = "c1", name = "read_file", result }]),
            GeminiModel("m2", 15, GeminiTokens(31_000, 0)),
        ];
        var path = WriteGemini(Path.Combine(_dir, "gemini", "chats", "session-2026-09-01T08-00-0a1b2c3d.json"), first);
        var ledger = Ledger();
        ledger.Update([new("g", path, AppFamily.Gemini)]);
        var before = ledger.Metrics("g");
        Assert.Equal((1, 1, 31_000L), (before?.ToolResults, before?.Rewrites, before?.MaxContext));

        WriteGemini(path, [.. first, GeminiModel("m3", 16, GeminiTokens(31_500, 31_000))]);
        ledger.Update([new("g", path, AppFamily.Gemini)]);
        Assert.Equal(before! with { MaxContext = 31_500 }, ledger.Metrics("g"));
    }

    [Fact]
    public void ALedgerOfTheLayoutBeforeIsDroppedAndReadAgain()
    {
        var database = Path.Combine(_dir, "older.sqlite");
        using (var older = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            older.Open();
            using var cmd = older.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE files (id INTEGER PRIMARY KEY, path TEXT NOT NULL UNIQUE, conversation TEXT NOT NULL, app INTEGER NOT NULL,
                  length INTEGER NOT NULL, written INTEGER NOT NULL, next_offset INTEGER NOT NULL, state TEXT, origin TEXT);
                INSERT INTO files(path, conversation, app, length, written, next_offset) VALUES('C:\gone.jsonl', 'a', 0, 1, 1, 1);
                PRAGMA user_version=2;
                """;
            cmd.ExecuteNonQuery();
        }
        // A process beside the window does not read it, and does not touch it.
        Assert.Null(UsageLedger.OpenReadOnly(database));

        var ledger = new UsageLedger(database);
        _ledgers.Add(ledger);
        Assert.Equal(0, ledger.FileCount());
        var path = Write(Path.Combine(_dir, "a.jsonl"), ClaudeSession());
        ledger.Update([new("a", path, AppFamily.Claude)]);
        Assert.Equal(2, ledger.Metrics("a")?.Compactions);
        using var reader = UsageLedger.OpenReadOnly(database);
        Assert.NotNull(reader);
    }

    [Fact]
    public void RewritesAreKeptByTheDayOfTheCallThatMadeThem()
    {
        // Two pauses long enough for the cache to expire, a day apart: a range of days ranks by its own.
        var path = Write(Path.Combine(_dir, "days.jsonl"),
        [
            Assistant("1", 0, 10, 30_000, 0),
            Assistant("2", 1, 5, 500, 30_010),
            Assistant("3", 20, 5, 31_000, 0),
            Assistant("4", 21, 5, 100, 31_005),
            Assistant("5", 24 * 60 + 21, 5, 31_200, 0),
        ]);
        var ledger = Ledger();
        ledger.Update([new("a", path, AppFamily.Claude)]);

        static DateOnly Day(double minutes) => DateOnly.FromDateTime(Start.AddMinutes(minutes).ToLocalTime());
        Assert.Equal(
            [new RewriteRow("a", AppFamily.Claude, Day(20), 1, 31_000), new RewriteRow("a", AppFamily.Claude, Day(24 * 60 + 21), 1, 31_200)],
            ledger.RewriteRows().OrderBy(r => r.Day));
        Assert.Equal((2, 62_200L), (ledger.Metrics("a")?.Rewrites, ledger.Metrics("a")?.RewrittenTokens));

        using var reader = UsageLedger.OpenReadOnly(ledger.DatabasePath);
        Assert.NotNull(reader);
        Assert.Equal(ledger.RewriteRows().OrderBy(r => r.Day), reader.RewriteRows().OrderBy(r => r.Day));
    }

    [Fact]
    public void AProcessBesideTheWindowReadsTheMetrics()
    {
        var ledger = Ledger();
        ledger.Update([new("a", Write(Path.Combine(_dir, "a.jsonl"), ClaudeSession()), AppFamily.Claude)]);
        using var reader = UsageLedger.OpenReadOnly(ledger.DatabasePath);
        Assert.NotNull(reader);
        Assert.Equal(ledger.Metrics("a"), reader.Metrics("a"));
        Assert.Equal(ledger.AllMetrics(), reader.AllMetrics());
    }

    public void Dispose()
    {
        foreach (var ledger in _ledgers) ledger.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
