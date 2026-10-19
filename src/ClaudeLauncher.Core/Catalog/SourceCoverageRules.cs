using ClaudeLauncher.Core.Contracts;

namespace ClaudeLauncher.Core.Catalog;

/// <summary>One source's contribution to the catalog, as observed in a single sync.</summary>
public sealed record SourceTally(
    string Backend,
    int Records,
    int Archived,
    SourceCoverage Coverage,
    string? Note = null);

/// <summary>
/// The honest answer to "how many conversations do I have".
/// <para>
/// Deliberately never adds Desktop and CLI counts together: a Desktop record and the CLI
/// transcript it points at are two views of one conversation, and an unmatched id means
/// "source not resolved", never "deleted".
/// </para>
/// </summary>
public sealed record CoverageReport
{
    public required IReadOnlyList<SourceTally> Sources { get; init; }

    /// <summary>Desktop records whose cliSessionId was found among the CLI root candidates.</summary>
    public required int VerifiedDesktopCliLinks { get; init; }

    /// <summary>Desktop records carrying a cliSessionId we could not locate. Not lost — unresolved.</summary>
    public required IReadOnlyList<string> UnknownSourceIds { get; init; }

    /// <summary>Nested JSONL (sub-agent transcripts). Counted, never shown as root conversations.</summary>
    public required int NonRootTranscripts { get; init; }

    /// <summary>
    /// Null whenever any source is less than fully Resolved. A number here would be a
    /// claim we cannot support, so the UI shows per-source counts instead.
    /// </summary>
    public long? GlobalUniqueConversationCount { get; init; }

    public string? GlobalCountReason { get; init; }

    /// <summary>
    /// The records read from a source this sync. A partial read's records were read all the
    /// same; only marking rows gone waits for a complete one.
    /// </summary>
    public int RecordsRead(string backend) => Sources
        .Where(s => s.Backend == backend && s.Coverage is SourceCoverage.Resolved or SourceCoverage.Partial)
        .Sum(s => s.Records);
}

public static class SourceCoverageRules
{
    /// <summary>
    /// Builds the report. The global total is only filled in when every source resolved
    /// and no id is left unattributed; otherwise it stays null with a stated reason.
    /// </summary>
    public static CoverageReport Build(
        IReadOnlyList<SourceTally> sources,
        int verifiedLinks,
        IReadOnlyList<string> unknownSourceIds,
        int nonRootTranscripts)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(unknownSourceIds);

        var unresolved = sources.Where(s => s.Coverage != SourceCoverage.Resolved).ToList();
        long? total = null;
        string? reason;

        if (unresolved.Count > 0)
        {
            reason = "source-coverage-incomplete: "
                + string.Join(", ", unresolved.Select(s => $"{s.Backend}={s.Coverage}"));
        }
        else if (unknownSourceIds.Count > 0)
        {
            reason = $"unresolved-links: {unknownSourceIds.Count}";
        }
        else
        {
            // Every source resolved and every cross-reference attributed. One conversation
            // may still appear in two backends, so linked pairs are counted once.
            total = sources.Sum(s => (long)s.Records) - verifiedLinks;
            reason = null;
        }

        return new CoverageReport
        {
            Sources = sources,
            VerifiedDesktopCliLinks = verifiedLinks,
            UnknownSourceIds = unknownSourceIds,
            NonRootTranscripts = nonRootTranscripts,
            GlobalUniqueConversationCount = total,
            GlobalCountReason = reason,
        };
    }

    /// <summary>
    /// A source that could not be read this round keeps its cached rows, marked Unreachable.
    /// An empty read is never allowed to overwrite a populated catalog.
    /// </summary>
    public static bool MayReplaceCachedRows(SourceCoverage observed, int observedRows, int cachedRows)
    {
        if (observed is SourceCoverage.Unreachable) return false;
        if (observedRows == 0 && cachedRows > 0) return false;
        return true;
    }
}
