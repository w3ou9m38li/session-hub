using System.Globalization;
using System.Text;
using ClaudeLauncher.Core.Contracts;
using ClaudeLauncher.Infrastructure.Backends;
using ClaudeLauncher.Infrastructure.Search;
using ClaudeLauncher.Infrastructure.Usage;

namespace ClaudeLauncher.IntegrationTests;

/// <summary>
/// Token usage read from the transcripts: each call counted once — however often it is
/// written, and whichever conversation copied it — a file read on from where it stopped.
/// </summary>
public sealed class UsageLedgerTests : IDisposable
{
    private readonly string _dir;
    private readonly UsageLedger _ledger;
    private static readonly DateTime Start = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);

    public UsageLedgerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "launcher-usage", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
        _ledger = new UsageLedger(Path.Combine(_dir, "usage.sqlite"));
    }

    private static string Claude(string id, string at, long input, long write, long read, long output,
        string model = "claude-opus-5", string block = "text") =>
        "{\"type\":\"assistant\",\"timestamp\":\"" + at + "\",\"requestId\":\"req_" + id + "\",\"message\":{\"model\":\"" + model
        + "\",\"id\":\"msg_" + id + "\",\"role\":\"assistant\",\"content\":[{\"type\":\"" + block + "\",\"text\":\"…\"}],"
        + "\"usage\":{\"input_tokens\":" + input + ",\"cache_creation_input_tokens\":" + write
        + ",\"cache_read_input_tokens\":" + read + ",\"output_tokens\":" + output + "}}}";

    private static string ToolResult(string text) =>
        "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",\"content\":\"" + text + "\"}]}}";

    private static string Turn(string model) =>
        "{\"timestamp\":\"2026-09-01T08:00:00Z\",\"type\":\"turn_context\",\"payload\":{\"model\":\"" + model + "\"}}";

    private static string Count(string at, (long In, long Cached, long Out) total, (long In, long Cached, long Out) last) =>
        "{\"timestamp\":\"" + at + "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{"
        + "\"total_token_usage\":{\"input_tokens\":" + total.In + ",\"cached_input_tokens\":" + total.Cached
        + ",\"output_tokens\":" + total.Out + ",\"total_tokens\":" + (total.In + total.Out) + "},"
        + "\"last_token_usage\":{\"input_tokens\":" + last.In + ",\"cached_input_tokens\":" + last.Cached
        + ",\"output_tokens\":" + last.Out + ",\"total_tokens\":" + (last.In + last.Out) + "}}}}";

    private static string Meta(string id, string? forkedFrom = null) =>
        "{\"timestamp\":\"2026-09-01T08:00:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"" + id + "\""
        + (forkedFrom is null ? "" : ",\"forked_from_id\":\"" + forkedFrom + "\"") + "}}";

    private string Write(string name, int minutesAfterStart, params string[] lines)
    {
        var path = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Join('\n', lines) + '\n', new UTF8Encoding(false));
        File.SetCreationTimeUtc(path, Start.AddMinutes(minutesAfterStart));
        return path;
    }

    [Fact]
    public void TheContextACallReadsIsWhatWentInAveragedOverTheCalls()
    {
        // Two calls: fresh input, a cache write and cache reads go in; output does not.
        var tokens = new TokenCounts(Calls: 2, Input: 1_000, CacheWrite: 4_000, CacheRead: 195_000, Output: 9_999);
        Assert.Equal(200_000, tokens.Context);
        Assert.Equal(100_000, tokens.ContextPerCall);
        Assert.Equal(0.975, tokens.CacheHit, precision: 6);

        Assert.Equal(0, new TokenCounts().ContextPerCall);                  // no calls: nothing to average
        Assert.Equal(0, new TokenCounts(Calls: 1, 0, 0, 0, Output: 10).CacheHit);
        Assert.Equal(100_000, (tokens + tokens + tokens).ContextPerCall);  // added up, the average stays
    }

    private TokenCounts Total(string? conversation = null)
    {
        var total = new TokenCounts();
        foreach (var row in _ledger.Rows())
            if (conversation is null || row.Conversation == conversation) total += row.Tokens;
        return total;
    }

    [Fact]
    public void AResponseWrittenOnSeveralLinesCountsOnce()
    {
        var path = Write("a.jsonl", 0,
            Claude("1", "2026-09-01T08:00:00Z", 2, 300, 1000, 50, block: "thinking"),
            Claude("1", "2026-09-01T08:00:01Z", 2, 300, 1000, 50, block: "tool_use"),       // same response, next block
            ToolResult("big output"),
            Claude("2", "2026-09-01T08:01:00Z", 3, 10, 1400, 70));
        _ledger.Update([new("a", path, AppFamily.Claude)]);

        var total = Total();
        Assert.Equal(2, total.Calls);
        Assert.Equal(5, total.Input);
        Assert.Equal(310, total.CacheWrite);
        Assert.Equal(2400, total.CacheRead);
        Assert.Equal(120, total.Output);
        var row = Assert.Single(_ledger.Rows());
        Assert.Equal("claude-opus-5", row.Model);
        Assert.Equal(UsageLedger.DayOf(DateTimeOffset.Parse("2026-09-01T08:00:00Z", CultureInfo.InvariantCulture)),
            row.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    }

    [Fact]
    public void ACopiedConversationCountsOnlyWhatIsNew()
    {
        // Resumed into a new file that starts with the old one's lines: those calls stay the original's.
        var original = Write("original.jsonl", 0,
            Claude("1", "2026-09-01T08:00:00Z", 1, 0, 100, 10),
            Claude("2", "2026-09-01T08:01:00Z", 1, 0, 200, 20));
        var resumed = Write("resumed.jsonl", 30,
            Claude("1", "2026-09-01T08:00:00Z", 1, 0, 100, 10),
            Claude("2", "2026-09-01T08:01:00Z", 1, 0, 200, 20),
            Claude("3", "2026-09-01T08:40:00Z", 1, 0, 300, 30));

        // Listed newest first: the ledger still reads the older file first.
        _ledger.Update([new("resumed", resumed, AppFamily.Claude), new("original", original, AppFamily.Claude)]);

        Assert.Equal(2, Total("original").Calls);
        Assert.Equal(1, Total("resumed").Calls);
        Assert.Equal(30, Total("resumed").Output);
    }

    [Fact]
    public void CodexCountsEachResponseByItsOwnFigures()
    {
        var path = Write("rollout.jsonl", 0,
            Turn("gpt-5.6-sol"),
            Count("2026-09-01T08:00:00Z", (1000, 0, 50), (1000, 0, 50)),
            Count("2026-09-01T08:00:00Z", (1000, 0, 50), (1000, 0, 50)),        // the same event again
            Count("2026-09-01T08:01:00Z", (2200, 900, 90), (1200, 900, 40)),
            // A compaction's note: the running totals unchanged, a figure of its own that is no call.
            "{\"timestamp\":\"2026-09-01T08:01:30Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{"
            + "\"total_token_usage\":{\"input_tokens\":2200,\"cached_input_tokens\":900,\"output_tokens\":90,\"total_tokens\":2290},"
            + "\"last_token_usage\":{\"input_tokens\":0,\"cached_input_tokens\":0,\"output_tokens\":0,\"total_tokens\":37417}}}}",
            Turn("gpt-6-astra"),
            Count("2026-09-01T08:02:00Z", (500, 400, 5), (500, 400, 5)));       // the running totals started over
        _ledger.Update([new("x", path, AppFamily.Codex)]);

        var total = Total();
        Assert.Equal(3, total.Calls);
        Assert.Equal(1000 + 300 + 100, total.Input);     // input less what came from the cache
        Assert.Equal(900 + 400, total.CacheRead);
        Assert.Equal(95, total.Output);
        Assert.Equal(["gpt-5.6-sol", "gpt-6-astra"], _ledger.Rows().Select(r => r.Model).Order().ToArray());
    }

    [Fact]
    public void ALongLineIsReadOnlyWhenItsBeginningSaysItCanCount()
    {
        // Past its first 16 KB a line is kept only when it may carry usage: a turn context with
        // long instructions still names the model; a long message is passed over unread.
        var path = Write("rollout-long.jsonl", 0,
            Turn("gpt-5.6-sol").Replace("}}", ",\"user_instructions\":\"" + new string('i', 40_000) + "\"}}", StringComparison.Ordinal),
            "{\"timestamp\":\"2026-09-01T08:00:30Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"user_message\",\"message\":\""
            + new string('m', 100_000) + "\"}}",
            Count("2026-09-01T08:01:00Z", (1000, 0, 50), (1000, 0, 50)));
        _ledger.Update([new("x", path, AppFamily.Codex)]);

        var row = Assert.Single(_ledger.Rows());
        Assert.Equal("gpt-5.6-sol", row.Model);
        Assert.Equal(50, row.Tokens.Output);
    }

    [Fact]
    public void AForkedRolloutDoesNotCountTheEventsItCopied()
    {
        var parent = Write("rollout-parent.jsonl", 0,
            Meta("p"),
            Turn("gpt-5.5"),
            Count("2026-09-01T08:00:00Z", (100, 0, 10), (100, 0, 10)),
            Count("2026-09-01T08:01:00Z", (300, 100, 30), (200, 100, 20)));
        // Codex writes the copies with the time of the fork.
        var fork = Write("rollout-fork.jsonl", 60,
            Meta("f", forkedFrom: "p"),
            Turn("gpt-5.5"),
            Count("2026-09-01T09:00:00Z", (100, 0, 10), (100, 0, 10)),
            Count("2026-09-01T09:00:00Z", (300, 100, 30), (200, 100, 20)),
            Count("2026-09-01T09:05:00Z", (700, 400, 60), (400, 300, 30)));
        // A fork of the fork copies both generations.
        var again = Write("rollout-again.jsonl", 120,
            Meta("g", forkedFrom: "f"),
            Turn("gpt-5.5"),
            Count("2026-09-01T10:00:00Z", (100, 0, 10), (100, 0, 10)),
            Count("2026-09-01T10:00:00Z", (300, 100, 30), (200, 100, 20)),
            Count("2026-09-01T10:00:00Z", (700, 400, 60), (400, 300, 30)),
            Count("2026-09-01T10:02:00Z", (900, 600, 65), (200, 200, 5)));

        _ledger.Update([new("again", again, AppFamily.Codex), new("fork", fork, AppFamily.Codex),
            new("parent", parent, AppFamily.Codex)]);

        Assert.Equal(2, Total("parent").Calls);
        Assert.Equal(1, Total("fork").Calls);
        Assert.Equal(30, Total("fork").Output);
        Assert.Equal(1, Total("again").Calls);
        Assert.Equal(5, Total("again").Output);
    }

    [Fact]
    public void UnrelatedConversationsWithTheSameFirstReplyBothCount()
    {
        // Two runs of the same prompt: identical figures, different threads.
        var one = Write("rollout-one.jsonl", 0, Meta("one"), Turn("gpt-5.5"),
            Count("2026-09-01T08:00:00Z", (500, 0, 20), (500, 0, 20)));
        var two = Write("rollout-two.jsonl", 5, Meta("two"), Turn("gpt-5.5"),
            Count("2026-09-01T08:05:00Z", (500, 0, 20), (500, 0, 20)));

        _ledger.Update([new("one", one, AppFamily.Codex), new("two", two, AppFamily.Codex)]);

        Assert.Equal(1, Total("one").Calls);
        Assert.Equal(1, Total("two").Calls);
    }

    [Fact]
    public void AResponsesLaterLinesCanOnlyAddToIt()
    {
        // Claude Code's first line of a response can carry part of its output; a later line all of it.
        var path = Write("partial.jsonl", 0,
            Claude("1", "2026-09-01T08:00:00Z", 2, 100, 1000, 3, block: "thinking"),
            Claude("1", "2026-09-01T08:00:01Z", 2, 100, 1000, 161, block: "text"));
        _ledger.Update([new("a", path, AppFamily.Claude)]);
        Assert.Equal(1, Total().Calls);
        Assert.Equal(161, Total().Output);

        // The rest of a response arriving in a later read: what it adds, and only that.
        File.AppendAllText(path, Claude("1", "2026-09-01T08:00:02Z", 2, 100, 1000, 200, block: "tool_use") + "\n"
            + Claude("1", "2026-09-01T08:00:03Z", 2, 100, 1000, 50) + "\n", new UTF8Encoding(false));
        _ledger.Update([new("a", path, AppFamily.Claude)]);
        Assert.Equal(1, Total().Calls);
        Assert.Equal(200, Total().Output);
        Assert.Equal(1000, Total().CacheRead);
    }

    [Fact]
    public void ALongMessageThatBeginsAtTheEndOfABlockIsStillRead()
    {
        // A first line that ends a few bytes before the reader's 1 MB block does, then a message
        // of more than 1 MB: only a few of its bytes are held when its length is judged.
        // 40 bytes of JSON around the text and a newline: the next line starts 100 bytes before the block ends.
        var filler = "{\"type\":\"user\",\"message\":{\"content\":\"" + new string('x', 1024 * 1024 - 141) + "\"}}";
        var big = Claude("9", "2026-09-01T08:00:00Z", 1, 0, 10, 7).Replace("\"text\":\"…\"",
            "\"text\":\"" + new string('y', 1200 * 1024) + "\"", StringComparison.Ordinal);
        var path = Write("long.jsonl", 0, filler, big);

        _ledger.Update([new("a", path, AppFamily.Claude)]);

        Assert.Equal(1, Total().Calls);
        Assert.Equal(7, Total().Output);
    }

    [Fact]
    public void AGrowingFileIsReadOnFromWhereItStopped()
    {
        var path = Write("growing.jsonl", 0, Claude("1", "2026-09-01T08:00:00Z", 1, 0, 10, 1));
        _ledger.Update([new("a", path, AppFamily.Claude)]);
        Assert.Equal(1, Total().Calls);

        // One more line, and one still being written: the half line waits for the next read.
        var line3 = Claude("3", "2026-09-01T08:02:00Z", 1, 0, 30, 3);
        File.AppendAllText(path, Claude("2", "2026-09-01T08:01:00Z", 1, 0, 20, 2) + "\n" + line3[..40],
            new UTF8Encoding(false));
        var run = _ledger.Update([new("a", path, AppFamily.Claude)]);
        Assert.Equal(1, run.Read);
        Assert.Equal(2, Total().Calls);

        File.AppendAllText(path, line3[40..] + "\n", new UTF8Encoding(false));
        _ledger.Update([new("a", path, AppFamily.Claude)]);
        Assert.Equal(3, Total().Calls);
        Assert.Equal(6, Total().Output);

        Assert.Equal(0, _ledger.Update([new("a", path, AppFamily.Claude)]).Read);     // unchanged: not read at all
    }

    [Fact]
    public void ARewrittenFileIsCountedAfresh()
    {
        var path = Write("rewritten.jsonl", 0,
            Claude("1", "2026-09-01T08:00:00Z", 1, 0, 10, 1),
            Claude("2", "2026-09-01T08:01:00Z", 1, 0, 20, 2),
            Claude("3", "2026-09-01T08:02:00Z", 1, 0, 30, 3));
        _ledger.Update([new("a", path, AppFamily.Claude)]);
        Assert.Equal(3, Total().Calls);

        File.WriteAllText(path, Claude("9", "2026-09-01T09:00:00Z", 1, 0, 90, 9) + "\n", new UTF8Encoding(false));
        _ledger.Update([new("a", path, AppFamily.Claude)]);

        Assert.Equal(1, Total().Calls);
        Assert.Equal(9, Total().Output);
    }

    [Fact]
    public void SubagentsAndSpawnedRolloutsCountForTheirConversation()
    {
        var claudeDir = Path.Combine(_dir, "claude");
        var id = "0f0e0d0c-aaaa-bbbb-cccc-111122223333";
        Write(Path.Combine("claude", "projects", "proj", id + ".jsonl"), 0, Claude("1", "2026-09-01T08:00:00Z", 1, 0, 10, 1));
        Write(Path.Combine("claude", "projects", "proj", id, "subagents", "agent-1.jsonl"), 1,
            Claude("2", "2026-09-01T08:01:00Z", 1, 0, 20, 2, model: "claude-sonnet-5"));

        var thread = "01a07b31-0271-72b3-acc1-e69a725e36d3";
        var child = "01a09df0-c756-7970-91cf-aec1d36fb0a5";
        var codex = new CodexPaths(Path.Combine(_dir, "codex"), Path.Combine(_dir, "codex", "state.sqlite"),
            Path.Combine(_dir, "codex", "sessions"), Path.Combine(_dir, "codex", "archived_sessions"));
        var own = Write(Path.Combine("codex", "sessions", $"rollout-2026-09-01T08-00-00-{thread}.jsonl"), 2,
            "{\"timestamp\":\"2026-09-01T08:00:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"" + thread + "\"}}",
            Turn("gpt-5.6-sol"), Count("2026-09-01T08:00:00Z", (100, 0, 10), (100, 0, 10)));
        Write(Path.Combine("codex", "sessions", $"rollout-2026-09-01T08-05-00-{child}.jsonl"), 3,
            "{\"timestamp\":\"2026-09-01T08:05:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"" + child
            + "\",\"session_id\":\"" + thread + "\",\"parent_thread_id\":\"" + thread + "\"}}",
            Turn("gpt-5.6-terra"), Count("2026-09-01T08:05:00Z", (50, 0, 5), (50, 0, 5)));

        SessionMetadata Row(string backend, string native, string? openRef = null) => new()
        {
            Key = new ConversationKey(backend, "acc", "org", native),
            Title = native,
            SearchKey = native,
            SourceRevision = "1",
            NativeOpenRef = openRef,
            NativeArchiveState = NativeArchiveState.Unsupported,
            SourceCoverage = SourceCoverage.Resolved,
        };
        var cli = Row(ConversationKey.CliCode, id);
        var task = Row(ConversationKey.Codex, thread, own);

        var sources = _ledger.SourcesFor([cli, task], claudeDir, codex);
        Assert.Equal(4, sources.Count);
        _ledger.Update(sources);

        var cliKey = ContentIndex.KeyOf(cli.Key);
        var taskKey = ContentIndex.KeyOf(task.Key);
        Assert.Equal(2, Total(cliKey).Calls);          // the transcript and its subagent
        Assert.Equal(2, Total(taskKey).Calls);         // the thread and the one it spawned
        Assert.Equal(4, _ledger.Rows().Select(r => r.Model).Distinct().Count());
        Assert.Equal(4, _ledger.FileCount());
    }

    [Fact]
    public void AProcessBesideTheWindowReadsTheLedgerAndNeverWritesIt()
    {
        var path = Write("a.jsonl", 0,
            Claude("1", "2026-09-01T08:00:00Z", 2, 300, 1000, 50),
            Claude("2", "2026-09-01T08:01:00Z", 3, 10, 1400, 70));
        _ledger.Update([new("a", path, AppFamily.Claude)]);

        // Named as code built on it names it: the parameter is part of what it was promised.
        using (var reader = UsageLedger.OpenReadOnly(path: _ledger.DatabasePath))
        {
            Assert.NotNull(reader);
            Assert.Equal(_ledger.Rows(), reader.Rows());
            Assert.Equal(new TokenCounts(2, 5, 310, 2400, 120), reader.Totals("a"));
            Assert.Null(reader.Totals("b"));
            Assert.Equal(1, reader.FileCount());
            Assert.Throws<InvalidOperationException>(() => reader.Update([new("b", path, AppFamily.Claude)]));

            // The window counts on beside it.
            File.AppendAllText(path, Claude("3", "2026-09-01T08:02:00Z", 1, 0, 1500, 10) + "\n", new UTF8Encoding(false));
            _ledger.Update([new("a", path, AppFamily.Claude)]);
            Assert.Equal(3, reader.Totals("a")?.Calls);
        }

        // None there — the usage window was never opened — or one of another layout: not opened, not made.
        var missing = Path.Combine(_dir, "missing.sqlite");
        Assert.Null(UsageLedger.OpenReadOnly(missing));
        Assert.False(File.Exists(missing));
        var older = Path.Combine(_dir, "older.sqlite");
        using (var other = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={older};Pooling=False"))
        {
            other.Open();
            using var cmd = other.CreateCommand();
            cmd.CommandText = "CREATE TABLE files(id INTEGER PRIMARY KEY); PRAGMA user_version=1;";
            cmd.ExecuteNonQuery();
        }
        Assert.Null(UsageLedger.OpenReadOnly(older));
        using (var other = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={older};Pooling=False;Mode=ReadOnly"))
        {
            other.Open();
            using var cmd = other.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name='files';";
            Assert.Equal(1L, cmd.ExecuteScalar());              // not dropped
        }
    }

    public void Dispose()
    {
        _ledger.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
