using System.Globalization;
using System.Text;
using ClaudeLauncher.Core.Display;

namespace ClaudeLauncher.Infrastructure.Backends;

/// <summary>What an exported conversation says about itself at the top.</summary>
public sealed record TranscriptHeader(
    string Title, string Source, string? WorkingDirectory, string SessionId, string? LastActivity);

/// <summary>A whole transcript, read to the end.</summary>
public sealed record TranscriptContent(IReadOnlyList<TranscriptEntry> Entries, int ToolOutputsFolded);

/// <summary>
/// Turns transcript entries into text a person keeps: Markdown for a file, plain text for the
/// clipboard.
///
/// <para>Both work from the whole transcript, never from whatever happens to be on screen.
/// "Export this conversation" that quietly stopped at the last page the viewer had loaded
/// would be a silent truncation — the one failure an export must not have.</para>
/// </summary>
public static class TranscriptExport
{
    /// <summary>
    /// Reads every entry of a transcript in one sequential pass.
    /// </summary>
    /// <param name="progress">Receives the fraction of the file read so far, 0–1.</param>
    /// <param name="keepImages">Keep the pictures sent in the conversation, for a page that shows them.</param>
    public static TranscriptContent ReadAll(
        string path, IProgress<double>? progress = null, CancellationToken cancellation = default, bool keepImages = false)
    {
        using var reader = new TranscriptReader(path, keepImages);
        var size = Math.Max(1, reader.SizeBytes);
        var entries = new List<TranscriptEntry>();
        var offset = 0;
        var folded = 0;
        while (true)
        {
            var page = reader.Read(offset, 2000, cancellation);
            entries.AddRange(page.Entries);
            folded = page.ToolOutputsFolded;
            offset = page.NextLineOffset;
            progress?.Report(Math.Clamp(page.BytePosition / (double)size, 0, 1));
            if (page.ReachedEnd) break;
        }
        progress?.Report(1);
        return new TranscriptContent(entries, folded);
    }

    /// <summary>The conversation as Markdown: the dialogue in full, tool calls as one line each, tool content only when asked for.</summary>
    public static string ToMarkdown(
        TranscriptHeader header, TranscriptContent content, string assistantName, bool includeTools)
    {
        var sb = new StringBuilder();
        sb.Append("# ").Append(OneLine(header.Title)).Append("\n\n")
          .Append("| | |\n|---|---|\n")
          .Append("| 来源 | ").Append(Cell(header.Source)).Append(" |\n")
          .Append("| 工作目录 | `").Append(Cell(header.WorkingDirectory ?? "—")).Append("` |\n")
          .Append("| 会话 ID | `").Append(Cell(header.SessionId)).Append("` |\n")
          .Append("| 最后活动 | ").Append(Cell(header.LastActivity ?? "—")).Append(" |\n")
          .Append("| 条目 | ").Append(content.Entries.Count).Append(" 条（")
          .Append(includeTools ? "含" : "不含").Append("工具内容；工具输出 ")
          .Append(content.ToolOutputsFolded).Append(" 条） |\n\n---\n");

        foreach (var entry in content.Entries)
        {
            switch (entry.Role)
            {
                case MessageRole.User:
                    sb.Append("\n## 👤 你");
                    if (entry.Timestamp is { } userTime) sb.Append("　·　").Append(userTime);
                    sb.Append("\n\n").Append(entry.Text).Append('\n');
                    break;
                case MessageRole.Assistant:
                    sb.Append("\n### 🤖 ").Append(assistantName);
                    if (entry.Timestamp is { } assistantTime) sb.Append("　·　").Append(assistantTime);
                    sb.Append("\n\n").Append(entry.Text).Append('\n');
                    break;
                case MessageRole.ToolCall:
                    sb.Append("\n> `").Append(entry.Text.Replace("`", "'", StringComparison.Ordinal)).Append("`\n");
                    if (includeTools && !string.IsNullOrEmpty(entry.Detail)) AppendFence(sb, entry.Detail);
                    break;
                case MessageRole.ToolResult:
                    if (includeTools && !string.IsNullOrEmpty(entry.Detail)) AppendFence(sb, entry.Detail);
                    break;
                case MessageRole.Attachment:
                    sb.Append("\n> ").Append(entry.Text).Append('\n');
                    break;
                // System notes are app scaffolding, not part of the conversation.
            }
        }
        return sb.ToString();
    }

    /// <summary>Pictures put into one page at most; past it a picture is named instead, so the file stays openable.</summary>
    public const long MaxPagePictureBytes = 150L * 1024 * 1024;

    /// <summary>
    /// The conversation as a single web page that needs nothing else: the assistant's Markdown
    /// rendered, the user's words as typed, the pictures inside the file, tool calls one line
    /// each (their content folded underneath when asked for). Nothing on the page is fetched
    /// from anywhere and it runs no script; it follows the reader's light or dark setting.
    /// </summary>
    /// <returns>How many pictures the page holds.</returns>
    public static int WriteHtml(TextWriter page, TranscriptHeader header, TranscriptContent content, string assistantName,
        bool includeTools, DateTime exportedAt)
    {
        string E(string text) => MarkdownHtml.Escape(text);
        page.Write("<!DOCTYPE html>\n<html lang=\"zh-CN\">\n<head>\n<meta charset=\"utf-8\">\n");
        page.Write("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        page.Write("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; img-src data:; style-src 'unsafe-inline'\">\n");
        page.Write("<meta name=\"generator\" content=\"会话管家\">\n<title>");
        page.Write(E(OneLine(header.Title)));
        page.Write("</title>\n<style>");
        page.Write(PageStyle);
        page.Write("</style>\n</head>\n<body>\n<main>\n<header>\n<h1>");
        page.Write(E(OneLine(header.Title)));
        page.Write("</h1>\n<dl class=\"meta\">");
        void Meta(string name, string? value)
        {
            page.Write("<dt>");
            page.Write(E(name));
            page.Write("</dt><dd>");
            page.Write(E(value ?? "—"));
            page.Write("</dd>");
        }
        Meta("来源", header.Source);
        Meta("工作目录", header.WorkingDirectory);
        Meta("会话 ID", header.SessionId);
        Meta("最后活动", header.LastActivity);
        page.Write("</dl>\n</header>\n");

        long pictureBytes = 0;
        var pictures = 0;
        foreach (var entry in content.Entries)
        {
            switch (entry.Role)
            {
                case MessageRole.User:
                case MessageRole.Assistant:
                    var user = entry.Role == MessageRole.User;
                    page.Write(user ? "<section class=\"msg user\">" : "<section class=\"msg assistant\">");
                    page.Write("<div class=\"who\">");
                    page.Write(E(user ? "你" : assistantName));
                    if (entry.Timestamp is { } when)
                    {
                        page.Write("<span class=\"when\">");
                        page.Write(E(when));
                        page.Write("</span>");
                    }
                    page.Write("</div>");
                    if (user)
                    {
                        page.Write("<div class=\"plain\">");
                        page.Write(E(entry.Text));
                    }
                    else
                    {
                        page.Write("<div class=\"md\">");
                        page.Write(MarkdownHtml.Render(Markdown.Parse(entry.Text)));
                    }
                    page.Write("</div></section>\n");
                    break;
                case MessageRole.ToolCall:
                case MessageRole.ToolResult:
                    var detail = includeTools && !string.IsNullOrEmpty(entry.Detail) ? entry.Detail : null;
                    if (entry.Role == MessageRole.ToolResult && detail is null) break;
                    var line = entry.Role == MessageRole.ToolCall ? entry.Text : "工具输出";
                    if (detail is null)
                    {
                        page.Write("<div class=\"tool\">");
                        page.Write(E(line));
                        page.Write("</div>\n");
                    }
                    else
                    {
                        page.Write("<details class=\"tool\"><summary>");
                        page.Write(E(line));
                        page.Write("</summary><pre>");
                        page.Write(E(detail));
                        page.Write("</pre></details>\n");
                    }
                    break;
                case MessageRole.Attachment when entry.Image is { } image && pictureBytes + image.Length <= MaxPagePictureBytes:
                    pictureBytes += image.Length;
                    pictures++;
                    page.Write("<figure class=\"picture\"><img alt=\"");
                    page.Write(E(entry.Text));
                    page.Write("\" src=\"data:");
                    page.Write(PictureType(image));
                    page.Write(";base64,");
                    page.Write(Convert.ToBase64String(image));
                    page.Write("\"><figcaption>");
                    page.Write(E(entry.Text));
                    page.Write("</figcaption></figure>\n");
                    break;
                case MessageRole.Attachment:
                    page.Write("<div class=\"tool\">");
                    page.Write(E(entry.Text + (entry.Image is not null ? "（图片太多，这一张没有放进网页）" : "")));
                    page.Write("</div>\n");
                    break;
                // System notes are app scaffolding, not part of the conversation.
            }
        }

        page.Write("<footer>由会话管家导出　·　");
        page.Write(exportedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
        page.Write("　·　");
        page.Write(content.Entries.Count.ToString(CultureInfo.InvariantCulture));
        page.Write(" 条");
        page.Write(includeTools ? "（含工具内容）" : "（工具内容已折叠为一行）");
        page.Write("</footer>\n</main>\n</body>\n</html>\n");
        return pictures;
    }

    /// <summary>The same page as a string, for a small conversation or a test.</summary>
    public static string ToHtml(TranscriptHeader header, TranscriptContent content, string assistantName, bool includeTools,
        DateTime exportedAt)
    {
        using var page = new StringWriter(CultureInfo.InvariantCulture);
        WriteHtml(page, header, content, assistantName, includeTools, exportedAt);
        return page.ToString();
    }

    /// <summary>A picture's media type, from its first bytes.</summary>
    public static string PictureType(byte[] bytes) => bytes switch
    {
        [0x89, 0x50, 0x4E, 0x47, ..] => "image/png",
        [0xFF, 0xD8, ..] => "image/jpeg",
        [0x47, 0x49, 0x46, 0x38, ..] => "image/gif",
        [0x42, 0x4D, ..] => "image/bmp",
        [0x52, 0x49, 0x46, 0x46, _, _, _, _, 0x57, 0x45, 0x42, 0x50, ..] => "image/webp",
        _ => "image/png",
    };

    private const string PageStyle = """

:root { color-scheme: light dark; --bg: #f6f5f2; --panel: #ffffff; --text: #1f1f1d; --muted: #6b6862; --faint: #9a968e;
  --line: #e4e1da; --user: #fbeee6; --user-line: #ebc3ad; --accent: #b4552f; --code: #f1efea; --code-text: #2a2926; --link: #22668a; }
@media (prefers-color-scheme: dark) {
  :root { --bg: #1e1e20; --panel: #262628; --text: #e8e6e3; --muted: #a5a19a; --faint: #7d7a74; --line: #3a3a3e;
    --user: #3a2d27; --user-line: #6e4532; --accent: #e08a66; --code: #1a1a1d; --code-text: #d9d5cd; --link: #7fb6d9; } }
* { box-sizing: border-box; }
body { margin: 0; background: var(--bg); color: var(--text);
  font: 15px/1.7 "Microsoft YaHei UI", "PingFang SC", "Segoe UI", system-ui, sans-serif; }
main { max-width: 920px; margin: 0 auto; padding: 32px 20px 48px; }
header h1 { font-size: 24px; line-height: 1.35; margin: 0 0 12px; }
dl.meta { display: grid; grid-template-columns: max-content 1fr; gap: 3px 16px; margin: 0 0 28px; font-size: 13px; color: var(--muted); }
dl.meta dt { color: var(--faint); }
dl.meta dd { margin: 0; word-break: break-all; }
.msg { margin: 18px 0; padding: 12px 16px 14px; border-radius: 10px; background: var(--panel); border: 1px solid var(--line); }
.msg.user { margin-left: 64px; background: var(--user); border-color: var(--user-line); }
.msg.assistant { margin-right: 64px; }
.who { font-size: 12.5px; font-weight: 600; color: var(--accent); margin-bottom: 6px; }
.msg.assistant .who { color: var(--link); }
.when { font-weight: 400; color: var(--faint); margin-left: 10px; }
.plain { white-space: pre-wrap; overflow-wrap: anywhere; }
.md { overflow-wrap: anywhere; }
.md > :first-child { margin-top: 0; }
.md > :last-child { margin-bottom: 0; }
.md p { margin: 0 0 10px; }
.md h1, .md h2, .md h3, .md h4, .md h5, .md h6 { line-height: 1.4; margin: 16px 0 8px; }
.md h1 { font-size: 20px; } .md h2 { font-size: 18px; } .md h3 { font-size: 16px; } .md h4, .md h5, .md h6 { font-size: 15px; }
pre { background: var(--code); color: var(--code-text); padding: 10px 12px; border-radius: 6px; overflow-x: auto; margin: 0 0 10px;
  font: 13px/1.55 "Cascadia Mono", Consolas, "Microsoft YaHei UI", monospace; white-space: pre; }
code { font-family: "Cascadia Mono", Consolas, "Microsoft YaHei UI", monospace; font-size: 0.92em; background: var(--code);
  padding: 1px 5px; border-radius: 4px; }
pre code { background: none; padding: 0; font-size: inherit; }
blockquote { margin: 0 0 10px; padding: 2px 12px; border-left: 3px solid var(--line); color: var(--muted); }
ul, ol { margin: 0 0 10px; padding-left: 24px; }
ul.tasks { list-style: none; padding-left: 4px; }
.box { display: inline-block; width: 14px; height: 14px; border: 1.5px solid var(--faint); border-radius: 3px; margin-right: 8px;
  vertical-align: -2px; font-size: 11px; line-height: 12px; text-align: center; }
.box.done { background: var(--accent); border-color: var(--accent); color: #fff; }
.table { overflow-x: auto; margin: 0 0 10px; }
table { border-collapse: collapse; font-size: 14px; }
th, td { border: 1px solid var(--line); padding: 5px 10px; vertical-align: top; }
th { background: var(--code); }
hr { border: 0; border-top: 1px solid var(--line); margin: 14px 0; }
a { color: var(--link); }
.ref { color: var(--link); border-bottom: 1px dotted var(--link); }
.tool { margin: 6px 4px; font: 12.5px/1.6 "Cascadia Mono", Consolas, "Microsoft YaHei UI", monospace; color: var(--faint);
  white-space: pre-wrap; overflow-wrap: anywhere; }
details.tool summary { cursor: pointer; }
details.tool pre { margin: 6px 0 0; font-size: 12px; max-height: 480px; overflow: auto; white-space: pre-wrap; }
figure.picture { margin: 18px 0 18px 64px; text-align: right; }
figure.picture img { max-width: 100%; border-radius: 8px; border: 1px solid var(--line); }
figcaption { font-size: 12px; color: var(--faint); margin-top: 4px; }
footer { margin-top: 36px; padding-top: 12px; border-top: 1px solid var(--line); font-size: 12px; color: var(--faint); }
@media (max-width: 640px) { .msg.user, figure.picture { margin-left: 24px; } .msg.assistant { margin-right: 24px; } }
@media print { body { background: #fff; } .msg, figure.picture { break-inside: avoid; } }
""";

    /// <summary>Only what the two sides said, speaker by speaker — what pasting a conversation somewhere wants.</summary>
    public static string ToPlainText(IEnumerable<TranscriptEntry> entries, string assistantName)
    {
        var sb = new StringBuilder();
        foreach (var entry in entries)
        {
            if (entry.Role is not (MessageRole.User or MessageRole.Assistant)) continue;
            sb.Append(entry.Role == MessageRole.User ? "你" : assistantName).Append('\n')
              .Append(entry.Text).Append("\n\n");
        }
        return sb.ToString();
    }

    /// <summary>A file name made from a title: no characters Windows refuses, no trailing dots, not too long.</summary>
    public static string SafeFileName(string title, int maxLength = 60)
    {
        var name = string.IsNullOrWhiteSpace(title) ? "对话记录" : OneLine(title);
        foreach (var c in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = name.Trim().TrimEnd('.');
        if (name.Length > maxLength) name = name[..maxLength].TrimEnd();
        return name.Length == 0 ? "对话记录" : name;
    }

    private static void AppendFence(StringBuilder sb, string detail) =>
        sb.Append("\n```\n").Append(detail.Replace("```", "'''", StringComparison.Ordinal)).Append("\n```\n");

    private static string OneLine(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Cell(string text) => OneLine(text).Replace("|", "\\|", StringComparison.Ordinal);
}
