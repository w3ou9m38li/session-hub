using System.ComponentModel;
using System.Windows.Media;
using ClaudeLauncher.Core.Catalog;
using ClaudeLauncher.Core.Contracts;
using ClaudeLauncher.Core.Display;
using ClaudeLauncher.Infrastructure.Launch;

namespace ClaudeLauncher.App.ViewModels;

/// <summary>
/// One list row, and the detail panel when it is the one selected. Holds only what the
/// catalog already has, so scrolling never touches a transcript body; the body is read only
/// when the user opens the conversation.
/// </summary>
public sealed class SessionRowViewModel(SessionMetadata metadata) : INotifyPropertyChanged
{
    public SessionMetadata Metadata { get; } = metadata;

    /// <summary>What this row's source is: its names, its app family, and what can be done with it.</summary>
    public SourceInfo Source { get; } = Sources.Describe(metadata.Key.Backend);

    /// <summary>
    /// The heading this row is listed under when the list is sorted by a category, or null.
    /// Set by the list when it builds the row; rows of one category carry equal values, which
    /// is what groups them.
    /// </summary>
    public SectionHeader? Section { get; init; }

    /// <summary>
    /// The search word only what was said in this conversation holds — its title, folder and
    /// id do not — or null. The list marks such a row, the side panel shows where the word was
    /// said, and the transcript opens looking for it.
    /// </summary>
    public string? ContentTerm { get; init; }

    public bool MatchedByContent => ContentTerm is not null;

    /// <summary>How many messages say <see cref="ContentTerm"/>; 0 when not counted.</summary>
    public int ContentHitCount { get; init; }

    /// <summary>The search looked only at what the user said.</summary>
    public bool ContentByUser { get; init; }

    public string ContentTag => ContentHitCount > 0 ? $"内容 {ContentHitCount}" : "内容";

    public string ContentTagTooltip => ContentTerm is null ? string.Empty
        : $"标题里没有「{ContentTerm}」，是{(ContentByUser ? "你说过的话" : "对话内容")}里"
          + (ContentHitCount > 0 ? $"有 {ContentHitCount} 条消息提到了它" : "提到了它")
          + "。选中后右侧显示提到它的地方";

    public bool IsCodex => Metadata.Key.Backend == ConversationKey.Codex;

    /// <summary>The app no longer has this conversation's record; this tool keeps the row (and its copy, when one was made).</summary>
    public bool IsSourceGone => Metadata.SourceGoneUtc is not null;

    public string GoneTooltip => Metadata.SourceGoneUtc is { } gone
        ? $"{AppName} 自己的记录在 {gone.ToLocalTime():yyyy-MM-dd HH:mm} 前后不见了：被应用清理、删除或移走。"
          + "会话管家一直保留这一行；做过副本的话照常能看、能搜。只有你在会话管家里删除它才会删掉。"
        : "";

    /// <summary>
    /// Whether the app of a source opened by <see cref="OpenRoute.AppOnly"/> has an entry point to
    /// start it by — DeepSeek Harness's shortcut, say. Set by the window once it has looked.
    /// </summary>
    public static Func<SourceInfo, bool> CanStartApp { get; set; } = _ => false;

    /// <summary>Archived in its own app (not in this tool's archive).</summary>
    public bool IsArchivedInApp => Metadata.NativeArchiveState == NativeArchiveState.Archived;
    public bool IsClaude => ConversationKey.IsClaude(Metadata.Key.Backend);

    /// <summary>
    /// A terminal transcript a Claude desktop session has since continued from: the earlier
    /// part of that conversation, which in Claude is the desktop session itself.
    /// </summary>
    public bool IsEarlierPart => Metadata.ContinuedIn is not null;

    /// <summary>A desktop session Claude continued across transcripts: its earlier ones are listed too.</summary>
    public bool HasEarlierParts => Metadata.EarlierParts > 0;

    /// <summary>Begun by resuming another session: its history starts with a copy of that one's.</summary>
    public bool IsResumedCopy => Metadata.ResumedFrom is not null;

    /// <summary>The list's small 「续接」 tag: a desktop session with earlier parts, or a resumed copy.</summary>
    public bool ShowsContinuationTag => HasEarlierParts || IsResumedCopy;

    public string ContinuationTag => HasEarlierParts ? $"续接 {Metadata.EarlierParts}" : IsCodex ? "分叉" : "续接";

    public string ContinuationTagTooltip => HasEarlierParts
        ? $"这条会话在 Claude 里续接过 {Metadata.EarlierParts} 次：更早的对话在另外 {Metadata.EarlierParts} 份记录里"
          + "（「应用内」写着「续接前」）。选中后右侧「续接」一行可以直接打开"
        : IsCodex
            ? "这条任务是在 ChatGPT 里从另一条任务分叉出来的：开头的历史是从那里复制来的，"
              + "搜索和「改动的文件」只算分叉之后的部分。选中后右侧「续接」一行可以打开原任务"
            : "这条会话是用 claude --resume 续接另一条会话开始的：开头的历史是从那里复制来的，"
              + "搜索和「改动的文件」只算续接之后的部分。选中后右侧「续接」一行可以打开原会话";

    /// <summary>The user's own name for it when they gave one, otherwise what the source wrote.</summary>
    public string Title => HasLocalTitle ? Metadata.DisplayTitle
        : Metadata.TitleIsPlaceholder ? $"（未解析出标题）{Metadata.Title}"
        : Metadata.Title;

    public bool HasLocalTitle => !string.IsNullOrWhiteSpace(Metadata.LocalTitle);

    /// <summary>Under a name of the user's own, what the app itself calls it.</summary>
    public string OriginalTitleLine => HasLocalTitle ? "原标题：" + Metadata.Title : string.Empty;

    public bool IsPinned => Metadata.LocalPinned;

    public string PinLabel => IsPinned ? "取消置顶" : "置顶";

    public string? Note => Metadata.LocalNote;
    public bool HasNote => !string.IsNullOrWhiteSpace(Metadata.LocalNote);

    /// <summary>Screen readers and the list's own text search read this.</summary>
    public override string ToString() => Title;

    /// <summary>For the narrow source column: 「终端 CLI」「Gemini 网页」.</summary>
    public string Backend => Source.ColumnName;

    /// <summary>Which app the row belongs to, at a glance: Claude's orange, ChatGPT's green, Gemini's blue, DeepSeek Harness's violet.</summary>
    public Brush BackendBrush => Palette.TextOf(Source.Family);
    public Brush BackendFill => Palette.FillOf(Source.Family);

    public string? ProjectRef => Metadata.ProjectRef;
    public string Project => Metadata.ProjectRef ?? "—";
    public string ProjectFull => Metadata.ProjectRef ?? "（这条会话没有记录工作目录）";
    public bool HasProject => !string.IsNullOrWhiteSpace(Metadata.ProjectRef);

    /// <summary>
    /// A slugged folder name (kept when a transcript never recorded its directory) is not a
    /// path anything can open, so "打开" is offered only for something that looks like one.
    /// </summary>
    public bool ProjectLooksOpenable => Metadata.ProjectRef is { Length: > 2 } p
        && (p[1] == ':' || p.StartsWith(@"\\", StringComparison.Ordinal) || p.StartsWith('/'));

    // ------------------------------------------------------------------ group

    /// <summary>
    /// The group this conversation counts as being in: the user's own label when they set
    /// one, otherwise the app's own grouping (Claude's sidebar group, Codex's section).
    /// Null means ungrouped.
    /// </summary>
    public string? EffectiveGroup => GroupRules.Effective(Metadata.LocalGroup, Metadata.SidebarGroup);

    public string GroupDisplay => EffectiveGroup ?? "—";

    /// <summary>True when the name shown came from the app rather than from the user.</summary>
    public bool GroupIsApps =>
        Metadata.LocalGroup is null && !string.IsNullOrEmpty(Metadata.SidebarGroup);

    /// <summary>
    /// Colour carries where the group came from, so the column stays one narrow column:
    /// the user's own groups read as normal text, the app's in the accent used elsewhere for
    /// app-side facts, and no group at all is muted.
    /// </summary>
    public Brush GroupBrush =>
        GroupIsApps ? Palette.Info
        : EffectiveGroup is null ? Palette.Faint
        : Palette.Text;

    public string GroupSource =>
        GroupIsApps ? (IsCodex ? "ChatGPT 侧边栏分区" : "Claude 侧边栏分组")
        : EffectiveGroup is not null ? "本工具的分组"
        : Metadata.LocalGroup is null ? "未分组"
        : "已明确移出分组";

    public string GroupTooltip =>
        GroupIsApps ? $"{AppName} 自己的分组：{EffectiveGroup}　（设为分组会覆盖显示，{AppName} 侧不变）"
        : EffectiveGroup is not null ? $"本工具的分组：{EffectiveGroup}"
        : Metadata.LocalGroup is null ? "未分组"
        : "已明确移出分组";

    // ------------------------------------------------------------------- time

    /// <summary>"3 分钟前", "昨天 21:40"… recomputed on read, so <see cref="RefreshTime"/> keeps it honest.</summary>
    public string LastActivity => Metadata.LastActivityUtc is { } t
        ? RelativeTime.Format(t.LocalDateTime, DateTime.Now)
        : "无时间戳";

    public string LastActivityFull => Metadata.LastActivityUtc is { } t
        ? RelativeTime.Full(t.LocalDateTime)
        : "来源没有记录最后活动时间";

    /// <summary>"刚刚" must not still say so an hour later: the list calls this once a minute.</summary>
    public void RefreshTime() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LastActivity)));

    // ------------------------------------------------------------ app & source

    /// <summary>
    /// Where the conversation stands inside its own app. An observation read from the app's
    /// files; this tool never changes it. The source column already names the app, so this
    /// stays one word.
    /// </summary>
    public string AppState => OrderingRules.Label(Standing);

    /// <summary>The same rule the catalog sorts the column by.</summary>
    public AppStanding Standing =>
        OrderingRules.Standing(Metadata.Key.Backend, Metadata.NativeArchiveState, Metadata.ContinuedIn is not null);

    /// <summary>The standing in words, as the source puts it; the one-word label where it has no words for it.</summary>
    public string AppStateLong => Source.StateTexts.TryGetValue(Standing, out var words) ? words : OrderingRules.Label(Standing);

    public string AppStateTooltip => (Standing == AppStanding.Imported ? AppStateLong : AppStateLong + "（读自应用自己的文件，本工具不改动它）")
                                     + (IsSourceGone ? "\n" + GoneTooltip : "");

    public bool HasCoverageWarning => Metadata.SourceCoverage != SourceCoverage.Resolved;

    /// <summary>What is incomplete about this row, in words — shown only when something is.</summary>
    public string CoverageWarning => Metadata.SourceCoverage switch
    {
        SourceCoverage.Partial when IsCodex => "找不到这条任务的 rollout 文件，只有目录里的元数据",
        SourceCoverage.Partial => "只读了 transcript 开头的一段，标题可能不完整",
        SourceCoverage.UnknownSource => "来源未解析：记录指向的文件不在已扫描的目录里",
        SourceCoverage.Unreachable => "来源这次不可达，显示的是上次同步的缓存",
        _ => string.Empty,
    };

    // ------------------------------------------------------------- identity

    public string SessionId => Metadata.Key.NativeSessionId;

    /// <summary>The transcript's size for the list: 「423 MB」「860 KB」, 「—」 without a file.</summary>
    public string SizeText => Metadata.TranscriptBytes is { } bytes ? Size(bytes) : "—";

    public string SizeFull => Metadata.TranscriptBytes is { } bytes
        ? $"{Size(bytes)}（{bytes:N0} 字节）"
        : "磁盘上没有这条会话的记录文件";

    /// <summary>For the detail card: the size alone; the exact byte count is its tooltip.</summary>
    public string SizeDetail => Metadata.TranscriptBytes is { } bytes ? Size(bytes) : "磁盘上没有记录文件";

    public bool HasTranscriptFile => Metadata.TranscriptBytes is not null;

    /// <summary>Three significant figures at most, the way a file manager would say it.</summary>
    public static string Size(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
        >= 100L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0} MB",
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        >= 1024 => $"{Math.Max(1, bytes / 1024)} KB",
        _ => $"{bytes} B",
    };

    public string SessionIdShort => Metadata.Key.NativeSessionId.Length > 8
        ? Metadata.Key.NativeSessionId[..8]
        : Metadata.Key.NativeSessionId;

    public string CliSessionId => Metadata.CliSessionId ?? Metadata.Key.NativeSessionId;

    /// <summary>The app that owns this conversation, for labels.</summary>
    public string AppName => Source.AppName;

    /// <summary>Who answered, for the transcript view.</summary>
    public string AssistantName => Source.AssistantName;

    // --------------------------------------------------------------- actions

    /// <summary>Claude and ChatGPT have links of their own that open one conversation; others a page, or only the app.</summary>
    public bool CanOpenInApp => Source.Open switch
    {
        OpenRoute.ClaudeLink => IsClaude,
        OpenRoute.CodexLink => ChatGptDeepLink.CanOpen(Metadata.Key.NativeSessionId),
        OpenRoute.WebLink => WebLinks.CanOpen(Metadata.Key.Backend, Metadata.Key.NativeSessionId),
        OpenRoute.AppOnly => CanStartApp(Source),
        _ => false,
    };

    public string OpenInAppLabel => Source.Open switch
    {
        OpenRoute.ClaudeLink => "在 Claude 中打开此会话",
        OpenRoute.CodexLink => "在 ChatGPT 中打开此任务",
        _ => Source.OpenWords is { } words ? (CanOpenInApp ? words.Label : words.NoLabel) : "没有可打开它的应用",
    };

    /// <summary>For a half-width button; the full wording is in its tooltip.</summary>
    public string OpenInAppShortLabel => Source.Open switch
    {
        OpenRoute.ClaudeLink => "在 Claude 中打开",
        OpenRoute.CodexLink => "在 ChatGPT 中打开",
        _ => Source.OpenWords is { } words ? (CanOpenInApp ? words.ShortLabel : words.NoShortLabel) : "没有应用",
    };

    public string OpenInAppTooltip => Source.Open switch
    {
        OpenRoute.CodexLink =>
            "通过 ChatGPT 自己的 codex:// 链接打开这条任务：交给正在运行的 ChatGPT（Codex++ 的那份也行）；"
            + "没在运行时先按平时的方式启动它（有「Codex++（全部任务）」快捷方式就走快捷方式）"
            + (IsArchivedInApp ? "\n这条任务在 ChatGPT 里已归档：ChatGPT 会先问是否「取消归档并打开」，由你在那边决定" : ""),
        OpenRoute.ClaudeLink => IsEarlierPart
            ? "这份记录是一条桌面会话续接前的部分：通过官方 claude:// 链接打开它所属的那条桌面会话，不会再导入一份"
            : Metadata.Key.Backend == ConversationKey.CliCode
                ? "通过官方 claude://resume 链接请求 Claude 导入这条终端会话并打开：Claude 侧边栏会多出这条会话（已导入过的直接打开）"
                : "通过官方 claude:// 链接请求 Claude 打开这条会话",
        _ => Source.OpenWords is { } words ? (CanOpenInApp ? words.Tooltip : words.NoTooltip) : $"{AppName} 没有可以打开这条会话的应用",
    };

    /// <summary>
    /// Anything with a session id its CLI accepts can be continued: a Claude transcript (or a
    /// desktop record pointing at one) with <c>claude --resume</c>, a Codex thread with
    /// <c>codex resume</c>, a Gemini CLI session with <c>gemini --resume</c>.
    /// </summary>
    public bool CanResume => Source.Resume switch
    {
        ResumeRoute.CodexCli => CodexSessionLauncher.IsValidThreadId(Metadata.Key.NativeSessionId),
        ResumeRoute.GeminiCli => GeminiSessionLauncher.IsValidSessionId(Metadata.Key.NativeSessionId),
        ResumeRoute.ClaudeCli => IsClaude && Metadata.CliSessionId is { Length: > 0 },
        _ => false,
    };

    private string ResumeCommand => Source.Resume switch
    {
        ResumeRoute.CodexCli => "codex resume",
        ResumeRoute.GeminiCli => "gemini --resume",
        _ => "claude --resume",
    };

    public string ResumeLabel => Source.Resume == ResumeRoute.None
        ? Source.NoResumeLabel ?? "没有命令行可以接着聊"
        : $"用 {ResumeCommand} 继续";

    public string ResumeShortLabel => Source.Resume switch
    {
        ResumeRoute.None => "没有命令行",
        ResumeRoute.CodexCli => "codex resume 继续",
        _ => ResumeCommand,
    };

    public string ResumeTooltip => CanResume
        ? $"在原工作目录打开一个终端窗口，用 {ResumeCommand} 接着这条会话继续"
          + (IsEarlierPart ? "\n注意：这是续接前的记录，从这里继续会接着较早的内容聊，接不上桌面会话后来的对话" : "")
        : Source.Resume switch
        {
            ResumeRoute.CodexCli => "这条 Codex 任务的 ID 不是可恢复的格式",
            ResumeRoute.GeminiCli => "这条 Gemini 会话的 ID 不是可恢复的格式",
            ResumeRoute.ClaudeCli => "这条会话没有关联的 CLI 会话 ID，无法用 --resume 继续",
            _ => Source.NoResumeTooltip ?? "这个来源没有可以接着聊的命令行",
        };

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// A section heading in the list: what the category is and how many rows it holds in the
/// whole view — not just on the pages loaded so far. A record, so equal headings group together.
/// </summary>
public sealed record SectionHeader(string Label, string CountText, string? Tooltip = null)
{
    /// <summary>What a screen reader says for the heading's group.</summary>
    public override string ToString() => $"{Label}，{CountText}";
}
