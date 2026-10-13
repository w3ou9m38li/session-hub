using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClaudeLauncher.Infrastructure.Backends;

public enum MessageRole { User, Assistant, ToolCall, ToolResult, Attachment, Note }

/// <summary>
/// One renderable entry. The readable text is <see cref="Text"/>; anything bulky — tool
/// arguments, tool output — lives in <see cref="Detail"/>, bounded, so a page never carries a
/// whole file dump and the viewer can fold it by default.
/// </summary>
public sealed record TranscriptEntry(
    int Index, MessageRole Role, string Text, string? Detail = null, string? Timestamp = null)
{
    public bool IsTool => Role is MessageRole.ToolCall or MessageRole.ToolResult;

    /// <summary>
    /// A picture sent in the conversation — its encoded bytes, as the transcript holds them —
    /// when the reader was asked to keep pictures; null otherwise.
    /// </summary>
    public byte[]? Image { get; init; }
}

/// <summary>The end of a transcript, read from the end.</summary>
/// <param name="WholeFile">The window reached the start: these are all the entries there are.</param>
public sealed record TranscriptTail(IReadOnlyList<TranscriptEntry> Entries, bool WholeFile, int ToolOutputsFolded);

/// <param name="BytePosition">How far into the file reading has got, for a progress display. Approximate: it includes read-ahead.</param>
public sealed record TranscriptPage(
    IReadOnlyList<TranscriptEntry> Entries,
    int NextLineOffset,
    bool ReachedEnd,
    long BytesScanned,
    int ToolOutputsFolded,
    long BytePosition = 0);

/// <summary>
/// Streams a transcript for display. Understands both formats found on this machine:
/// Claude Code's <c>{type:user|assistant, message:{content:[…]}}</c> lines and Codex's
/// <c>{type:response_item|event_msg, payload:{…}}</c> rollout envelopes.
///
/// <para>These files reach hundreds of megabytes, and most of that is tool output. The reader
/// keeps the conversation readable: user and assistant text in full, each tool call collapsed
/// to a summary line with its arguments and output kept only as bounded detail, system
/// context folded to a one-line note. Nothing is ever loaded whole: it resumes from a line
/// offset, stops at an entry budget, and skips any single line too large to hold.</para>
///
/// <para>Codex writes most user and assistant messages twice — once as an event, once as a
/// response item. The reader pairs those mirrors by content hash and shows each once, without
/// mistaking a genuinely repeated message for a mirror.</para>
///
/// <para>Text is data, never an instruction: control sequences are stripped, nothing is
/// executed, and no remote media is fetched.</para>
///
/// <para>Reading on from where the previous page stopped continues from an open file
/// position, so reading a whole transcript page by page is one pass over the file rather
/// than one pass per page. One reader serves one caller at a time.</para>
/// </summary>
public sealed class TranscriptReader : IDisposable
{
    /// <summary>Largest line of tool traffic kept in memory; a longer one is skipped unread.</summary>
    private const int MaxLineBytes = 4 * 1024 * 1024;

    /// <summary>
    /// Largest line kept when its beginning says it is a message rather than tool traffic.
    /// Screenshots travel inside the message they were sent with, and skipping the line would
    /// lose what the user asked along with them.
    /// </summary>
    private const int MaxMessageLineBytes = 32 * 1024 * 1024;

    /// <summary>Longest tool argument/output text kept per entry.</summary>
    public const int MaxDetailChars = 16_000;

    /// <summary>Largest picture kept for display; a bigger one is named instead.</summary>
    public const int MaxImageBytes = 12 * 1024 * 1024;

    /// <summary>Picture bytes one read of a transcript keeps at most, however many screenshots it holds.</summary>
    private const long MaxImageBytesPerParser = 256L * 1024 * 1024;

    private const int MaxContentBlocks = 512;

    private readonly string _path;

    // Mirror pairing needs to see consecutive lines, so the parser survives across pages as
    // long as the caller reads sequentially; any other offset starts a fresh one.
    private Parser? _parser;
    private int _parserLine = -1;

    // Open position after line _linesRead. Null until the first read, after a read that had
    // to stop short of an unfinished last line, and after disposal.
    private FileStream? _stream;
    private StreamReader? _text;
    private int _linesRead;
    private bool _disposed;

    /// <param name="keepImages">
    /// Keep the pictures sent in the conversation, for a reader that shows them. Off for
    /// everything else — export, copy, memory drafts — so they never hold picture data.
    /// </param>
    public TranscriptReader(string transcriptPath, bool keepImages = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transcriptPath);
        _path = transcriptPath;
        _keepImages = keepImages;
    }

    private readonly bool _keepImages;

    public string FilePath => _path;
    public bool Exists => File.Exists(_path);
    public long SizeBytes => Exists ? new FileInfo(_path).Length : 0;

    /// <summary>Tail windows tried in turn; most conversations end with dialogue inside the first.</summary>
    private static readonly int[] TailWindows = [128 * 1024, 1024 * 1024, 8 * 1024 * 1024];

    /// <summary>
    /// The last <paramref name="count"/> things the user and the assistant said, read from the
    /// end of the file: a conversation hundreds of megabytes long costs a few hundred kilobytes.
    /// Tool traffic and app context are passed over. A window that begins mid-line drops that
    /// partial line, and a last line still being written simply fails to parse.
    /// </summary>
    public static IReadOnlyList<TranscriptEntry> RecentDialogue(
        string path, int count, CancellationToken cancellation = default)
    {
        var dialogue = TailDialogue(path, found => found.Count >= count, cancellation);
        return dialogue.Count <= count ? dialogue : dialogue.GetRange(dialogue.Count - count, count);
    }

    /// <summary>
    /// Where a conversation stands: the user's last message and the assistant's last reply
    /// after it — or only that reply, when the user's message lies further back than the
    /// widest tail window, as it can after a long unattended run of tool calls.
    /// </summary>
    public static IReadOnlyList<TranscriptEntry> LastExchange(string path, CancellationToken cancellation = default)
    {
        var dialogue = TailDialogue(path, found => found.Exists(e => e.Role == MessageRole.User), cancellation);
        var asked = dialogue.FindLastIndex(e => e.Role == MessageRole.User);
        var answered = dialogue.FindLastIndex(e => e.Role == MessageRole.Assistant);
        var exchange = new List<TranscriptEntry>(2);
        if (asked >= 0) exchange.Add(dialogue[asked]);
        if (answered > asked) exchange.Add(dialogue[answered]);
        return exchange;
    }

    /// <summary>
    /// What the user and the assistant said from <paramref name="offset"/> to the end of the
    /// file, for the search index — and the byte position the next read should start from.
    /// A last line still being written is left for that next read, so a growing transcript is
    /// indexed a piece at a time rather than reread whole every time it changes.
    /// </summary>
    /// <param name="part">
    /// Which part is the conversation's own: for a copy that began with another conversation's
    /// history, what was said after the copy — the copied part is the other conversation's.
    /// </param>
    public static (IReadOnlyList<TranscriptEntry> Entries, long NextOffset) ReadDialogueFrom(
        string path, long offset, CancellationToken cancellation = default, OwnPart part = default)
    {
        using var line = new PooledBytes();
        return ReadDialogueFrom(path, offset, line, cancellation, part);
    }

    /// <summary>
    /// The same, with a line buffer kept for a whole pass over many transcripts. A message with
    /// a screenshot is a line of megabytes that has to be read whole for the words beside it; a
    /// buffer of each transcript's own grew to that size again in every transcript that has one.
    /// </summary>
    internal static (IReadOnlyList<TranscriptEntry> Entries, long NextOffset) ReadDialogueFrom(
        string path, long offset, PooledBytes line, CancellationToken cancellation, OwnPart part)
    {
        // A Gemini session is one document written again whole: there is no offset to read on from.
        if (GeminiSession.IsSessionFile(path))
            return (GeminiSession.ReadDialogue(path, cancellation), new FileInfo(path).Length);

        line.ResetWrittenCount();
        var own = part.Filter(path, offset < 0 ? 0 : offset);
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
        if (offset < 0 || offset > stream.Length) offset = 0;
        stream.Position = offset;

        var parser = new Parser(dialogueOnly: true);
        var entries = new List<TranscriptEntry>();
        var oversize = false;
        var buffer = PooledBytes.RentBlock();
        Span<byte> scratch = stackalloc byte[SkimBytes];
        long position = offset, lineStart = offset;
        var index = 0;
        int read;
        try
        {
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
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
                        var length = line.WrittenCount + piece.Length;
                        // Only what was said is read here. A long line whose head is tool traffic —
                        // most of a transcript's bytes — is passed over from there on, never held.
                        if (length >= SkimFromBytes && IsToolHead(PooledBytes.Head(line.WrittenSpan, piece, SkimBytes, scratch)))
                        { oversize = true; line.ResetWrittenCount(); }
                        else if (length <= MaxMessageLineBytes) line.Write(piece);
                        else { oversize = true; line.ResetWrittenCount(); }     // a giant message: skipped, never held
                    }
                    if (newline < 0) break;                     // the line goes on in the next block

                    if (!oversize && line.WrittenCount > 0 && !IsToolTraffic(line.WrittenSpan)
                        && (own is null || own.Keep(line.WrittenSpan)))
                    {
                        var bytes = line.WrittenMemory;
                        if (bytes.Span[^1] == (byte)'\r') bytes = bytes[..^1];
                        foreach (var entry in parser.Parse(bytes, index))
                            if (entry.Role is MessageRole.User or MessageRole.Assistant) entries.Add(entry);
                    }
                    index++;
                    line.ResetWrittenCount();
                    oversize = false;
                    at += newline + 1;
                    lineStart = position + at;
                }
                position += read;
            }
        }
        finally
        {
            PooledBytes.Return(buffer);
        }
        return (entries, lineStart);
    }

    /// <summary>
    /// How a record names its session: <c>"sessionId":"…"</c>. The quotes around the key are
    /// the record's own — inside a message's text they would be escaped — so a message that
    /// quotes the id cannot pass for a record of that session.
    /// </summary>
    public static byte[] SessionMarker(string sessionId) => Encoding.UTF8.GetBytes($"\"sessionId\":\"{sessionId}\"");

    /// <summary>Lines shorter than this are always parsed; skimming them would save nothing.</summary>
    private const int SkimFromBytes = 64 * 1024;

    /// <summary>How far into a long line its kind is looked for.</summary>
    private const int SkimBytes = 8 * 1024;

    private static readonly byte[][] ToolMarkers =
    [
        .. new[]
        {
            "tool_result", "tool_use", "thinking", "redacted_thinking",
            "function_call_output", "custom_tool_call_output", "function_call", "custom_tool_call",
            "reasoning", "exec_command_end", "file-history-snapshot",
            // Codex records the reader passes over whole: finished items, compaction, images.
            "item_completed", "compacted", "image_generation_call", "prompt_snapshot",
        }.Select(kind => Encoding.UTF8.GetBytes($"\"type\":\"{kind}\"")),
    ];

    private static readonly byte[][] TextMarkers =
    [
        .. new[] { "text", "input_text", "output_text", "user_message", "agent_message" }
            .Select(kind => Encoding.UTF8.GetBytes($"\"type\":\"{kind}\"")),
    ];

    /// <summary>
    /// Whether a long line is tool traffic — output, a call's arguments, thinking — judged from
    /// its first few kilobytes, so it can be passed over without being decoded or parsed. Most
    /// of a transcript's bytes are such lines, and none of them holds anything the user or the
    /// assistant said. A line whose first block is text is read in full, whatever follows it.
    /// </summary>
    private static bool IsToolTraffic(ReadOnlySpan<byte> line) =>
        line.Length >= SkimFromBytes && IsToolHead(line[..SkimBytes]);

    /// <summary>The same judgement on a line read as text.</summary>
    private static bool IsToolTraffic(StringBuilder line) =>
        line.Length >= SkimFromBytes && IsToolHead(Encoding.UTF8.GetBytes(line.ToString(0, SkimBytes)));

    private static bool IsToolTraffic(string line) =>
        line.Length >= SkimFromBytes && IsToolHead(Encoding.UTF8.GetBytes(line[..SkimBytes]));

    private static bool IsToolHead(ReadOnlySpan<byte> head)
    {
        var tool = FirstOf(head, ToolMarkers);
        if (tool < 0) return false;
        var text = FirstOf(head, TextMarkers);
        return text < 0 || text > tool;
    }

    private static int FirstOf(ReadOnlySpan<byte> head, byte[][] markers)
    {
        var first = -1;
        foreach (var marker in markers)
        {
            var at = head.IndexOf(marker);
            if (at >= 0 && (first < 0 || at < first)) first = at;
        }
        return first;
    }

    /// <summary>Windows tried in turn when opening at the end; widened until they hold enough entries.</summary>
    private static readonly int[] OpeningWindows = [512 * 1024, 4 * 1024 * 1024, 32 * 1024 * 1024];

    /// <summary>
    /// The last <paramref name="count"/> entries of the file, tool traffic included, read from
    /// its end — what 「看最新」 shows at once while the whole conversation is read behind it. A
    /// window that starts mid-line drops that line; a last line still being written simply
    /// fails to parse.
    /// </summary>
    public static TranscriptTail ReadTail(
        string path, int count, bool keepImages = false, CancellationToken cancellation = default)
    {
        if (GeminiSession.IsSessionFile(path))
        {
            var all = GeminiSession.ReadEntries(path, keepImages, cancellation);
            return new TranscriptTail(all.Count <= count ? all : all.GetRange(all.Count - count, count), true,
                all.Count(e => e.Role == MessageRole.ToolResult));
        }

        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.RandomAccess);
        var length = stream.Length;
        var entries = new List<TranscriptEntry>();
        var folded = 0;
        var whole = false;

        foreach (var window in OpeningWindows)
        {
            cancellation.ThrowIfCancellationRequested();
            var start = Math.Max(0, length - window);
            var bytes = new byte[length - start];
            stream.Position = start;
            stream.ReadExactly(bytes);

            var at = 0;
            if (start > 0)
            {
                at = Array.IndexOf(bytes, (byte)'\n') + 1;
                if (at == 0) continue;              // one line wider than the window: widen it
            }

            entries.Clear();
            var parser = new Parser(keepImages: keepImages);
            var index = 0;
            while (at < bytes.Length)
            {
                cancellation.ThrowIfCancellationRequested();
                var end = Array.IndexOf(bytes, (byte)'\n', at);
                var stop = end < 0 ? bytes.Length : end;
                var size = stop - at;
                if (size > 0 && (size <= MaxLineBytes
                                 || (size <= MaxMessageLineBytes && !IsToolTraffic(bytes.AsSpan(at, size)))))
                {
                    var line = bytes.AsMemory(at, size);
                    if (line.Span[^1] == (byte)'\r') line = line[..^1];
                    entries.AddRange(parser.Parse(line, index));
                }
                index++;
                at = stop + 1;
            }
            folded = parser.ToolOutputsFolded;
            whole = start == 0;
            if (entries.Count >= count || whole) break;
        }

        var kept = entries.Count <= count ? entries : entries.GetRange(entries.Count - count, count);
        return new TranscriptTail(kept, whole, folded);
    }

    /// <summary>
    /// Dialogue entries in the last window of the file that satisfies <paramref name="enough"/>,
    /// widening the window until it does or the whole file has been read.
    /// </summary>
    private static List<TranscriptEntry> TailDialogue(
        string path, Predicate<List<TranscriptEntry>> enough, CancellationToken cancellation)
    {
        if (GeminiSession.IsSessionFile(path)) return GeminiSession.ReadDialogue(path, cancellation);

        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.RandomAccess);
        var length = stream.Length;
        var dialogue = new List<TranscriptEntry>();

        foreach (var window in TailWindows)
        {
            cancellation.ThrowIfCancellationRequested();
            var start = Math.Max(0, length - window);
            var bytes = new byte[length - start];
            stream.Position = start;
            stream.ReadExactly(bytes);

            var from = 0;
            if (start > 0)
            {
                from = Array.IndexOf(bytes, (byte)'\n') + 1;
                if (from == 0) continue;            // one line wider than the window: widen it
            }

            dialogue.Clear();
            var parser = new Parser();
            var index = 0;
            foreach (var line in Encoding.UTF8.GetString(bytes, from, bytes.Length - from).Split('\n'))
            {
                cancellation.ThrowIfCancellationRequested();
                var raw = line.TrimEnd('\r');
                if (raw.Length > 0 && (raw.Length <= MaxLineBytes / 4
                                       || (raw.Length <= MaxMessageLineBytes && !IsToolTraffic(raw))))
                    foreach (var entry in parser.Parse(raw, index))
                        if (entry.Role is MessageRole.User or MessageRole.Assistant) dialogue.Add(entry);
                index++;
            }
            if (enough(dialogue) || start == 0) break;
        }
        return dialogue;
    }

    /// <summary>
    /// Reads up to <paramref name="maxEntries"/> renderable entries starting at
    /// <paramref name="fromLineOffset"/>. Lines are counted, not bytes, so a page boundary
    /// is stable across calls even though line sizes vary wildly.
    /// </summary>
    public TranscriptPage Read(
        int fromLineOffset = 0,
        int maxEntries = 200,
        CancellationToken cancellation = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (GeminiSession.IsSessionFile(_path)) return ReadDocument(fromLineOffset, maxEntries, cancellation);

        var entries = new List<TranscriptEntry>(Math.Min(Math.Max(maxEntries, 1), 1024));
        long bytes = 0;
        var end = true;

        if (!Exists)
        {
            Close();
            return new TranscriptPage(entries, fromLineOffset, true, 0, 0);
        }

        if (_parser is null || _parserLine != fromLineOffset)
        {
            _parser = new Parser(keepImages: _keepImages);
            _parserLine = fromLineOffset;
        }
        var parser = _parser;

        // Reading on from where the last page stopped continues from the open position.
        // Anything earlier can only be found again by counting lines from the top.
        if (_text is null || _linesRead > fromLineOffset) Reopen();
        var text = _text!;

        while (true)
        {
            cancellation.ThrowIfCancellationRequested();

            var raw = ReadBoundedLine(text, out var skippedOversize, out var terminated, out var consumed);
            bytes += consumed;
            if (raw is null && !skippedOversize) break;      // genuine EOF

            if (!terminated && raw is not null && !IsCompleteRecord(raw))
            {
                // The last line of a file its app is still writing. Counting it now would
                // lose that message for good; the next read starts over and finds it whole.
                Close();
                break;
            }

            var current = _linesRead++;
            if (current < fromLineOffset) continue;          // fast-forward to the page start
            if (raw is null) continue;                       // oversize line: counted, not shown

            entries.AddRange(parser.Parse(raw, current));
            if (entries.Count >= maxEntries)
            {
                end = false;
                break;
            }
        }

        _parserLine = _linesRead;
        return new TranscriptPage(entries, _linesRead, end, bytes, parser.ToolOutputsFolded,
            _stream?.Position ?? SizeBytes);
    }

    // A Gemini session, read whole once and paged through by entry; read again from the top
    // when the file changed since.
    private List<TranscriptEntry>? _document;
    private (long Length, long Written) _documentStamp;

    private TranscriptPage ReadDocument(int from, int maxEntries, CancellationToken cancellation)
    {
        if (!Exists) return new TranscriptPage([], from, true, 0, 0);
        var info = new FileInfo(_path);
        var stamp = (info.Length, info.LastWriteTimeUtc.Ticks);
        if (_document is null || (from == 0 && _documentStamp != stamp))
        {
            _document = GeminiSession.ReadEntries(_path, _keepImages, cancellation);
            _documentStamp = stamp;
        }
        var start = Math.Clamp(from, 0, _document.Count);
        var page = _document.GetRange(start, Math.Min(Math.Max(maxEntries, 1), _document.Count - start));
        var next = start + page.Count;
        var end = next >= _document.Count;
        return new TranscriptPage(page, next, end, info.Length, _document.Count(e => e.Role == MessageRole.ToolResult),
            end ? info.Length : info.Length * next / Math.Max(1, _document.Count));
    }

    private void Reopen()
    {
        Close();
        _stream = new FileStream(
            _path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            128 * 1024, FileOptions.SequentialScan);
        _text = new StreamReader(_stream, Encoding.UTF8, true, 128 * 1024);
        _linesRead = 0;
    }

    /// <summary>Releases the file. The line count is kept, so the next read knows where it was.</summary>
    private void Close()
    {
        _text?.Dispose();
        _stream?.Dispose();
        _text = null;
        _stream = null;
    }

    public void Dispose()
    {
        _disposed = true;
        Close();
    }

    /// <summary>
    /// Whether an unterminated last line is a whole record. Only an unfinished JSON object is
    /// "incomplete"; anything else the parser would ignore either way.
    /// </summary>
    private static bool IsCompleteRecord(string raw)
    {
        if (raw.Length == 0 || raw[0] != '{') return true;
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

    /// <summary>
    /// Reads one line, but refuses to materialise one larger than the cap: an embedded image
    /// or a giant file dump would otherwise be pulled into memory just to be thrown away.
    /// </summary>
    private static string? ReadBoundedLine(
        StreamReader reader, out bool skippedOversize, out bool terminated, out long consumed)
    {
        skippedOversize = false;
        terminated = false;
        consumed = 0;

        var sb = new StringBuilder(4096);
        var limit = MaxLineBytes;
        int ch;
        while ((ch = reader.Read()) != -1)
        {
            consumed++;
            if (ch == '\n')
            {
                terminated = true;
                if (skippedOversize) return null;
                return sb.Length == 0 ? string.Empty : sb.ToString();
            }
            if (skippedOversize) continue;
            if (sb.Length == limit)
            {
                // Long: a message carrying pictures goes on; tool output stops being held here.
                if (limit == MaxLineBytes && !IsToolTraffic(sb)) limit = MaxMessageLineBytes;
                else
                {
                    skippedOversize = true;
                    sb.Clear();
                    continue;
                }
            }
            sb.Append((char)ch);
        }

        if (sb.Length > 0 && !skippedOversize) return sb.ToString();
        return null;
    }

    // ------------------------------------------------------------------ parsing

    /// <summary>Text that is app scaffolding rather than something a person wrote or read.</summary>
    private static readonly string[] Boilerplate =
    [
        "<system-reminder", "<command-name>", "<local-command", "<task-notification",
        "<ci-monitor-event", "[Request interrupted",
        "This session is being continued from a previous conversation",
        // Codex desktop context blocks, injected as user/developer messages.
        "<environment_context", "<app-context", "<multi_agent_role", "<user_instructions",
        "<permissions_context", "<turn_aborted", "<collaboration_mode",
    ];

    private sealed class Parser
    {
        // Only opposite Codex record families pair up. Repeated messages in the same family
        // are real occurrences. A different message ends the matching run, so an old
        // "continue" cannot swallow a later real one. Hashes only; bounded window.
        private readonly LinkedList<(string Family, MessageRole Role, string Hash)> _unmatched = new();

        /// <summary>
        /// Only what the user and the assistant said is wanted — the search index. Tool calls,
        /// their output and attachments are then passed over instead of being cleaned and cut
        /// down just to be thrown away; text around them is split exactly as it otherwise is.
        /// </summary>
        private readonly bool _dialogueOnly;

        private readonly bool _keepImages;

        /// <summary>Picture bytes this parser has kept: a long run of screenshots stops being kept past a bound.</summary>
        private long _imageBytes;

        public Parser(bool dialogueOnly = false, bool keepImages = false)
        {
            _dialogueOnly = dialogueOnly;
            _keepImages = keepImages;
        }

        public int ToolOutputsFolded { get; private set; }

        public List<TranscriptEntry> Parse(string raw, int index)
        {
            if (raw.Length == 0 || raw[0] != '{') return [];
            JsonDocument doc;
            try { doc = JsonDocument.Parse(raw); }
            catch (JsonException) { return []; }
            catch (InvalidOperationException) { return []; }
            return Read(doc, index);
        }

        /// <summary>The same, from a line's UTF-8 bytes: no detour through a string and back.</summary>
        public List<TranscriptEntry> Parse(ReadOnlyMemory<byte> utf8, int index)
        {
            if (utf8.Length == 0 || utf8.Span[0] != (byte)'{') return [];
            JsonDocument doc;
            try { doc = JsonDocument.Parse(utf8); }
            // Bytes that are not valid UTF-8: decoded leniently, as a string, like any other line.
            catch (JsonException) { return Parse(Encoding.UTF8.GetString(utf8.Span), index); }
            catch (InvalidOperationException) { return []; }
            return Read(doc, index);
        }

        private List<TranscriptEntry> Read(JsonDocument doc, int index)
        {
            try { return ReadRecord(doc, index); }
            // A record the JSON reader parses but will not hand a value from (a number where
            // text belongs, say): that one record is skipped, never the rest of the file.
            catch (InvalidOperationException) { return []; }
        }

        private List<TranscriptEntry> ReadRecord(JsonDocument doc, int index)
        {
            var entries = new List<TranscriptEntry>(2);
            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return entries;
                var type = GetString(root, "type");
                var when = Stamp(GetString(root, "timestamp"));
                var mirror = new List<TranscriptEntry>(1);
                var family = string.Empty;

                switch (type)
                {
                    case "user" or "assistant":
                        if (root.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Object)
                            ReadMessage(msg,
                                IsInjected(root) ? MessageRole.Note
                                : type == "user" ? MessageRole.User : MessageRole.Assistant,
                                index, when, entries, mirror);
                        break;

                    case "response_item" when root.TryGetProperty("payload", out var item):
                        family = "response_item";
                        ReadCodexItem(item, index, when, entries, mirror);
                        break;

                    case "event_msg" when root.TryGetProperty("payload", out var evt):
                        family = "event_msg";
                        ReadCodexEvent(evt, index, when, entries, mirror);
                        break;
                }

                return family.Length == 0 ? entries : SuppressMirror(family, entries, mirror);
            }
        }

        /// <summary>
        /// What Claude Code put into the conversation itself — a skill's whole definition, a
        /// slash command's expansion, a local command's caveat, the summary a compaction leaves
        /// behind. It arrives as a user message but nobody typed it, and Claude Code says so
        /// with <c>isMeta</c> or <c>isCompactSummary</c>; it is folded like any other app
        /// context instead of being shown as the user's words.
        /// </summary>
        private static bool IsInjected(JsonElement root) =>
            IsTrue(root, "isMeta") || IsTrue(root, "isCompactSummary");

        private static bool IsTrue(JsonElement root, string property) =>
            root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

        /// <summary>
        /// Codex writes a user turn as an <c>event_msg</c> and again as a <c>response_item</c>.
        /// The second copy of the same text, from the other family, is dropped.
        /// </summary>
        private List<TranscriptEntry> SuppressMirror(string family, List<TranscriptEntry> entries, List<TranscriptEntry> mirror)
        {
            if (mirror.Count == 0) return entries;
            var role = mirror[0].Role;
            if (role is not (MessageRole.User or MessageRole.Assistant)) return entries;

            var hash = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(string.Join("\n", mirror.Select(e => e.Text)))));

            if (_unmatched.First is { Value: var previous } && (previous.Role != role || previous.Hash != hash))
                _unmatched.Clear();

            var opposite = _unmatched.First;
            while (opposite is not null)
            {
                var v = opposite.Value;
                if (v.Family != family && v.Role == role && v.Hash == hash) break;
                opposite = opposite.Next;
            }

            if (opposite is not null)
            {
                _unmatched.Remove(opposite);
                var mirrored = new HashSet<TranscriptEntry>(mirror, ReferenceEqualityComparer.Instance);
                return entries.Where(e => !mirrored.Contains(e)).ToList();
            }

            _unmatched.AddLast((family, role, hash));
            if (_unmatched.Count > 128) _unmatched.RemoveFirst();
            return entries;
        }

        private void ReadCodexItem(JsonElement item, int index, string? when, List<TranscriptEntry> entries, List<TranscriptEntry> mirror)
        {
            switch (GetString(item, "type"))
            {
                case "message":
                    var role = GetString(item, "role") switch
                    {
                        "user" => MessageRole.User,
                        "assistant" => MessageRole.Assistant,
                        _ => MessageRole.Note,          // developer / system context
                    };
                    ReadMessage(item, role, index, when, entries, mirror);
                    break;

                case "function_call" or "custom_tool_call" or "function_call_output" or "custom_tool_call_output"
                    when _dialogueOnly:
                    break;
                case "function_call":
                    AddToolCall(entries, index, when, GetString(item, "name"), item, "arguments");
                    break;
                case "custom_tool_call":
                    AddToolCall(entries, index, when, GetString(item, "name"), item, "input");
                    break;
                case "function_call_output" or "custom_tool_call_output":
                    AddToolResult(entries, index, when, ValueText(item, "output"));
                    break;
                // reasoning, token counts and the rest carry nothing a reader reads.
            }
        }

        private static void ReadCodexEvent(JsonElement evt, int index, string? when, List<TranscriptEntry> entries, List<TranscriptEntry> mirror)
        {
            var kind = GetString(evt, "type");
            if (kind is not ("user_message" or "agent_message")) return;
            var text = GetString(evt, "message") ?? GetString(evt, "text") ?? string.Empty;
            var entry = AddText(entries, index, when,
                kind == "user_message" ? MessageRole.User : MessageRole.Assistant, text);
            if (entry is not null) mirror.Add(entry);
        }

        private void ReadMessage(JsonElement message, MessageRole role, int index, string? when,
            List<TranscriptEntry> entries, List<TranscriptEntry> mirror)
        {
            if (!message.TryGetProperty("content", out var content)) return;

            if (content.ValueKind == JsonValueKind.String)
            {
                var entry = AddText(entries, index, when, role, JsonText.Of(content) ?? string.Empty);
                if (entry is not null) mirror.Add(entry);
                return;
            }
            if (content.ValueKind != JsonValueKind.Array) return;

            var text = new StringBuilder();
            var blocks = 0;
            foreach (var block in content.EnumerateArray())
            {
                if (++blocks > MaxContentBlocks) break;
                if (block.ValueKind == JsonValueKind.String)
                {
                    text.Append(JsonText.Of(block)).Append('\n');
                    continue;
                }
                if (block.ValueKind != JsonValueKind.Object) continue;

                switch (GetString(block, "type"))
                {
                    case "text" or "input_text" or "output_text":
                        text.Append(GetString(block, "text")).Append('\n');
                        break;
                    case "tool_use":
                        Flush();
                        if (!_dialogueOnly) AddToolCall(entries, index, when, GetString(block, "name"), block, "input");
                        break;
                    case "tool_result":
                        Flush();
                        if (!_dialogueOnly) AddToolResult(entries, index, when, ValueText(block, "content"));
                        break;
                    case "image" or "input_image" or "image_url":
                        Flush();
                        if (!_dialogueOnly) entries.Add(Picture(block, index, when));
                        break;
                    // thinking / redacted_thinking and unknown blocks are not shown.
                }
            }
            Flush();

            void Flush()
            {
                if (text.Length == 0) return;
                var entry = AddText(entries, index, when, role, text.ToString());
                if (entry is not null) mirror.Add(entry);
                text.Clear();
            }
        }

        /// <summary>
        /// A picture block: kept as its bytes when pictures are wanted and it is not too big;
        /// otherwise a line saying what it is. A picture on the web is named, never fetched.
        /// </summary>
        private TranscriptEntry Picture(JsonElement block, int index, string? when)
        {
            string? data = null, url = null;
            if (block.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.Object)
            {
                if (GetString(source, "type") == "base64") data = GetString(source, "data");
                else url = GetString(source, "url");
            }
            else if (block.TryGetProperty("image_url", out var imageUrl))
            {
                url = imageUrl.ValueKind == JsonValueKind.String ? imageUrl.GetString()
                    : imageUrl.ValueKind == JsonValueKind.Object ? GetString(imageUrl, "url") : null;
            }
            if (url is not null && url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                && url.IndexOf(";base64,", StringComparison.OrdinalIgnoreCase) is var comma and > 0)
            {
                data = url[(comma + ";base64,".Length)..];
                url = null;
            }

            if (url is not null)
                return new TranscriptEntry(index, MessageRole.Attachment, $"（网络图片，未加载）{url}", null, when);
            if (data is null)
                return new TranscriptEntry(index, MessageRole.Attachment, "（图片附件，未加载）", null, when);

            var size = (long)data.Length * 3 / 4;
            var label = $"（图片 · {Math.Max(1, size / 1024):N0} KB）";
            if (!_keepImages) return new TranscriptEntry(index, MessageRole.Attachment, label, null, when);
            if (size > MaxImageBytes || _imageBytes + size > MaxImageBytesPerParser)
                return new TranscriptEntry(index, MessageRole.Attachment, label + "太大，未显示", null, when);
            try
            {
                var bytes = Convert.FromBase64String(data);
                _imageBytes += bytes.Length;
                return new TranscriptEntry(index, MessageRole.Attachment, label, null, when) { Image = bytes };
            }
            catch (FormatException)
            {
                return new TranscriptEntry(index, MessageRole.Attachment, "（图片数据无法读取）", null, when);
            }
        }

        private void AddToolCall(List<TranscriptEntry> entries, int index, string? when, string? name, JsonElement holder, string argumentsProperty)
        {
            var arguments = ValueText(holder, argumentsProperty);
            var label = string.IsNullOrWhiteSpace(name) ? "tool" : name;
            var hint = Hint(holder, argumentsProperty, arguments);
            entries.Add(new TranscriptEntry(index, MessageRole.ToolCall,
                string.IsNullOrEmpty(hint) ? label : $"{label}  {hint}", Bound(arguments), when));
        }

        private void AddToolResult(List<TranscriptEntry> entries, int index, string? when, string output)
        {
            ToolOutputsFolded++;
            var cleaned = TranscriptText.Clean(output);
            entries.Add(new TranscriptEntry(index, MessageRole.ToolResult,
                cleaned.Length == 0 ? "工具输出（空）" : $"工具输出（{cleaned.Length:N0} 字符）",
                Bound(cleaned), when));
        }

        private static TranscriptEntry? AddText(List<TranscriptEntry> entries, int index, string? when, MessageRole role, string value)
        {
            var trimmed = TranscriptText.Clean(value).Trim();
            if (trimmed.Length == 0) return null;

            // A slash command the user typed: the markup is the app's, the words are theirs.
            if (role == MessageRole.User && ClaudeCodeMarkup.TryReadCommand(trimmed, out var command))
                trimmed = command.Typed;
            else
            {
                // The app's own blocks in front of what the user typed, in one message: they
                // fold, and the user's words after them stay the user's.
                if (role == MessageRole.User
                    && ClaudeCodeMarkup.PeelInjected(trimmed) is { Blocks.Count: > 0, Words.Length: > 0 } peeled)
                {
                    foreach (var block in peeled.Blocks)
                        entries.Add(new TranscriptEntry(index, MessageRole.Note, NoteLabel(block), Bound(block), when));
                    trimmed = ClaudeCodeMarkup.TryReadCommand(peeled.Words, out var typed) ? typed.Typed : peeled.Words;
                }
                if (role == MessageRole.Note || IsBoilerplate(trimmed))
                {
                    entries.Add(new TranscriptEntry(index, MessageRole.Note, NoteLabel(trimmed), Bound(trimmed), when));
                    return null;        // never a mirror candidate
                }
            }

            var entry = new TranscriptEntry(index, role, trimmed, null, when);
            entries.Add(entry);
            return entry;
        }

        /// <summary>
        /// A few words on what a folded block is — which skill was loaded, what a command
        /// printed — so a run of them can be told apart without opening each one.
        /// </summary>
        private static string NoteLabel(string text)
        {
            if (ClaudeCodeMarkup.TryReadCommand(text, out var command))
                return "命令 · " + Flatten(command.Typed, 80);

            const string skill = "Base directory for this skill:";
            if (text.StartsWith(skill, StringComparison.OrdinalIgnoreCase))
            {
                var folder = FirstLine(text)[skill.Length..].Trim().TrimEnd('\\', '/');
                var name = folder[(folder.LastIndexOfAny(['\\', '/']) + 1)..];
                return name.Length == 0 ? "技能说明" : "技能说明 · " + Flatten(name, 60);
            }

            const string stdout = "<local-command-stdout>";
            if (text.StartsWith(stdout, StringComparison.Ordinal))
            {
                var printed = Flatten(text[stdout.Length..].Replace("</local-command-stdout>", " "), 80);
                return printed.Length == 0 ? "命令输出" : "命令输出 · " + printed;
            }

            foreach (var (prefix, label) in NoteKinds)
                if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return label;

            // An injected document (a command's prompt, AGENTS.md) usually opens with a heading.
            if (text.StartsWith("# ", StringComparison.Ordinal))
                return "说明 · " + Flatten(FirstLine(text)[2..], 80);

            // Short injected text ("Continue from where you left off.") is clearer shown as is.
            return text.Length <= 240 && !OpensWithLowerCaseTag(text) ? Flatten(text, 80) : "系统提示";
        }

        private static readonly (string Prefix, string Label)[] NoteKinds =
        [
            ("<system-reminder", "系统提醒"),
            ("<local-command-caveat", "本地命令说明"),
            ("<local-command", "本地命令"),
            ("<task-notification", "后台任务通知"),
            ("<ci-monitor-event", "CI 事件"),
            ("[Request interrupted", "请求被中断"),
            ("[Image:", "图片说明"),
            ("This session is being continued", "上下文压缩摘要"),
            ("<environment_context", "环境信息"),
            ("<user_instructions", "项目指令"),
            ("<turn_aborted", "回合被中断"),
        ];

        private static string FirstLine(string text)
        {
            var end = text.IndexOfAny(['\r', '\n']);
            return end < 0 ? text : text[..end];
        }

        /// <summary>
        /// Injected context, not something a person typed. Both apps wrap what they inject in
        /// lower-case XML-style tags (<c>&lt;environment_context&gt;</c>, <c>&lt;recommended_plugins&gt;</c>,
        /// <c>&lt;system-reminder&gt;</c>…), so a message that opens with such a tag is folded
        /// whatever the tag's name; the explicit list covers the few that do not start that way.
        /// </summary>
        private static bool IsBoilerplate(string text)
        {
            foreach (var b in Boilerplate)
                if (text.StartsWith(b, StringComparison.OrdinalIgnoreCase)) return true;
            return OpensWithLowerCaseTag(text);
        }

        private static bool OpensWithLowerCaseTag(string text)
        {
            if (text.Length < 3 || text[0] != '<' || !char.IsAsciiLetterLower(text[1])) return false;
            for (var i = 2; i < text.Length && i < 64; i++)
            {
                var c = text[i];
                if (c == '>') return true;
                if (!(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '_' or '-')) return false;
            }
            return false;
        }

        /// <summary>A short, readable hint about what a tool call did.</summary>
        private static string Hint(JsonElement holder, string property, string fallback)
        {
            if (holder.TryGetProperty(property, out var input))
            {
                // Claude puts structured input here; Codex puts a JSON string.
                JsonDocument? parsed = null;
                try
                {
                    if (input.ValueKind == JsonValueKind.String && fallback.StartsWith('{'))
                        parsed = JsonDocument.Parse(fallback);
                    var obj = parsed?.RootElement ?? input;
                    if (obj.ValueKind == JsonValueKind.Object)
                        foreach (var key in new[] { "description", "command", "cmd", "file_path", "path", "pattern", "query", "prompt" })
                            if (obj.TryGetProperty(key, out var v))
                            {
                                var s = v.ValueKind == JsonValueKind.String ? JsonText.Of(v) ?? "" : v.GetRawText();
                                return Flatten(s, 110);
                            }
                }
                catch (JsonException) { /* not JSON: fall through to the raw text */ }
                finally { parsed?.Dispose(); }
            }
            return Flatten(fallback, 110);
        }

        private static string Flatten(string s, int max)
        {
            var flat = string.Join(' ', TranscriptText.Clean(s).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return flat.Length > max ? flat[..max] + "…" : flat;
        }

        private static string Bound(string s) =>
            s.Length <= MaxDetailChars ? s : s[..MaxDetailChars] + $"\n…（已截断，共 {s.Length:N0} 字符；原文件未修改）";

        /// <summary>Text of a value that may be a string, an array of text blocks, or any JSON.</summary>
        private static string ValueText(JsonElement holder, string property)
        {
            if (!holder.TryGetProperty(property, out var value)) return string.Empty;
            if (value.ValueKind == JsonValueKind.String) return JsonText.Of(value) ?? string.Empty;
            if (value.ValueKind == JsonValueKind.Array)
            {
                var sb = new StringBuilder();
                var n = 0;
                foreach (var item in value.EnumerateArray())
                {
                    if (++n > MaxContentBlocks) { sb.Append("\n[更多内容块未展开]"); break; }
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(item.ValueKind == JsonValueKind.String
                        ? JsonText.Of(item)
                        : GetString(item, "text") ?? "[非文本内容块]");
                }
                return sb.ToString();
            }
            return value.GetRawText();
        }

        private static string? Stamp(string? iso)
        {
            if (string.IsNullOrEmpty(iso) || iso.Length > 64) return null;
            return DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var when)
                ? when.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture)
                : null;
        }

        private static string? GetString(JsonElement element, string property) =>
            element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String
                ? JsonText.Of(v)
                : null;
    }
}

/// <summary>
/// Makes untrusted transcript text inert for display: terminal control sequences (CSI, OSC,
/// DCS and friends) and other control characters are removed in one linear pass — no regex,
/// so hostile tool output cannot make the cleaner itself slow. Tabs and line breaks survive.
/// </summary>
public static class TranscriptText
{
    public static string Clean(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var result = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '' || c is '' or '' or '' or '' or '' or '')
            {
                var command = c == ''
                    ? (++i < text.Length ? text[i] : '\0')
                    : c switch { '' => '[', '' => ']', '' => 'P', '' => 'X', '' => '^', _ => '_' };
                if (command == '[')
                {
                    while (++i < text.Length && text[i] is not (>= '\x40' and <= '\x7e')) { }
                }
                else if (command is ']' or 'P' or 'X' or '^' or '_')
                {
                    while (++i < text.Length)
                    {
                        if (text[i] is '' or '') break;
                        if (text[i] == '' && i + 1 < text.Length && text[i + 1] == '\\') { i++; break; }
                    }
                }
                else
                {
                    while (i < text.Length && text[i] is >= '\x20' and <= '\x2f') i++;
                }
                continue;
            }
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                result.Append('\n');
            }
            else if (c is '\n' or '\t' || !char.IsControl(c)) result.Append(c);
        }
        return result.ToString();
    }
}
