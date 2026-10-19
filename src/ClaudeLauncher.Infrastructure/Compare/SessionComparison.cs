using ClaudeLauncher.Core.Contracts;
using ClaudeLauncher.Infrastructure.Backends;
using ClaudeLauncher.Infrastructure.Changes;
using ClaudeLauncher.Infrastructure.Usage;

namespace ClaudeLauncher.Infrastructure.Compare;

/// <summary>One of the two conversations being compared: where its transcript is and how to read it.</summary>
/// <param name="Path">The readable transcript: the app's own file, or this tool's copy of it.</param>
/// <param name="Backend">The row's backend id: which app, and what its transcripts record.</param>
/// <param name="Part">Which part is the conversation's own (<see cref="OwnPart.For"/>): a copy's copied history is not.</param>
/// <param name="ProjectFolder">Its working directory: changed files are lined up by where they are inside it.</param>
/// <param name="KnownTokens">Its tokens as the usage ledger counted them, when there is a ledger; otherwise they are read from the transcript.</param>
public sealed record ConversationSide(
    string Path, string Backend, OwnPart Part = default, string? ProjectFolder = null, TokenCounts? KnownTokens = null)
{
    public SourceInfo Source => Sources.Describe(Backend);

    public AppFamily App => Source.Family;

    /// <summary>Who answers in it: 「Claude」「Codex」…</summary>
    public string AssistantName => Source.AssistantName;
}

/// <summary>
/// One prompt of the user's and what the assistant said back to it, up to the next prompt.
/// Texts are kept whole up to <see cref="SessionComparer.MaxTextChars"/>; tool calls and their
/// output are not part of it.
/// </summary>
/// <param name="Number">Its place among the conversation's prompts, from 1; 0 for what the assistant said before any prompt.</param>
/// <param name="PromptLength">The prompt's whole length, which <see cref="Prompt"/> may be cut short of.</param>
/// <param name="At">When the prompt was written; null when the record has no time.</param>
/// <param name="Answer">Everything the assistant said before the next prompt, its messages a blank line apart; empty when it said nothing.</param>
/// <param name="AnsweredAt">When the last of those messages was written.</param>
public sealed record CompareTurn(
    int Number, string Prompt, int PromptLength, DateTimeOffset? At,
    string Answer, int AnswerLength, DateTimeOffset? AnsweredAt)
{
    public bool HasPrompt => Number > 0;
}

public enum TurnMatch
{
    /// <summary>The same prompt on both sides (once case, width and spacing are set aside).</summary>
    Identical,
    /// <summary>Prompts that read alike: at least <see cref="PromptAligner.SimilarAt"/> of their letter pairs shared.</summary>
    Similar,
    /// <summary>Asked only in the left (older) conversation.</summary>
    OnlyA,
    /// <summary>Asked only in the right (newer) conversation.</summary>
    OnlyB,
}

/// <summary>A row of the comparison: a turn of each side whose prompts match, or one side's turn alone.</summary>
/// <param name="Similarity">How alike the prompts read, 0 to 1; 1 for identical ones, 0 for a turn alone.</param>
/// <param name="OpeningOnly">Alike prompts judged by their first <see cref="PromptAligner.ComparedChars"/> characters: one runs on past them.</param>
public sealed record AlignedTurn(CompareTurn? A, CompareTurn? B, TurnMatch Match, double Similarity, bool OpeningOnly = false)
{
    /// <summary>
    /// How alike the prompts read in whole percent, as it is shown: rounded down, and 100 for
    /// identical prompts alone. Prompts alike by the same opening, or but a letter apart, are
    /// still not the same, and never read as all alike.
    /// </summary>
    public int Percent => Match switch
    {
        TurnMatch.Identical => 100,
        TurnMatch.Similar => Math.Min(99, (int)Math.Floor(Similarity * 100)),
        _ => 0,
    };
}

/// <summary>Where a side's token count came from.</summary>
public enum TokenSource
{
    /// <summary>The source records no tokens (an imported web conversation).</summary>
    NotRecorded,
    /// <summary>The usage ledger: the conversation's subagents included, a copy's copied calls left to the original.</summary>
    Ledger,
    /// <summary>Read from the transcript just now: its own calls, each once; subagents not included.</summary>
    Transcript,
}

/// <summary>A file one side changed, its changes added up.</summary>
/// <param name="Key">Where it is: inside the conversation's working directory, relative to it; otherwise its full path.</param>
/// <param name="FullPath">Where the transcript says it is.</param>
/// <param name="Kind">Created, changed or deleted, over the conversation as a whole.</param>
/// <param name="Times">How many successful changes it had.</param>
public sealed record ChangedPath(string Key, string FullPath, ChangeKind Kind, int Added, int Removed, int Times);

/// <summary>A file both sides changed.</summary>
public sealed record SharedPath(string Key, ChangedPath A, ChangedPath B);

/// <summary>The changed files, set side by side. A side whose source records no edits contributes nothing.</summary>
public sealed record FileComparison(
    IReadOnlyList<ChangedPath> OnlyA, IReadOnlyList<ChangedPath> OnlyB, IReadOnlyList<SharedPath> Both);

/// <summary>What one side amounts to: its prompts, its time, its tokens and its changed files.</summary>
/// <param name="First">When its first message (in its own part) was written; null when none has a time.</param>
/// <param name="Last">When its last message was written.</param>
/// <param name="Tokens">Its tokens; null when its source records none.</param>
/// <param name="Files">The files it changed, by <see cref="ChangedPath.Key"/>; null when its source records no edits.</param>
public sealed record SideSummary(
    int Prompts, DateTimeOffset? First, DateTimeOffset? Last,
    TokenCounts? Tokens, TokenSource TokensFrom, IReadOnlyList<ChangedPath>? Files)
{
    /// <summary>From the first message to the last; null without both.</summary>
    public TimeSpan? Span => First is { } first && Last is { } last && last >= first ? last - first : null;
}

/// <summary>
/// Two conversations side by side: their prompts lined up, what each amounts to, and the files
/// each changed. Immutable; nothing of it is kept anywhere once the caller lets it go.
/// </summary>
/// <param name="Files">The files set side by side; null when neither source records edits.</param>
/// <param name="Approximate">
/// Too many prompts to line up exactly: only identical prompts were matched (in order), and
/// prompts that merely read alike may stand apart.
/// </param>
public sealed record SessionComparison(
    SideSummary A, SideSummary B, IReadOnlyList<AlignedTurn> Rows, FileComparison? Files, bool Approximate)
{
    public int Identical => Rows.Count(r => r.Match == TurnMatch.Identical);
    public int Similar => Rows.Count(r => r.Match == TurnMatch.Similar);
    public int OnlyA => Rows.Count(r => r.Match == TurnMatch.OnlyA && r.A!.HasPrompt);
    public int OnlyB => Rows.Count(r => r.Match == TurnMatch.OnlyB && r.B!.HasPrompt);
}

/// <summary>
/// A side's transcript could not be read — gone, locked, or being written just then — and which
/// one: the other may have read fine. The comparison is not made.
/// </summary>
/// <param name="a">Why the left side could not be read; null when it could.</param>
/// <param name="b">Why the right side could not be read; null when it could.</param>
public sealed class UnreadableSideException(Exception? a, Exception? b)
    : IOException((a ?? b)?.Message, a ?? b)
{
    /// <summary>Why the left (older) side could not be read; null when it could.</summary>
    public Exception? A { get; } = a;

    /// <summary>Why the right (newer) side could not be read; null when it could.</summary>
    public Exception? B { get; } = b;
}
