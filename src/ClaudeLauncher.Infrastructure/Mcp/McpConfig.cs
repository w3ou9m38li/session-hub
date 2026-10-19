using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ClaudeLauncher.Infrastructure.Mcp;

/// <summary>
/// What to put into each agent's own configuration to start this program as its MCP server —
/// shown for the user to copy; this program never edits those files itself.
/// </summary>
public static partial class McpConfig
{
    /// <summary>
    /// The command that registers the server with Claude Code, for every project of this user. The
    /// separator is quoted: PowerShell drops a bare <c>--</c> on its way into a script, and an
    /// npm-installed <c>claude</c> is one (claude.ps1) — Claude Code would then take <c>--mcp</c>
    /// for an option of its own. Quoted, it arrives as it is from every shell.
    /// </summary>
    public static string ClaudeCode(string exe) =>
        $"claude mcp add --scope user {McpServer.Name} \"--\" \"{Checked(exe)}\" {McpHost.Argument}";

    /// <summary>The table for Codex's <c>%USERPROFILE%\.codex\config.toml</c>.</summary>
    public static string Codex(string exe) =>
        $"[mcp_servers.{McpServer.Name}]\ncommand = {Toml(Checked(exe))}\nargs = [\"{McpHost.Argument}\"]";

    /// <summary>The entry for <c>"mcpServers"</c> in Claude's desktop app's <c>claude_desktop_config.json</c>.</summary>
    public static string ClaudeDesktop(string exe) =>
        $"\"{McpServer.Name}\": {{ \"command\": {Json(Checked(exe))}, \"args\": [\"{McpHost.Argument}\"] }}";

    /// <summary>
    /// Whether the program's file name carries its version — the shared build is named
    /// 会话管家-0.24.0-win-x64.exe — so a configured path stops working when the next version
    /// replaces this file.
    /// </summary>
    public static bool NameCarriesVersion(string exe) => VersionInName().IsMatch(Path.GetFileName(Checked(exe)));

    [GeneratedRegex(@"\d+\.\d+")]
    private static partial Regex VersionInName();

    private static string Checked(string exe)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exe);
        return exe;
    }

    /// <summary>A TOML literal string, which takes a Windows path as it is; a basic one, escaped, for a path a literal cannot hold.</summary>
    private static string Toml(string text)
    {
        if (!text.Contains('\'') && !text.Any(char.IsControl)) return "'" + text + "'";
        var basic = new StringBuilder("\"");
        foreach (var c in text)
        {
            if (c is '"' or '\\') basic.Append('\\').Append(c);
            else if (char.IsControl(c)) basic.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
            else basic.Append(c);
        }
        return basic.Append('"').ToString();
    }

    private static readonly JsonSerializerOptions Readable = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>A JSON string, its Chinese left readable.</summary>
    private static string Json(string text) => JsonValue.Create(text).ToJsonString(Readable);
}
