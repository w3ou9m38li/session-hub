namespace ClaudeLauncher.Infrastructure.Usage;

/// <summary>What a transcript records around its calls besides their tokens.</summary>
public enum UsageFactKind
{
    /// <summary>The app compacted the conversation here: what follows starts again from a summary.</summary>
    Compaction,

    /// <summary>The model called a tool: its call id and the tool's name.</summary>
    ToolCall,

    /// <summary>A tool's result went into the conversation: its call id and its size in bytes.</summary>
    ToolResult,
}

/// <summary>
/// One fact, in file order with the calls: <see cref="Before"/> says how many of the read's
/// calls came before it, so the next call is the first to read it in.
/// </summary>
/// <param name="Bytes">A tool result's size as written, without the copy Claude Code keeps beside it.</param>
public readonly record struct UsageFact(UsageFactKind Kind, int Before, DateTimeOffset? At, string? Id, string? Name, long Bytes);

/// <summary>What else a call's line says about it.</summary>
[Flags]
public enum CallTraits : byte
{
    None = 0,

    /// <summary>A subagent's call written into its parent's transcript (older Claude Code: <c>isSidechain</c>).</summary>
    Sidechain = 1,

    /// <summary>It wrote to the cache for five minutes.</summary>
    ShortCache = 2,

    /// <summary>It wrote to the cache for an hour.</summary>
    LongCache = 4,

    /// <summary>Copied from another conversation — a resumed or forked copy's history: not this one's own.</summary>
    Copied = 8,
}

/// <summary>
/// The facts one read found: a trait per call (in step with <see cref="UsageScan.Calls"/>) and the
/// compactions and tool traffic between them.
/// </summary>
public sealed record UsageFacts(IReadOnlyList<CallTraits> Traits, IReadOnlyList<UsageFact> Events);
