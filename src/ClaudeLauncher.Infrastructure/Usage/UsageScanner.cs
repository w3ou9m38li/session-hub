using ClaudeLauncher.Core.Contracts;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ClaudeLauncher.Infrastructure.Backends;

namespace ClaudeLauncher.Infrastructure.Usage;


/// <summary>
/// One call to a model as its transcript recorded it: when, which model, and its tokens.
/// <see cref="Key"/> names the call itself, so a call written twice — a line per content
/// block, a repeated event, a copy in a forked or resumed conversation — counts once.
/// </summary>
public readonly record struct UsageCall(
    long Key, DateTimeOffset At, string Model, long Input, long CacheWrite, long CacheRead, long Output);

/// <summary>What one read of a file found, and where — and in what state — the next read goes on.</summary>
/// <param name="Thread">A Codex rollout's own thread id, when its first line was read this time.</param>
/// <param name="Root">The thread its calls are counted under: the one it was forked from, all the way back.</param>
/// <param name="Facts">What else the read found around the calls, when it was asked to look.</param>
public sealed record UsageScan(IReadOnlyList<UsageCall> Calls, long NextOffset, string? State,
    string? Thread = null, string? Root = null, UsageFacts? Facts = null);

/// <summary>
/// Reads the calls a transcript records, from a byte offset on: Claude writes <c>usage</c> into
/// every assistant message, Codex a <c>token_count</c> event after every response. Only those
/// lines are parsed; tool output, the bulk of every transcript, is passed over unread. A last
/// line still being written is left for the next read.
/// </summary>
public static class UsageScanner
{
    /// <summary>Largest line looked at; anything longer is tool output or a picture.</summary>
    private const int MaxLineBytes = 1024 * 1024;

    /// <summary>Largest Claude assistant message kept: one that wrote a big file carries it in full.</summary>
    private const int MaxAssistantLineBytes = 16 * 1024 * 1024;

    private static readonly byte[] UsageKey = "\"usage\":{"u8.ToArray();
    private static readonly byte[] AssistantRole = "\"role\":\"assistant\""u8.ToArray();
    private static readonly byte[] TokenCountType = "\"type\":\"token_count\""u8.ToArray();
    private static readonly byte[] TurnContextType = "\"type\":\"turn_context\""u8.ToArray();
    private static readonly byte[] SessionMetaType = "\"type\":\"session_meta\""u8.ToArray();

    private static readonly JsonDocumentOptions Json = new() { MaxDepth = 256 };

    /// <param name="state">What an earlier read of the same file left: Codex's model and running totals.</param>
    /// <param name="rootOf">For a forked Codex thread, the thread its parent is counted under, when known.</param>
    public static UsageScan Scan(string path, AppFamily app, long offset, string? state,
        Func<string, string?>? rootOf = null, CancellationToken cancellation = default) =>
        Scan(path, app, offset, state, facts: false, part: default, rootOf, cancellation);

    /// <param name="facts">
    /// Also note what an analysis of the conversation needs (<see cref="UsageScan.Facts"/>): each
    /// call's traits, the compactions, and the tools called and the size of what they gave back.
    /// </param>
    /// <param name="part">
    /// Which lines are the conversation's own. The others — a resumed or forked copy's history —
    /// are read all the same, their calls marked <see cref="CallTraits.Copied"/> and their tool
    /// traffic left out: they still tell what the cache held when the copy's own calls began.
    /// Only looked at with <paramref name="facts"/>; a Gemini or dsh session is always its own.
    /// </param>
    public static UsageScan Scan(string path, AppFamily app, long offset, string? state, bool facts,
        OwnPart part = default, Func<string, string?>? rootOf = null, CancellationToken cancellation = default)
    {
        if (app == AppFamily.Gemini) return ScanGemini(path, facts, cancellation);
        if (app == AppFamily.DeepSeek) return ScanDsh(path, facts, cancellation);

        using var stream = TranscriptFiles.OpenSeekable(path, 1, FileOptions.SequentialScan);
        if (offset < 0 || offset > stream.Length)
        {
            offset = 0;
            state = null;
        }
        stream.Position = offset;

        var codex = app == AppFamily.Codex ? CodexState.Parse(state) : null;
        var calls = new List<UsageCall>();
        var notes = facts ? new Notes(codex is not null, part.Filter(path, offset)) : null;
        using var line = new PooledBytes();
        var skipping = false;
        var buffer = PooledBytes.RentBlock();
        Span<byte> scratch = stackalloc byte[Glance];
        long position = offset, lineStart = offset;
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
                    if (!skipping)
                    {
                        var length = line.WrittenCount + piece.Length;
                        // Past a glance, only a line that can carry usage is held: most bytes are tool
                        // output, which never does.
                        var head = length <= Glance ? default : PooledBytes.Head(line.WrittenSpan, piece, Glance, scratch);
                        var worth = length <= Glance || MayCarryUsage(head, app, facts);
                        if (worth && (length <= MaxLineBytes || (length <= MaxAssistantLineBytes && app == AppFamily.Claude)))
                            line.Write(piece);
                        else
                        {
                            skipping = true;                // passed over, never held
                            notes?.Skip(head, line.WrittenSpan, piece);
                            line.ResetWrittenCount();
                        }
                    }
                    else notes?.Watch(piece);
                    if (newline < 0) break;                 // the line goes on in the next block

                    if (skipping) notes?.Skipped(calls.Count, position + at + newline - lineStart);
                    else if (line.WrittenCount > 0)
                    {
                        var own = notes?.Keep(line.WrittenSpan) ?? true;
                        if (codex is null) ReadClaude(line.WrittenMemory, calls, notes, own);
                        else ReadCodex(line.WrittenMemory, codex, calls, rootOf, notes, own);
                        notes?.Held(line.WrittenSpan, calls.Count, own);
                    }
                    line.ResetWrittenCount();
                    skipping = false;
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
        return new UsageScan(calls, lineStart, codex?.ToString(), codex?.Thread, codex?.Root, notes?.ToFacts());
    }

    /// <summary>How much of a line is looked at before deciding whether to read the rest.</summary>
    private const int Glance = 16 * 1024;

    /// <summary>
    /// Whether a line's head says it can carry usage: a Claude assistant message; a Codex token
    /// count, or the turn context and session header that say whose tokens they are — and, when
    /// facts are noted, a Codex tool call, whose name and id come at either end of it.
    /// </summary>
    private static bool MayCarryUsage(ReadOnlySpan<byte> head, AppFamily app, bool facts) =>
        app == AppFamily.Claude
            ? head.IndexOf(AssistantRole) >= 0
            : head.IndexOf(TokenCountType) >= 0 || head.IndexOf(TurnContextType) >= 0 || head.IndexOf(SessionMetaType) >= 0
              || (facts && Notes.IsCodexCall(head));

    // ------------------------------------------------------------------ Claude

    /// <summary>
    /// An assistant message's usage. Claude Code writes one line per content block of a
    /// response, each with the same message id and request id: the ids make the key. The usage
    /// on those lines can grow from one to the next — the first may carry only part of the
    /// output — so the ledger keeps the largest figures any of them gave.
    /// </summary>
    private static void ReadClaude(ReadOnlyMemory<byte> bytes, List<UsageCall> calls, Notes? notes, bool own)
    {
        if (bytes.Span.IndexOf(UsageKey) < 0) return;
        try
        {
            using var doc = JsonDocument.Parse(bytes, Json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object
                || !message.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
                return;
            var model = Text(message, "model") ?? "unknown";
            if (model == "<synthetic>") return;
            var input = Number(usage, "input_tokens");
            var write = Number(usage, "cache_creation_input_tokens");
            var cached = Number(usage, "cache_read_input_tokens");
            var output = Number(usage, "output_tokens");
            if (input + write + cached + output > 0 && TryTime(root, out var at))
            {
                var id = Text(message, "id");
                var request = Text(root, "requestId");
                var key = id is null && request is null
                    ? $"c|{at.UtcTicks}|{input}|{write}|{cached}|{output}"
                    : $"c|{id}|{request}";
                calls.Add(new UsageCall(Hash(key), at, model, input, write, cached, output));
                notes?.Call(ClaudeTraits(root, usage, own));
            }
            if (notes is not null && own && message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                foreach (var block in content.EnumerateArray())
                    if (block.ValueKind == JsonValueKind.Object && Text(block, "type") == "tool_use" && Text(block, "id") is { } tool)
                        notes.ToolCall(calls.Count, tool, Text(block, "name"));
        }
        catch (JsonException)
        {
            // A damaged line: its call is not counted.
        }
    }

    /// <summary>A side chain's call, and for how long it wrote to the cache — five minutes or an hour, when it says.</summary>
    private static CallTraits ClaudeTraits(JsonElement root, JsonElement usage, bool own)
    {
        var traits = own ? CallTraits.None : CallTraits.Copied;
        if (root.TryGetProperty("isSidechain", out var side) && side.ValueKind == JsonValueKind.True) traits |= CallTraits.Sidechain;
        if (usage.TryGetProperty("cache_creation", out var written) && written.ValueKind == JsonValueKind.Object)
        {
            if (Number(written, "ephemeral_5m_input_tokens") > 0) traits |= CallTraits.ShortCache;
            if (Number(written, "ephemeral_1h_input_tokens") > 0) traits |= CallTraits.LongCache;
        }
        return traits;
    }

    // ------------------------------------------------------------------- Codex

    /// <summary>Codex's figures for one response, or its running totals.</summary>
    private readonly record struct OpenAiTokens(long Input, long Cached, long CacheWrite, long Output, long Total)
    {
        public string Signature => string.Create(CultureInfo.InvariantCulture, $"{Input},{Cached},{CacheWrite},{Output},{Total}");

        public static OpenAiTokens operator -(OpenAiTokens a, OpenAiTokens b) => new(
            a.Input - b.Input, a.Cached - b.Cached, a.CacheWrite - b.CacheWrite, a.Output - b.Output, a.Total - b.Total);

        public static OpenAiTokens? Parse(string signature)
        {
            var parts = signature.Split(',');
            if (parts.Length != 5) return null;
            var n = new long[5];
            for (var i = 0; i < 5; i++)
                if (!long.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out n[i])) return null;
            return new OpenAiTokens(n[0], n[1], n[2], n[3], n[4]);
        }
    }

    /// <summary>
    /// The model of the turn under way, the last running totals, the rollout's thread and the
    /// thread its calls are counted under — carried from one read to the next.
    /// </summary>
    private sealed class CodexState
    {
        public string? Model;
        public OpenAiTokens? Total;
        public string? Thread;
        public string? Root;

        private const char Separator = '\u001F';

        public static CodexState Parse(string? state)
        {
            var parsed = new CodexState();
            if (string.IsNullOrEmpty(state)) return parsed;
            var parts = state.Split(Separator);
            parsed.Model = parts[0].Length > 0 ? parts[0] : null;
            if (parts.Length > 1) parsed.Total = OpenAiTokens.Parse(parts[1]);
            if (parts.Length > 2 && parts[2].Length > 0) parsed.Thread = parts[2];
            if (parts.Length > 3 && parts[3].Length > 0) parsed.Root = parts[3];
            return parsed;
        }

        public override string ToString() =>
            (Model ?? "") + Separator + (Total?.Signature ?? "") + Separator + (Thread ?? "") + Separator + (Root ?? "");
    }

    /// <summary>
    /// A response's usage. Codex repeats the same event now and then, and a forked conversation
    /// starts with a copy of every event of the one it came from; the event's figures — running
    /// totals and the response's own — together with the thread the fork family started from
    /// make the key, so neither counts twice, while two unrelated conversations whose first
    /// replies happen to match still count apart. The response's own figures are what it used:
    /// the running totals start over from time to time.
    /// </summary>
    private static void ReadCodex(ReadOnlyMemory<byte> bytes, CodexState state, List<UsageCall> calls,
        Func<string, string?>? rootOf, Notes? notes, bool own)
    {
        var span = bytes.Span;
        var isCount = span.IndexOf(TokenCountType) >= 0;
        var isMeta = !isCount && span.IndexOf(SessionMetaType) >= 0;
        if (!isCount && !isMeta && span.IndexOf(TurnContextType) < 0) return;
        try
        {
            using var doc = JsonDocument.Parse(bytes, Json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
                return;
            var type = Text(root, "type");
            if (type == "session_meta")
            {
                // A fork starts with a copy of its parent's events: counted under the parent's family.
                if (Text(payload, "id") is not { Length: > 0 } thread) return;
                var forkedFrom = Text(payload, "forked_from_id");
                state.Thread = thread;
                state.Root = forkedFrom is { Length: > 0 } ? rootOf?.Invoke(forkedFrom) ?? forkedFrom : thread;
                return;
            }
            if (type == "turn_context")
            {
                if (Text(payload, "model") is { Length: > 0 } model) state.Model = model;
                return;
            }
            if (type != "event_msg" || Text(payload, "type") != "token_count"
                || !payload.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object
                || !TryTime(root, out var at))
                return;

            var total = Tokens(info, "total_token_usage");
            var last = Tokens(info, "last_token_usage");
            // Running totals that did not move mean nothing new was used: a repeat, or the note
            // a compaction leaves — its own figure, the size the conversation was compacted to.
            if (total is { } now && state.Total is { } previous && now == previous)
            {
                if (last is { Input: 0, Output: 0, Total: > 0 }) notes?.Compaction(calls.Count, at);
                return;
            }
            // An older Codex recorded running totals only: then a response used what they grew by.
            var used = last ?? (total is { } t
                ? state.Total is { } before && t.Total >= before.Total ? t - before : t
                : (OpenAiTokens?)null);
            if (total is not null) state.Total = total;
            if (used is not { } u || u.Total <= 0) return;

            var key = $"x|{state.Root}|{total?.Signature}|{last?.Signature}";
            // Input counts the cached part as well; kept apart here, as Claude's figures are. A
            // response that gave only its total counts it as input.
            var (fresh, cached, output) = u.Input + u.Output > 0
                ? (Math.Max(0, u.Input - u.Cached - u.CacheWrite), u.Cached, u.Output)
                : (u.Total, 0L, 0L);
            calls.Add(new UsageCall(Hash(key), at, state.Model ?? "unknown", fresh, u.Input + u.Output > 0 ? u.CacheWrite : 0,
                cached, output));
            notes?.Call(own ? CallTraits.None : CallTraits.Copied);
        }
        catch (JsonException)
        {
        }
    }

    private static OpenAiTokens? Tokens(JsonElement info, string name)
    {
        if (!info.TryGetProperty(name, out var tokens) || tokens.ValueKind != JsonValueKind.Object) return null;
        var input = Number(tokens, "input_tokens");
        var output = Number(tokens, "output_tokens");
        var total = tokens.TryGetProperty("total_tokens", out _) ? Number(tokens, "total_tokens") : input + output;
        return new OpenAiTokens(input, Number(tokens, "cached_input_tokens"), Number(tokens, "cache_write_input_tokens"),
            output, total);
    }

    // ----------------------------------------------------------------- shared

    private static string? Text(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long Number(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n)
            ? Math.Max(0, n) : 0;

    private static bool TryTime(JsonElement root, out DateTimeOffset at)
    {
        at = default;
        return Text(root, "timestamp") is { } stamp
               && DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out at);
    }

    /// <summary>
    /// A Gemini CLI session, read whole — it is one document written again as it grows. Each
    /// model message records its tokens: <c>input</c> counts the prompt including what came from
    /// the cache (<c>cached</c>), and the thinking (<c>thoughts</c>) is output the model wrote.
    /// </summary>
    private static UsageScan ScanGemini(string path, bool facts, CancellationToken cancellation)
    {
        var events = facts ? new List<UsageFact>() : null;
        var calls = GeminiSession.ReadCalls(path, events, cancellation)
            .Select(c => new UsageCall(Hash("gemini:" + c.Id), c.When, c.Model.Length > 0 ? c.Model : "unknown",
                Input: Math.Max(0, c.Input - c.Cached) + c.Tool, CacheWrite: 0, CacheRead: c.Cached, Output: c.Output + c.Thoughts))
            .ToList();
        return new UsageScan(calls, TranscriptFiles.Stat(path)?.Length ?? 0, null, Facts: Whole(calls.Count, events));
    }

    /// <summary>
    /// A DeepSeek Harness session, read whole. Each model message records its tokens, disjoint:
    /// <c>inputTokens</c> is the prompt not served from the cache, the cache's share is apart, and
    /// the reasoning is already in <c>outputTokens</c>. A message copied into another session
    /// keeps its id, so it counts once.
    /// </summary>
    private static UsageScan ScanDsh(string path, bool facts, CancellationToken cancellation)
    {
        var events = facts ? new List<UsageFact>() : null;
        var calls = DshSession.ReadCalls(path, events, cancellation)
            .Select(c => new UsageCall(Hash("dsh:" + c.Id), c.When, c.Model.Length > 0 ? c.Model : "unknown",
                Input: c.Input, CacheWrite: c.CacheWrite, CacheRead: c.CacheRead, Output: c.Output))
            .ToList();
        return new UsageScan(calls, TranscriptFiles.Stat(path)?.Length ?? 0, null, Facts: Whole(calls.Count, events));
    }

    /// <summary>A whole document's facts: its calls have no traits to tell.</summary>
    private static UsageFacts? Whole(int calls, List<UsageFact>? events) =>
        events is null ? null : new UsageFacts(new CallTraits[calls], events);

    /// <summary>FNV-1a over the key's UTF-8: 64 bits, so a hundred thousand calls never collide in practice.</summary>
    internal static long Hash(string key)
    {
        const ulong Offset = 14695981039346656037, Prime = 1099511628211;
        var hash = Offset;
        foreach (var b in Encoding.UTF8.GetBytes(key))
        {
            hash ^= b;
            hash *= Prime;
        }
        return unchecked((long)hash);
    }

    // ----------------------------------------------------------------- facts

    /// <summary>
    /// What a read notes besides the calls, line by line: whether a line is the conversation's
    /// own, a compaction the app marked, a tool's call and the size of its result. A long line —
    /// a tool's output, most often — is never held for it: its kind and ids come from its first
    /// kilobytes, its size from where it ends, and the two markers that can lie further on are
    /// looked for in its bytes as they go by.
    /// </summary>
    private sealed class Notes(bool codex, OwnPart.LineFilter? own)
    {
        private readonly List<CallTraits> _traits = [];
        private readonly List<UsageFact> _events = [];

        // The line being passed over.
        private bool _skipOwn;
        private List<string>? _skipResults;
        private string? _skipOutput;
        private (string Id, string? Name)? _skipCall;
        private bool _skipCompacted;
        private DateTimeOffset? _skipAt;
        private readonly Seam _seam = new();

        private static ReadOnlySpan<byte> ToolUseId => "\"tool_use_id\":\""u8;
        private static ReadOnlySpan<byte> CompactBoundary => "\"subtype\":\"compact_boundary\""u8;
        private static ReadOnlySpan<byte> CompactSummary => "\"isCompactSummary\":true"u8;
        private static ReadOnlySpan<byte> Duplicate => ",\"toolUseResult\":"u8;
        private static ReadOnlySpan<byte> Compacted => "\"type\":\"compacted\""u8;
        private static ReadOnlySpan<byte> FunctionCall => "\"payload\":{\"type\":\"function_call\""u8;
        private static ReadOnlySpan<byte> CustomCall => "\"payload\":{\"type\":\"custom_tool_call\""u8;
        private static ReadOnlySpan<byte> FunctionOutput => "\"payload\":{\"type\":\"function_call_output\""u8;
        private static ReadOnlySpan<byte> CustomOutput => "\"payload\":{\"type\":\"custom_tool_call_output\""u8;
        private static ReadOnlySpan<byte> CallId => "\"call_id\":\""u8;
        private static ReadOnlySpan<byte> Name => "\"name\":\""u8;
        private static ReadOnlySpan<byte> Timestamp => "\"timestamp\":\""u8;

        /// <summary>How far into a Codex line its record's kind is: right after the time it opens with.</summary>
        private const int KindBytes = 256;

        public UsageFacts ToFacts() => new(_traits, _events);

        /// <summary>Whether the line is the conversation's own; asked once per line, in file order.</summary>
        public bool Keep(ReadOnlySpan<byte> line) => own?.Keep(line) ?? true;

        public void Call(CallTraits traits) => _traits.Add(traits);

        public void ToolCall(int before, string id, string? name) =>
            _events.Add(new UsageFact(UsageFactKind.ToolCall, before, null, id, name, 0));

        public void Compaction(int before, DateTimeOffset? at) =>
            _events.Add(new UsageFact(UsageFactKind.Compaction, before, at, null, null, 0));

        public static bool IsCodexCall(ReadOnlySpan<byte> head)
        {
            // The kinds end with their closing quote: an output's kind is not taken for a call's.
            var kind = head[..Math.Min(head.Length, KindBytes)];
            return kind.IndexOf(FunctionCall) >= 0 || kind.IndexOf(CustomCall) >= 0;
        }

        /// <summary>A line read whole: a tool's result or call, or a compaction's mark.</summary>
        public void Held(ReadOnlySpan<byte> line, int before, bool keep)
        {
            if (!codex)
            {
                if (line.IndexOf(CompactBoundary) >= 0 || line.IndexOf(CompactSummary) >= 0) Compaction(before, Time(line));
                if (keep && line.IndexOf(ToolUseId) >= 0)
                {
                    var cut = line.IndexOf(Duplicate);
                    var said = cut < 0 ? line : line[..cut];
                    Results(Values(said, ToolUseId), said.Length, before);
                }
                return;
            }
            var kind = line[..Math.Min(line.Length, KindBytes)];
            if (kind.IndexOf(Compacted) >= 0) Compaction(before, Time(line));
            if (!keep) return;
            if (kind.IndexOf(FunctionOutput) >= 0 || kind.IndexOf(CustomOutput) >= 0)
            {
                if (Value(line, CallId) is { } output) Result(output, null, line.Length, before);
            }
            else if ((kind.IndexOf(FunctionCall) >= 0 || kind.IndexOf(CustomCall) >= 0) && Value(line, CallId) is { } call)
                ToolCall(before, call, Value(line, Name));
        }

        /// <summary>A line about to be passed over: what its head says, and what to look for in the rest.</summary>
        public void Skip(ReadOnlySpan<byte> head, ReadOnlySpan<byte> held, ReadOnlySpan<byte> piece)
        {
            _skipOwn = Keep(head);
            _skipResults = null;
            _skipOutput = null;
            _skipCall = null;
            _skipCompacted = false;
            _skipAt = Time(head);
            _seam.Reset(watch: !codex);
            if (!codex)
            {
                if (_skipOwn && head.IndexOf(ToolUseId) >= 0) _skipResults = Values(head, ToolUseId);
                _seam.Feed(held);
                _seam.Feed(piece);
                return;
            }
            var kind = head[..Math.Min(head.Length, KindBytes)];
            _skipCompacted = kind.IndexOf(Compacted) >= 0;
            if (!_skipOwn) return;
            if (kind.IndexOf(FunctionOutput) >= 0 || kind.IndexOf(CustomOutput) >= 0) _skipOutput = Value(head, CallId);
            else if ((kind.IndexOf(FunctionCall) >= 0 || kind.IndexOf(CustomCall) >= 0) && Value(head, CallId) is { } call)
                _skipCall = (call, Value(head, Name));
        }

        public void Watch(ReadOnlySpan<byte> piece) => _seam.Feed(piece);

        /// <summary>The end of a line passed over, <paramref name="length"/> bytes long.</summary>
        public void Skipped(int before, long length)
        {
            if (_skipCompacted || _seam.Summary) Compaction(before, _skipAt);
            if (_skipResults is { Count: > 0 } ids) Results(ids, _seam.Cut >= 0 ? _seam.Cut : length, before);
            if (_skipOutput is { } output) Result(output, null, length, before);
            if (_skipCall is { } call) ToolCall(before, call.Id, call.Name);
        }

        /// <summary>One line with the results of several calls: its size shared among them.</summary>
        private void Results(List<string> ids, long bytes, int before)
        {
            foreach (var id in ids) Result(id, null, bytes / Math.Max(1, ids.Count), before);
        }

        private void Result(string id, string? name, long bytes, int before) =>
            _events.Add(new UsageFact(UsageFactKind.ToolResult, before, null, id, name, bytes));

        /// <summary>The string after a key, when it is a short one with nothing escaped in it: an id, a name, a time.</summary>
        private static string? Value(ReadOnlySpan<byte> line, ReadOnlySpan<byte> key)
        {
            var at = line.IndexOf(key);
            if (at < 0) return null;
            var value = line[(at + key.Length)..];
            var end = value[..Math.Min(value.Length, 256)].IndexOfAny((byte)'"', (byte)'\\');
            return end > 0 && value[end] == '"' ? Encoding.UTF8.GetString(value[..end]) : null;
        }

        private static List<string> Values(ReadOnlySpan<byte> line, ReadOnlySpan<byte> key)
        {
            var values = new List<string>(1);
            while (line.IndexOf(key) is var at and >= 0)
            {
                if (Value(line[at..], key) is { } value) values.Add(value);
                line = line[(at + key.Length)..];
            }
            return values;
        }

        private static DateTimeOffset? Time(ReadOnlySpan<byte> line) =>
            Value(line, Timestamp) is { } stamp
            && DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
                ? at
                : null;
    }

    /// <summary>
    /// Looks for Claude Code's two late markers in a line that goes by a block at a time: where the
    /// copy of a tool's result it keeps for itself begins (the model never sees it — it is often
    /// as large again), and a compaction's summary. A marker can be split between two blocks.
    /// </summary>
    private sealed class Seam
    {
        private static ReadOnlySpan<byte> Duplicate => ",\"toolUseResult\":"u8;
        private static ReadOnlySpan<byte> SummaryMarker => "\"isCompactSummary\":true"u8;

        /// <summary>One byte less than the longest marker: enough of each side of a seam to find one across it.</summary>
        private const int Overlap = 22;

        private readonly byte[] _join = new byte[Overlap * 2];
        private int _carried;
        private long _seen;
        private bool _watch;

        /// <summary>Where the kept copy begins, from the start of the line; −1 when it was not found.</summary>
        public long Cut { get; private set; } = -1;

        public bool Summary { get; private set; }

        public void Reset(bool watch)
        {
            _watch = watch;
            _carried = 0;
            _seen = 0;
            Cut = -1;
            Summary = false;
        }

        public void Feed(ReadOnlySpan<byte> piece)
        {
            if (!_watch || piece.IsEmpty) return;
            if (_carried > 0)
            {
                var take = Math.Min(piece.Length, Overlap);
                piece[..take].CopyTo(_join.AsSpan(_carried));
                Look(_join.AsSpan(0, _carried + take), _seen - _carried);
            }
            Look(piece, _seen);
            _seen += piece.Length;
            if (piece.Length >= Overlap)
            {
                piece[^Overlap..].CopyTo(_join);
                _carried = Overlap;
            }
            else
            {
                // What was carried, then this short piece: the last bytes of the two.
                var all = _join.AsSpan(0, _carried + piece.Length);
                if (_carried > 0) piece.CopyTo(_join.AsSpan(_carried));
                else piece.CopyTo(_join);
                var keep = Math.Min(all.Length, Overlap);
                all[^keep..].CopyTo(_join);
                _carried = keep;
            }
        }

        private void Look(ReadOnlySpan<byte> bytes, long offset)
        {
            if (Cut < 0 && bytes.IndexOf(Duplicate) is var at and >= 0) Cut = offset + at;
            if (!Summary && bytes.IndexOf(SummaryMarker) >= 0) Summary = true;
        }
    }
}
