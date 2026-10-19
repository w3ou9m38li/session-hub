using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeLauncher.IntegrationTests;

/// <summary>
/// Transcript lines shaped as the apps write them, for the analysis and the ledger's facts:
/// Claude Code's assistant and tool-result lines (with the copy of the result it keeps beside
/// it), its compaction marks; Codex's events; Gemini CLI's and DeepSeek Harness's documents.
/// </summary>
internal static class AnalysisFixtures
{
    public static readonly DateTime Start = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);

    private static readonly JsonSerializerOptions Options = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static string At(double minutes) =>
        Start.AddMinutes(minutes).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    public static string Json(object value) => JsonSerializer.Serialize(value, Options);

    // ------------------------------------------------------------------ Claude

    public static object ToolUse(string id, string name) => new { type = "tool_use", id, name, input = new { file_path = "D:\\w\\a.txt" } };

    public static string Assistant(string id, double minute, long input, long write, long read, long output = 10,
        object? block = null, bool side = false, string session = "s-1", bool longCache = false, string model = "claude-opus-5") =>
        Json(new
        {
            parentUuid = "p-" + id,
            isSidechain = side,
            sessionId = session,
            type = "assistant",
            timestamp = At(minute),
            requestId = "req_" + id,
            message = new
            {
                model,
                id = "msg_" + id,
                role = "assistant",
                content = new[] { block ?? new { type = "text", text = "好的" } },
                usage = new
                {
                    input_tokens = input,
                    cache_creation_input_tokens = write,
                    cache_read_input_tokens = read,
                    output_tokens = output,
                    cache_creation = new
                    {
                        ephemeral_5m_input_tokens = longCache ? 0 : write,
                        ephemeral_1h_input_tokens = longCache ? write : 0,
                    },
                },
            },
            uuid = "u-" + id,
        });

    /// <summary>A tool's result, and the copy of it Claude Code keeps for itself (never sent to the model).</summary>
    public static string Result(string toolId, double minute, string content, string session = "s-1", bool side = false) =>
        Json(new
        {
            parentUuid = "p",
            isSidechain = side,
            type = "user",
            sessionId = session,
            message = new { role = "user", content = new object[] { new { tool_use_id = toolId, type = "tool_result", content } } },
            uuid = "r-" + toolId,
            timestamp = At(minute),
            toolUseResult = new { stdout = content, interrupted = false },
        });

    /// <summary>The bytes of a result line the model sees: up to where the kept copy begins.</summary>
    public static long Seen(string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line);
        var cut = bytes.AsSpan().IndexOf(",\"toolUseResult\":"u8);
        return cut < 0 ? bytes.Length : cut;
    }

    public static string CompactBoundary(double minute, string session = "s-1") =>
        Json(new
        {
            parentUuid = (string?)null,
            isSidechain = false,
            type = "system",
            subtype = "compact_boundary",
            content = "Conversation compacted",
            sessionId = session,
            timestamp = At(minute),
            uuid = "cb-" + minute.ToString(CultureInfo.InvariantCulture),
            compactMetadata = new { trigger = "manual", preTokens = 31_005 },
        });

    public static string CompactSummary(double minute, string text, string session = "s-1") =>
        Json(new
        {
            parentUuid = "p",
            isSidechain = false,
            type = "user",
            sessionId = session,
            message = new { role = "user", content = text },
            isCompactSummary = true,
            uuid = "cs-" + minute.ToString(CultureInfo.InvariantCulture),
            timestamp = At(minute),
        });

    /// <summary>
    /// A Claude conversation that does everything the analysis looks for: a response on two
    /// lines, tool results (one past the first 16 KB, its kept copy further on), a pause long
    /// enough for the cache to expire, a compaction the app marked and one it did not, and a
    /// side chain's call written into the same file.
    /// </summary>
    public static List<string> ClaudeSession(string session = "s-1") =>
    [
        Assistant("1", 0, 10, 30_000, 0, session: session),
        Assistant("1", 0, 10, 30_000, 0, block: ToolUse("toolu_1", "Read"), session: session),       // the same response, next block
        Result("toolu_1", 0.1, new string('a', 2_000), session),
        Assistant("2", 1, 5, 500, 30_010, block: ToolUse("toolu_2", "Bash"), session: session),
        Result("toolu_2", 1.1, new string('b', 40_000), session),                                   // passed over unread
        Assistant("3", 20, 5, 31_000, 0, session: session),                                         // 19 minutes later: written again
        CompactBoundary(25, session),
        CompactSummary(25, "This session is being continued from a previous conversation.", session),
        Assistant("4", 26, 5, 3_000, 0, session: session),                                          // after the compaction
        Assistant("5", 27, 5, 36_900, 3_005, session: session),
        Assistant("6", 28, 10, 9_990, 0, session: session),                                         // halved: compacted, unmarked
        Assistant("7", 29, 3, 100, 0, side: true, session: session),                                // a side chain's own cache
        Assistant("8", 31, 5, 50, 10_000, session: session),
        Assistant("9", 32, 5, 20_000, 10_055, session: session),
    ];

    public static string Write(string path, IEnumerable<string> lines, double createdMinutes = 0)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Join('\n', lines) + '\n', new UTF8Encoding(false));
        File.SetCreationTimeUtc(path, Start.AddMinutes(createdMinutes));
        return path;
    }

    // ------------------------------------------------------------------- Codex

    public static string Codex(double minute, string type, object payload) =>
        Json(new { timestamp = At(minute), type, payload });

    public static string Count(double minute, (long In, long Cached, long Out) total, (long In, long Cached, long Out, long Total)? last) =>
        Codex(minute, "event_msg", new
        {
            type = "token_count",
            info = new
            {
                total_token_usage = new { input_tokens = total.In, cached_input_tokens = total.Cached, output_tokens = total.Out, total_tokens = total.In + total.Out },
                last_token_usage = last is { } l
                    ? new { input_tokens = l.In, cached_input_tokens = l.Cached, output_tokens = l.Out, total_tokens = l.Total }
                    : null,
            },
        });

    /// <summary>A rollout with tool calls and their outputs, a pause, a compaction (its record and its note) and an apply_patch.</summary>
    public static List<string> CodexSession(out long shellBytes, out long patchBytes)
    {
        var output1 = Codex(0.2, "response_item", new { type = "function_call_output", call_id = "call_1", output = new string('o', 3_000) });
        var output2 = Codex(1.2, "response_item", new { type = "function_call_output", call_id = "call_2", output = new string('p', 40_000) });
        var output3 = Codex(15.2, "response_item", new { type = "custom_tool_call_output", call_id = "call_3", output = new string('q', 100) });
        shellBytes = Encoding.UTF8.GetByteCount(output1) + Encoding.UTF8.GetByteCount(output2);
        patchBytes = Encoding.UTF8.GetByteCount(output3);
        return
        [
            Codex(0, "session_meta", new { id = "0199aaaa-0000-7000-8000-000000000001", cwd = "D:\\w" }),
            Codex(0, "turn_context", new { model = "gpt-5.5" }),
            Codex(0, "response_item", new { type = "function_call", name = "shell", arguments = "{\"command\":[\"ls\"]}", call_id = "call_1" }),
            Count(0.1, (1_000, 0, 50), (1_000, 0, 50, 1_050)),
            output1,
            Codex(0.3, "event_msg", new { type = "exec_command_end", call_id = "call_1", stdout = new string('o', 3_000) }),
            // Long arguments: the call id comes after them.
            Codex(0.5, "response_item", new { type = "function_call", name = "shell", arguments = "{\"script\":\"" + new string('s', 30_000) + "\"}", call_id = "call_2" }),
            Count(1, (31_000, 25_000, 90), (30_000, 25_000, 40, 30_040)),
            output2,
            Count(13, (62_000, 25_000, 130), (31_000, 0, 40, 31_040)),                // 12 minutes later: read again in full
            Codex(14, "compacted", new
            {
                message = "summary",
                replacement_history = new object[]
                {
                    new { type = "function_call_output", call_id = "zz", output = "not a result of this conversation" },
                },
            }),
            Count(14, (62_000, 25_000, 130), (0, 0, 0, 5_000)),                       // the note a compaction leaves
            Count(15, (67_000, 25_000, 150), (5_000, 0, 20, 5_020)),
            Codex(15.1, "response_item", new { type = "custom_tool_call", status = "completed", call_id = "call_3", name = "apply_patch", input = "*** Begin Patch\n*** End Patch" }),
            output3,
            Count(16, (73_000, 29_000, 160), (6_000, 4_000, 10, 6_010)),
        ];
    }

    // ------------------------------------------------------------------ Gemini

    public static object GeminiTokens(long input, long cached, long output = 10) => new { input, output, cached, thoughts = 0, tool = 0, total = input + output };

    public static object GeminiModel(string id, double minute, object tokens, object[]? toolCalls = null) =>
        new { id, timestamp = At(minute), type = "gemini", content = "回答", tokens, model = "gemini-3-pro", toolCalls };

    public static string WriteGemini(string path, object[] messages)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Json(new
        {
            sessionId = "0a1b2c3d-0000-4000-8000-00000000000a",
            projectHash = "p",
            startTime = At(0),
            lastUpdated = At(30),
            kind = "main",
            messages,
        }), new UTF8Encoding(false));
        return path;
    }

    // --------------------------------------------------------------------- dsh

    public static readonly long DshStart = new DateTimeOffset(Start).ToUnixTimeMilliseconds();

    public static object DshEvent(string type, int seq, double minute, object data) =>
        new { type, seq, time = DshStart + (long)(minute * 60_000), data };

    public static object DshAnswer(int seq, double minute, string id, long input, long cacheRead, object[]? blocks = null) =>
        DshEvent("assistant/message", seq, minute, new
        {
            turn = 1,
            step = 1,
            message = new
            {
                role = "assistant",
                content = blocks ?? [new { type = "text", text = "答" }],
                source = new { kind = "model", provider = "deepseek-official", model = "deepseek-flash" },
                id,
            },
            usage = new { inputTokens = input, outputTokens = 10, cacheReadTokens = cacheRead, cacheWriteTokens = 0L, reasoningTokens = 0L },
        });

    /// <summary>A dsh log as dsh writes it: each group of lines one Zstandard frame, appended.</summary>
    public static string WriteDsh(string folder, params object[][] groups)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "session.v4.jsonl.zstd");
        using var compressor = new ZstdSharp.Compressor();
        var bytes = new List<byte>();
        foreach (var group in groups)
            bytes.AddRange(compressor.Wrap(Encoding.UTF8.GetBytes(string.Concat(group.Select(l => JsonSerializer.Serialize(l) + "\n")))).ToArray());
        File.WriteAllBytes(path, [.. bytes]);
        return path;
    }
}
