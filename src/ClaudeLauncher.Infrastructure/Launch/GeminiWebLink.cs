using System.Diagnostics;
using ClaudeLauncher.Infrastructure.Backends;

namespace ClaudeLauncher.Infrastructure.Launch;

/// <summary>
/// Opens an imported conversation on Gemini's website, in the default browser: the link
/// Takeout recorded for it, <c>https://gemini.google.com/app/&lt;id&gt;</c>. Only an id that came
/// out of such a link — hex — is ever put into one.
/// </summary>
public static class GeminiWebLink
{
    public static bool CanOpen(string? id) => GeminiTakeout.IsConversationId(id);

    public static string? Build(string? id) => CanOpen(id) ? "https://gemini.google.com/app/" + id : null;

    public static DeepLinkResult Open(string id)
    {
        if (Build(id) is not { } uri)
            return new DeepLinkResult(DeepLinkOutcome.InvalidSessionId, "这段对话没有网页版的链接");
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            return new DeepLinkResult(DeepLinkOutcome.Requested, "已在浏览器里打开这段 Gemini 对话", uri);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new DeepLinkResult(DeepLinkOutcome.ShellUnavailable, "打不开浏览器：" + ex.Message, uri);
        }
    }
}
