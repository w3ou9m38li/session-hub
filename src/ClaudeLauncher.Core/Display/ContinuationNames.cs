namespace ClaudeLauncher.Core.Display;

/// <summary>
/// What the parts of a conversation Claude continued across transcripts are called, seen
/// from one of them. The chain runs in the order it happened: the earlier transcripts, then
/// the desktop session that carries the conversation now.
/// </summary>
public static class ContinuationNames
{
    /// <param name="index">The part being named, as its place in the chain.</param>
    /// <param name="at">The part it is seen from.</param>
    /// <param name="count">How many parts the chain has, the desktop session included.</param>
    public static string Of(int index, int at, int count) =>
        index == count - 1 ? "续接后的会话"
        : count == 2 ? "续接前的部分"
        : $"{(index < at ? "续接前" : "续接后")} 第 {index + 1} 段";

    /// <summary>
    /// A copy that began with another conversation's history — <c>claude --resume</c>'s
    /// (续接), a forked Codex task (分叉): the original.
    /// </summary>
    public static string ResumedFrom(bool fork) => fork ? "分叉自" : "续接自";

    /// <summary>The copies made of a conversation, as the detail panel lists them.</summary>
    public static string ResumedInto(int index, int count, bool fork = false) =>
        fork
            ? count == 1 ? "从这里分叉的任务" : $"从这里分叉的第 {index + 1} 个任务"
            : count == 1 ? "从这里续接的会话" : $"从这里续接的第 {index + 1} 个会话";

    /// <summary>Copies nothing was said in, folded into the row that holds the original.</summary>
    public static string Folded(int count) => $"另有 {count} 次续接后没有说话";

    /// <summary>The line above a copy's transcript.</summary>
    public static string ResumedFromWhere(bool fork) =>
        fork ? "这条任务是从另一条任务分叉出来的：开头是从那里复制来的历史"
             : "这条会话是续接另一条会话开始的：开头是从那里复制来的历史";

    /// <summary>The line above a transcript copies were made of.</summary>
    public static string ResumedIntoWhere(int count, bool fork = false) =>
        fork
            ? count == 1 ? "这条任务后来被分叉出去一次" : $"这条任务后来被分叉过 {count} 次"
            : count == 1 ? "这条会话后来在另一条会话里续接" : $"这条会话后来被续接过 {count} 次";

    /// <summary>The line above a transcript that is part of such a chain: where it stands.</summary>
    public static string Where(int at, int count) =>
        at == count - 1
            ? count == 2
                ? "这条会话在 Claude 里续接过：更早的对话在另一份记录里"
                : $"这条会话在 Claude 里续接过 {count - 1} 次：更早的对话在另外 {count - 1} 份记录里"
            : count == 2
                ? "这是一条会话续接前的部分：后来的对话在另一份记录里"
                : $"这是一条会话续接前的第 {at + 1} 段（共 {count - 1} 段）";
}
