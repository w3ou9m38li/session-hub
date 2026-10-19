using System.Text;
using System.Text.Json;
using ClaudeLauncher.Infrastructure.Backends;

namespace ClaudeLauncher.IntegrationTests;

/// <summary>
/// A conversation read a stretch at a time by a caller that keeps nothing between stretches — an
/// agent's server. Put back together, the stretches are the conversation: nothing twice, nothing
/// missing, wherever they fall — between a Codex message and its mirror too.
/// </summary>
public sealed class TranscriptChunkTests : IDisposable
{
    private readonly string _dir;

    public TranscriptChunkTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "launcher-chunks", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
    }

    private string Write(params string[] lines) => WriteRaw(string.Join('\n', lines) + '\n');

    private string WriteRaw(string text, string extension = ".jsonl")
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("n") + extension);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }

    private static string User(string text, string at = "2026-10-01T08:00:00Z") =>
        JsonSerializer.Serialize(new { type = "user", timestamp = at, message = new { content = new[] { new { type = "text", text } } } });

    private static string Assistant(string text, string at = "2026-10-01T08:00:30Z") =>
        JsonSerializer.Serialize(new { type = "assistant", timestamp = at, message = new { content = new[] { new { type = "text", text } } } });

    private static string ToolUse(string name, string command) => JsonSerializer.Serialize(new
    {
        type = "assistant",
        message = new { content = new object[] { new { type = "tool_use", id = "t1", name, input = new { command } } } },
    });

    private static string ToolResult(string output) => JsonSerializer.Serialize(new
    {
        type = "user",
        message = new { content = new object[] { new { type = "tool_result", tool_use_id = "t1", content = output } } },
    });

    private static string Response(string role, string text, string kind = "input_text") => JsonSerializer.Serialize(new
    {
        timestamp = "2026-09-22T02:00:00.000Z",
        type = "response_item",
        payload = new { type = "message", role, content = new[] { new { type = kind, text } } },
    });

    private static string Event(string type, string message) =>
        JsonSerializer.Serialize(new { type = "event_msg", payload = new { type, message } });

    private static string Call(string name) => JsonSerializer.Serialize(new
    {
        type = "response_item",
        payload = new { type = "function_call", name, arguments = "{\"cmd\":\"ls\"}", call_id = "c1" },
    });

    /// <summary>Every stretch from the top to the end, with the cursors handed out on the way.</summary>
    private static (List<TranscriptEntry> Entries, List<TranscriptChunk> Chunks) ReadAll(
        string path, int maxChars, bool includeTools = false)
    {
        var entries = new List<TranscriptEntry>();
        var chunks = new List<TranscriptChunk>();
        string? cursor = null;
        for (var i = 0; i < 1000; i++)
        {
            var chunk = TranscriptReader.ReadChunk(path, cursor, maxChars, includeTools);
            chunks.Add(chunk);
            entries.AddRange(chunk.Entries);
            if (chunk.ReachedEnd) return (entries, chunks);
            Assert.NotEqual(cursor, chunk.Next);                    // always moves on
            cursor = chunk.Next;
        }
        throw new InvalidOperationException("the chunks never reached the end");
    }

    private static string[] Said(IEnumerable<TranscriptEntry> entries) =>
        [.. entries.Select(e => $"{e.Role}:{e.Text}")];

    [Fact]
    public void StretchesPutTogetherAreTheWholeConversationWhateverTheirSize()
    {
        var lines = new List<string>();
        var expected = new List<string>();
        for (var i = 0; i < 30; i++)
        {
            lines.Add(User($"第 {i} 个问题：" + new string('问', i * 13 % 200), $"2026-10-01T08:{i:00}:00Z"));
            expected.Add($"User:第 {i} 个问题：" + new string('问', i * 13 % 200));
            if (i % 4 == 0)
            {
                lines.Add(ToolUse("Bash", $"ls {i}"));
                lines.Add(ToolResult(new string('x', 70_000)));      // long tool output: passed over unread
                expected.AddRange([$"ToolCall:Bash  ls {i}", "ToolResult:" + TranscriptReader.PassedOverOutput]);
            }
            lines.Add(Assistant($"第 {i} 个回答：" + new string('答', i * 29 % 300), $"2026-10-01T08:{i:00}:30Z"));
            expected.Add($"Assistant:第 {i} 个回答：" + new string('答', i * 29 % 300));
        }
        var path = Write([.. lines]);
        var whole = TranscriptReader.ReadDialogueFrom(path, 0).Entries;
        Assert.Equal(60, whole.Count);

        foreach (var budget in new[] { 1, 200, 1000, 5000, 1_000_000 })
        {
            var (entries, chunks) = ReadAll(path, budget);
            Assert.Equal(expected, Said(entries));
            var dialogue = entries.Where(e => e.Role is MessageRole.User or MessageRole.Assistant).ToList();
            Assert.Equal(Said(whole), Said(dialogue));
            Assert.Equal(whole.Select(e => e.At), dialogue.Select(e => e.At));
            Assert.All(entries, e => Assert.Null(e.Detail));                // no output without asking for it
            // Each stretch keeps to its budget, but always takes at least one line.
            Assert.All(chunks, c => Assert.True(c.Entries.Count <= 1 || c.Entries.Sum(e => e.Text.Length + 40) <= budget,
                $"budget {budget}: {c.Entries.Sum(e => e.Text.Length + 40)}"));
            Assert.All(chunks.SkipLast(1), c => Assert.NotEmpty(c.Entries));
            // Byte positions follow on from each other and end at the file's end.
            for (var i = 1; i < chunks.Count; i++) Assert.Equal(chunks[i - 1].End, chunks[i].Start);
            Assert.Equal(new FileInfo(path).Length, chunks[^1].End);
            Assert.All(chunks, c => Assert.StartsWith("b", c.Next));
        }
        Assert.Equal(76, ReadAll(path, 1).Chunks.Count);            // one line a stretch
    }

    [Fact]
    public void WithToolsTheCallsAndTheirOutputComeTooCutToSize()
    {
        var path = Write(User("跑一下"), ToolUse("Bash", "dir"), ToolResult(new string('o', 5000)), Assistant("跑完了"));
        var (entries, _) = ReadAll(path, 1, includeTools: true);
        var whole = new TranscriptReader(path).Read(0, 1000).Entries.Where(e => e.Role != MessageRole.Note).ToList();

        Assert.Equal(Said(whole), Said(entries));
        Assert.Equal([MessageRole.User, MessageRole.ToolCall, MessageRole.ToolResult, MessageRole.Assistant], entries.Select(e => e.Role));
        Assert.Equal("Bash  dir", entries[1].Text);
        Assert.Null(entries[1].Detail);                                                  // a call is its one line
        Assert.Equal(TranscriptReader.ChunkToolChars + "…（截断）".Length, entries[2].Detail!.Length);
        Assert.EndsWith("…（截断）", entries[2].Detail);
        Assert.StartsWith(new string('o', 100), entries[2].Detail);

        // Without them: every call still in its line, its output only as long as it was.
        var (plain, _) = ReadAll(path, 1);
        Assert.Equal(["User:跑一下", "ToolCall:Bash  dir", "ToolResult:工具输出（5,000 字符）", "Assistant:跑完了"], Said(plain));
        Assert.All(plain, e => Assert.Null(e.Detail));
    }

    [Fact]
    public void LongToolTrafficIsPassedOverYetEveryCallAndOutputShows()
    {
        var write = JsonSerializer.Serialize(new
        {
            type = "assistant",
            message = new { content = new object[] { new { type = "tool_use", id = "t2", name = "Write", input = new { file_path = @"D:\work\big.cs", content = new string('c', 100_000) } } } },
        });
        var thinking = JsonSerializer.Serialize(new
        {
            type = "assistant",
            message = new { content = new object[] { new { type = "thinking", thinking = new string('想', 40_000) } } },
        });
        var huge = JsonSerializer.Serialize(new
        {
            type = "response_item",
            payload = new { type = "function_call", name = "apply_patch", arguments = new string('p', 4 * 1024 * 1024 + 10), call_id = "c9" },
        });
        var path = Write(User("写个大文件"), write, ToolResult(new string('o', 100_000)), thinking, huge, Assistant("写好了"));

        // Not asked for: the long output and the thinking passed over unparsed; the output still shows that
        // it was there, and the call — read past that threshold — names what was written.
        var (plain, _) = ReadAll(path, 1);
        Assert.Equal(["User:写个大文件", @"ToolCall:Write  D:\work\big.cs", "ToolResult:" + TranscriptReader.PassedOverOutput,
            "ToolCall:" + TranscriptReader.PassedOverCall, "Assistant:写好了"], Said(plain));

        // Asked for: the output is read and cut; a call too long for any read is still a line.
        var (tools, _) = ReadAll(path, 100_000, includeTools: true);
        Assert.Equal(["User:写个大文件", @"ToolCall:Write  D:\work\big.cs", "ToolResult:工具输出（100,000 字符）",
            "ToolCall:" + TranscriptReader.PassedOverCall, "Assistant:写好了"], Said(tools));
        Assert.StartsWith(new string('o', TranscriptReader.ChunkToolChars), tools[2].Detail);
    }

    [Fact]
    public void AFileThatGrowsWhileItIsReadIsReadOnFromWhereTheReadEnded()
    {
        // The file had one line when its length was taken; by the time it was read, the app had
        // finished a second record and not yet put a line break after it.
        var first = Encoding.UTF8.GetBytes(User("一") + "\n");
        var grown = Encoding.UTF8.GetBytes(User("一") + "\n" + Assistant("二"));
        var chunk = TranscriptReader.ReadChunk(new StaleLengthStream(grown, first.Length), null, 100_000, false);
        Assert.Equal(["User:一", "Assistant:二"], Said(chunk.Entries));
        Assert.Equal(grown.Length, chunk.End);
        Assert.True(chunk.End <= chunk.Length, $"{chunk.End} of {chunk.Length}");

        // Reading on from there: nothing twice.
        var on = TranscriptReader.ReadChunk(new MemoryStream(grown), chunk.Next, 100_000, false);
        Assert.Empty(on.Entries);

        // Whole lines added meanwhile: read, and the position never past what the read found.
        var lines = Encoding.UTF8.GetBytes(User("一") + "\n" + Assistant("二") + "\n");
        var more = TranscriptReader.ReadChunk(new StaleLengthStream(lines, first.Length), null, 100_000, false);
        Assert.Equal(["User:一", "Assistant:二"], Said(more.Entries));
        Assert.Equal(lines.Length, more.End);
        Assert.Equal(lines.Length, more.Length);
    }

    /// <summary>A file whose length was taken before the app wrote more to it.</summary>
    private sealed class StaleLengthStream(byte[] bytes, long length) : MemoryStream(bytes)
    {
        public override long Length => length;
    }

    [Fact]
    public void ALongMessageIsCutWithItsLengthSaid()
    {
        var path = Write(User(new string('长', 10_000)), Assistant("短"));
        var chunk = TranscriptReader.ReadChunk(path, null, 100_000, includeTools: false);
        Assert.Equal(new string('长', TranscriptReader.ChunkMessageChars) + "…（截断，共 10,000 字）", chunk.Entries[0].Text);
        Assert.Equal("短", chunk.Entries[1].Text);

        // Never between the two halves of a character outside the basic plane.
        var emoji = Write(User(new string('a', TranscriptReader.ChunkMessageChars - 1) + "😀" + "tail"));
        var cut = TranscriptReader.ReadChunk(emoji, null, 100_000, false).Entries[0].Text;
        Assert.StartsWith(new string('a', TranscriptReader.ChunkMessageChars - 1) + "…", cut);
        Assert.DoesNotContain(cut, char.IsSurrogate);
    }

    [Fact]
    public void ACodexMessageAndItsMirrorShowOnceWhereverAStretchEnds()
    {
        // The user says the same thing twice before Codex writes the copies of both.
        var path = Write(
            Event("user_message", "继续"), Event("user_message", "继续"),
            Response("user", "继续"), Response("user", "继续"),
            Response("assistant", "好", "output_text"), Event("agent_message", "好"),
            Event("user_message", "下一步"), Response("user", "下一步"));
        var whole = TranscriptReader.ReadDialogueFrom(path, 0).Entries;
        Assert.Equal(["User:继续", "User:继续", "Assistant:好", "User:下一步"], Said(whole));

        // One line a stretch: a stretch ends between the copies of the second 继续.
        var (entries, chunks) = ReadAll(path, 1);
        Assert.Equal(Said(whole), Said(entries));
        Assert.Contains(chunks, c => c.Next.Contains('.'));            // a cursor that carries the waiting copy
    }

    [Fact]
    public void AToolCallBetweenAMessageAndItsMirrorDoesNotSplitThem()
    {
        var path = Write(Event("user_message", "看看目录"), Call("shell"), Response("user", "看看目录"),
            Response("assistant", "看完了", "output_text"));
        var whole = new TranscriptReader(path).Read(0, 1000).Entries;
        Assert.Equal(["User:看看目录", "ToolCall:shell  ls", "Assistant:看完了"], Said(whole));

        var (entries, chunks) = ReadAll(path, 1, includeTools: true);
        Assert.Equal(Said(whole), Said(entries));
        Assert.Equal(3, chunks.Count);
    }

    [Fact]
    public void ReadingOnFromTheEndFindsWhatTheAppWroteSinceAndPairsItsMirrors()
    {
        var path = Write(Response("user", "问一句"), Event("user_message", "问一句"), Event("agent_message", "先答一半"));
        var first = TranscriptReader.ReadChunk(path, null, 100_000, false);
        Assert.True(first.ReachedEnd);
        Assert.Equal(["User:问一句", "Assistant:先答一半"], Said(first.Entries));

        // Nothing new: nothing read, the same place handed back.
        var again = TranscriptReader.ReadChunk(path, first.Next, 100_000, false);
        Assert.Empty(again.Entries);
        Assert.Equal(first.Next, again.Next);

        // Codex writes the mirror of the last message, a line still being written, then the rest of it.
        File.AppendAllText(path, Response("assistant", "先答一半", "output_text") + "\n" + "{\"type\":\"response_it");
        var later = TranscriptReader.ReadChunk(path, first.Next, 100_000, false);
        Assert.Empty(later.Entries);                                        // the mirror, not a second answer
        File.AppendAllText(path, "em\",\"payload\":{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"再问\"}]}}\n");
        Assert.Equal(["User:再问"], Said(TranscriptReader.ReadChunk(path, later.Next, 100_000, false).Entries));
    }

    [Fact]
    public void ALastLineWithoutALineBreakIsReadOnceItIsWhole()
    {
        var path = WriteRaw(User("一") + "\n" + Assistant("二"));                    // whole, never terminated
        var chunk = TranscriptReader.ReadChunk(path, null, 100_000, false);
        Assert.Equal(["User:一", "Assistant:二"], Said(chunk.Entries));
        Assert.Equal(new FileInfo(path).Length, chunk.End);

        var half = WriteRaw(User("一") + "\n" + "{\"type\":\"assistant\",\"message\":{\"con");
        var partial = TranscriptReader.ReadChunk(half, null, 100_000, false);
        Assert.Equal(["User:一"], Said(partial.Entries));
        Assert.Equal(Encoding.UTF8.GetByteCount(User("一") + "\n"), partial.End);   // the half line: left for later
    }

    [Fact]
    public void ADocumentReadWholeIsPagedByEntry()
    {
        var messages = Enumerable.Range(0, 12).SelectMany(i => new object[]
        {
            new { id = $"u{i}", timestamp = $"2026-10-01T08:{i:00}:00Z", type = "user", content = new[] { new { text = $"问题 {i} " + new string('问', i * 37) } } },
            new { id = $"m{i}", timestamp = $"2026-10-01T08:{i:00}:30Z", type = "gemini", content = $"回答 {i}", model = "gemini-3-pro" },
        }).ToArray();
        var path = WriteRaw(JsonSerializer.Serialize(new
        {
            sessionId = "0a1b2c3d-0000-4000-8000-00000000000a", startTime = "2026-10-01T08:00:00Z", messages,
        }), ".json");
        var whole = WholeTranscript.ReadDialogue(path, default);
        Assert.Equal(24, whole.Count);

        foreach (var budget in new[] { 1, 300, 100_000 })
        {
            var (entries, chunks) = ReadAll(path, budget);
            Assert.Equal(Said(whole), Said(entries));
            Assert.All(chunks, c => Assert.True(c.Document));
            Assert.All(chunks, c => Assert.StartsWith("e", c.Next));
            Assert.Equal(chunks[^1].Length, chunks[^1].End);
        }

        // A cursor past the end of a document written again shorter starts over, and says so.
        var restarted = TranscriptReader.ReadChunk(path, "e9999", 100_000, false);
        Assert.True(restarted.Restarted);
        Assert.Equal(Said(whole), Said(restarted.Entries));
    }

    [Fact]
    public void ACursorItDidNotHandOutIsRefused()
    {
        var lines = Write(User("一"), Assistant("二"));
        var document = WriteRaw(JsonSerializer.Serialize(new { sessionId = "x", messages = Array.Empty<object>() }), ".json");
        foreach (var bad in new[] { "x12", "b-1", "b12.zz1.ABC", "b12.eu0." + new string('A', 64), "b12.eu1." + new string('a', 64), "e3" })
            Assert.Equal("cursor", Assert.Throws<ArgumentException>(() => TranscriptReader.ReadChunk(lines, bad, 1000, false)).ParamName);
        Assert.Equal("cursor", Assert.Throws<ArgumentException>(() => TranscriptReader.ReadChunk(document, "b0", 1000, false)).ParamName);

        // A file written again shorter than the cursor: read from the top.
        var restarted = TranscriptReader.ReadChunk(lines, "b999999", 1000, false);
        Assert.True(restarted.Restarted);
        Assert.Equal(["User:一", "Assistant:二"], Said(restarted.Entries));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
    }
}
