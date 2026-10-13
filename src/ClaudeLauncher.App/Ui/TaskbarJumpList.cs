using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Shell;
using ClaudeLauncher.Infrastructure.Launch;

namespace ClaudeLauncher.App;

/// <summary>
/// The taskbar button's right-click list: the pinned and most recent conversations, each
/// opened in its own app, and 「打开 Claude」「打开 ChatGPT」. An entry starts this program again
/// with the entry's arguments; that start opens what it names and leaves — no window, and no
/// running copy involved. Windows keeps the list with its own record of recent items, so it is
/// there on a pinned taskbar button too; the 「⋯」 menu can leave the conversations out.
/// </summary>
internal static class TaskbarJumpList
{
    /// <summary>What was last handed to Windows: an unchanged list is not written again.</summary>
    private static string? _applied;

    /// <summary>Hands the list to Windows when it differs from the one it has. UI thread only.</summary>
    public static void Apply(IReadOnlyList<QuickOpenItem> conversations, string? claudeIcon, string? chatGptIcon)
    {
        var program = Environment.ProcessPath;
        if (string.IsNullOrEmpty(program)) return;

        var fingerprint = string.Join('\n', conversations.Select(c => $"{c.Category}\t{c.Title}\t{c.Description}\t{c.Arguments}"))
                          + $"\n{claudeIcon}\n{chatGptIcon}\n{program}";
        if (fingerprint == _applied) return;

        JumpItem[] tasks =
        [
            Entry(program, "打开 Claude", "启动 Claude 桌面版；已在运行就切换过去",
                QuickOpen.LaunchArguments(QuickOpenApp.Claude), claudeIcon, category: null),
            Entry(program, "打开 ChatGPT", "启动 ChatGPT 桌面版（Codex 所在的应用），有「Codex++（全部任务）」快捷方式就走它",
                QuickOpen.LaunchArguments(QuickOpenApp.ChatGpt), chatGptIcon, category: null),
        ];
        var entries = conversations.Select(c => Entry(program, c.Title, c.Description, c.Arguments,
            c.App == QuickOpenApp.Claude ? claudeIcon : chatGptIcon, c.Category));

        // Windows may refuse the conversations' categories; the two tasks are still worth having then.
        if (TryApply([.. entries, .. tasks]) || TryApply(tasks)) _applied = fingerprint;
    }

    private static JumpTask Entry(
        string program, string title, string description, string arguments, string? icon, string? category) => new()
    {
        Title = title,
        Description = description,
        ApplicationPath = program,
        Arguments = arguments,
        // The app's own icon beside what opens it; without one, this program's.
        IconResourcePath = icon ?? program,
        IconResourceIndex = 0,
        CustomCategory = category,
    };

    private static bool TryApply(IEnumerable<JumpItem> items)
    {
        try
        {
            new JumpList(items, showFrequent: false, showRecent: false).Apply();
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException
                                       or ArgumentException or FileNotFoundException)
        {
            return false;
        }
    }
}
