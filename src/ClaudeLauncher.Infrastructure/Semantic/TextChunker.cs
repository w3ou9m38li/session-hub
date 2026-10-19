namespace ClaudeLauncher.Infrastructure.Semantic;

/// <summary>A piece of one message, embedded on its own: where it starts in the message, and its words.</summary>
public readonly record struct TextChunk(int Start, string Text);

/// <summary>
/// Cuts a message into the pieces that are embedded: short enough for the model to read whole
/// (about <see cref="Window"/> characters, a couple of hundred tokens), overlapping a little so
/// a thought cut in two is still found, and cut at a line or sentence end where one is near.
/// </summary>
public static class TextChunker
{
    /// <summary>Longest piece, in characters.</summary>
    public const int Window = 400;

    /// <summary>How much of the end of one piece the next one repeats.</summary>
    public const int Overlap = 50;

    /// <summary>Shorter pieces carry no meaning worth finding: 「好的」「继续」.</summary>
    public const int MinChars = 8;

    /// <summary>Characters around which a sentence or clause ends, CJK and western.</summary>
    private static readonly char[] SentenceEnds = ['。', '！', '？', '；', '…', '.', '!', '?', ';'];

    /// <summary>The pieces of one message, in order; none for a message too short to be worth it.</summary>
    public static IReadOnlyList<TextChunk> Chunk(string text, int window = Window, int overlap = Overlap, int minChars = MinChars)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfLessThan(window, 16);
        ArgumentOutOfRangeException.ThrowIfNegative(overlap);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(overlap, window / 2);

        var chunks = new List<TextChunk>();
        var start = SkipSpace(text, 0);
        while (start < text.Length)
        {
            var end = Math.Min(text.Length, start + window);
            if (end < text.Length) end = CutAt(text, start, end, window);
            // Never between the halves of a surrogate pair.
            if (end < text.Length && char.IsLowSurrogate(text[end]) && end - 1 > start) end--;

            var piece = text[start..end].Trim();
            if (piece.Length >= minChars) chunks.Add(new TextChunk(start, piece));
            if (end >= text.Length) break;

            // The next piece repeats the end of this one, from a word or sentence start within it.
            var next = Math.Max(start + 1, end - overlap);
            next = StartAt(text, next, end);
            if (next < text.Length && char.IsLowSurrogate(text[next])) next++;
            start = SkipSpace(text, next);
        }
        return chunks;
    }

    /// <summary>
    /// Where to end a piece that would run to <paramref name="end"/>: the last line end in its
    /// second half, else the last sentence end there, else the last space, else right at the limit.
    /// </summary>
    private static int CutAt(string text, int start, int end, int window)
    {
        var earliest = start + window / 2;
        var line = text.LastIndexOf('\n', end - 1, end - earliest);
        if (line >= earliest) return line + 1;
        for (var i = end - 1; i >= earliest; i--)
            if (Array.IndexOf(SentenceEnds, text[i]) >= 0 && (i + 1 >= text.Length || char.IsWhiteSpace(text[i + 1]) || text[i] > 0x2000))
                return i + 1;
        for (var i = end - 1; i >= earliest; i--)
            if (char.IsWhiteSpace(text[i])) return i + 1;
        return end;
    }

    /// <summary>The start of the overlap: just after a sentence end or space inside it when there is one, so it opens on a whole word.</summary>
    private static int StartAt(string text, int from, int end)
    {
        for (var i = from; i < end - 1; i++)
            if (text[i] == '\n' || Array.IndexOf(SentenceEnds, text[i]) >= 0) return i + 1;
        for (var i = from; i < end - 1; i++)
            if (char.IsWhiteSpace(text[i])) return i + 1;
        return from;
    }

    private static int SkipSpace(string text, int at)
    {
        while (at < text.Length && char.IsWhiteSpace(text[at])) at++;
        return at;
    }

    /// <summary>A piece on one line, cut to about <paramref name="max"/> characters: what a result shows.</summary>
    public static string Snippet(string text, int max = 160)
    {
        var line = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (line.Length <= max) return line;
        var cut = max;
        if (char.IsLowSurrogate(line[cut])) cut--;
        return line[..cut] + "…";
    }
}
