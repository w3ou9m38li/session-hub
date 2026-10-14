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
public sealed record UsageScan(IReadOnlyList<UsageCall> Calls, long NextOffset, string? State,
    string? Thread = null, string? Root = null);

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
        Func<string, string?>? rootOf = null, CancellationToken cancellation = default)
    {
        if (app == AppFamily.Gemini) return ScanGemini(path, cancellation);
        if (app == AppFamily.DeepSeek) return ScanDsh(path, cancellation);

        using var stream = TranscriptFiles.OpenSeekable(path, 1, FileOptions.SequentialScan);
        if (offset < 0 || offset > stream.Length)
        {
            offset = 0;
            state = null;
        }
        stream.Position = offset;

        var codex = app == AppFamily.Codex ? CodexState.Parse(state) : null;
        var calls = new List<UsageCall>();
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
                        var worth = length <= Glance || MayCarryUsage(PooledBytes.Head(line.WrittenSpan, piece, Glance, scratch), app);
                        if (worth && (length <= MaxLineBytes || (length <= MaxAssistantLineBytes && app == AppFamily.Claude)))
                            line.Write(piece);
                        else
                        {
                            skipping = true;                // passed over, never held
                            line.ResetWrittenCount();
                        }
                    }
                    if (newline < 0) break;                 // the line goes on in the next block

                    if (!skipping && line.WrittenCount > 0)
                    {
                        if (codex is null) ReadClaude(line.WrittenMemory, calls);
                        else ReadCodex(line.WrittenMemory, codex, calls, rootOf);
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
        return new UsageScan(calls, lineStart, codex?.ToString(), codex?.Thread, codex?.Root);
    }

    /// <summary>How much of a line is looked at before deciding whether to read the rest.</summary>
    private const int Glance = 16 * 1024;

    /// <summary>
    /// Whether a line's head says it can carry usage: a Claude assistant message; a Codex token
    /// count, or the turn context and session header that say whose tokens they are.
    /// </summary>
    private static bool MayCarryUsage(ReadOnlySpan<byte> head, AppFamily app) =>
        app == AppFamily.Claude
            ? head.IndexOf(AssistantRole) >= 0
            : head.IndexOf(TokenCountType) >= 0 || head.IndexOf(TurnContextType) >= 0 || head.IndexOf(SessionMetaType) >= 0;

    // ------------------------------------------------------------------ Claude

    /// <summary>
    /// An assistant message's usage. Claude Code writes one line per content block of a
    /// response, each with the same message id and request id: the ids make the key. The usage
    /// on those lines can grow from one to the next — the first may carry only part of the
    /// output — so the ledger keeps the largest figures any of them gave.
    /// </summary>
    private static void ReadClaude(ReadOnlyMemory<byte> bytes, List<UsageCall> calls)
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
            if (input + write + cached + output == 0 || !TryTime(root, out var at)) return;

            var id = Text(message, "id");
            var request = Text(root, "requestId");
            var key = id is null && request is null
                ? $"c|{at.UtcTicks}|{input}|{write}|{cached}|{output}"
                : $"c|{id}|{request}";
            calls.Add(new UsageCall(Hash(key), at, model, input, write, cached, output));
        }
        catch (JsonException)
        {
            // A damaged line: its call is not counted.
        }
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
        Func<string, string?>? rootOf)
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
            // a compaction leaves.
            if (total is { } now && state.Total is { } previous && now == previous) return;
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
    private static UsageScan ScanGemini(string path, CancellationToken cancellation)
    {
        var calls = GeminiSession.ReadCalls(path, cancellation)
            .Select(c => new UsageCall(Hash("gemini:" + c.Id), c.When, c.Model.Length > 0 ? c.Model : "unknown",
                Input: Math.Max(0, c.Input - c.Cached) + c.Tool, CacheWrite: 0, CacheRead: c.Cached, Output: c.Output + c.Thoughts))
            .ToList();
        return new UsageScan(calls, TranscriptFiles.Stat(path)?.Length ?? 0, null);
    }

    /// <summary>
    /// A DeepSeek Harness session, read whole. Each model message records its tokens, disjoint:
    /// <c>inputTokens</c> is the prompt not served from the cache, the cache's share is apart, and
    /// the reasoning is already in <c>outputTokens</c>. A message copied into another session
    /// keeps its id, so it counts once.
    /// </summary>
    private static UsageScan ScanDsh(string path, CancellationToken cancellation)
    {
        var calls = DshSession.ReadCalls(path, cancellation)
            .Select(c => new UsageCall(Hash("dsh:" + c.Id), c.When, c.Model.Length > 0 ? c.Model : "unknown",
                Input: c.Input, CacheWrite: c.CacheWrite, CacheRead: c.CacheRead, Output: c.Output))
            .ToList();
        return new UsageScan(calls, TranscriptFiles.Stat(path)?.Length ?? 0, null);
    }

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
}
