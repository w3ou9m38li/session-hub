using System.Diagnostics;
using System.Text;
using ClaudeLauncher.Infrastructure.Backends;

namespace ClaudeLauncher.IntegrationTests;

/// <summary>
/// The viewer has to open transcripts that are hundreds of megabytes and ~98% tool output,
/// so these tests are mostly about what it refuses to load.
/// </summary>
public sealed class TranscriptReaderTests : IDisposable
{
    private readonly string _dir;

    public TranscriptReaderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "launcher-viewer", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
    }

    private string Write(params string[] lines)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("n") + ".jsonl");
        File.WriteAllText(path, string.Join('\n', lines) + '\n', new UTF8Encoding(false));
        return path;
    }

    private static string User(string text) =>
        "{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"" + text + "\"}]}}";

    private static string Assistant(string text) =>
        "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"" + text + "\"}]}}";

    private static string ToolUse(string name, string desc) =>
        "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"" + name
        + "\",\"input\":{\"description\":\"" + desc + "\"}}]}}";

    private static string ToolResult(string payload) =>
        "{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"content\":\""
        + payload + "\"}]}}";

    /// <summary>The smallest PNG there is: one transparent pixel.</summary>
    private const string Pixel =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

    private static string ClaudePicture(string base64) =>
        "{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"看图\"},"
        + "{\"type\":\"image\",\"source\":{\"type\":\"base64\",\"media_type\":\"image/png\",\"data\":\"" + base64 + "\"}}]}}";

    private static string CodexPicture(string url) =>
        "{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"user\",\"content\":["
        + "{\"type\":\"input_image\",\"image_url\":\"" + url + "\"}]}}";

    [Fact]
    public void PicturesAreKeptOnlyForAReaderThatShowsThem()
    {
        var path = Write(ClaudePicture(Pixel), CodexPicture("data:image/png;base64," + Pixel));

        var shown = new TranscriptReader(path, keepImages: true).Read().Entries
            .Where(e => e.Role == MessageRole.Attachment).ToList();
        Assert.Equal(2, shown.Count);
        Assert.All(shown, e => Assert.Equal(Convert.FromBase64String(Pixel), e.Image));
        Assert.All(shown, e => Assert.StartsWith("（图片 · ", e.Text, StringComparison.Ordinal));

        // Export, copy and memory drafts read without them: a label, no bytes.
        var named = new TranscriptReader(path).Read().Entries.Where(e => e.Role == MessageRole.Attachment).ToList();
        Assert.Equal(2, named.Count);
        Assert.All(named, e => Assert.Null(e.Image));

        // The text beside a picture is still the user's message.
        Assert.Contains(new TranscriptReader(path).Read().Entries, e => e is { Role: MessageRole.User, Text: "看图" });
    }

    [Fact]
    public void AMessageCarryingBigPicturesIsKeptWithItsWords()
    {
        // About 5.6 MB on one line: over the 4 MB limit for tool output, under the one for messages.
        var picture = new byte[4 * 1024 * 1024];
        Convert.FromBase64String(Pixel).CopyTo(picture, 0);
        var line = "{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"看这张大图\"},"
                   + "{\"type\":\"image\",\"source\":{\"type\":\"base64\",\"media_type\":\"image/png\",\"data\":\""
                   + Convert.ToBase64String(picture) + "\"}}]}}";
        var bigToolOutput = ToolResult(new string('x', 5 * 1024 * 1024));
        var path = Write(User("开头"), line, bigToolOutput, Assistant("收到"));

        var entries = new TranscriptReader(path, keepImages: true).Read().Entries;
        Assert.Contains(entries, e => e is { Role: MessageRole.User, Text: "看这张大图" });
        Assert.Contains(entries, e => e.Role == MessageRole.Attachment && e.Image?.Length == picture.Length);
        Assert.DoesNotContain(entries, e => e.Role == MessageRole.ToolResult);      // still skipped unread

        Assert.Contains(TranscriptReader.ReadDialogueFrom(path, 0).Entries, e => e.Text == "看这张大图");
        Assert.Contains(TranscriptReader.ReadTail(path, 10).Entries, e => e.Text == "看这张大图");
    }

    [Fact]
    public void APictureOnTheWebIsNamedNeverFetched()
    {
        var entry = Assert.Single(new TranscriptReader(Write(CodexPicture("https://example.com/cat.png")), keepImages: true)
            .Read().Entries);
        Assert.Null(entry.Image);
        Assert.Equal("（网络图片，未加载）https://example.com/cat.png", entry.Text);
    }

    [Fact]
    public void BrokenPictureDataIsSaidNotThrown()
    {
        var entry = Assert.Single(new TranscriptReader(Write(ClaudePicture("not*base64!")), keepImages: true)
            .Read().Entries, e => e.Role == MessageRole.Attachment);
        Assert.Null(entry.Image);
        Assert.Equal("（图片数据无法读取）", entry.Text);
    }

    [Fact]
    public void TheEndIsReadFromTheEnd()
    {
        // Two megabytes of tool output ahead of the last exchange: the end needs none of it.
        var lines = new List<string> { User("最早的话") };
        for (var i = 0; i < 40; i++) lines.Add(ToolResult(new string('x', 50_000)));
        lines.Add(User("倒数第二"));
        lines.Add(ToolUse("Bash", "run"));
        lines.Add(Assistant("最后一句"));
        var path = Write([.. lines]);

        var tail = TranscriptReader.ReadTail(path, 3);
        Assert.False(tail.WholeFile);
        Assert.Equal(3, tail.Entries.Count);
        Assert.Equal("倒数第二", tail.Entries[0].Text);
        Assert.Equal(MessageRole.ToolCall, tail.Entries[1].Role);
        Assert.Equal("最后一句", tail.Entries[2].Text);

        // Asking for more than the file holds reads it all and says so.
        var all = TranscriptReader.ReadTail(path, 1000);
        Assert.True(all.WholeFile);
        Assert.Equal("最早的话", all.Entries[0].Text);
    }

    [Fact]
    public void DialogueIsKeptInOrder()
    {
        var page = new TranscriptReader(Write(User("第一个问题"), Assistant("回答"), User("追问")))
            .Read();

        Assert.Equal(3, page.Entries.Count);
        Assert.Equal(MessageRole.User, page.Entries[0].Role);
        Assert.Equal("第一个问题", page.Entries[0].Text);
        Assert.Equal(MessageRole.Assistant, page.Entries[1].Role);
        Assert.True(page.ReachedEnd);
    }

    [Fact]
    public void ToolOutputsAreFoldedIntoDetailNotShownAsText()
    {
        var page = new TranscriptReader(Write(
            User("问"), ToolResult("十万行文件内容"), ToolResult("更多输出"), Assistant("答"))).Read();

        // The bulk of a real transcript: kept behind a fold, never in the readable text.
        Assert.DoesNotContain(page.Entries, e => e.Text.Contains("文件内容", StringComparison.Ordinal));
        Assert.Equal(2, page.ToolOutputsFolded);
        Assert.Equal(4, page.Entries.Count);
        var folded = page.Entries.Where(e => e.Role == MessageRole.ToolResult).ToList();
        Assert.Equal(2, folded.Count);
        Assert.Contains(folded, e => e.Detail == "十万行文件内容");
        Assert.Equal([MessageRole.User, MessageRole.ToolResult, MessageRole.ToolResult, MessageRole.Assistant],
            page.Entries.Select(e => e.Role));
    }

    [Fact]
    public void ToolCallsKeepTheirArgumentsAsDetail()
    {
        var page = new TranscriptReader(Write(ToolUse("Bash", "列出目录"))).Read();

        var entry = Assert.Single(page.Entries);
        Assert.Equal(MessageRole.ToolCall, entry.Role);
        Assert.Contains("列出目录", entry.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ToolCallsCollapseToOneLine()
    {
        var page = new TranscriptReader(Write(ToolUse("Bash", "列出目录"))).Read();

        var entry = Assert.Single(page.Entries);
        Assert.Equal(MessageRole.ToolCall, entry.Role);
        Assert.Contains("Bash", entry.Text, StringComparison.Ordinal);
        Assert.Contains("列出目录", entry.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void PagingResumesWithoutRepeatingOrLosingEntries()
    {
        var lines = Enumerable.Range(0, 50).Select(i => User("消息" + i)).ToArray();
        var reader = new TranscriptReader(Write(lines));

        var first = reader.Read(0, maxEntries: 20);
        Assert.Equal(20, first.Entries.Count);
        Assert.False(first.ReachedEnd);

        var second = reader.Read(first.NextLineOffset, maxEntries: 20);
        var third = reader.Read(second.NextLineOffset, maxEntries: 20);

        var all = first.Entries.Concat(second.Entries).Concat(third.Entries)
                       .Select(e => e.Text).ToList();
        Assert.Equal(50, all.Count);
        Assert.Equal(50, all.Distinct(StringComparer.Ordinal).Count());
        Assert.True(third.ReachedEnd);
    }

    [Fact]
    public void AnEnormousLineIsSkippedNotLoaded()
    {
        // A 40 MB single line — an embedded image or a giant dump. Loading it to throw it
        // away is exactly what makes a 400 MB transcript unopenable.
        var huge = "{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\""
                   + new string('x', 40 * 1024 * 1024) + "\"}]}}";
        var path = Write(User("前"), huge, User("后"));

        var sw = Stopwatch.StartNew();
        var page = new TranscriptReader(path).Read();
        sw.Stop();

        Assert.Contains(page.Entries, e => e.Text == "前");
        Assert.Contains(page.Entries, e => e.Text == "后");
        Assert.DoesNotContain(page.Entries, e => e.Text.Length > 1_000_000);
        Assert.True(sw.ElapsedMilliseconds < 15000, $"耗时 {sw.ElapsedMilliseconds} ms，疑似整行载入");
    }

    [Fact]
    public void SystemBoilerplateIsCollapsedNotShownRaw()
    {
        var page = new TranscriptReader(Write(User("<system-reminder>内部提示</system-reminder>"))).Read();

        var entry = Assert.Single(page.Entries);
        Assert.Equal(MessageRole.Note, entry.Role);
        Assert.DoesNotContain("内部提示", entry.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void WhatTheUserTypedAfterTheAppsReminderIsTheirs()
    {
        // Claude's desktop app writes its reminder into the same message as the first thing typed.
        var page = new TranscriptReader(Write(
            User("<system-reminder>\\nno project folder, working in a scratch directory\\n</system-reminder>\\n\\n帮我整理小麦数据"),
            Assistant("好的"))).Read();

        Assert.Equal([MessageRole.Note, MessageRole.User, MessageRole.Assistant], page.Entries.Select(e => e.Role));
        Assert.Equal("系统提醒", page.Entries[0].Text);
        Assert.Equal("帮我整理小麦数据", page.Entries[1].Text);
    }

    [Fact]
    public void TextClaudeCodeInjectedIsFoldedNotShownAsTheUsers()
    {
        // A skill's definition arrives as a "user" message; isMeta says nobody typed it.
        var injected = "{\"type\":\"user\",\"isMeta\":true,\"message\":{\"content\":[{\"type\":\"text\","
            + "\"text\":\"# Workflow authoring reference\\n以后每次都先写测试\"}]}}";
        var page = new TranscriptReader(Write(User("真正的提问"), injected, Assistant("回答"))).Read();

        Assert.Equal([MessageRole.User, MessageRole.Note, MessageRole.Assistant], page.Entries.Select(e => e.Role));
        Assert.Equal("真正的提问", page.Entries[0].Text);
        // Folded under its heading; the body stays in the detail.
        Assert.Equal("说明 · Workflow authoring reference", page.Entries[1].Text);
        Assert.Contains("以后每次都先写测试", page.Entries[1].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ABrokenSurrogateCostsOneCharacterNotTheMessage()
    {
        // A tool output cut in the middle of an emoji: JSON.stringify writes the lone half as \ud83d,
        // which JsonElement.GetString refuses outright.
        var path = Write(User("问题"), Assistant("结果：半个表情\\ud83d到此"), Assistant("后面照常"));

        Assert.Equal(["问题", "结果：半个表情�到此", "后面照常"],
            new TranscriptReader(path).Read().Entries.Select(e => e.Text));
        Assert.Equal(["问题", "后面照常"], TranscriptReader.LastExchange(path).Select(e => e.Text));
    }

    [Fact]
    public void RecentDialogueIsTheLastThingsSaid()
    {
        var path = Write(User("第一问"), Assistant("第一答"), ToolUse("Bash", "list files"),
            User("第二问"), Assistant("第二答"),
            "{\"type\":\"user\",\"isMeta\":true,\"message\":{\"content\":\"Continue from where you left off.\"}}",
            ToolUse("Read", "a file"));

        var recent = TranscriptReader.RecentDialogue(path, 2);

        Assert.Equal([MessageRole.User, MessageRole.Assistant], recent.Select(e => e.Role));
        Assert.Equal(["第二问", "第二答"], recent.Select(e => e.Text));
    }

    [Fact]
    public void TheLastExchangeIsTheLastQuestionAndTheLastReplyToIt()
    {
        var path = Write(User("第一问"), Assistant("第一答"), User("第二问"), Assistant("先看一下"),
            ToolUse("Bash", "run tests"), Assistant("测试都过了"), ToolUse("Read", "a file"));

        Assert.Equal(["第二问", "测试都过了"], TranscriptReader.LastExchange(path).Select(e => e.Text));
    }

    [Fact]
    public void AQuestionNotYetAnsweredStandsAlone()
    {
        var path = Write(User("第一问"), Assistant("第一答"), User("还没回答的问题"), ToolUse("Bash", "working"));

        Assert.Equal(["还没回答的问题"], TranscriptReader.LastExchange(path).Select(e => e.Text));
    }

    [Fact]
    public void ALongToolTailWidensTheWindow()
    {
        // The last line alone is wider than the first window, so that window holds no dialogue.
        var output = "{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"content\":\""
                     + new string('x', 300 * 1024) + "\"}]}}";
        var path = Write(User("问题"), Assistant("回答"), output);

        Assert.Equal(["问题", "回答"], TranscriptReader.RecentDialogue(path, 2).Select(e => e.Text));
    }

    [Fact]
    public void AShortConversationGivesWhatItHas()
    {
        Assert.Equal(["只有一句"], TranscriptReader.RecentDialogue(Write(User("只有一句")), 2).Select(e => e.Text));
        Assert.Empty(TranscriptReader.RecentDialogue(Write(ToolUse("Bash", "only tools")), 2));
    }

    [Fact]
    public void ALastLineStillBeingWrittenIsLeftOut()
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("n") + ".jsonl");
        File.WriteAllText(path, User("写完的一句") + "\n" + "{\"type\":\"assistant\",\"message\":{\"content\":[{\"ty",
            new UTF8Encoding(false));

        Assert.Equal(["写完的一句"], TranscriptReader.RecentDialogue(path, 2).Select(e => e.Text));
    }

    [Fact]
    public void SlashCommandShowsWhatTheUserTyped()
    {
        // The markup is Claude Code's; the request after the command is the user's own words.
        var page = new TranscriptReader(Write(
            User("<command-message>loop</command-message>\\n<command-name>/loop</command-name>\\n"
                 + "<command-args>规划一个 AI 交互平台</command-args>"),
            User("<command-name>/model</command-name>\\n    <command-message>model</command-message>"),
            User("<command-name>/compact</command-name> 以后一律这样"))).Read();

        Assert.Equal([MessageRole.User, MessageRole.User, MessageRole.Note], page.Entries.Select(e => e.Role));
        Assert.Equal("/loop 规划一个 AI 交互平台", page.Entries[0].Text);
        Assert.Equal("/model", page.Entries[1].Text);
    }

    [Fact]
    public void FoldedBlocksSayWhatTheyAre()
    {
        static string Injected(string text, string flag = "isMeta") =>
            "{\"type\":\"user\",\"" + flag + "\":true,\"message\":{\"content\":[{\"type\":\"text\",\"text\":\""
            + text + "\"}]}}";

        var page = new TranscriptReader(Write(
            Injected("Base directory for this skill: C:\\\\skills\\\\docx\\n\\n# DOCX creation"),
            User("<local-command-stdout>Set model to x</local-command-stdout>"),
            User("<system-reminder>hook output</system-reminder>"),
            Injected("This session is being continued from a previous conversation.", "isCompactSummary"),
            Injected("<command-message>docx</command-message><command-name>docx</command-name>"),
            Injected("Continue from where you left off."))).Read();

        Assert.All(page.Entries, e => Assert.Equal(MessageRole.Note, e.Role));
        Assert.Equal(
            ["技能说明 · docx", "命令输出 · Set model to x", "系统提醒", "上下文压缩摘要", "命令 · /docx",
             "Continue from where you left off."],
            page.Entries.Select(e => e.Text));
    }

    [Fact]
    public void MalformedLineIsSkippedWithoutAbortingTheRead()
    {
        var page = new TranscriptReader(Write(User("前"), "{ 坏行", User("后"))).Read();

        Assert.Equal(2, page.Entries.Count);
        Assert.True(page.ReachedEnd);
    }

    [Fact]
    public void MissingFileIsReportedNotThrown()
    {
        var reader = new TranscriptReader(Path.Combine(_dir, "nope.jsonl"));

        Assert.False(reader.Exists);
        Assert.Empty(reader.Read().Entries);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
    }
}
