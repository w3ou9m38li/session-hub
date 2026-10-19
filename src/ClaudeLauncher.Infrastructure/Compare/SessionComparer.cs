using System.Text;
using ClaudeLauncher.Core.Contracts;
using ClaudeLauncher.Infrastructure.Backends;
using ClaudeLauncher.Infrastructure.Changes;
using ClaudeLauncher.Infrastructure.Usage;

namespace ClaudeLauncher.Infrastructure.Compare;

/// <summary>
/// Sets two conversations side by side, read on demand from their transcripts: the prompts
/// lined up (<see cref="PromptAligner"/>), each with the answer it got; each side's time span,
/// tokens and changed files. Only the dialogue is read — tool lines are passed over unparsed —
/// and only each conversation's own part: a resumed or forked copy's copied history belongs to
/// the conversation it came from. Nothing is kept once the result is let go.
/// </summary>
public static class SessionComparer
{
    /// <summary>
    /// Longest prompt or answer text kept, about what a window can lay out; the transcript keeps
    /// the rest. Pasted documents run to megabytes.
    /// </summary>
    public const int MaxTextChars = 100_000;

    /// <summary>
    /// Reads both conversations and lines them up. Throws <see cref="OperationCanceledException"/>
    /// when cancelled, and <see cref="UnreadableSideException"/> naming the side whose file could
    /// not be read — each side is read even when the other could not be.
    /// </summary>
    public static SessionComparison Compare(ConversationSide a, ConversationSide b, CancellationToken cancellation = default)
    {
        var turnsA = Attempt(() => ReadTurns(a.Path, a.Part, cancellation), out var failedA);
        var turnsB = Attempt(() => ReadTurns(b.Path, b.Part, cancellation), out var failedB);
        if (turnsA is null || turnsB is null) throw new UnreadableSideException(failedA, failedB);

        var alignment = PromptAligner.Align(Prompts(turnsA), Prompts(turnsB), cancellation);
        var rows = new List<AlignedTurn>(alignment.Pairs.Count);
        foreach (var pair in alignment.Pairs)
        {
            rows.Add(pair switch
            {
                { A: < 0 } => new AlignedTurn(null, turnsB[pair.B], TurnMatch.OnlyB, 0),
                { B: < 0 } => new AlignedTurn(turnsA[pair.A], null, TurnMatch.OnlyA, 0),
                _ => new AlignedTurn(turnsA[pair.A], turnsB[pair.B],
                    pair.Identical ? TurnMatch.Identical : TurnMatch.Similar, pair.Similarity, pair.OpeningOnly),
            });
        }

        var summaryA = Attempt(() => Summarize(a, turnsA, cancellation), out failedA);
        var summaryB = Attempt(() => Summarize(b, turnsB, cancellation), out failedB);
        if (summaryA is null || summaryB is null) throw new UnreadableSideException(failedA, failedB);
        var files = summaryA.Files is null && summaryB.Files is null ? null : Match(summaryA.Files, summaryB.Files);
        return new SessionComparison(summaryA, summaryB, rows, files, alignment.Approximate);
    }

    private static List<string?> Prompts(IReadOnlyList<CompareTurn> turns) =>
        turns.Select(t => t.HasPrompt ? t.Prompt : null).ToList();

    /// <summary>One side's reading; null, with the reason, when its file cannot be read.</summary>
    private static T? Attempt<T>(Func<T> read, out Exception? failed) where T : class
    {
        failed = null;
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            failed = ex;
            return null;
        }
    }

    // ----------------------------------------------------------------- turns

    /// <summary>
    /// A conversation's turns: its own part's dialogue read in one pass, tool lines skipped
    /// unparsed (<see cref="TranscriptReader.ReadDialogueFrom(string, long, CancellationToken, OwnPart)"/>).
    /// </summary>
    public static IReadOnlyList<CompareTurn> ReadTurns(string path, OwnPart part, CancellationToken cancellation = default)
    {
        var (dialogue, _) = TranscriptReader.ReadDialogueFrom(path, 0, cancellation, part);
        cancellation.ThrowIfCancellationRequested();
        return BuildTurns(dialogue);
    }

    /// <summary>
    /// Groups dialogue into turns: each of the user's messages, with the assistant's messages up
    /// to the next one of theirs. What the assistant said before any prompt is a turn of its own,
    /// number 0, that never matches. Other entries (tool traffic, notes) are passed over.
    /// </summary>
    public static IReadOnlyList<CompareTurn> BuildTurns(IEnumerable<TranscriptEntry> dialogue)
    {
        var turns = new List<CompareTurn>();
        TranscriptEntry? asked = null;
        var answer = new StringBuilder();
        var answerLength = 0;
        DateTimeOffset? answeredAt = null;
        var open = false;
        var number = 0;

        foreach (var entry in dialogue)
        {
            if (entry.Role == MessageRole.User)
            {
                Close();
                asked = entry;
                open = true;
                number++;
            }
            else if (entry.Role == MessageRole.Assistant)
            {
                open = true;        // before any prompt: the opening turn without one
                if (answerLength > 0) answerLength += 2;
                answerLength += entry.Text.Length;
                if (answer.Length < MaxTextChars)
                {
                    if (answer.Length > 0) answer.Append("\n\n");
                    answer.Append(entry.Text.AsSpan(0, Math.Min(entry.Text.Length, MaxTextChars + 1 - answer.Length)));
                }
                answeredAt = entry.At ?? answeredAt;
            }
        }
        Close();
        return turns;

        void Close()
        {
            if (!open) return;
            var prompt = asked?.Text ?? "";
            turns.Add(new CompareTurn(asked is null ? 0 : number, Cut(prompt, prompt.Length), prompt.Length, asked?.At,
                Cut(answer.ToString(), answerLength), answerLength, answeredAt));
            asked = null;
            answer.Clear();
            answerLength = 0;
            answeredAt = null;
            open = false;
        }
    }

    /// <summary>Text cut at <see cref="MaxTextChars"/>, saying how much more there is.</summary>
    private static string Cut(string text, int whole)
    {
        if (text.Length <= MaxTextChars && whole <= text.Length) return text;
        var keep = Math.Min(text.Length, MaxTextChars);
        if (keep > 0 && char.IsHighSurrogate(text[keep - 1])) keep--;
        return text[..keep] + $"\n…（后面还有 {whole - keep:N0} 字，完整内容在对话记录里）";
    }

    // --------------------------------------------------------------- summary

    private static SideSummary Summarize(ConversationSide side, IReadOnlyList<CompareTurn> turns, CancellationToken cancellation)
    {
        DateTimeOffset? first = null, last = null;
        foreach (var turn in turns)
        {
            first ??= turn.At ?? turn.AnsweredAt;
            last = turn.AnsweredAt ?? turn.At ?? last;
        }
        var (tokens, from) = Tokens(side, first, cancellation);
        return new SideSummary(turns.Count(t => t.HasPrompt), first, last, tokens, from, ChangedFiles(side, cancellation));
    }

    /// <summary>
    /// The side's tokens: the ledger's count when the caller has one, else its transcript's
    /// calls, each counted once. A copy counts only calls from its own first message on: the
    /// copied history's calls were made in the conversation it was copied from.
    /// </summary>
    private static (TokenCounts?, TokenSource) Tokens(ConversationSide side, DateTimeOffset? ownFrom, CancellationToken cancellation)
    {
        if (side.KnownTokens is { } known) return (known, TokenSource.Ledger);
        if (!side.Source.CountsTokens) return (null, TokenSource.NotRecorded);

        var scan = UsageScanner.Scan(side.Path, side.App, 0, null, cancellation: cancellation);
        var since = side.Part.IsWhole ? (DateTimeOffset?)null : ownFrom ?? DateTimeOffset.MaxValue;
        // A response written over several lines repeats its key, its figures growing: the largest stand.
        var calls = new Dictionary<long, UsageCall>();
        foreach (var call in scan.Calls)
        {
            if (since is { } from && call.At < from) continue;
            calls[call.Key] = calls.TryGetValue(call.Key, out var seen)
                ? seen with
                {
                    Input = Math.Max(seen.Input, call.Input),
                    CacheWrite = Math.Max(seen.CacheWrite, call.CacheWrite),
                    CacheRead = Math.Max(seen.CacheRead, call.CacheRead),
                    Output = Math.Max(seen.Output, call.Output),
                }
                : call;
        }
        var total = new TokenCounts();
        foreach (var call in calls.Values)
            total += new TokenCounts(1, call.Input, call.CacheWrite, call.CacheRead, call.Output);
        return (total, TokenSource.Transcript);
    }

    // ----------------------------------------------------------------- files

    /// <summary>
    /// The files the side's own part changed and that a change succeeded on, added up per file.
    /// The conversation's own transcript only: subagents' and spawned tasks' changes are not read.
    /// </summary>
    private static List<ChangedPath>? ChangedFiles(ConversationSide side, CancellationToken cancellation)
    {
        if (side.Source.Changes == ChangesSupport.None) return null;
        var changes = ChangeScanner.Read(side.Path, side.Source.Changes == ChangesSupport.CodexPatches, cancellation, side.Part);
        var project = side.ProjectFolder is { Length: > 0 } folder ? folder.Replace('/', '\\').TrimEnd('\\') : null;
        return changes
            .Where(c => c.Succeeded)
            .GroupBy(c => KeyOf(c.Path, project), StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var list = g.ToList();
                var kind = list[^1].Kind == ChangeKind.Deleted ? ChangeKind.Deleted
                    : list[0].Kind == ChangeKind.Added ? ChangeKind.Added
                    : ChangeKind.Updated;
                return new ChangedPath(g.Key, list[^1].Path, kind, list.Sum(c => c.Added), list.Sum(c => c.Removed), list.Count);
            })
            .OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Where a file is, as both sides can agree on it: inside the working directory, relative to
    /// it — two attempts at one task in two checkouts change the same files there — otherwise its
    /// full path. Separators are Windows'.
    /// </summary>
    internal static string KeyOf(string path, string? project)
    {
        var file = path.Replace('/', '\\');
        if (project is not null && file.Length > project.Length + 1
            && file.StartsWith(project, StringComparison.OrdinalIgnoreCase) && file[project.Length] == '\\')
            return file[(project.Length + 1)..];
        return file;
    }

    private static FileComparison Match(IReadOnlyList<ChangedPath>? a, IReadOnlyList<ChangedPath>? b)
    {
        a ??= [];
        b ??= [];
        var inB = b.ToDictionary(f => f.Key, StringComparer.OrdinalIgnoreCase);
        var inA = new HashSet<string>(a.Select(f => f.Key), StringComparer.OrdinalIgnoreCase);
        var both = new List<SharedPath>();
        var onlyA = new List<ChangedPath>();
        foreach (var file in a)
        {
            if (inB.TryGetValue(file.Key, out var other)) both.Add(new SharedPath(file.Key, file, other));
            else onlyA.Add(file);
        }
        return new FileComparison(onlyA, b.Where(f => !inA.Contains(f.Key)).ToList(), both);
    }
}
