namespace ClaudeLauncher.Core.Contracts;

/// <summary>How complete our knowledge of a conversation's origin is.</summary>
public enum SourceCoverage
{
    /// <summary>Source root found and the record parsed.</summary>
    Resolved,
    /// <summary>Record exists but we cannot attribute it to a known source root.</summary>
    UnknownSource,
    /// <summary>The source root could not be read this sync. NOT the same as deleted.</summary>
    Unreachable,
    /// <summary>Parsed within budget but some fields were skipped.</summary>
    Partial,
}

/// <summary>This tool's own list preference. Never mirrors Claude's own state.</summary>
public enum Visibility
{
    Active,
    ArchivedView,
}

/// <summary>Observed state of Claude's own archive flag. An observation, not an intent.</summary>
public enum NativeArchiveState
{
    /// <summary>The backend has no native archive concept at all (e.g. CLI transcripts).</summary>
    Unsupported,
    /// <summary>We have not observed it, or could not read it back.</summary>
    Unknown,
    Active,
    Archived,
}

/// <summary>
/// One catalog row. Deliberately holds no transcript body, credential, cookie or token:
/// bodies are read on demand and never on the startup path.
/// </summary>
public sealed record SessionMetadata
{
    public required ConversationKey Key { get; init; }

    /// <summary>Desktop records carry a CLI session id; kept in its own field, never merged into the key.</summary>
    public string? CliSessionId { get; init; }

    public required string Title { get; init; }

    /// <summary>Display title exactly as the source wrote it; search uses <see cref="SearchKey"/>.</summary>
    public string? ProjectRef { get; init; }

    /// <summary>What <see cref="Visibility"/> and paging order use. NFKC-normalised, never shown to the user.</summary>
    public required string SearchKey { get; init; }

    /// <summary>A verified open target, or null when we have not proven one.</summary>
    public string? NativeOpenRef { get; init; }

    /// <summary>UTC, round-trip format. Null when the source had none — such rows still appear in the catalog.</summary>
    public DateTimeOffset? LastActivityUtc { get; init; }

    /// <summary>Source fingerprint (size + mtime), used to skip re-parsing unchanged files.</summary>
    public required string SourceRevision { get; init; }

    public Visibility Visibility { get; init; } = Visibility.Active;
    public NativeArchiveState NativeArchiveState { get; init; } = NativeArchiveState.Unknown;
    public SourceCoverage SourceCoverage { get; init; } = SourceCoverage.Resolved;

    /// <summary>
    /// Claude's own sidebar group, when it is known. Read from Claude's local preference
    /// cache and never written back. Null means either ungrouped or not knowable — the UI
    /// must not present those as the same thing.
    /// </summary>
    public string? SidebarGroup { get; init; }

    /// <summary>
    /// This launcher's own group for the conversation, with three distinct states:
    /// <c>null</c> = no preference, so Claude's <see cref="SidebarGroup"/> shows through;
    /// <c>""</c> = the user said this one has no group, even if Claude groups it;
    /// anything else = the group name. Resolved by <c>GroupRules.Effective</c>.
    /// </summary>
    public string? LocalGroup { get; init; }

    /// <summary>Pin inside this launcher only. Never writes Claude's own pin.</summary>
    public bool LocalPinned { get; init; }

    /// <summary>
    /// A name the user gave the conversation in this launcher only, shown in place of
    /// <see cref="Title"/> — which stays what the source wrote. Never written back.
    /// </summary>
    public string? LocalTitle { get; init; }

    /// <summary>The user's own note on the conversation, kept in this launcher only.</summary>
    public string? LocalNote { get; init; }

    /// <summary>
    /// Set on a terminal transcript that is the same conversation as a Claude desktop session:
    /// the desktop row's native id. Such a row is not listed on its own — the desktop row stands
    /// for both and carries the later of the two activity times.
    /// </summary>
    public string? MergedInto { get; init; }

    /// <summary>
    /// Set on a terminal transcript that a Claude desktop session has since moved on from —
    /// Claude starts a new transcript when it continues a conversation, and remembers the old
    /// ones as <c>priorCliSessionIds</c>: that desktop row's native id. Unlike a folded twin the
    /// row stays listed, since it holds the earlier part of the conversation; but in Claude it is
    /// that desktop session, so it is opened as that rather than imported a second time.
    /// </summary>
    public string? ContinuedIn { get; init; }

    /// <summary>
    /// On a desktop session: the terminal transcripts it ran on before its current one. Read
    /// with every sync to mark them (<see cref="ContinuedIn"/>); not stored.
    /// </summary>
    public IReadOnlyList<string> PriorCliSessionIds { get; init; } = [];

    /// <summary>
    /// On a terminal transcript Claude Code started by resuming another session — its history
    /// begins with a copy of that session's: the other session's id. Set only while the other
    /// session is on disk too; without it, this copy is all there is of that history.
    /// </summary>
    public string? ResumedFrom { get; init; }

    /// <summary>
    /// When a sync that read this row's source completely no longer found it — the app
    /// deleted, cleaned up or moved its record. The row stays listed (this tool keeps what it
    /// has seen until the user deletes it here); null while the source still has it.
    /// </summary>
    public DateTimeOffset? SourceGoneUtc { get; init; }

    /// <summary>
    /// On such a copy, as a sync reads it: nothing was said in it after the resume — a second
    /// copy of the same conversation, folded into the row that holds the original. Not stored.
    /// </summary>
    public bool ResumedSilently { get; init; }

    /// <summary>
    /// On a desktop session read from the catalog: how many terminal transcripts it was
    /// continued from — the rows whose <see cref="ContinuedIn"/> names it. Derived, not stored.
    /// </summary>
    public int EarlierParts { get; init; }

    /// <summary>Size of the transcript file behind the row, in bytes; null when there is none on disk.</summary>
    public long? TranscriptBytes { get; init; }

    /// <summary>What the list shows: the user's own name for it when there is one.</summary>
    public string DisplayTitle => string.IsNullOrWhiteSpace(LocalTitle) ? Title : LocalTitle;

    public long PreferenceRevision { get; init; }

    /// <summary>True when the metadata budget ran out before a title was found; the row is kept regardless.</summary>
    public bool TitleIsPlaceholder { get; init; }
}
