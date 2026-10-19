using System.Text;
using System.Text.Json;
using ClaudeLauncher.Infrastructure.Backends;

namespace ClaudeLauncher.IntegrationTests;

/// <summary>
/// Codex rollouts are a different envelope from Claude transcripts and write most turns
/// twice. The reader must show each turn once, fold the machinery, and never let tool output
/// carry terminal control sequences into the window.
/// </summary>
public sealed class CodexTranscriptTests : IDisposable
{
    private readonly string _dir;

    public CodexTranscriptTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "launcher-codex-transcript", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
    }

    private string Write(params string[] lines)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("n") + ".jsonl");
        File.WriteAllText(path, string.Join('\n', lines) + '\n', new UTF8Encoding(false));
        return path;
    }

    private static string Response(string role, string text, string kind = "input_text") => JsonSerializer.Serialize(new
    {
        timestamp = "2026-09-22T02:00:00.000Z",
        type = "response_item",
        payload = new { type = "message", role, content = new[] { new { type = kind, text } } },
    });

    private static string Event(string type, string message) =>
        JsonSerializer.Serialize(new { type = "event_msg", payload = new { type, message } });

    private static List<TranscriptEntry> ReadAll(string path)
    {
        var reader = new TranscriptReader(path);
        var all = new List<TranscriptEntry>();
        var offset = 0;
        for (var i = 0; i < 100; i++)
        {
            var page = reader.Read(offset, 1);      // one entry per page: the hardest case for mirrors
            all.AddRange(page.Entries);
            if (page.ReachedEnd) return all;
            offset = page.NextLineOffset;
        }
        throw new InvalidOperationException("reader did not finish");
    }

    [Fact]
    public void ResponseItemsReadAsAConversation()
    {
        var page = new TranscriptReader(Write(
            Response("user", "问题"), Response("assistant", "回答", "output_text"))).Read();

        Assert.Equal(2, page.Entries.Count);
        Assert.Equal(MessageRole.User, page.Entries[0].Role);
        Assert.Equal("问题", page.Entries[0].Text);
        Assert.Equal(MessageRole.Assistant, page.Entries[1].Role);
        Assert.Equal("回答", page.Entries[1].Text);
        Assert.NotNull(page.Entries[0].Timestamp);
        Assert.Equal(new DateTimeOffset(2026, 9, 22, 2, 0, 0, TimeSpan.Zero), page.Entries[0].At);

        // An event line carries no time of its own.
        Assert.Null(Assert.Single(new TranscriptReader(Write(Event("agent_message", "只有事件"))).Read().Entries).At);
    }

    [Fact]
    public void EventAndResponseCopiesOfTheSameTurnShowOnce()
    {
        var entries = ReadAll(Write(
            Event("user_message", "继续"), Response("user", "继续"),
            Response("assistant", "答复", "output_text"), Event("agent_message", "答复"),
            Event("task_complete", ""), Event("task_started", ""),
            Event("user_message", "继续"), Response("user", "继续"),
            Response("assistant", "答复", "output_text"), Event("agent_message", "答复")));

        Assert.Equal(["继续", "答复", "继续", "答复"], entries.Select(e => e.Text));
    }

    [Fact]
    public void ARepeatedMessageInOneFamilyIsNotAMirror()
    {
        var entries = ReadAll(Write(Response("user", "继续"), Response("user", "继续")));
        Assert.Equal(2, entries.Count);
    }

    [Fact]
    public void AnInterveningTurnEndsTheMirrorWindow()
    {
        var entries = ReadAll(Write(
            Event("user_message", "继续"), Response("assistant", "第一轮答复", "output_text"), Response("user", "继续")));

        Assert.Equal(["继续", "第一轮答复", "继续"], entries.Select(e => e.Text));
    }

    [Fact]
    public void DeveloperContextAndEnvironmentBlocksAreFoldedToNotes()
    {
        var entries = ReadAll(Write(
            Response("developer", "<app-context>\n# Codex desktop context…"),
            Response("user", "<environment_context>\n  <cwd>D:\\x</cwd>\n</environment_context>"),
            Response("user", "真正的问题")));

        Assert.Equal([MessageRole.Note, MessageRole.Note, MessageRole.User], entries.Select(e => e.Role));
        Assert.All(entries.Where(e => e.Role == MessageRole.Note), n => Assert.DoesNotContain("Codex desktop", n.Text));
        Assert.Contains("Codex desktop", entries[0].Detail);
        Assert.Equal("真正的问题", entries[2].Text);
    }

    [Fact]
    public void AnyInjectedTagBlockIsFoldedButHtmlPastedMidSentenceIsNot()
    {
        var entries = ReadAll(Write(
            Response("user", "<recommended_plugins>\nHere is a list of plugins…\n</recommended_plugins>\n<environment_context>x</environment_context>"),
            Response("user", "<Some Title> 这是我自己写的"),
            Response("user", "看看 <div> 这个标签怎么处理")));

        Assert.Equal([MessageRole.Note, MessageRole.User, MessageRole.User], entries.Select(e => e.Role));
        Assert.Contains("recommended_plugins", entries[0].Detail);
    }

    [Fact]
    public void ToolCallsAndOutputsAreFoldedAndControlSequencesRemoved()
    {
        var call = JsonSerializer.Serialize(new
        {
            type = "response_item",
            payload = new { type = "function_call", name = "shell", arguments = "{\"command\":[\"ls\",\"-la\"]}" },
        });
        var output = JsonSerializer.Serialize(new
        {
            type = "response_item",
            payload = new { type = "function_call_output", output = "\u001b[31m红色\u001b[0m\r\n\u001b]8;;https://invalid.example\u0007链接\u001b]8;;\u0007" },
        });

        var page = new TranscriptReader(Write(call, output, Response("assistant", "完成", "output_text"))).Read();

        Assert.Equal(3, page.Entries.Count);
        var callEntry = page.Entries[0];
        Assert.Equal(MessageRole.ToolCall, callEntry.Role);
        Assert.StartsWith("shell", callEntry.Text, StringComparison.Ordinal);
        Assert.Contains("ls", callEntry.Text, StringComparison.Ordinal);
        var result = page.Entries[1];
        Assert.Equal(MessageRole.ToolResult, result.Role);
        Assert.Equal("红色\n链接", result.Detail);
        Assert.DoesNotContain("\u001b", result.Detail, StringComparison.Ordinal);
        Assert.Equal(1, page.ToolOutputsFolded);
        Assert.Equal("完成", page.Entries[2].Text);
    }

    [Fact]
    public void OversizedToolOutputIsBoundedPerEntry()
    {
        var output = JsonSerializer.Serialize(new
        {
            type = "response_item",
            payload = new { type = "custom_tool_call_output", output = new string('x', 200_000) },
        });

        var entry = Assert.Single(new TranscriptReader(Write(output)).Read().Entries);

        Assert.True(entry.Detail!.Length < 200_000);
        Assert.Contains("已截断", entry.Detail, StringComparison.Ordinal);
        Assert.Contains("200,000", entry.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void MetadataAndReasoningRecordsProduceNothing()
    {
        var page = new TranscriptReader(Write(
            """{"type":"session_meta","payload":{"id":"x"}}""",
            """{"type":"event_msg","payload":{"type":"token_count"}}""",
            """{"type":"response_item","payload":{"type":"reasoning","summary":[]}}""")).Read();

        Assert.Empty(page.Entries);
        Assert.True(page.ReachedEnd);
    }

    [Fact]
    public void MemoryDraftsSeeCodexUserMessages()
    {
        var path = Write(
            Event("user_message", "以后一律用中文回答我。"),
            Response("user", "以后一律用中文回答我。"),
            Response("assistant", "好的。", "output_text"));

        var draft = MemoryCandidateExtractor.Extract(path, "id", "t", null, null);

        Assert.Equal(1, draft.UserMessagesScanned);
        var candidate = Assert.Single(draft.Candidates);
        Assert.Equal(CandidateKind.Instruction, candidate.Kind);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
    }
}
