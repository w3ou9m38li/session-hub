using ClaudeLauncher.Core.Contracts;

namespace ClaudeLauncher.Core.Catalog;

public enum ViewArchiveOutcome
{
    Applied,
    /// <summary>Same operation id replayed — the first result stands.</summary>
    AlreadyApplied,
    /// <summary>Someone changed the row after the caller read it; nothing was written.</summary>
    Conflict,
}

public sealed record ViewArchiveResult(
    ViewArchiveOutcome Outcome,
    SessionMetadata Row,
    long Revision);

/// <summary>
/// Rules for this launcher's own "collapse into archive" action.
///
/// It moves a row between this tool's Active and ArchivedView lists and nothing else:
/// Claude's own history, archive flag, pins and worktrees are untouched, so the action
/// is fully reversible and must never be described as a Claude-side archive.
/// </summary>
public static class ViewArchiveRules
{
    public static ViewArchiveResult Archive(SessionMetadata row, long expectedRevision, long newRevision)
        => Apply(row, Visibility.ArchivedView, expectedRevision, newRevision);

    public static ViewArchiveResult Restore(SessionMetadata row, long expectedRevision, long newRevision)
        => Apply(row, Visibility.Active, expectedRevision, newRevision);

    private static ViewArchiveResult Apply(
        SessionMetadata row, Visibility target, long expectedRevision, long newRevision)
    {
        ArgumentNullException.ThrowIfNull(row);

        // Optimistic concurrency: a stale caller must not overwrite a newer preference.
        if (row.PreferenceRevision != expectedRevision)
            return new ViewArchiveResult(ViewArchiveOutcome.Conflict, row, row.PreferenceRevision);

        // Idempotent: archiving an already-archived row is a no-op, not an error.
        if (row.Visibility == target)
            return new ViewArchiveResult(ViewArchiveOutcome.AlreadyApplied, row, row.PreferenceRevision);

        var updated = row with
        {
            Visibility = target,
            PreferenceRevision = newRevision,
            // Deliberately preserved across the round trip:
            //   LocalPinned, ProjectRef  -> so Undo restores the row exactly
            //   NativeArchiveState       -> an observation of Claude, never our intent
        };

        return new ViewArchiveResult(ViewArchiveOutcome.Applied, updated, newRevision);
    }

    /// <summary>
    /// Undo only applies while the row still carries the revision the operation produced;
    /// otherwise a later user action would be silently reverted.
    /// </summary>
    public static ViewArchiveResult Undo(
        SessionMetadata row, Visibility previousVisibility, long revisionProducedByOperation, long newRevision)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.PreferenceRevision != revisionProducedByOperation)
            return new ViewArchiveResult(ViewArchiveOutcome.Conflict, row, row.PreferenceRevision);

        var updated = row with { Visibility = previousVisibility, PreferenceRevision = newRevision };
        return new ViewArchiveResult(ViewArchiveOutcome.Applied, updated, newRevision);
    }
}
