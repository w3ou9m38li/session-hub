using ClaudeLauncher.Core.Contracts;
using ClaudeLauncher.Core.Display;
using ClaudeLauncher.Infrastructure.Catalog;

namespace ClaudeLauncher.App.ViewModels;

/// <summary>
/// One other part of a conversation Claude carried on across transcripts, as the detail panel
/// lists it: what it is to the selected row, when it was last written, and how big it is. A
/// line without a row only says something — how many copies were folded into this one.
/// </summary>
public sealed record ContinuationEntry(SessionMetadata? Row, string Label, string Detail, string Tooltip)
{
    public string OpenName => $"查看{Label}的对话记录";
    public string LocateName => $"在列表里定位{Label}";

    /// <summary>A part that can be opened or found in the list; a folded count cannot.</summary>
    public bool HasRow => Row is not null;

    /// <summary>
    /// The other parts of a conversation as seen from <paramref name="row"/>: its desktop
    /// session's chain in order, then the session a resumed copy came from, then the copies
    /// resumed from it — and how many copies nothing was said in were folded into it.
    /// </summary>
    public static IReadOnlyList<ContinuationEntry> For(SessionMetadata row, Relatives relatives, DateTime now)
    {
        var entries = new List<ContinuationEntry>();
        var chain = relatives.Chain;
        var at = -1;
        for (var i = 0; i < chain.Count; i++)
            if (chain[i].Key == row.Key) at = i;
        if (at >= 0)
            for (var i = 0; i < chain.Count; i++)
                if (i != at) entries.Add(Of(chain[i], ContinuationNames.Of(i, at, chain.Count), now));

        var resumes = relatives.Resumes;
        var fork = row.Key.Backend == ConversationKey.Codex;
        if (resumes.From is { } from) entries.Add(Of(from, ContinuationNames.ResumedFrom(fork), now));
        for (var i = 0; i < resumes.Into.Count; i++)
            entries.Add(Of(resumes.Into[i], ContinuationNames.ResumedInto(i, resumes.Into.Count, fork), now));
        if (resumes.Folded > 0)
            entries.Add(new ContinuationEntry(null, ContinuationNames.Folded(resumes.Folded), "已并入这一行",
                "用 claude --resume 打开过、但一句话没说就离开的副本：内容和这里一样，所以不再单独列出"));
        return entries;
    }

    private static ContinuationEntry Of(SessionMetadata part, string label, DateTime now)
    {
        var when = part.LastActivityUtc is { } t ? RelativeTime.Format(t.LocalDateTime, now) : "无时间戳";
        var size = part.TranscriptBytes is { } bytes ? SessionRowViewModel.Size(bytes) : "没有记录文件";
        return new ContinuationEntry(part, label, $"{when} · {size}",
            part.DisplayTitle
            + (part.LastActivityUtc is { } full ? $"\n最后活动 {RelativeTime.Full(full.LocalDateTime)}" : "")
            + $"\n会话 ID {part.Key.NativeSessionId}");
    }
}

/// <summary>
/// A conversation's other parts: the desktop session chain it belongs to (earlier transcripts,
/// then the session), and its resumes — the session a copy came from, the copies made of it.
/// </summary>
public sealed record Relatives(IReadOnlyList<SessionMetadata> Chain, SqliteCatalog.ResumeRelatives Resumes)
{
    public static Relatives None { get; } = new([], SqliteCatalog.ResumeRelatives.None);

    public bool Any => Chain.Count > 0 || Resumes.Any;
}
