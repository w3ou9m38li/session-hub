using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ClaudeLauncher.Infrastructure.Backends;

/// <summary>
/// One stretch of a transcript read by <see cref="TranscriptReader.ReadChunk"/>: what was said
/// there, the pictures sent and each tool call in a line, with its output's length — and, when
/// asked for, the output itself — each entry cut to size, and where to read on.
/// </summary>
/// <param name="Next">
/// Where to read on: the rest of the conversation, or — once <paramref name="ReachedEnd"/> — whatever
/// its app writes after this. Opaque; handed back as it is.
/// </param>
/// <param name="Start">Where the stretch begins: a byte of the file, or an entry of a document read whole.</param>
/// <param name="End">Where the next one begins, in the same unit.</param>
/// <param name="Length">The file's bytes, or the document's entries, as this read found them — never fewer than <paramref name="End"/>.</param>
/// <param name="Document">A transcript read whole (Gemini, dsh, an imported conversation): positions count entries, not bytes.</param>
/// <param name="Restarted">
/// The cursor pointed past the end of what is there now — the app wrote the transcript again,
/// shorter — so the read started over from the top.
/// </param>
public sealed record TranscriptChunk(
    IReadOnlyList<TranscriptEntry> Entries,
    string Next,
    bool ReachedEnd,
    long Start,
    long End,
    long Length,
    bool Document,
    bool Restarted = false);

public sealed partial class TranscriptReader
{
    /// <summary>Longest message text a chunk carries; a longer one is cut, with a note saying how long it was.</summary>
    public const int ChunkMessageChars = 6000;

    /// <summary>Longest tool output a chunk carries, when the output is asked for at all.</summary>
    public const int ChunkToolChars = 2000;

    /// <summary>What an entry costs of a chunk's budget besides its words: the heading it is shown under.</summary>
    private const int ChunkEntryOverhead = 40;

    /// <summary>What stands for a tool output too long to be read for its length.</summary>
    public const string PassedOverOutput = "工具输出（太长，未读）";

    /// <summary>What stands for a tool call whose arguments are too long to be read for its one line.</summary>
    public const string PassedOverCall = "（参数太长，未读）";

    /// <summary>
    /// A stretch of about <paramref name="maxChars"/> characters of a conversation, from
    /// <paramref name="cursor"/> (null: the start), for a reader that cannot hold a whole
    /// transcript and keeps nothing between calls — a server answering an agent page by page.
    ///
    /// <para>A transcript of lines is read from a byte position, the way the search index reads
    /// on, and a last line its app is still writing is left for a later read. A long line of tool
    /// output or thinking is passed over unparsed from its first kilobytes on, unless
    /// <paramref name="includeTools"/>; any line of tool traffic past the bound the transcript view
    /// keeps is too. A call or an output passed over still shows, as <see cref="PassedOverCall"/> or
    /// <see cref="PassedOverOutput"/>. The cursor also carries
    /// the Codex messages still waiting for their mirror, so a turn written twice shows once however
    /// the stretches fall. A transcript read whole (<see cref="WholeTranscript"/>) is paged by entry.</para>
    ///
    /// <para>Stretches end on whole lines (whole entries, for a document): the line that would go
    /// over the budget begins the next one, unless nothing was taken yet. Messages are cut at
    /// <see cref="ChunkMessageChars"/>. A tool call is its one line (name and gist, never its
    /// argument text) and its output the line with its length; the output itself only with
    /// <paramref name="includeTools"/>, cut at <see cref="ChunkToolChars"/>. Pictures are named,
    /// never carried; app context (notes) is never included.</para>
    /// </summary>
    /// <exception cref="ArgumentException">A cursor this method did not hand out for this kind of transcript (<c>ParamName</c> "cursor").</exception>
    /// <exception cref="IOException">The transcript cannot be read now.</exception>
    public static TranscriptChunk ReadChunk(
        string path, string? cursor, int maxChars, bool includeTools, CancellationToken cancellation = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var document = WholeTranscript.Is(path);
        var from = ChunkCursor.Parse(cursor, document);
        maxChars = Math.Max(1, maxChars);
        if (document) return ReadDocumentChunk(path, from, maxChars, includeTools, cancellation);
        using var stream = TranscriptFiles.OpenSeekable(path, 1, FileOptions.SequentialScan);
        return ReadLineChunk(stream, from, maxChars, includeTools, cancellation);
    }

    /// <summary>The same for a transcript of lines held in <paramref name="lines"/> — for a test standing in for a file that grows while it is read.</summary>
    internal static TranscriptChunk ReadChunk(Stream lines, string? cursor, int maxChars, bool includeTools) =>
        ReadLineChunk(lines, ChunkCursor.Parse(cursor, document: false), Math.Max(1, maxChars), includeTools, default);

    private static TranscriptChunk ReadLineChunk(
        Stream stream, ChunkCursor from, int maxChars, bool includeTools, CancellationToken cancellation)
    {
        var length = stream.Length;
        var restarted = from.Position > length;
        if (restarted) from = default;
        stream.Position = from.Position;

        var parser = new Parser { Pending = from.Pending };
        var entries = new List<TranscriptEntry>();
        var used = 0;
        using var line = new PooledBytes();
        var oversize = false;
        // What the line being read is, once it is long enough for that to matter.
        LineKind? kind = null;
        var buffer = PooledBytes.RentBlock();
        Span<byte> scratch = stackalloc byte[SkimBytes];
        long position = from.Position, lineStart = from.Position;
        // Where the next chunk starts when a line is refused: that line, and the mirrors pending before it.
        (long Position, MirrorRun? Pending)? stop = null;
        MirrorRun? before = null;
        var index = 0;
        int read;
        try
        {
            while (stop is null && (read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellation.ThrowIfCancellationRequested();
                var at = 0;
                while (at < read)
                {
                    var rest = buffer.AsSpan(at, read - at);
                    var newline = rest.IndexOf((byte)'\n');
                    var piece = newline < 0 ? rest : rest[..newline];
                    if (!oversize)
                    {
                        var size = line.WrittenCount + piece.Length;
                        if (size >= SkimFromBytes)
                            kind ??= KindOf(PooledBytes.Head(line.WrittenSpan, piece, SkimBytes, scratch));
                        if (kind is { } tool && tool != LineKind.Message && size >= SkimFrom(tool, includeTools))
                        { oversize = true; line.ResetWrittenCount(); }
                        else if (size <= MaxMessageLineBytes) line.Write(piece);
                        else { oversize = true; line.ResetWrittenCount(); }
                    }
                    if (newline < 0) break;                     // the line goes on in the next block

                    before = parser.Pending;
                    var said = oversize ? PassedOver(kind, index)
                        : line.WrittenCount > 0 ? parser.Parse(Unterminated(line.WrittenMemory), index)
                        : [];
                    if (!Take(said))
                    {
                        stop = (lineStart, before);
                        break;
                    }
                    index++;
                    line.ResetWrittenCount();
                    oversize = false;
                    kind = null;
                    at += newline + 1;
                    lineStart = position + at;
                }
                position += read;
            }

            // The last line has no line break: a whole record is read (its app wrote it without one);
            // one still being written is left for the next read, which starts where it does. Either
            // way the read ended where the file did then — later than its length said, if it grew.
            var next = lineStart;
            if (stop is null && !oversize && line.WrittenCount > 0 && IsCompleteRecord(line.WrittenMemory))
            {
                before = parser.Pending;
                if (Take(parser.Parse(Unterminated(line.WrittenMemory), index))) next = position;
                else stop = (lineStart, before);
            }
            var (end, pending) = stop ?? (next, parser.Pending);
            return new TranscriptChunk(entries, ChunkCursor.Format(false, end, pending), stop is null,
                from.Position, end, Math.Max(length, position), Document: false, restarted);
        }
        finally
        {
            PooledBytes.Return(buffer);
        }

        // Adds a line's entries when they fit — always, while nothing is taken yet: a stretch moves on.
        bool Take(List<TranscriptEntry> said)
        {
            var shown = Shown(said, includeTools, out var cost);
            if (shown.Count == 0) return true;
            if (entries.Count > 0 && used + cost > maxChars) return false;
            entries.AddRange(shown);
            used += cost;
            return true;
        }
    }

    private static ReadOnlyMemory<byte> Unterminated(ReadOnlyMemory<byte> bytes) =>
        bytes.Span[^1] == (byte)'\r' ? bytes[..^1] : bytes;

    /// <summary>What a long line is, judged from its first kilobytes as the index judges it.</summary>
    private enum LineKind { Message, Call, Output, Other }

    private static readonly byte[][] CallMarkers =
        [.. new[] { "tool_use", "function_call", "custom_tool_call" }.Select(kind => Encoding.UTF8.GetBytes($"\"type\":\"{kind}\""))];

    private static readonly byte[][] OutputMarkers =
        [.. new[] { "tool_result", "function_call_output", "custom_tool_call_output" }.Select(kind => Encoding.UTF8.GetBytes($"\"type\":\"{kind}\""))];

    private static LineKind KindOf(ReadOnlySpan<byte> head)
    {
        if (!IsToolHead(head)) return LineKind.Message;
        var first = FirstOf(head, ToolMarkers);
        return FirstOf(head, CallMarkers) == first ? LineKind.Call
            : FirstOf(head, OutputMarkers) == first ? LineKind.Output
            : LineKind.Other;
    }

    /// <summary>
    /// How long a line of tool traffic grows before it is passed over unparsed. A call is read up to
    /// the transcript view's bound always — its one line names what was run or written — output and
    /// thinking only that far when the output is wanted, and otherwise from the index's threshold.
    /// </summary>
    private static int SkimFrom(LineKind kind, bool includeTools) =>
        includeTools || kind == LineKind.Call ? MaxLineBytes + 1 : SkimFromBytes;

    /// <summary>What shows for a line passed over unparsed: the call or the output it was — thinking and the like, nothing.</summary>
    private static List<TranscriptEntry> PassedOver(LineKind? kind, int index) => kind switch
    {
        LineKind.Call => [new(index, MessageRole.ToolCall, PassedOverCall)],
        LineKind.Output => [new(index, MessageRole.ToolResult, PassedOverOutput)],
        _ => [],
    };

    private static TranscriptChunk ReadDocumentChunk(
        string path, ChunkCursor from, int maxChars, bool includeTools, CancellationToken cancellation)
    {
        var all = WholeTranscript.ReadEntries(path, keepImages: false, cancellation);
        var restarted = from.Position > all.Count;
        var start = restarted ? 0 : (int)from.Position;
        var entries = new List<TranscriptEntry>();
        var used = 0;
        var next = all.Count;
        for (var i = start; i < all.Count; i++)
        {
            var shown = Shown([all[i]], includeTools, out var cost);
            if (shown.Count == 0) continue;
            if (entries.Count > 0 && used + cost > maxChars)
            {
                next = i;
                break;
            }
            entries.AddRange(shown);
            used += cost;
        }
        return new TranscriptChunk(entries, ChunkCursor.Format(true, next, null), next >= all.Count,
            start, next, all.Count, Document: true, restarted);
    }

    /// <summary>The entries of a chunk among those read, cut to size, and what they cost of its budget.</summary>
    private static List<TranscriptEntry> Shown(List<TranscriptEntry> read, bool includeTools, out int cost)
    {
        cost = 0;
        var shown = new List<TranscriptEntry>(read.Count);
        foreach (var entry in read)
        {
            if (entry.Role == MessageRole.Note) continue;               // the app's own context
            var cut = entry with
            {
                Text = Cut(entry.Text, ChunkMessageChars, counted: true),
                // A call is its one line and an output the line with its length; the output itself
                // only when asked for, up to a bound.
                Detail = includeTools && entry.Role == MessageRole.ToolResult && entry.Detail is { } output
                    ? Cut(output, ChunkToolChars, counted: false)
                    : null,
                Image = null,
            };
            shown.Add(cut);
            cost += cut.Text.Length + (cut.Detail?.Length ?? 0) + ChunkEntryOverhead;
        }
        return shown;
    }

    /// <summary>The text, cut at <paramref name="max"/> characters — never between the halves of a pair — with a note when it was.</summary>
    private static string Cut(string text, int max, bool counted)
    {
        if (text.Length <= max) return text;
        var at = char.IsHighSurrogate(text[max - 1]) ? max - 1 : max;
        return text[..at] + (counted
            ? string.Create(CultureInfo.InvariantCulture, $"…（截断，共 {text.Length:N0} 字）")
            : "…（截断）");
    }

    /// <summary>Whether a line without a line break is a whole record, from its bytes (see <see cref="IsCompleteRecord(string)"/>).</summary>
    private static bool IsCompleteRecord(ReadOnlyMemory<byte> raw)
    {
        if (raw.Length > 0 && raw.Span[^1] == (byte)'\r') raw = raw[..^1];
        if (raw.Length == 0 || raw.Span[0] != (byte)'{') return true;
        try
        {
            using var _ = JsonDocument.Parse(raw);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>A run of Codex messages written once so far, waiting for the copy of the other family (see <c>Parser.SuppressMirror</c>).</summary>
    private readonly record struct MirrorRun(string Family, MessageRole Role, string Hash, int Count);

    /// <summary>
    /// Where a chunk starts: "b&lt;byte&gt;" — with ".&lt;family&gt;&lt;speaker&gt;&lt;count&gt;.&lt;hash&gt;"
    /// when messages wait for their mirror there — or "e&lt;entry&gt;" in a document.
    /// </summary>
    private readonly record struct ChunkCursor(long Position, MirrorRun? Pending)
    {
        private const string EventFamily = "event_msg", ItemFamily = "response_item";
        private const int MaxRun = 128;

        public static string Format(bool document, long position, MirrorRun? pending)
        {
            var at = (document ? "e" : "b") + position.ToString(CultureInfo.InvariantCulture);
            if (document || pending is not { } run) return at;
            return string.Create(CultureInfo.InvariantCulture,
                $"{at}.{(run.Family == EventFamily ? 'e' : 'r')}{(run.Role == MessageRole.User ? 'u' : 'a')}{run.Count}.{run.Hash}");
        }

        public static ChunkCursor Parse(string? text, bool document)
        {
            if (string.IsNullOrWhiteSpace(text)) return default;
            text = text.Trim();
            var kind = document ? 'e' : 'b';
            if (text[0] != kind)
                throw Invalid(text[0] is 'e' or 'b'
                    ? "这个 cursor 属于另一种记录：请从头读（不给 cursor），或用这条会话上一页给出的 cursor。"
                    : null);
            var parts = text[1..].Split('.');
            if (!long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var position)) throw Invalid();
            if (parts.Length == 1) return new ChunkCursor(position, null);
            if (document || parts.Length != 3 || parts[1].Length < 3 || parts[2].Length != 64
                || !parts[2].All(char.IsAsciiHexDigitUpper)
                || parts[1][0] is not ('e' or 'r') || parts[1][1] is not ('u' or 'a')
                || !int.TryParse(parts[1].AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out var count)
                || count is < 1 or > MaxRun)
                throw Invalid();
            var family = parts[1][0] == 'e' ? EventFamily : ItemFamily;
            var role = parts[1][1] == 'u' ? MessageRole.User : MessageRole.Assistant;
            return new ChunkCursor(position, new MirrorRun(family, role, parts[2], count));
        }

        private static ArgumentException Invalid(string? why = null) =>
            new(why ?? "cursor 不对：请原样使用上一页末尾给出的 cursor，或不给 cursor 从头读。", "cursor");
    }
}
