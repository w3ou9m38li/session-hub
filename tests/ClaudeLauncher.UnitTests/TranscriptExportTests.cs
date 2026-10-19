using ClaudeLauncher.Infrastructure.Backends;

namespace ClaudeLauncher.UnitTests;

public sealed class TranscriptExportTests
{
    private static readonly TranscriptHeader Header =
        new("修复 | 排版", "终端 CLI", @"G:\wheat GWAS 2", "3f3f2c8b-64ab-4453-8617-2ebd879b9a8f", "2026-09-22 12:56");

    private static TranscriptContent Content() => new(
    [
        new TranscriptEntry(0, MessageRole.User, "把图改好看", Timestamp: "09-22 12:50"),
        new TranscriptEntry(1, MessageRole.ToolCall, "Bash  ls", Detail: "{\"command\":\"ls\"}"),
        new TranscriptEntry(2, MessageRole.ToolResult, "工具输出（12 字符）", Detail: "a.png\nb.png"),
        new TranscriptEntry(3, MessageRole.Note, "（系统提示，已折叠）", Detail: "<system-reminder>…"),
        new TranscriptEntry(4, MessageRole.Assistant, "已经改好了"),
    ], ToolOutputsFolded: 1);

    [Fact]
    public void MarkdownKeepsTheDialogueAndOneLinePerToolCall()
    {
        var md = TranscriptExport.ToMarkdown(Header, Content(), "Claude", includeTools: false);

        Assert.Contains("把图改好看", md, StringComparison.Ordinal);
        Assert.Contains("已经改好了", md, StringComparison.Ordinal);
        Assert.Contains("> `Bash  ls`", md, StringComparison.Ordinal);
        Assert.DoesNotContain("a.png", md, StringComparison.Ordinal);
        Assert.DoesNotContain("system-reminder", md, StringComparison.Ordinal);
    }

    [Fact]
    public void MarkdownIncludesToolContentOnlyWhenAsked()
    {
        var md = TranscriptExport.ToMarkdown(Header, Content(), "Claude", includeTools: true);
        Assert.Contains("a.png", md, StringComparison.Ordinal);
        Assert.Contains("\"command\":\"ls\"", md, StringComparison.Ordinal);
    }

    [Fact]
    public void ATitlePipeCannotBreakTheHeaderTable()
    {
        var md = TranscriptExport.ToMarkdown(Header, Content(), "Claude", includeTools: false);
        Assert.StartsWith("# 修复 | 排版", md, StringComparison.Ordinal);
        Assert.DoesNotContain("| 修复 | 排版 |", md, StringComparison.Ordinal);
    }

    [Fact]
    public void AFenceInsideToolOutputCannotCloseTheBlock()
    {
        var content = new TranscriptContent(
            [new TranscriptEntry(0, MessageRole.ToolResult, "工具输出", Detail: "```\nrm -rf\n```")], 1);
        var md = TranscriptExport.ToMarkdown(Header, content, "Claude", includeTools: true);
        Assert.Equal(2, md.Split("```").Length - 1);
    }

    [Fact]
    public void MarkdownGoesToTheFileAPieceAtATimeNotBuiltWhole()
    {
        // Thousands of tool outputs at full length make hundreds of megabytes: never one string.
        var entries = Enumerable.Range(0, 2000)
            .Select(i => new TranscriptEntry(i, MessageRole.ToolResult, "工具输出", Detail: new string('x', 1000)))
            .ToList();
        var content = new TranscriptContent(entries, entries.Count);
        using var file = new LargestWrite();

        TranscriptExport.WriteMarkdown(file, Header, content, "Claude", includeTools: true);

        Assert.InRange(file.Largest, 1, 1000);
        Assert.Equal(TranscriptExport.ToMarkdown(Header, content, "Claude", includeTools: true), file.ToString());
    }

    /// <summary>
    /// Remembers the longest piece it was handed in one go. Every other way of writing to a
    /// TextWriter — a string, a span, a StringBuilder, a line — ends in one of these two, so
    /// no overload gets a whole document past it.
    /// </summary>
    private sealed class LargestWrite : TextWriter
    {
        private readonly System.Text.StringBuilder _written = new();

        public int Largest { get; private set; }

        public override System.Text.Encoding Encoding => System.Text.Encoding.Unicode;

        public override void Write(char value)
        {
            Largest = Math.Max(Largest, 1);
            _written.Append(value);
        }

        public override void Write(char[] buffer, int index, int count)
        {
            Largest = Math.Max(Largest, count);
            _written.Append(buffer, index, count);
        }

        public override string ToString() => _written.ToString();
    }

    [Fact]
    public void PlainTextIsOnlyWhatTheTwoSidesSaid()
    {
        var text = TranscriptExport.ToPlainText(Content().Entries, "Codex");
        Assert.Equal("你\n把图改好看\n\nCodex\n已经改好了\n\n", text);
    }

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

    [Fact]
    public void APageIsSelfContainedAndRunsNothing()
    {
        var content = new TranscriptContent(
        [
            new TranscriptEntry(0, MessageRole.User, "看看 <script>alert(1)</script>\n第二行", Timestamp: "09-22 12:50"),
            new TranscriptEntry(1, MessageRole.Attachment, "图片 1") { Image = Png },
            new TranscriptEntry(2, MessageRole.ToolCall, "Bash  ls", Detail: "{\"command\":\"ls\"}"),
            new TranscriptEntry(3, MessageRole.ToolResult, "工具输出", Detail: "a.png"),
            new TranscriptEntry(4, MessageRole.Assistant, "**改好了**：见 [文档](https://example.com/a?b=1&c=2) 与 [本地](C:\\x.txt)"),
        ], ToolOutputsFolded: 1);

        var page = TranscriptExport.ToHtml(Header, content, "Claude", includeTools: false, new DateTime(2026, 9, 23, 22, 0, 0));

        Assert.StartsWith("<!DOCTYPE html>", page, StringComparison.Ordinal);
        Assert.Contains("Content-Security-Policy", page, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", page, StringComparison.Ordinal);                          // the user's text stays text
        Assert.Contains("看看 &lt;script&gt;alert(1)&lt;/script&gt;\n第二行", page, StringComparison.Ordinal);
        Assert.Contains("src=\"data:image/png;base64,iVBORw0KGgoBAgM=\"", page, StringComparison.Ordinal);
        Assert.Contains("<strong>改好了</strong>", page, StringComparison.Ordinal);
        Assert.Contains("<a href=\"https://example.com/a?b=1&amp;c=2\"", page, StringComparison.Ordinal);
        Assert.Contains("<span class=\"ref\" title=\"C:\\x.txt\">本地</span>", page, StringComparison.Ordinal);
        Assert.Contains("Bash  ls", page, StringComparison.Ordinal);
        Assert.DoesNotContain("a.png", page, StringComparison.Ordinal);                             // tool output folded away
        Assert.Contains("a.png", TranscriptExport.ToHtml(Header, content, "Claude", includeTools: true, DateTime.Now),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a/b:c*?", "a_b_c__")]
    [InlineData("  结尾的点... ", "结尾的点")]
    [InlineData("", "对话记录")]
    [InlineData("...", "对话记录")]
    public void FileNamesAreSafeOnWindows(string title, string expected)
    {
        Assert.Equal(expected, TranscriptExport.SafeFileName(title));
    }
}
