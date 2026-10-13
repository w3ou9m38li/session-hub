using System.Text.RegularExpressions;
using System.Windows.Media;
using ClaudeLauncher.Infrastructure.Backends;

namespace ClaudeLauncher.App.ViewModels;

/// <summary>One line of the side panel's 最近对话: who said it, when, and how it began.</summary>
public sealed record PreviewLine(string Speaker, string When, string Text, bool IsUser)
{
    /// <summary>The colours the transcript window gives the two sides.</summary>
    public Brush Accent => IsUser ? Palette.ClaudeText : Palette.Info;
}

/// <summary>One place the search word was said, for the side panel: who, when, and the words around it.</summary>
public sealed record PreviewHit(string Speaker, string When, string Text, bool IsUser, string Term)
{
    public Brush Accent => IsUser ? Palette.ClaudeText : Palette.Info;
}

/// <summary>
/// The last things said in a conversation. It is what tells apart conversations that share a
/// title — several 「项目完善与功能精简」 in a row say nothing until you see how each one ended.
/// </summary>
public sealed partial record ConversationPreview(IReadOnlyList<PreviewLine> Lines, string? Note)
{
    /// <summary>Longest text kept per line; the panel shows four lines of it at most.</summary>
    private const int MaxChars = 360;

    public static ConversationPreview From(IReadOnlyList<TranscriptEntry> entries, string assistantName) =>
        new(entries.Select(e => new PreviewLine(
                e.Role == MessageRole.User ? "你" : assistantName,
                e.Timestamp ?? string.Empty,
                Flatten(e.Text),
                e.Role == MessageRole.User)).ToList(),
            entries.Count == 0 ? "记录末尾没有对话文字，只有工具调用或系统内容" : null);

    public static ConversationPreview Unavailable(string note) => new([], note);

    private static string Flatten(string text)
    {
        var flat = Plain(string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
        return flat.Length > MaxChars ? flat[..MaxChars] + "…" : flat;
    }

    /// <summary>
    /// A one-line excerpt without Markdown's marks — bold, inline code, heading hashes — which
    /// are noise when the text is not rendered.
    /// </summary>
    public static string Plain(string text) =>
        Headings().Replace(text.Replace("**", "", StringComparison.Ordinal).Replace("`", "", StringComparison.Ordinal), "$1");

    [GeneratedRegex(@"(^|\s)#{1,6}\s+")]
    private static partial Regex Headings();
}
