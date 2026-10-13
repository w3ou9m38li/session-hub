using System.Text;
using System.Text.Json;
using ClaudeLauncher.Core.Catalog;
using ClaudeLauncher.Core.Contracts;

namespace ClaudeLauncher.Infrastructure.Backends;

public sealed record ExportResult(
    string Directory,
    int Conversations,
    int AlreadyInSidebar,
    int WorkingDirectories,
    int TrustedDirectories,
    int RecoverableSessions);

/// <summary>
/// Writes the catalog out to a fixed folder, grouped the way recovery actually works.
///
/// <para>Claude's import skips sessions whose working directory has not been trusted, so the
/// useful unit is the <b>directory</b>, not the session: grouping by folder and marking each
/// one trusted or not turns "a few hundred sessions somewhere" into a short list of folders
/// to act on, with the count each one would unlock.</para>
///
/// <para>Only an index is written. The transcripts already live at a known path, and copying
/// gigabytes to restate that would cost space without making anything more recoverable.</para>
/// </summary>
public static class SessionExporter
{
    /// <summary>Directories Claude has been trusted for, read from its own config.</summary>
    public static HashSet<string> ReadTrustedDirectories(string claudeConfigDir)
    {
        var trusted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(claudeConfigDir, ".claude.json");
        if (!File.Exists(path)) return trusted;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("projects", out var projects)) return trusted;
            if (projects.ValueKind != JsonValueKind.Object) return trusted;

            foreach (var project in projects.EnumerateObject())
            {
                var accepted = project.Value.ValueKind == JsonValueKind.Object
                    && project.Value.TryGetProperty("hasTrustDialogAccepted", out var flag)
                    && flag.ValueKind == JsonValueKind.True;
                if (accepted) trusted.Add(Normalize(project.Name));
            }
        }
        catch (JsonException)
        {
            // Unreadable config: report nothing as trusted. Guessing the other way would hide
            // folders the user still has to act on.
        }
        return trusted;
    }

    /// <summary>Config keys mix separators (G:/ABC vs G:\ABC), so compare on a normal form.</summary>
    private static string Normalize(string path) =>
        path.Replace('/', Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar);

    /// <summary>
    /// True when the value is a real filesystem path rather than the slugged folder name used
    /// as a fallback for transcripts that never recorded a working directory.
    /// </summary>
    private static bool IsRealPath(string value) =>
        value.Contains(':') || value.StartsWith(Path.DirectorySeparatorChar);

    private static string DescribeExistence(string value) =>
        !IsRealPath(value) ? "路径未知（只剩目录名）"
        : Directory.Exists(value) ? "是"
        : "**否**";

    /// <summary>
    /// One conversation per row.
    ///
    /// <para>A Desktop record and the CLI transcript it was imported from are the same
    /// conversation recorded twice. Listing both would tell the user they have far more
    /// history than they do, and would put conversations that are already in the sidebar onto
    /// the list of things still to recover.</para>
    /// </summary>
    private static List<SessionMetadata> Deduplicate(IReadOnlyList<SessionMetadata> rows)
    {
        var linked = rows
            .Where(r => r.Key.Backend == ConversationKey.DesktopCode
                        && r.CliSessionId is { Length: > 0 })
            .Select(r => r.CliSessionId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Keep the Desktop side of a pair: it is the one that proves the conversation is
        // already in the sidebar, which is the fact this whole report turns on.
        return rows
            .Where(r => r.Key.Backend != ConversationKey.CliCode
                        || !linked.Contains(r.Key.NativeSessionId))
            .ToList();
    }

    /// <summary>Listed inside its own app: a Claude desktop record, or any Codex thread.</summary>
    private static bool InSidebar(SessionMetadata row) =>
        row.Key.Backend is ConversationKey.DesktopCode or ConversationKey.Codex;

    private static string SourceName(SessionMetadata row) => row.Key.Backend switch
    {
        ConversationKey.DesktopCode => "Claude 桌面",
        ConversationKey.CliCode => "Claude 终端",
        ConversationKey.Codex => "Codex",
        ConversationKey.Gemini => "Gemini CLI",
        ConversationKey.GeminiWeb => "Gemini 网页版",
        var other => other,
    };

    public static ExportResult Write(
        IReadOnlyList<SessionMetadata> rows, string claudeConfigDir, string outputDirectory)
    {
        ArgumentNullException.ThrowIfNull(rows);
        Directory.CreateDirectory(outputDirectory);

        var conversations = Deduplicate(rows);
        var trusted = ReadTrustedDirectories(claudeConfigDir);
        var groups = conversations
            .GroupBy(r => r.ProjectRef ?? "(未知目录)")
            .OrderByDescending(g => g.Count())
            .ToList();

        var trustedCount = groups.Count(g => trusted.Contains(Normalize(g.Key)));
        var inSidebar = conversations.Count(InSidebar);

        // What an import could actually add: not yet in the sidebar, and in an untrusted
        // folder that still exists. Everything else is either already there or out of reach.
        var recoverable = groups
            .Where(g => !trusted.Contains(Normalize(g.Key))
                        && IsRealPath(g.Key) && Directory.Exists(g.Key))
            .Sum(g => g.Count(r => !InSidebar(r)));

        WriteIndex(groups, trusted, outputDirectory, conversations.Count, inSidebar);
        WriteUntrusted(groups, trusted, outputDirectory);
        WriteCsv(conversations, trusted, outputDirectory);
        WriteReadme(outputDirectory, conversations.Count, inSidebar,
            groups.Count, trustedCount, recoverable);

        return new ExportResult(outputDirectory, conversations.Count, inSidebar,
            groups.Count, trustedCount, recoverable);
    }

    private static void WriteIndex(
        List<IGrouping<string, SessionMetadata>> groups,
        HashSet<string> trusted, string dir, int total, int inSidebar)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# 全部对话（按工作目录分组）").AppendLine();
        sb.AppendLine($"共 **{total}** 个不重复对话，分布在 **{groups.Count}** 个工作目录。").AppendLine();
        sb.AppendLine($"其中 **{inSidebar}** 个已经在 Claude 侧边栏里，"
            + $"**{total - inSidebar}** 个只以终端 transcript 的形式存在。").AppendLine();
        sb.AppendLine("桌面端记录和它导入自的 CLI transcript 是同一个对话，这里只算一次。").AppendLine();
        sb.AppendLine("目录是否被 Claude 信任，决定了「Import Claude Code CLI Sessions…」能否看到它们。")
          .AppendLine();

        foreach (var group in groups)
        {
            var isTrusted = trusted.Contains(Normalize(group.Key));
            var sidebar = group.Count(InSidebar);
            sb.AppendLine($"## {(isTrusted ? "✅" : "⬜")} `{group.Key}`").AppendLine();
            sb.AppendLine($"{group.Count()} 个对话（{sidebar} 个已在侧边栏）　·　"
                + $"{(isTrusted ? "已信任" : "**未信任** — 导入看不到")}").AppendLine();
            sb.AppendLine("| 来源 | 在应用内 | 标题 | 最后活动 | 会话 ID |");
            sb.AppendLine("|---|---|---|---|---|");

            foreach (var row in group.OrderByDescending(r => r.LastActivityUtc ?? DateTimeOffset.MinValue))
            {
                var title = row.Title.Replace("|", "\\|");
                if (title.Length > 46) title = title[..46] + "…";
                var when = row.LastActivityUtc?.ToLocalTime().ToString("yyyy-MM-dd") ?? "—";
                var id = row.CliSessionId ?? row.Key.NativeSessionId;
                sb.AppendLine($"| {SourceName(row)} | {(InSidebar(row) ? "是" : "否")} | {title} | {when} | `{id}` |");
            }
            sb.AppendLine();
        }

        File.WriteAllText(Path.Combine(dir, "全部对话_按目录分组.md"),
            sb.ToString(), new UTF8Encoding(true));
    }

    private static void WriteUntrusted(
        List<IGrouping<string, SessionMetadata>> groups,
        HashSet<string> trusted, string dir)
    {
        var untrusted = groups.Where(g => !trusted.Contains(Normalize(g.Key)))
                              .OrderByDescending(g => g.Count(r => !InSidebar(r)))
                              .ToList();

        var live = untrusted.Where(g => IsRealPath(g.Key) && Directory.Exists(g.Key)).ToList();

        var sb = new StringBuilder();
        sb.AppendLine("# 未信任的目录").AppendLine();
        sb.AppendLine("导入对话框会跳过这些目录里的会话"
            + "（\"are in folders you haven't trusted in Claude yet\"）。").AppendLine();
        sb.AppendLine($"**{untrusted.Count}** 个目录，合计 **{untrusted.Sum(g => g.Count())}** 个对话。")
          .AppendLine();
        sb.AppendLine($"其中目录仍在、信任后有望导入的：**{live.Count}** 个目录 / "
            + $"**{live.Sum(g => g.Count(r => !InSidebar(r)))}** 个尚未进侧边栏的对话。").AppendLine();
        sb.AppendLine("信任一个目录的做法：在 Claude 里用该目录新建一个会话，会弹信任确认；"
            + "接受之后再执行「Import Claude Code CLI Sessions…」。").AppendLine();
        sb.AppendLine("| 待恢复 | 对话数 | 工作目录 | 目录当前是否存在 |");
        sb.AppendLine("|---|---|---|---|");

        foreach (var group in untrusted)
        {
            sb.AppendLine($"| {group.Count(r => !InSidebar(r))} | {group.Count()} "
                + $"| `{group.Key}` | {DescribeExistence(group.Key)} |");
        }

        sb.AppendLine().AppendLine("---").AppendLine();
        sb.AppendLine("目录已不存在、或只剩目录名的，信任它没有意义——"
            + "那些对话的正文仍在，可以用启动器的「查看对话记录」直接读。");

        File.WriteAllText(Path.Combine(dir, "未信任目录.md"), sb.ToString(), new UTF8Encoding(true));
    }

    private static void WriteCsv(
        IReadOnlyList<SessionMetadata> rows, HashSet<string> trusted, string dir)
    {
        var sb = new StringBuilder();
        sb.AppendLine("来源,工作目录,目录已信任,已在应用内,分组,标题,最后活动,会话ID,CLI会话ID,归档状态");
        foreach (var row in rows)
        {
            var cwd = row.ProjectRef ?? string.Empty;
            sb.Append(Csv(SourceName(row))).Append(',')
              .Append(Csv(cwd)).Append(',')
              .Append(trusted.Contains(Normalize(cwd)) ? "是" : "否").Append(',')
              .Append(InSidebar(row) ? "是" : "否").Append(',')
              .Append(Csv(GroupRules.Effective(row.LocalGroup, row.SidebarGroup) ?? "")).Append(',')
              .Append(Csv(row.Title)).Append(',')
              .Append(Csv(row.LastActivityUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "")).Append(',')
              .Append(Csv(row.Key.NativeSessionId)).Append(',')
              .Append(Csv(row.CliSessionId ?? "")).Append(',')
              .Append(Csv(row.NativeArchiveState.ToString())).AppendLine();
        }
        // BOM so Excel reads the Chinese columns as UTF-8 rather than the system code page.
        File.WriteAllText(Path.Combine(dir, "全部对话.csv"), sb.ToString(), new UTF8Encoding(true));
    }

    private static string Csv(string value) =>
        value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;

    private static void WriteReadme(
        string dir, int conversations, int inSidebar, int dirs, int trustedDirs, int recoverable)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# 对话清单导出").AppendLine();
        sb.AppendLine($"生成于 {DateTimeOffset.Now:yyyy-MM-dd HH:mm}").AppendLine();
        sb.AppendLine($"- 不重复对话 **{conversations}** 个");
        sb.AppendLine($"  - 已在 Claude 侧边栏：**{inSidebar}** 个");
        sb.AppendLine($"  - 只有终端 transcript：**{conversations - inSidebar}** 个");
        sb.AppendLine($"- 工作目录 **{dirs}** 个，已信任 **{trustedDirs}** 个，"
            + $"未信任 **{dirs - trustedDirs}** 个");
        sb.AppendLine($"- 信任对应目录后有望被导入的：**{recoverable}** 个对话").AppendLine();
        sb.AppendLine("## 文件").AppendLine();
        sb.AppendLine("| 文件 | 用途 |");
        sb.AppendLine("|---|---|");
        sb.AppendLine("| `未信任目录.md` | **先看这个**：需要信任的目录，按能解锁的对话数排序 |");
        sb.AppendLine("| `全部对话_按目录分组.md` | 逐目录列出对话，标注是否已在侧边栏 |");
        sb.AppendLine("| `全部对话.csv` | 机器可读，可在 Excel 里筛选排序 |");
        sb.AppendLine().AppendLine("## 说明").AppendLine();
        sb.AppendLine("桌面端记录与它导入自的 CLI transcript 是同一个对话，全部只计一次；"
            + "两边相加会把已经恢复好的对话重复算进待恢复里。").AppendLine();
        sb.AppendLine("这里只有索引，没有复制 transcript —— 正文本来就在 "
            + "`%CLAUDE_CONFIG_DIR%\\projects\\` 下，复制一份不会让它更好恢复，只会多占几个 GB。")
          .AppendLine();
        sb.AppendLine("信任状态读自 Claude 自己的 `.claude.json` 里 "
            + "`projects[].hasTrustDialogAccepted`，只读，不修改。");

        File.WriteAllText(Path.Combine(dir, "README.md"), sb.ToString(), new UTF8Encoding(true));
    }
}
