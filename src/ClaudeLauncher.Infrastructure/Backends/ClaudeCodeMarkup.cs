namespace ClaudeLauncher.Infrastructure.Backends;

/// <summary>
/// How Claude Code records a slash command in a transcript: a user message made of
/// <c>&lt;command-name&gt;/loop&lt;/command-name&gt;</c>, <c>&lt;command-message&gt;</c> and
/// <c>&lt;command-args&gt;</c> elements, in either order. The markup is the app's, but the
/// arguments are what the person typed — often the whole request — so they are shown and
/// used for titles as the user's words instead of being folded away with the scaffolding.
/// </summary>
public static class ClaudeCodeMarkup
{
    /// <summary>
    /// Reads <paramref name="text"/> as a recorded slash command. False unless the text is
    /// nothing but such elements, so a command with prose around it, or any other markup,
    /// keeps being treated as markup. Elements it does not know (<c>&lt;skill-format&gt;</c>)
    /// are allowed and ignored.
    /// </summary>
    public static bool TryReadCommand(string text, out SlashCommand command)
    {
        command = default;
        string? name = null, arguments = null;
        var rest = text.AsSpan().Trim();
        while (!rest.IsEmpty)
        {
            if (!TryTakeElement(ref rest, out var tag, out var inner)) return false;
            if (tag == "command-name")
            {
                if (name is not null) return false;
                name = inner.Trim();
            }
            else if (tag == "command-args")
            {
                if (arguments is not null) return false;
                arguments = inner.Trim();
            }
            rest = rest.TrimStart();
        }

        if (string.IsNullOrEmpty(name) || name.Any(char.IsWhiteSpace)) return false;
        command = new SlashCommand(name[0] == '/' ? name : "/" + name, arguments ?? string.Empty);
        return true;
    }

    /// <summary>
    /// The app's own blocks at the head of a message, split from what the user wrote after
    /// them. Claude's desktop app puts a <c>&lt;system-reminder&gt;…&lt;/system-reminder&gt;</c> in
    /// front of the first thing typed in a session without a project folder — in the same
    /// message, so the whole message looked injected and the user's words went with it. Only
    /// closed blocks are taken; the words are empty when the message is nothing else.
    /// </summary>
    public static (IReadOnlyList<string> Blocks, string Words) PeelInjected(string text)
    {
        var blocks = new List<string>();
        var rest = text.AsSpan().TrimStart();
        while (true)
        {
            var before = rest;
            if (!TryTakeElement(ref rest, out var tag, out _) || !InjectedContext.Contains(tag))
            {
                rest = before;
                break;
            }
            blocks.Add(before[..(before.Length - rest.Length)].ToString());
            rest = rest.TrimStart();
        }
        return (blocks, rest.ToString());
    }

    /// <summary>
    /// Context the apps put in front of what was typed: the desktop app's reminder, the IDE
    /// extension's open file and selection. Command markup is not context — prose after a
    /// command's elements is no command, and stays folded with them.
    /// </summary>
    private static readonly HashSet<string> InjectedContext = new(StringComparer.Ordinal)
    {
        "system-reminder", "ide_opened_file", "ide_selection",
    };

    /// <summary>What the user wrote after the app's own blocks; the text as it was when there are none, or nothing else.</summary>
    public static string UsersWords(string text) =>
        PeelInjected(text) is { Blocks.Count: > 0, Words.Length: > 0 } peeled ? peeled.Words : text;

    /// <summary>Takes one <c>&lt;tag&gt;…&lt;/tag&gt;</c> off the front; the tag must be a plain lower-case name.</summary>
    private static bool TryTakeElement(ref ReadOnlySpan<char> rest, out string tag, out string inner)
    {
        tag = inner = string.Empty;
        if (rest.Length < 3 || rest[0] != '<') return false;

        var close = rest.IndexOf('>');
        if (close < 2) return false;
        var name = rest[1..close];
        foreach (var c in name)
            if (!(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '_')) return false;

        var body = rest[(close + 1)..];
        var end = "</" + name.ToString() + ">";
        var at = body.IndexOf(end, StringComparison.Ordinal);
        if (at < 0) return false;

        tag = name.ToString();
        inner = body[..at].ToString();
        rest = body[(at + end.Length)..];
        return true;
    }
}

/// <param name="Name">The command with its leading slash: <c>/loop</c>.</param>
/// <param name="Arguments">What was typed after it; empty when nothing was.</param>
public readonly record struct SlashCommand(string Name, string Arguments)
{
    /// <summary>The command as it was typed: <c>/loop 每天检查一次</c>.</summary>
    public string Typed => Arguments.Length == 0 ? Name : Name + " " + Arguments;
}
