using ClaudeLauncher.Core.Catalog;

namespace ClaudeLauncher.Core.Contracts;

/// <summary>
/// Which app a conversation belongs to: one entry of the list's app filter, one colour, one
/// series in the usage window. The numbers are stored in the usage ledger: never renumber them.
/// </summary>
public enum AppFamily
{
    Claude = 0,
    Codex = 1,
    Gemini = 2,
    DeepSeek = 3,
}

/// <summary>How 「应用内」 reads for a source's rows (<c>OrderingRules.Standing</c>, and the catalog's SQL beside it).</summary>
public enum StandingRule
{
    /// <summary>As the app's own list has it: active, archived, not kept in any list (Unsupported), or not observed.</summary>
    AppState,
    /// <summary>Archived in the app, else in its list: nothing in between (Codex).</summary>
    ArchivedOrListed,
    /// <summary>A terminal transcript: the earlier part of the desktop session it was continued in, else terminal only.</summary>
    TerminalTranscript,
    /// <summary>A copy imported from an export: what the app holds now is not observed.</summary>
    Imported,
}

/// <summary>How a conversation is opened in its own app, when it can be.</summary>
public enum OpenRoute
{
    /// <summary>No app to open it in (a terminal tool).</summary>
    None,
    /// <summary>Claude's own <c>claude://</c> links.</summary>
    ClaudeLink,
    /// <summary>ChatGPT's <c>codex://threads/&lt;id&gt;</c>, handed to the running app.</summary>
    CodexLink,
    /// <summary>The conversation's own page on the web.</summary>
    WebLink,
    /// <summary>The app opens; the conversation is found in it by hand (no link reaches one conversation).</summary>
    AppOnly,
}

/// <summary>How a conversation is continued from a terminal, when it can be.</summary>
public enum ResumeRoute
{
    None,
    /// <summary><c>claude --resume</c>.</summary>
    ClaudeCli,
    /// <summary><c>codex resume</c>.</summary>
    CodexCli,
    /// <summary><c>gemini --resume</c>.</summary>
    GeminiCli,
}

/// <summary>What 「改动的文件」 can read from a source's transcripts.</summary>
public enum ChangesSupport
{
    /// <summary>Claude Code's Edit / Write / MultiEdit calls.</summary>
    ClaudeEdits,
    /// <summary>Codex's applied patches.</summary>
    CodexPatches,
    /// <summary>Nothing: <see cref="SourceInfo.NoChangesSummary"/> and <see cref="SourceInfo.NoChangesText"/> say why.</summary>
    None,
}

/// <summary>Which id a row's transcript copy is kept under (<c>TranscriptArchive.IdentityOf</c>).</summary>
public enum CopyKey
{
    /// <summary>No copy: the transcript is this tool's own already (an import), or there is none.</summary>
    None,
    /// <summary>The conversation's own id.</summary>
    Native,
    /// <summary>The terminal session a desktop record runs on.</summary>
    CliSession,
    /// <summary>The terminal session it runs on, else its own id (a terminal transcript is named by it).</summary>
    CliSessionOrNative,
}

/// <summary>An app family's names where the family itself is meant: the list's filter, the usage window.</summary>
/// <param name="FilterName">The app filter's entry: 「ChatGPT · Codex」.</param>
/// <param name="UsageName">The usage window's series and labels: 「Codex」.</param>
public sealed record FamilyInfo(AppFamily Family, string FilterName, string UsageName);

/// <summary>
/// Everything about one source the rest of the program needs to know besides how to read it:
/// what it is called in each place, its family, how 「应用内」 reads, whether it can be opened
/// in an app, continued from a terminal, shown file by file, counted for tokens and kept as a
/// copy. One record per source: a new source is one entry here, its reader, and its line in the
/// sync — not a branch in every window.
/// </summary>
public sealed record SourceInfo
{
    /// <summary>The backend id rows carry (<see cref="ConversationKey.Backend"/>).</summary>
    public required string Backend { get; init; }

    public required AppFamily Family { get; init; }

    /// <summary>The order of sections when the list is sorted by source; the catalog's SQL repeats it.</summary>
    public required int Rank { get; init; }

    /// <summary>
    /// Whether this tool reads it. Claude's plain chats and Cowork are not read — they keep no
    /// transcript on this computer — and are named in the coverage report as such.
    /// </summary>
    public bool Read { get; init; } = true;

    /// <summary>Named in every sync summary, found or not; the others only when found.</summary>
    public bool AlwaysReported { get; init; }

    /// <summary>The narrow source column, and the sync summary: 「终端 CLI」「Gemini 网页」.</summary>
    public required string ColumnName { get; init; }

    /// <summary>The coverage report: 「Gemini 网页版」.</summary>
    public required string ReportName { get; init; }

    /// <summary>An exported list, read away from this program: 「Claude 终端」.</summary>
    public required string ExportName { get; init; }

    /// <summary>The list's heading when sorted by source: 「Claude · 终端 CLI」.</summary>
    public required string SectionName { get; init; }

    public string? SectionTooltip { get; init; }

    /// <summary>The app that owns the conversation, for labels: 「ChatGPT」.</summary>
    public required string AppName { get; init; }

    /// <summary>Who answers, in the transcript view.</summary>
    public required string AssistantName { get; init; }

    public required StandingRule Standing { get; init; }

    /// <summary>The detail panel's longer words for each standing this source's rows can have.</summary>
    public IReadOnlyDictionary<AppStanding, string> StateTexts { get; init; } = new Dictionary<AppStanding, string>();

    /// <summary>Why a row of this source has nothing to show, for the UI to show verbatim.</summary>
    public string MissingText { get; init; } = "找不到对应的 transcript 文件——可能已被清理，或它属于另一个配置目录。";

    /// <summary>The folder of this tool's copies the source's transcripts are kept in; null when none are kept.</summary>
    public string? CopyFamily { get; init; }

    public CopyKey CopyKey { get; init; }

    public OpenRoute Open { get; init; }

    /// <summary>
    /// For a source opened by <see cref="OpenRoute.WebLink"/> or <see cref="OpenRoute.AppOnly"/>,
    /// or not at all: its button's words when it can be opened, and when it cannot. Claude's and
    /// ChatGPT's depend on the row and are written where the row is.
    /// </summary>
    public OpenTexts? OpenWords { get; init; }

    public ResumeRoute Resume { get; init; }

    /// <summary>For a source without a terminal to continue in: what the button says, and why.</summary>
    public string? NoResumeLabel { get; init; }

    public string? NoResumeTooltip { get; init; }

    public ChangesSupport Changes { get; init; }

    public string? NoChangesSummary { get; init; }

    public string? NoChangesText { get; init; }

    /// <summary>Whether its transcripts record tokens the usage window counts.</summary>
    public bool CountsTokens { get; init; }
}

/// <summary>The words of an 「在应用中打开」 button: when it can open the conversation, and when it cannot.</summary>
public sealed record OpenTexts(
    string Label, string ShortLabel, string Tooltip,
    string NoLabel, string NoShortLabel, string NoTooltip);

/// <summary>The sources this program knows, and the app families they belong to.</summary>
public static class Sources
{
    public static SourceInfo DesktopCode { get; } = new()
    {
        Backend = ConversationKey.DesktopCode,
        Family = AppFamily.Claude,
        Rank = 1,
        AlwaysReported = true,
        ColumnName = "桌面 Code",
        ReportName = "桌面 Code",
        ExportName = "Claude 桌面",
        SectionName = "Claude · 桌面 Code",
        SectionTooltip = "在 Claude 桌面版侧边栏里的会话",
        AppName = "Claude",
        AssistantName = "Claude",
        Standing = StandingRule.AppState,
        StateTexts = new Dictionary<AppStanding, string>
        {
            [AppStanding.Active] = "在 Claude 桌面侧边栏中",
            [AppStanding.Archived] = "在 Claude 桌面中已归档",
            [AppStanding.Unobserved] = "未观测到在 Claude 中的状态",
        },
        CopyFamily = "claude",
        CopyKey = CopyKey.CliSession,
        Open = OpenRoute.ClaudeLink,
        Resume = ResumeRoute.ClaudeCli,
        Changes = ChangesSupport.ClaudeEdits,
        CountsTokens = true,
    };

    public static SourceInfo CliCode { get; } = new()
    {
        Backend = ConversationKey.CliCode,
        Family = AppFamily.Claude,
        Rank = 2,
        AlwaysReported = true,
        ColumnName = "终端 CLI",
        ReportName = "终端 CLI",
        ExportName = "Claude 终端",
        SectionName = "Claude · 终端 CLI",
        SectionTooltip = "终端里 claude 命令留下的会话",
        AppName = "Claude",
        AssistantName = "Claude",
        Standing = StandingRule.TerminalTranscript,
        StateTexts = new Dictionary<AppStanding, string>
        {
            [AppStanding.EarlierPart] =
                "桌面会话续接前的部分：Claude 续接这段对话时换了一份新的记录，这一份是之前的内容；在 Claude 侧边栏里是那条桌面会话",
            [AppStanding.TerminalOnly] = "只在终端 transcript 里，不在 Claude 桌面侧边栏",
        },
        CopyFamily = "claude",
        CopyKey = CopyKey.CliSessionOrNative,
        Open = OpenRoute.ClaudeLink,
        Resume = ResumeRoute.ClaudeCli,
        Changes = ChangesSupport.ClaudeEdits,
        CountsTokens = true,
    };

    public static SourceInfo ClaudeWeb { get; } = new()
    {
        Backend = ConversationKey.ClaudeWeb,
        Family = AppFamily.Claude,
        Rank = 3,
        ColumnName = "Claude 网页",
        ReportName = "Claude 网页版",
        ExportName = "Claude 网页版",
        SectionName = "Claude · 网页版",
        SectionTooltip = "从 Claude 的数据导出导入的 claude.ai 对话（也是桌面版里的普通聊天）",
        AppName = "Claude 网页版",
        AssistantName = "Claude",
        Standing = StandingRule.Imported,
        StateTexts = new Dictionary<AppStanding, string>
        {
            [AppStanding.Imported] =
                "从 Claude 的数据导出导入的副本：网页上后来的改动不会自动反映到这里，再导入一份新的导出才会补上",
        },
        MissingText = "找不到这段导入的 Claude 网页对话——导入的记录可能已被移除；重新导入 Claude 的数据导出即可。",
        CopyKey = CopyKey.None,
        Open = OpenRoute.WebLink,
        OpenWords = new OpenTexts(
            "在浏览器中打开这段对话", "在浏览器中打开",
            "在默认浏览器里打开 claude.ai 上的这段对话（要登录导出时的那个账号；网页上删掉了就打不开）",
            "没有这段对话的网页链接", "没有网页链接",
            "这段对话的编号不是 claude.ai 的链接能用的格式"),
        Resume = ResumeRoute.None,
        NoResumeLabel = "网页版没有命令行可以接着聊",
        NoResumeTooltip = "网页版的对话没有命令行可以接着聊：在浏览器里打开它，在网页上继续",
        Changes = ChangesSupport.None,
        NoChangesSummary = "这个来源不记录文件改动",
        NoChangesText = "从 Claude 数据导出导入的网页版对话里没有文件改动的记录，这里列不出来。",
        CountsTokens = false,          // the export records no token counts
    };

    public static SourceInfo Codex { get; } = new()
    {
        Backend = ConversationKey.Codex,
        Family = AppFamily.Codex,
        Rank = 4,
        AlwaysReported = true,
        ColumnName = "Codex",
        ReportName = "Codex",
        ExportName = "Codex",
        SectionName = "ChatGPT · Codex",
        SectionTooltip = "ChatGPT 桌面版里的 Codex 任务",
        AppName = "ChatGPT",
        AssistantName = "Codex",
        Standing = StandingRule.ArchivedOrListed,
        StateTexts = new Dictionary<AppStanding, string>
        {
            [AppStanding.Active] = "在 ChatGPT 的任务列表中",
            [AppStanding.Archived] = "在 ChatGPT 中已归档",
        },
        MissingText = "找不到这条 Codex 任务的 rollout 文件——可能已被 Codex 清理或移动到其他 CODEX_HOME。",
        CopyFamily = "codex",
        CopyKey = CopyKey.Native,
        Open = OpenRoute.CodexLink,
        Resume = ResumeRoute.CodexCli,
        Changes = ChangesSupport.CodexPatches,
        CountsTokens = true,
    };

    public static SourceInfo ChatGptWeb { get; } = new()
    {
        Backend = ConversationKey.ChatGptWeb,
        Family = AppFamily.Codex,
        Rank = 5,
        ColumnName = "ChatGPT 网页",
        ReportName = "ChatGPT 网页版",
        ExportName = "ChatGPT 网页版",
        SectionName = "ChatGPT · 网页版",
        SectionTooltip = "从 ChatGPT 的数据导出导入的 chatgpt.com 对话（也是桌面版里的普通聊天）",
        AppName = "ChatGPT 网页版",
        AssistantName = "ChatGPT",
        Standing = StandingRule.Imported,
        StateTexts = new Dictionary<AppStanding, string>
        {
            [AppStanding.Imported] =
                "从 ChatGPT 的数据导出导入的副本：网页上后来的改动不会自动反映到这里，再导入一份新的导出才会补上",
        },
        MissingText = "找不到这段导入的 ChatGPT 网页对话——导入的记录可能已被移除；重新导入 ChatGPT 的数据导出即可。",
        CopyKey = CopyKey.None,
        Open = OpenRoute.WebLink,
        OpenWords = new OpenTexts(
            "在浏览器中打开这段对话", "在浏览器中打开",
            "在默认浏览器里打开 chatgpt.com 上的这段对话（要登录导出时的那个账号；网页上删掉了就打不开）",
            "没有这段对话的网页链接", "没有网页链接",
            "这段对话的编号不是 chatgpt.com 的链接能用的格式"),
        Resume = ResumeRoute.None,
        NoResumeLabel = "网页版没有命令行可以接着聊",
        NoResumeTooltip = "网页版的对话没有命令行可以接着聊：在浏览器里打开它，在网页上继续",
        Changes = ChangesSupport.None,
        NoChangesSummary = "这个来源不记录文件改动",
        NoChangesText = "从 ChatGPT 数据导出导入的网页版对话里没有文件改动的记录，这里列不出来。",
        CountsTokens = false,          // the export records no token counts
    };

    public static SourceInfo Gemini { get; } = new()
    {
        Backend = ConversationKey.Gemini,
        Family = AppFamily.Gemini,
        Rank = 6,
        ColumnName = "Gemini",
        ReportName = "Gemini CLI",
        ExportName = "Gemini CLI",
        SectionName = "Gemini CLI",
        SectionTooltip = "终端里 gemini 命令留下的会话",
        AppName = "Gemini CLI",
        AssistantName = "Gemini",
        Standing = StandingRule.AppState,
        StateTexts = new Dictionary<AppStanding, string>
        {
            [AppStanding.TerminalOnly] = "Gemini CLI 的终端会话：没有应用内的列表或归档",
        },
        MissingText = "找不到这条 Gemini CLI 会话的记录文件——可能已被删除（~\\.gemini\\tmp 下的 chats 文件夹）。",
        CopyFamily = "gemini",
        CopyKey = CopyKey.Native,
        Open = OpenRoute.None,
        OpenWords = new OpenTexts(
            "没有可打开它的桌面应用", "没有桌面应用",
            "Gemini CLI 只在终端里用，没有桌面应用能打开这条会话：用 gemini --resume 在终端里接着聊",
            "没有可打开它的桌面应用", "没有桌面应用",
            "Gemini CLI 只在终端里用，没有桌面应用能打开这条会话：用 gemini --resume 在终端里接着聊"),
        Resume = ResumeRoute.GeminiCli,
        Changes = ChangesSupport.None,
        NoChangesSummary = "这个来源不记录文件改动",
        NoChangesText = "Gemini CLI 的会话记录里没有逐次记下改了哪些文件，这里列不出来。",
        CountsTokens = true,
    };

    public static SourceInfo GeminiWeb { get; } = new()
    {
        Backend = ConversationKey.GeminiWeb,
        Family = AppFamily.Gemini,
        Rank = 7,
        ColumnName = "Gemini 网页",
        ReportName = "Gemini 网页版",
        ExportName = "Gemini 网页版",
        SectionName = "Gemini 网页版",
        SectionTooltip = "从 Google Takeout 导入的网页版 Gemini 对话",
        AppName = "Gemini 网页版",
        AssistantName = "Gemini",
        Standing = StandingRule.Imported,
        StateTexts = new Dictionary<AppStanding, string>
        {
            [AppStanding.Imported] =
                "从 Google Takeout 导入的副本：网页版里后来的改动不会自动反映到这里，再导入一份新的导出才会补上",
        },
        MissingText = "找不到这段导入的 Gemini 网页对话——导入的记录可能已被移除；重新导入 Google Takeout 的导出即可。",
        CopyKey = CopyKey.None,
        Open = OpenRoute.WebLink,
        OpenWords = new OpenTexts(
            "在浏览器中打开这段对话", "在浏览器中打开",
            "在默认浏览器里打开 gemini.google.com 上的这段对话（要登录导出时的那个 Google 账号；网页版里删掉了就打不开）",
            "没有这段对话的网页链接", "没有网页链接",
            "导出里没有这段对话的链接：它是按时间相近拼起来的，网页版里没有对应的一段"),
        Resume = ResumeRoute.None,
        NoResumeLabel = "网页版没有命令行可以接着聊",
        NoResumeTooltip = "网页版的对话没有命令行可以接着聊：在浏览器里打开它，在网页上继续",
        Changes = ChangesSupport.None,
        NoChangesSummary = "这个来源不记录文件改动",
        NoChangesText = "从 Google Takeout 导入的网页版对话里没有文件改动的记录，这里列不出来。",
        CountsTokens = false,          // Takeout records no token counts
    };

    public static SourceInfo DeepSeek { get; } = new()
    {
        Backend = ConversationKey.DeepSeek,
        Family = AppFamily.DeepSeek,
        Rank = 8,
        ColumnName = "DeepSeek",
        ReportName = "DeepSeek Harness",
        ExportName = "DeepSeek Harness",
        SectionName = "DeepSeek Harness",
        SectionTooltip = "DeepSeek Harness（dsh）的会话",
        AppName = "DeepSeek Harness",
        // It runs other vendors' models too, so the harness is named, not the model.
        AssistantName = "DeepSeek Harness",
        Standing = StandingRule.AppState,
        StateTexts = new Dictionary<AppStanding, string>
        {
            [AppStanding.Active] = "在 DeepSeek Harness 中，未归档",
            [AppStanding.Archived] = "在 DeepSeek Harness 中已归档",
            [AppStanding.Unobserved] = "没能读到 DeepSeek Harness 的工作区记录，不知道它是否已归档",
        },
        MissingText = "找不到这条 DeepSeek Harness 会话的记录文件——可能已被删除，或它的数据目录（DSH_HOME）换了位置。",
        CopyFamily = "dsh",
        CopyKey = CopyKey.Native,
        Open = OpenRoute.AppOnly,
        OpenWords = new OpenTexts(
            "打开 DeepSeek Harness，在里面找这条会话", "打开 DeepSeek",
            "用你自己的 DeepSeek Harness 快捷方式（或启动脚本）启动它，已在运行时直接在浏览器里打开。"
            + "它的网页没有直接打开某条会话的链接：打开后在侧边栏里按工作目录找到这条会话",
            "没找到 DeepSeek Harness 的启动方式", "没有启动方式",
            "没找到 DeepSeek Harness 的启动方式：桌面和开始菜单里没有名字以「DeepSeek Harness」开头的快捷方式，"
            + "会话位置旁边也没有它的启动脚本"),
        Resume = ResumeRoute.None,
        NoResumeLabel = "在 DeepSeek Harness 的网页里接着聊",
        NoResumeTooltip = "DeepSeek Harness 要装了终端界面（tui）才能用 dsh --resume 接着聊；只装了网页界面时，在它的网页里打开这条会话继续",
        Changes = ChangesSupport.None,
        NoChangesSummary = "暂不支持这个来源",
        NoChangesText = "DeepSeek Harness 的编辑记录格式和 Claude、Codex 不同，这里暂时列不出改动的文件；对话记录里照常能看到每次编辑的工具调用。",
        CountsTokens = true,
    };

    /// <summary>Claude's plain chats: kept by Claude on its servers, not on this computer — named, never read.</summary>
    public static SourceInfo Chat { get; } = NotRead(ConversationKey.Chat, "普通聊天");

    /// <summary>Claude's Cowork: likewise.</summary>
    public static SourceInfo Cowork { get; } = NotRead(ConversationKey.Cowork, "Cowork");

    /// <summary>Every source, in the order the list sorts them.</summary>
    public static IReadOnlyList<SourceInfo> All { get; } =
        [DesktopCode, CliCode, ClaudeWeb, Codex, ChatGptWeb, Gemini, GeminiWeb, DeepSeek, Chat, Cowork];

    /// <summary>The sources this program reads, in order.</summary>
    public static IReadOnlyList<SourceInfo> Read { get; } = [.. All.Where(s => s.Read).OrderBy(s => s.Rank)];

    /// <summary>The app families, in the order the filter and the usage window list them.</summary>
    public static IReadOnlyList<FamilyInfo> Families { get; } =
    [
        new(AppFamily.Claude, "Claude", "Claude"),
        new(AppFamily.Codex, "ChatGPT · Codex", "Codex"),
        new(AppFamily.Gemini, "Gemini", "Gemini"),
        new(AppFamily.DeepSeek, "DeepSeek Harness", "DeepSeek"),
    ];

    private static readonly Dictionary<string, SourceInfo> ByBackend =
        All.ToDictionary(s => s.Backend, StringComparer.Ordinal);

    /// <summary>The source of a backend id; null for one this program does not know.</summary>
    public static SourceInfo? Of(string? backend) =>
        backend is not null && ByBackend.TryGetValue(backend, out var source) ? source : null;

    /// <summary>
    /// The source of a backend id; for one this program does not know (a newer version's rows in
    /// an older one), a description that names it by its id and offers nothing it cannot do.
    /// </summary>
    public static SourceInfo Describe(string backend) => Of(backend) ?? new SourceInfo
    {
        Backend = backend,
        Family = AppFamily.Claude,
        Rank = OrderingRules.UnknownRank,
        ColumnName = backend,
        ReportName = backend,
        ExportName = backend,
        SectionName = backend,
        AppName = backend,
        AssistantName = "助手",
        Standing = StandingRule.AppState,
        Changes = ChangesSupport.None,
        NoChangesSummary = "暂不支持这个来源",
        NoChangesText = "这个来源的记录格式这里读不出改动的文件。",
    };

    public static FamilyInfo Of(AppFamily family) => Families.First(f => f.Family == family);

    /// <summary>The backends of a family that this program reads.</summary>
    public static IReadOnlyList<string> BackendsOf(AppFamily family) =>
        [.. Read.Where(s => s.Family == family).Select(s => s.Backend)];

    private static SourceInfo NotRead(string backend, string name) => new()
    {
        Backend = backend,
        Family = AppFamily.Claude,
        Rank = OrderingRules.UnknownRank,
        Read = false,
        ColumnName = name,
        ReportName = name,
        ExportName = name,
        SectionName = name,
        AppName = "Claude",
        AssistantName = "Claude",
        Standing = StandingRule.AppState,
        MissingText = "该来源没有本地 transcript。",
        Changes = ChangesSupport.None,
    };
}
