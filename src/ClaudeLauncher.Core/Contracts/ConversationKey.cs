namespace ClaudeLauncher.Core.Contracts;

/// <summary>
/// Identity of one conversation. Account and organization are part of the key:
/// the same native id under a different account or org is a different conversation,
/// and a Desktop session id never collides with a CLI session id.
/// </summary>
public sealed record ConversationKey(
    string Backend,
    string AccountContextId,
    string OrganizationContextId,
    string NativeSessionId)
{
    public const string DesktopCode = "desktop-code";
    public const string CliCode = "cli-code";
    public const string Chat = "chat";
    public const string Cowork = "cowork";

    /// <summary>A Codex thread from the ChatGPT desktop app (its coding agent), read from CODEX_HOME.</summary>
    public const string Codex = "codex";

    /// <summary>A Gemini CLI session, read from <c>~\.gemini\tmp\&lt;project&gt;\chats</c>.</summary>
    public const string Gemini = "gemini";

    /// <summary>A conversation from Gemini on the web, imported from a Google Takeout export into this tool's own data root.</summary>
    public const string GeminiWeb = "gemini-web";

    /// <summary>True for the two Claude backends that have a transcript on this machine.</summary>
    public static bool IsClaude(string backend) => backend is DesktopCode or CliCode;

    /// <summary>Stable, non-reversible display form for logs. Never contains titles or bodies.</summary>
    public string ToLogForm() =>
        $"{Backend}/{Short(AccountContextId)}/{Short(OrganizationContextId)}/{Short(NativeSessionId)}";

    private static string Short(string value) =>
        string.IsNullOrEmpty(value) ? "-" : value.Length <= 8 ? value : value[..8];
}
