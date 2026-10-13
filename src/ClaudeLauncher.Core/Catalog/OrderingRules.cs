using ClaudeLauncher.Core.Contracts;

namespace ClaudeLauncher.Core.Catalog;

/// <summary>Where a conversation stands inside its own app, as one value that can be shown and sorted.</summary>
public enum AppStanding
{
    /// <summary>In the app's own list (Claude's sidebar, ChatGPT's task list).</summary>
    Active = 1,
    /// <summary>Archived inside the app. Still here, still readable.</summary>
    Archived = 2,
    /// <summary>
    /// A terminal transcript a Claude desktop session has since continued from: the earlier
    /// part of a conversation the desktop app lists — as that session.
    /// </summary>
    EarlierPart = 3,
    /// <summary>A terminal transcript only: the desktop app does not list it.</summary>
    TerminalOnly = 4,
    /// <summary>A copy imported from an export: what the app holds now is not observed.</summary>
    Imported = 5,
    /// <summary>Nothing observed either way.</summary>
    Unobserved = 6,
}

/// <summary>
/// The orders categories are listed in. One definition for the list's labels and the
/// catalog's sort: the catalog repeats these rules in SQL, and a test holds the two together,
/// so a column can never be sorted in an order that disagrees with what it shows.
/// </summary>
public static class OrderingRules
{
    /// <summary>
    /// Mirrors what the app itself says about the row. A Codex thread is archived or it is
    /// in the task list; a terminal transcript has no app-side state at all — unless a desktop
    /// session was <paramref name="continued"/> from it, which makes it that session's earlier part.
    /// </summary>
    public static AppStanding Standing(string backend, NativeArchiveState state, bool continued = false) => backend switch
    {
        ConversationKey.Codex => state == NativeArchiveState.Archived ? AppStanding.Archived : AppStanding.Active,
        ConversationKey.CliCode => continued ? AppStanding.EarlierPart : AppStanding.TerminalOnly,
        ConversationKey.GeminiWeb => AppStanding.Imported,
        _ => state switch
        {
            NativeArchiveState.Active => AppStanding.Active,
            NativeArchiveState.Archived => AppStanding.Archived,
            NativeArchiveState.Unsupported => AppStanding.TerminalOnly,
            _ => AppStanding.Unobserved,
        },
    };

    public static string Label(AppStanding standing) => standing switch
    {
        AppStanding.Active => "活跃",
        AppStanding.Archived => "已归档",
        AppStanding.EarlierPart => "续接前",
        AppStanding.TerminalOnly => "仅终端",
        AppStanding.Imported => "导入",
        _ => "未观测",
    };

    /// <summary>Claude's two views of its history first, desktop before terminal, then ChatGPT, then Gemini CLI and Gemini on the web.</summary>
    public static int SourceRank(string backend) => backend switch
    {
        ConversationKey.DesktopCode => 1,
        ConversationKey.CliCode => 2,
        ConversationKey.Codex => 3,
        ConversationKey.Gemini => 4,
        ConversationKey.GeminiWeb => 5,
        _ => 9,
    };
}
