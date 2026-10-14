using System.Diagnostics;
using ClaudeLauncher.Core.Contracts;
using ClaudeLauncher.Infrastructure.Backends;

namespace ClaudeLauncher.Infrastructure.Launch;

/// <summary>
/// Opens an imported conversation on the website it came from, in the default browser:
/// Gemini's (<c>gemini.google.com/app/&lt;id&gt;</c>, the link Takeout recorded), ChatGPT's
/// (<c>chatgpt.com/c/&lt;id&gt;</c>) and Claude's (<c>claude.ai/chat/&lt;id&gt;</c>). Only an id of the
/// form the site itself gives — hex for Gemini, a UUID for the others — is ever put into one.
/// </summary>
public static class WebLinks
{
    public static string? Build(string backend, string? id) => backend switch
    {
        ConversationKey.GeminiWeb when GeminiTakeout.IsConversationId(id) => "https://gemini.google.com/app/" + id,
        ConversationKey.ChatGptWeb when IsUuid(id) => "https://chatgpt.com/c/" + id,
        ConversationKey.ClaudeWeb when IsUuid(id) => "https://claude.ai/chat/" + id,
        _ => null,
    };

    public static bool CanOpen(string backend, string? id) => Build(backend, id) is not null;

    public static DeepLinkResult Open(string backend, string id)
    {
        if (Build(backend, id) is not { } uri)
            return new DeepLinkResult(DeepLinkOutcome.InvalidSessionId, "这段对话没有网页版的链接");
        var site = backend switch
        {
            ConversationKey.ChatGptWeb => "ChatGPT",
            ConversationKey.ClaudeWeb => "Claude",
            _ => "Gemini",
        };
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            return new DeepLinkResult(DeepLinkOutcome.Requested, $"已在浏览器里打开这段 {site} 对话", uri);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new DeepLinkResult(DeepLinkOutcome.ShellUnavailable, "打不开浏览器：" + ex.Message, uri);
        }
    }

    /// <summary>A UUID as the sites write them: 8-4-4-4-12 hex digits.</summary>
    private static bool IsUuid(string? id) => id is { Length: 36 } && Guid.TryParseExact(id, "D", out _);
}
