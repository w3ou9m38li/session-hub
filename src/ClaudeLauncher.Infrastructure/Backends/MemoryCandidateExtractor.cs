using System.Text;
using System.Text.RegularExpressions;

namespace ClaudeLauncher.Infrastructure.Backends;

/// <summary>What kind of durable fact a line looks like. Maps onto Claude's memory types.</summary>
public enum CandidateKind
{
    /// <summary>"从今以后…"、"不要…" — how the user wants Claude to work. → type: feedback</summary>
    Instruction,
    /// <summary>"就用 X"、"改成 Y" — a choice that was settled. → type: project</summary>
    Decision,
    /// <summary>"不对"、"错了" — a correction worth not repeating. → type: feedback</summary>
    Correction,
    /// <summary>"还没…"、"下一步" — open threads. → type: project</summary>
    OpenThread,
}

public sealed record MemoryCandidate(CandidateKind Kind, string Quote, int LineIndex);

public sealed record MemoryDraft(
    string SessionId,
    string Title,
    string? WorkingDirectory,
    DateTimeOffset? LastActivity,
    IReadOnlyList<MemoryCandidate> Candidates,
    int UserMessagesScanned,
    /// <summary>Pasted blocks (skills, documents, logs) excluded from scanning.</summary>
    int PastedBlocksSkipped);

/// <summary>
/// Pulls memory <b>candidates</b> out of a transcript.
///
/// <para><b>It quotes; it does not summarise.</b> Deciding what a conversation "really meant"
/// and which parts are worth remembering is judgment, and that judgment is the user's. So this
/// extractor only finds the user's own sentences that carry the shape of a durable fact and
/// reproduces them verbatim, with the line number, for review. Nothing here is a fact yet —
/// every output is labelled a candidate.</para>
///
/// <para>Only the user's own messages are scanned. Claude's replies are excluded on purpose:
/// filing Claude's wording back into memory as if it were the user's intent is how a small
/// misreading becomes a permanent one.</para>
///
/// <para>The draft is written to the launcher's own output directory, never into a memory
/// folder — the memory tools are how facts get written, after a human has read the draft.</para>
/// </summary>
public static partial class MemoryCandidateExtractor
{
    // Shapes of a durable instruction: a standing rule, not a one-off request.
    [GeneratedRegex(@"(以后|往后|从今以后|今后|每次|一律|始终|默认|记住|别再|不要再|务必|必须)",
        RegexOptions.None, 200)]
    private static partial Regex InstructionCn();

    [GeneratedRegex(@"\b(from now on|always|never|going forward|by default|remember to)\b",
        RegexOptions.IgnoreCase, 200)]
    private static partial Regex InstructionEn();

    [GeneratedRegex(@"(决定|就用|改成|改为|换成|选择|采用|确定用|定为|方案是)",
        RegexOptions.None, 200)]
    private static partial Regex DecisionCn();

    [GeneratedRegex(@"\b(let's use|switch to|go with|decided to|we'll use)\b",
        RegexOptions.IgnoreCase, 200)]
    private static partial Regex DecisionEn();

    [GeneratedRegex(@"(不对|错了|搞错|不是这个|应该是|我说的是|重来|反了)",
        RegexOptions.None, 200)]
    private static partial Regex CorrectionCn();

    [GeneratedRegex(@"\b(that's wrong|not what i|actually,? i meant|no,? i said)\b",
        RegexOptions.IgnoreCase, 200)]
    private static partial Regex CorrectionEn();

    [GeneratedRegex(@"(还没|尚未|待办|下一步|之后再|留到|先不|暂时不|TODO)",
        RegexOptions.None, 200)]
    private static partial Regex OpenCn();

    [GeneratedRegex(@"\b(todo|later|next step|not yet|for now,? skip)\b",
        RegexOptions.IgnoreCase, 200)]
    private static partial Regex OpenEn();

    // A slash command ("/loop …") is shown as what the user typed, but its arguments are
    // a task for that command, not a standing instruction.
    [GeneratedRegex(@"^\s*(<system-reminder|<command-|<local-command|<task-notification|"
        + @"Caveat:|\[Request interrupted|This session is being continued|/[\w:.-]+(\s|$))",
        RegexOptions.IgnoreCase, 200)]
    private static partial Regex Boilerplate();

    /// <summary>
    /// Markers of injected skill or document text. When a skill runs, its whole definition
    /// arrives as a user message, so "only scan user messages" is not enough on its own —
    /// without this, a draft fills up with a skill's rules instead of the user's.
    /// </summary>
    [GeneratedRegex(@"(Base directory for this skill|REQUIRED SUB-SKILL|^---\s*$|"
        + @"^\s*name:\s|^\s*description:\s|allowed-tools:|^\s*\|\s*Stage\s*\||"
        + @"Superpowers skills|Use the `Skill` tool)",
        RegexOptions.IgnoreCase | RegexOptions.Multiline, 400)]
    private static partial Regex InjectedContent();

    /// <summary>
    /// Longest user message still treated as something a person typed. A standing instruction
    /// is a sentence or two; anything this long is pasted material — a skill, a document, a
    /// log — and mining it produces the document's rules, not the user's.
    /// </summary>
    public const int MaxTypedMessageChars = 1500;

    /// <summary>Max candidates per session, so one long conversation cannot swamp a review.</summary>
    public const int MaxCandidates = 60;

    /// <param name="part">
    /// Which part is the conversation's own: a copy that began with another conversation's
    /// history is only mined for what was said after the copy.
    /// </param>
    public static MemoryDraft Extract(
        string transcriptPath,
        string sessionId,
        string title,
        string? workingDirectory,
        DateTimeOffset? lastActivity,
        CancellationToken cancellation = default,
        OwnPart part = default)
    {
        // A copy: its own part in one read of what was said (the paged read cannot leave the copy out).
        var own = part.IsWhole ? null : TranscriptReader.ReadDialogueFrom(transcriptPath, 0, cancellation, part).Entries;
        // Otherwise read a page at a time. The reader keeps the file open between pages — for a
        // packed copy, its decoder too — so it is closed here, not left for the finaliser.
        using var reader = own is null ? new TranscriptReader(transcriptPath) : null;
        var candidates = new List<MemoryCandidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var userMessages = 0;
        var pastedSkipped = 0;
        var offset = 0;

        while (candidates.Count < MaxCandidates)
        {
            cancellation.ThrowIfCancellationRequested();
            var page = own is null ? reader!.Read(offset, 500, cancellation) : null;
            if (page is not null && page.Entries.Count == 0 && page.ReachedEnd) break;

            foreach (var entry in own ?? page!.Entries)
            {
                // Only the user's own words. Claude's replies are deliberately not mined.
                if (entry.Role != MessageRole.User) continue;
                if (Boilerplate().IsMatch(entry.Text)) continue;

                // Pasted material is not the user speaking. Skipping it is why a draft ends up
                // holding your instructions rather than a skill's.
                if (entry.Text.Length > MaxTypedMessageChars) { pastedSkipped++; continue; }
                if (InjectedContent().IsMatch(entry.Text)) { pastedSkipped++; continue; }

                userMessages++;

                foreach (var sentence in Split(entry.Text))
                {
                    var kind = Classify(sentence);
                    if (kind is null) continue;
                    if (!seen.Add(sentence)) continue;      // the same instruction repeated
                    candidates.Add(new MemoryCandidate(kind.Value, sentence, entry.Index));
                    if (candidates.Count >= MaxCandidates) break;
                }
                if (candidates.Count >= MaxCandidates) break;
            }

            // The copy's own part came in one read.
            if (page is null || page.ReachedEnd) break;
            offset = page.NextLineOffset;
        }

        return new MemoryDraft(sessionId, title, workingDirectory, lastActivity,
            candidates, userMessages, pastedSkipped);
    }

    private static CandidateKind? Classify(string s)
    {
        if (CorrectionCn().IsMatch(s) || CorrectionEn().IsMatch(s)) return CandidateKind.Correction;
        if (InstructionCn().IsMatch(s) || InstructionEn().IsMatch(s)) return CandidateKind.Instruction;
        if (DecisionCn().IsMatch(s) || DecisionEn().IsMatch(s)) return CandidateKind.Decision;
        if (OpenCn().IsMatch(s) || OpenEn().IsMatch(s)) return CandidateKind.OpenThread;
        return null;
    }

    /// <summary>Splits into sentences on CJK and Latin terminators, keeping them quotable.</summary>
    private static IEnumerable<string> Split(string text)
    {
        foreach (var raw in text.Split(['\n', '。', '；', ';', '！', '!', '？', '?'],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var s = string.Join(' ', raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (s.Length is >= 6 and <= 300) yield return s;
        }
    }

    /// <summary>
    /// Renders the draft in Claude Code's memory-file shape, ready to edit down. It is a
    /// draft: the front-matter slug and description are placeholders, and every quote is
    /// marked as unreviewed so nothing can be mistaken for an established fact.
    /// </summary>
    public static string Render(MemoryDraft draft)
    {
        var sb = new StringBuilder();
        sb.Append("---\n")
          .Append("name: ").Append(Slug(draft.Title)).Append('\n')
          .Append("description: 【待你改写】").Append(OneLine(draft.Title)).Append('\n')
          .Append("metadata:\n  type: project\n")
          .Append("---\n\n");

        sb.Append("> **这是候选草稿，不是事实。** 下面每一条都是从你自己的发言里**原样摘录**的，\n")
          .Append("> 没有经过总结或改写——哪些值得记住、该怎么措辞，是你的判断。\n")
          .Append("> 确认后再用记忆工具写入 `memory/`，本文件不会自动生效。\n\n");

        sb.Append("| | |\n|---|---|\n")
          .Append("| 来源会话 | `").Append(draft.SessionId).Append("` |\n")
          .Append("| 标题 | ").Append(OneLine(draft.Title)).Append(" |\n")
          .Append("| 工作目录 | `").Append(draft.WorkingDirectory ?? "—").Append("` |\n")
          .Append("| 最后活动 | ")
          .Append(draft.LastActivity?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "—").Append(" |\n")
          .Append("| 扫描你的发言 | ").Append(draft.UserMessagesScanned).Append(" 条 |\n")
          .Append("| 候选 | ").Append(draft.Candidates.Count).Append(" 条")
          .Append(draft.Candidates.Count >= MaxCandidates ? "（已达上限，可能还有更多）" : "")
          .Append(" |\n\n");

        if (draft.Candidates.Count == 0)
        {
            sb.Append("没有找到符合模式的候选。**这不代表这段对话没有价值**——\n")
              .Append("只说明它没有出现\"以后一律…\"\"改成…\"\"不对，应该是…\"这类句式。\n");
            return sb.ToString();
        }

        foreach (var (kind, heading, note) in Sections())
        {
            var items = draft.Candidates.Where(c => c.Kind == kind).ToList();
            if (items.Count == 0) continue;

            sb.Append("\n## ").Append(heading).Append("　(").Append(items.Count).Append(")\n\n")
              .Append('_').Append(note).Append("_\n\n");
            foreach (var c in items)
                sb.Append("- [ ] 第 ").Append(c.LineIndex).Append(" 行：").Append(c.Quote).Append('\n');
        }

        sb.Append("\n---\n\n")
          .Append("**提醒**：模式匹配会有漏网和误报。漏了的不等于不重要，命中的也不一定值得记。\n")
          .Append("这份草稿的作用是把 ").Append(draft.UserMessagesScanned)
          .Append(" 条发言缩到 ").Append(draft.Candidates.Count).Append(" 条待审，不是替你决定。\n");
        return sb.ToString();
    }

    private static IEnumerable<(CandidateKind, string, string)> Sections()
    {
        yield return (CandidateKind.Instruction, "长期指示（→ type: feedback）",
            "\"以后一律…\"\"别再…\"这类跨会话有效的工作方式要求");
        yield return (CandidateKind.Correction, "纠正（→ type: feedback，记得写 Why）",
            "你指出我做错的地方——这类最值得记，因为它能防止重犯");
        yield return (CandidateKind.Decision, "决定（→ type: project）",
            "已经拍板的选择，含当时的取舍");
        yield return (CandidateKind.OpenThread, "未完成（→ type: project）",
            "还没做完的事；注意把\"下周\"这类相对时间换成绝对日期");
    }

    private static string Slug(string title)
    {
        var sb = new StringBuilder();
        foreach (var ch in title.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
            if (sb.Length >= 40) break;
        }
        var s = sb.ToString().Trim('-');
        return s.Length == 0 ? "session-notes" : s;
    }

    private static string OneLine(string s) =>
        string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
