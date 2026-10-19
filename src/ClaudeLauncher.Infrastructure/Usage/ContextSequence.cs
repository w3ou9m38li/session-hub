using System.Globalization;
using ClaudeLauncher.Core.Contracts;

namespace ClaudeLauncher.Infrastructure.Usage;

/// <summary>What one call did to the context, judged against the call before it in the same file.</summary>
/// <param name="Context">What it read in: fresh input, cache writes and cache reads.</param>
/// <param name="Gap">Time since the call before it; null for the first.</param>
/// <param name="LongGap">Longer than the cache keeps what was written.</param>
/// <param name="ContextBefore">The call before it's context; 0 for the first.</param>
/// <param name="Rewritten">Tokens written again because the cache had expired; 0 when it had not.</param>
/// <param name="Kept">How long the cache kept what the call before it wrote: what the gap was judged against.</param>
internal readonly record struct SequenceStep(
    long Context, TimeSpan? Gap, bool LongGap, bool Compaction, bool ExplicitCompaction, long ContextBefore, long Rewritten,
    TimeSpan Kept);

/// <summary>
/// Follows one file's calls in order and tells, call by call, what happened to the context: a
/// compaction — marked by the app, or seen as a context that suddenly halved — and a cache that
/// expired while the user was away, so the whole context was written (or read at full price)
/// again. Both the analysis window and the usage ledger judge by these same rules; the ledger
/// carries <see cref="Tail"/> from one read of a growing file to the next.
/// </summary>
internal sealed class ContextSequence
{
    /// <summary>How long a cache entry is kept: five minutes, or an hour for what Claude wrote for that long.</summary>
    public static readonly TimeSpan ShortCache = TimeSpan.FromMinutes(5);

    public static readonly TimeSpan LongCache = TimeSpan.FromHours(1);

    /// <summary>Below this the context is too small for a rewrite to matter.</summary>
    public const long RewriteFloor = 20_000;

    /// <summary>A context this large that more than halves at once was compacted.</summary>
    public const long CompactionFloor = 30_000;

    private readonly AppFamily _app;
    private bool _hasLast;
    private DateTimeOffset _lastAt;
    private long _lastContext;
    private string _lastModel = "";
    private bool _longCache;
    private bool _cacheSeen;
    private bool _compacted;
    private bool? _sidechain;

    /// <param name="tail">What an earlier read of the same file left (<see cref="Tail"/>); null to start afresh.</param>
    public ContextSequence(AppFamily app, string? tail = null)
    {
        _app = app;
        Load(tail);
    }

    /// <summary>The app marked a compaction: the next call starts from its summary.</summary>
    public void Compacted() => _compacted = true;

    /// <summary>A compaction was marked and no call has followed it yet.</summary>
    public bool CompactionPending => _compacted;

    /// <summary>
    /// Whether a call is on the line this sequence follows. A transcript is one line of calls; an
    /// older Claude Code wrote its subagents' calls into the same file, marked as a side chain,
    /// and those have a cache of their own. The first call decides which line is followed.
    /// </summary>
    public bool Follows(CallTraits traits)
    {
        var side = (traits & CallTraits.Sidechain) != 0;
        _sidechain ??= side;
        return _sidechain == side;
    }

    public SequenceStep Next(UsageCall call, CallTraits traits)
    {
        var context = call.Input + call.CacheWrite + call.CacheRead;
        TimeSpan? gap = _hasLast ? (call.At > _lastAt ? call.At - _lastAt : TimeSpan.Zero) : null;
        var ttl = _app == AppFamily.Claude && _longCache ? LongCache : ShortCache;
        var longGap = gap > ttl;
        var marked = _compacted;
        var compaction = marked || (_hasLast && _lastContext >= CompactionFloor && context * 2 < _lastContext);

        long rewritten = 0;
        if (!compaction && longGap && call.Model == _lastModel && _lastContext >= RewriteFloor
            && call.CacheRead * 2 < Math.Min(_lastContext, context))
        {
            // Claude writes the context to the cache again, at more than the price of input; the
            // others read it again at full price. An app whose cache was never read in this file
            // has none to lose.
            rewritten = _app == AppFamily.Claude ? call.CacheWrite : _cacheSeen ? context - call.CacheRead : 0;
        }

        var step = new SequenceStep(context, gap, longGap, compaction, marked, _hasLast ? _lastContext : 0, Math.Max(0, rewritten), ttl);
        if ((traits & CallTraits.ShortCache) != 0) _longCache = false;
        else if ((traits & CallTraits.LongCache) != 0) _longCache = true;
        _cacheSeen |= call.CacheRead > 0;
        _hasLast = true;
        _lastAt = call.At;
        _lastContext = context;
        _lastModel = call.Model;
        _compacted = false;
        return step;
    }

    // ------------------------------------------------------------------ carry-over

    private const char Separator = '|';

    /// <summary>The state to carry to the next read of the file: the last call and a compaction marked after it.</summary>
    public string Tail
    {
        get
        {
            var flags = (_hasLast ? 1 : 0) | (_longCache ? 2 : 0) | (_cacheSeen ? 4 : 0) | (_compacted ? 8 : 0)
                        | (_sidechain is null ? 0 : _sidechain.Value ? 16 | 32 : 16);
            return string.Create(CultureInfo.InvariantCulture,
                $"1{Separator}{flags}{Separator}{_lastAt.UtcTicks}{Separator}{_lastContext}{Separator}{_lastModel}");
        }
    }

    private void Load(string? tail)
    {
        if (string.IsNullOrEmpty(tail)) return;
        var parts = tail.Split(Separator, 5);
        if (parts.Length != 5 || parts[0] != "1"
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var flags)
            || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            || ticks > DateTimeOffset.MaxValue.UtcTicks
            || !long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var context))
            return;         // not one this version wrote: the file's next calls start a new line
        _hasLast = (flags & 1) != 0;
        _longCache = (flags & 2) != 0;
        _cacheSeen = (flags & 4) != 0;
        _compacted = (flags & 8) != 0;
        _sidechain = (flags & 16) == 0 ? null : (flags & 32) != 0;
        _lastAt = new DateTimeOffset(ticks, TimeSpan.Zero);
        _lastContext = context;
        _lastModel = parts[4];
    }
}
