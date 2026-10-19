using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ClaudeLauncher.Infrastructure.Launch;

public enum DeepLinkOutcome
{
    /// <summary>Handed to the shell. Claude decides what happens next; this is not proof it opened.</summary>
    Requested,
    InvalidSessionId,
    UnsupportedBackend,
    ShellUnavailable,
    Failed,
}

public sealed record DeepLinkResult(DeepLinkOutcome Outcome, string Message, string? Uri = null);

/// <summary>
/// Opens a conversation in the official Claude through its registered <c>claude://</c> protocol.
///
/// <para>
/// This is Claude's own entry point, not a private interface: the app declares the protocol in
/// its manifest, and the resume route is what the in-app "Import CLI sessions" action uses. The
/// launcher only asks the shell to follow a link — Claude performs the import and opens the
/// session itself, so sign-in, account selection and every permission prompt stay with Claude.
/// </para>
///
/// <para>
/// Update resilience: the URL carries no version and no file path, so it survives app updates
/// for the same reason AUMID activation does.
/// </para>
/// </summary>
public static partial class ClaudeDeepLink
{
    private const string Host = "claude://claude.ai";

    [GeneratedRegex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")]
    private static partial Regex Uuid();

    [GeneratedRegex("^local_[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")]
    private static partial Regex DesktopSessionId();

    /// <summary>
    /// Imports a CLI transcript into the desktop app and opens it — the same path the in-app
    /// "Import CLI sessions" action takes. Claude routes its links by host, and the import is
    /// the host <c>resume</c> itself: <c>claude://resume?session=…</c>. A link under the
    /// claude.ai host would be taken for a web page and import nothing.
    /// </summary>
    public static string? BuildResumeCli(string cliSessionId) =>
        Uuid().IsMatch(cliSessionId ?? string.Empty)
            ? $"claude://resume?session={cliSessionId}"
            : null;

    /// <summary>Opens a session the desktop app already owns.</summary>
    public static string? BuildOpenDesktop(string desktopSessionId) =>
        DesktopSessionId().IsMatch(desktopSessionId ?? string.Empty)
            ? $"{Host}/epitaxy/{desktopSessionId}"
            : null;

    /// <summary>
    /// Builds the right link for a backend. Ids are validated against a strict pattern before
    /// they reach a URL, so a title or path can never be smuggled into one.
    /// </summary>
    public static (string? Uri, DeepLinkOutcome Outcome) BuildFor(
        string backend, string? desktopSessionId, string? cliSessionId)
    {
        switch (backend)
        {
            case "desktop-code" when desktopSessionId is { Length: > 0 }:
            {
                var uri = BuildOpenDesktop(desktopSessionId);
                return (uri, uri is null ? DeepLinkOutcome.InvalidSessionId : DeepLinkOutcome.Requested);
            }
            case "cli-code" when cliSessionId is { Length: > 0 }:
            {
                var uri = BuildResumeCli(cliSessionId);
                return (uri, uri is null ? DeepLinkOutcome.InvalidSessionId : DeepLinkOutcome.Requested);
            }
            default:
                return (null, DeepLinkOutcome.UnsupportedBackend);
        }
    }

    public static DeepLinkResult Open(string backend, string? desktopSessionId, string? cliSessionId)
    {
        var (uri, outcome) = BuildFor(backend, desktopSessionId, cliSessionId);

        if (outcome == DeepLinkOutcome.UnsupportedBackend)
            return new DeepLinkResult(outcome,
                "该来源没有可用的官方打开方式（仅桌面 Code 与终端 CLI 支持深链接）");
        if (uri is null)
            return new DeepLinkResult(DeepLinkOutcome.InvalidSessionId,
                "会话 ID 格式不合法，拒绝拼入 URL");

        try
        {
            // ShellExecute is what resolves a registered protocol handler. Routing the URI
            // through explorer.exe as an argument does not reliably invoke a custom scheme,
            // so the URI is the FileName and UseShellExecute is on.
            var psi = new ProcessStartInfo { FileName = uri, UseShellExecute = true };
            QuickOpen.LetTheAppComeForward();
            using var _ = Process.Start(psi);

            return new DeepLinkResult(DeepLinkOutcome.Requested,
                backend == "cli-code"
                    ? "已请求 Claude 导入并打开这条终端会话（Claude 侧边栏会多出这条会话；已导入过的直接打开）"
                    : "已请求 Claude 打开该会话",
                uri);
        }
        catch (Exception ex)
        {
            return new DeepLinkResult(DeepLinkOutcome.Failed, ex.Message, uri);
        }
    }

    /// <summary>
    /// What Claude itself reports when a resume link fails. Surfaced verbatim so a failure is
    /// never mistaken for the launcher having done something wrong — or for success.
    /// </summary>
    public const string FailureHint =
        "若 Claude 未打开会话，常见原因：登录状态过期、账户信息暂时不可达、"
        + "该 CLI 会话的 transcript 已不在磁盘上，或设置中关闭了 claude:// 深链接处理。";

    // How far each route is verified, recorded rather than assumed:
    //
    // Desktop — the URL is exactly the link Claude itself publishes for a session. Verified end
    // to end on 2026-09-23 with Claude 2.7032: 「在 Claude 中打开」 on a listed desktop session
    // switched Claude's main pane to it, and the same link for the session shown before
    // switched it back.
    //
    // CLI import — earlier versions sent claude://claude.ai/resume?session=…, which a live test
    // showed imports nothing: Claude's handler switches on the link's host, so that link went
    // to the claude.ai web route. The import route is the host "resume" itself
    // (claude://resume?session=<uuid>; the id is checked against a UUID pattern, then
    // importCliSession adds local_<id> to the sidebar, or opens it when already imported) —
    // read from Claude 2.7032's own handler on 2026-09-24. Not run live here, since it adds a
    // session to the user's sidebar.
}
