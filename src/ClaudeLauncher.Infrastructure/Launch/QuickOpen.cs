using System.Runtime.InteropServices;
using ClaudeLauncher.Core.Contracts;
using ClaudeLauncher.Infrastructure.Discovery;

namespace ClaudeLauncher.Infrastructure.Launch;

/// <summary>Which official app an entry of the taskbar's jump list opens.</summary>
public enum QuickOpenApp { Claude, ChatGpt }

/// <summary>
/// One conversation in the taskbar's jump list. Clicking it starts this program again with
/// <see cref="Arguments"/>, and that start opens the conversation in its own app — through the
/// same links as 「在应用中打开」 — and leaves.
/// </summary>
public sealed record QuickOpenItem(
    string Category, string Title, string Description, string Arguments, QuickOpenApp App);

/// <summary>
/// What the taskbar's jump list offers, and what a start from it does. The arguments carry
/// only an app's name, or a backend and an id checked against the same patterns as the links
/// themselves — so a start from the jump list never needs the catalog, and nothing but an id
/// ever reaches a link.
/// </summary>
public static class QuickOpen
{
    public const string OpenArgument = "--open";
    public const string LaunchArgument = "--launch";

    public const string PinnedCategory = "置顶";
    public const string RecentCategory = "最近的对话";

    /// <summary>What the taskbar shows of a list by default; Windows cuts off the rest.</summary>
    public const int MaxItems = 10;

    /// <summary>Pinned conversations leave room for recent ones below them.</summary>
    public const int MaxPinned = 5;

    private const int MaxTitleLength = 60;

    /// <summary>
    /// The conversations worth a click from the taskbar, from rows in the list's usual order
    /// (pinned first, then newest): only those still open in their own app — archived there,
    /// ChatGPT would first ask to unarchive — and that the app's own link can reach.
    /// </summary>
    public static IReadOnlyList<QuickOpenItem> Pick(IEnumerable<SessionMetadata> rows, int max = MaxItems)
    {
        var items = new List<QuickOpenItem>(max);
        var pinned = 0;
        foreach (var row in rows)
        {
            if (items.Count >= max) break;
            // A transcript a desktop session was continued from is that session in Claude: its row is the entry.
            if (row.Visibility != Visibility.Active || row.MergedInto is not null || row.ContinuedIn is not null
                || row.NativeArchiveState == NativeArchiveState.Archived) continue;
            if (Target(row) is not { } target) continue;
            if (row.LocalPinned && ++pinned > MaxPinned) continue;

            var app = target.Backend == ConversationKey.Codex ? QuickOpenApp.ChatGpt : QuickOpenApp.Claude;
            var action = target.Backend switch
            {
                ConversationKey.Codex => "在 ChatGPT 中打开这条任务",
                ConversationKey.CliCode => "在 Claude 中打开这条终端会话（第一次会导入到 Claude 的侧边栏）",
                _ => "在 Claude 中打开这条会话",
            };
            var description = string.IsNullOrWhiteSpace(row.ProjectRef) ? action : $"{action}\n{row.ProjectRef}";
            items.Add(new QuickOpenItem(
                row.LocalPinned ? PinnedCategory : RecentCategory,
                TitleOf(row), description,
                $"{OpenArgument} {target.Backend} {target.Id}", app));
        }
        return items;
    }

    /// <summary>The backend and id a start from the jump list opens; null when the row has no link of its app.</summary>
    private static (string Backend, string Id)? Target(SessionMetadata row)
    {
        var key = row.Key;
        return key.Backend switch
        {
            ConversationKey.Codex when ChatGptDeepLink.CanOpen(key.NativeSessionId) => (key.Backend, key.NativeSessionId),
            ConversationKey.DesktopCode when ClaudeDeepLink.BuildOpenDesktop(key.NativeSessionId) is not null
                => (key.Backend, key.NativeSessionId),
            ConversationKey.CliCode when ClaudeDeepLink.BuildResumeCli(row.CliSessionId ?? key.NativeSessionId) is not null
                => (key.Backend, row.CliSessionId ?? key.NativeSessionId),
            _ => null,
        };
    }

    /// <summary>One line, the user's own name when there is one, short enough for the taskbar's menu.</summary>
    private static string TitleOf(SessionMetadata row)
    {
        var title = string.Join(' ', row.DisplayTitle.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (title.Length == 0) title = "（无标题）";
        return title.Length <= MaxTitleLength ? title : title[..(MaxTitleLength - 1)] + "…";
    }

    /// <summary>The backend and id of a start from the jump list; null for anything else.</summary>
    public static (string Backend, string Id)? ParseOpen(IReadOnlyList<string> args)
    {
        if (args.Count != 3 || args[0] != OpenArgument) return null;
        var (backend, id) = (args[1], args[2]);
        var valid = backend switch
        {
            ConversationKey.Codex => ChatGptDeepLink.CanOpen(id),
            ConversationKey.DesktopCode => ClaudeDeepLink.BuildOpenDesktop(id) is not null,
            ConversationKey.CliCode => ClaudeDeepLink.BuildResumeCli(id) is not null,
            _ => false,
        };
        return valid ? (backend, id) : null;
    }

    /// <summary>The app a 「打开 Claude」/「打开 ChatGPT」 task starts; null for anything else.</summary>
    public static QuickOpenApp? ParseLaunch(IReadOnlyList<string> args) =>
        args.Count != 2 || args[0] != LaunchArgument ? null
        : args[1] == "claude" ? QuickOpenApp.Claude
        : args[1] == "chatgpt" ? QuickOpenApp.ChatGpt
        : null;

    public static string LaunchArguments(QuickOpenApp app) =>
        $"{LaunchArgument} {(app == QuickOpenApp.Claude ? "claude" : "chatgpt")}";

    /// <summary>Opens one conversation in its own app: the very routes of 「在应用中打开」.</summary>
    public static async Task<DeepLinkResult> OpenAsync(
        string backend, string id, InstalledApps apps, CancellationToken cancellation = default) =>
        backend == ConversationKey.Codex
            ? await ChatGptDeepLink.OpenAsync(apps.ChatGpt, id, cancellation)
            : ClaudeDeepLink.Open(backend,
                backend == ConversationKey.DesktopCode ? id : null,
                backend == ConversationKey.CliCode ? id : null);

    /// <summary>
    /// The app's own program, for its icon beside the entries that open it: the copy the user
    /// starts — standalone Claude's, Codex++'s managed ChatGPT before the store's. Null when
    /// there is no such file; the entry then shows this program's icon.
    /// </summary>
    public static string? IconOf(QuickOpenApp app, InstalledApps apps)
    {
        string?[] candidates = app == QuickOpenApp.Claude
            ? [apps.Claude.Executable, Under(apps.Claude.InstallLocation, @"app\claude.exe")]
            :
            [
                Under(apps.ChatGpt.ManagedAppRoot, "ChatGPT.exe"), Under(apps.ChatGpt.ManagedAppRoot, "Codex.exe"),
                Under(apps.ChatGpt.InstallLocation, @"app\ChatGPT.exe"), Under(apps.ChatGpt.InstallLocation, @"app\Codex.exe"),
            ];
        foreach (var path in candidates)
        {
            try
            {
                if (path is not null && File.Exists(path)) return path;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* next */ }
        }
        return null;

        static string? Under(string? root, string relative)
        {
            if (string.IsNullOrWhiteSpace(root)) return null;
            try { return Path.Combine(root, relative); }
            catch (ArgumentException) { return null; }
        }
    }

    private const uint AnyProcess = unchecked((uint)-1);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint processId);

    /// <summary>
    /// Lets the app being asked to open something come to the front. Windows gives the
    /// foreground only to a process the user just acted in — this one, when they clicked here
    /// — so it is passed on before the link, or the app would only flash on the taskbar.
    /// Harmless when this process has nothing to pass on.
    /// </summary>
    public static void LetTheAppComeForward()
    {
        try { AllowSetForegroundWindow(AnyProcess); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { /* not Windows */ }
    }
}
