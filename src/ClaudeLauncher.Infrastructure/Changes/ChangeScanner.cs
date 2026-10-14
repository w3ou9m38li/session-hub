using System.Globalization;
using System.Text;
using System.Text.Json;
using ClaudeLauncher.Infrastructure.Backends;

namespace ClaudeLauncher.Infrastructure.Changes;

public enum ChangeKind { Added, Updated, Deleted }

/// <summary>
/// One line of a diff: <c>' '</c> unchanged, <c>'-'</c> removed, <c>'+'</c> added, <c>'@'</c> the
/// heading of a hunk, <c>'…'</c> a note that lines were left out. Line numbers when the record gave them.
/// </summary>
public sealed record DiffLine(char Kind, string Text, int? Old = null, int? New = null);

/// <summary>What one tool call did to one file, as the conversation recorded it.</summary>
/// <param name="Tool">The tool that did it: Edit, Write, MultiEdit, apply_patch…</param>
/// <param name="Succeeded">False when the tool reported that it failed; the change shown is what it tried.</param>
public sealed record FileChange(
    string Path, ChangeKind Kind, DateTimeOffset? At, string Tool, bool Succeeded,
    IReadOnlyList<DiffLine> Lines, int Added, int Removed, string? MovedTo = null);

/// <summary>
/// The files a conversation changed and how, read from its transcript alone — never from the
/// files themselves, which may have changed since. Claude records each Edit and Write with the
/// patch it made; Codex its apply_patch calls and, in newer versions, the unified diff each
/// applied. Only those lines are parsed.
/// </summary>
public static class ChangeScanner
{
    /// <summary>Lines kept per change; a file written whole can be thousands.</summary>
    public const int MaxLinesPerChange = 4000;

    /// <summary>Changes kept per conversation.</summary>
    public const int MaxChanges = 5000;

    /// <summary>Lines longer than this are only kept when their beginning says they may hold a change.</summary>
    private const int SkimBytes = 1024 * 1024;

    private const int MaxLineBytes = 64 * 1024 * 1024;

    private static readonly JsonDocumentOptions Json = new() { MaxDepth = 256 };

    private static readonly string[] ClaudeTools = ["Edit", "MultiEdit", "Write", "NotebookEdit"];

    private static readonly byte[][] ClaudeToolMarkers =
        [.. ClaudeTools.Select(name => Encoding.UTF8.GetBytes($"\"name\":\"{name}\""))];

    private static readonly byte[] ToolUseIdKey = "\"tool_use_id\":\""u8.ToArray();
    private static readonly byte[] BeginPatch = "*** Begin Patch"u8.ToArray();
    private static readonly byte[] PatchApplyEnd = "\"type\":\"patch_apply_end\""u8.ToArray();
    private static readonly byte[] TurnContext = "\"type\":\"turn_context\""u8.ToArray();
    private static readonly byte[] SessionMeta = "\"type\":\"session_meta\""u8.ToArray();
    private static readonly byte[][] CodexCallMarkers =
        [.. new[] { "custom_tool_call", "function_call" }.Select(t => Encoding.UTF8.GetBytes($"\"type\":\"{t}\""))];

    /// <param name="codex">A Codex rollout rather than a Claude transcript.</param>
    /// <param name="part">
    /// Which part is the conversation's own: a copy that began with another conversation's
    /// history made the copied changes in that other conversation, not in this one.
    /// </param>
    public static IReadOnlyList<FileChange> Read(string path, bool codex, CancellationToken cancellation = default,
        OwnPart part = default)
    {
        var own = part.Filter(path, 0);
        using var stream = TranscriptFiles.OpenSeekable(path, 1, FileOptions.SequentialScan);
        var reader = codex ? (LineReader)new CodexReader() : new ClaudeReader();
        using var line = new PooledBytes();
        var skipping = false;
        var buffer = PooledBytes.RentBlock();
        Span<byte> scratch = stackalloc byte[HeadBytes];
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
                        if (length <= SkimBytes || (length <= MaxLineBytes && reader.MayHoldChange(PooledBytes.Head(line.WrittenSpan, piece, HeadBytes, scratch))))
                            line.Write(piece);
                        else
                        {
                            skipping = true;
                            line.ResetWrittenCount();
                        }
                    }
                    if (newline < 0) break;
                    if (!skipping && line.WrittenCount > 0 && (own is null || own.Keep(line.WrittenSpan)))
                        reader.Line(line.WrittenMemory);
                    line.ResetWrittenCount();
                    skipping = false;
                    at += newline + 1;
                }
            }
        }
        finally
        {
            PooledBytes.Return(buffer);
        }
        return reader.Finish();
    }

    /// <summary>How much of a long line's beginning is looked at to tell whether it may hold a change.</summary>
    private const int HeadBytes = 16 * 1024;

    /// <summary>
    /// The transcripts that belong to a conversation besides its own, each with a label for the
    /// changes read from it: a Claude conversation's subagents, and the Codex threads a thread
    /// spawned — and those they spawned in turn. A fork is a conversation of its own, not one of these.
    /// </summary>
    public static List<(string Path, string Label)> Related(
        string transcriptPath, bool codex, CodexPaths? codexPaths, CancellationToken cancellation = default)
    {
        var related = new List<(string, string)>();
        if (!codex)
        {
            var subagents = Path.Combine(Path.GetDirectoryName(transcriptPath) ?? "",
                Path.GetFileNameWithoutExtension(transcriptPath), "subagents");
            foreach (var file in Files(subagents, "*.jsonl", recursive: false)) related.Add((file, "子代理"));
            return related;
        }

        const int IdLength = 36;
        var stem = Path.GetFileNameWithoutExtension(transcriptPath);
        if (codexPaths is null || stem.Length < "rollout-".Length + IdLength + 1) return related;
        var thread = stem[^IdLength..];
        var started = stem[..^(IdLength + 1)];          // rollout-2026-09-14T11-23-17
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { thread };
        foreach (var root in new[] { codexPaths.SessionsRoot, codexPaths.ArchivedSessionsRoot })
        {
            foreach (var file in Files(root, "rollout-*.jsonl", recursive: true))
            {
                cancellation.ThrowIfCancellationRequested();
                var name = Path.GetFileNameWithoutExtension(file);
                if (name.Length < "rollout-".Length + IdLength + 1 || found.Contains(name[^IdLength..])) continue;
                // Spawned after this thread began, as its name says: nothing earlier can be one of its own.
                if (string.CompareOrdinal(name[..^(IdLength + 1)], started) < 0) continue;
                if (ReadSessionMeta(file) is not { } meta || meta.Forked is not null) continue;
                if (string.Equals(meta.Parent, thread, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(meta.Session, thread, StringComparison.OrdinalIgnoreCase))
                {
                    found.Add(name[^IdLength..]);
                    related.Add((file, "子任务"));
                }
            }
        }
        return related;
    }

    private sealed record Meta(string? Parent, string? Session, string? Forked);

    /// <summary>A rollout's first line: which thread spawned it, the session it belongs to, what it was forked from.</summary>
    private static Meta? ReadSessionMeta(string path)
    {
        try
        {
            using var stream = TranscriptFiles.OpenRead(path);
            var buffer = new byte[64 * 1024];
            var length = 0;
            int end;
            while ((end = Array.IndexOf(buffer, (byte)'\n', 0, length)) < 0 && length < 2 * 1024 * 1024)
            {
                if (length == buffer.Length) Array.Resize(ref buffer, buffer.Length * 2);
                var got = stream.Read(buffer, length, buffer.Length - length);
                if (got == 0) break;
                length += got;
            }
            using var doc = JsonDocument.Parse(buffer.AsMemory(0, end < 0 ? length : end), Json);
            if (!doc.RootElement.TryGetProperty("payload", out var meta) || meta.ValueKind != JsonValueKind.Object) return null;
            var parent = Text(meta, "parent_thread_id");
            if (parent is null && meta.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.Object
                && source.TryGetProperty("subagent", out var subagent) && subagent.ValueKind == JsonValueKind.Object
                && subagent.TryGetProperty("thread_spawn", out var spawn) && spawn.ValueKind == JsonValueKind.Object)
                parent = Text(spawn, "parent_thread_id");
            var session = Text(meta, "session_id");
            return new Meta(parent, session == Text(meta, "id") ? null : session, Text(meta, "forked_from_id"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static List<string> Files(string directory, string pattern, bool recursive)
    {
        try
        {
            if (!Directory.Exists(directory)) return [];
            return Directory.EnumerateFiles(directory, pattern,
                new EnumerationOptions { RecurseSubdirectories = recursive, IgnoreInaccessible = true }).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private abstract class LineReader
    {
        protected readonly List<FileChange> Changes = [];

        /// <summary>Whether a long line, judged by its first kilobytes, is worth reading whole.</summary>
        public abstract bool MayHoldChange(ReadOnlySpan<byte> head);

        public abstract void Line(ReadOnlyMemory<byte> bytes);

        public abstract IReadOnlyList<FileChange> Finish();

        protected void Add(FileChange change)
        {
            if (Changes.Count < MaxChanges) Changes.Add(change);
        }
    }

    // ================================================================= Claude

    private sealed record Pending(string Tool, string Path, DateTimeOffset? At, JsonElement Input);

    private sealed class ClaudeReader : LineReader
    {
        // Calls waiting for their result, by tool_use id.
        private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);

        public override bool MayHoldChange(ReadOnlySpan<byte> head)
        {
            foreach (var marker in ClaudeToolMarkers)
                if (head.IndexOf(marker) >= 0) return true;
            // The result of a call waiting for one: its id comes early, the file it carries after.
            return AnswersPending(head);
        }

        /// <summary>Whether a line holds the result of a call waiting for one — checked on the bytes, before any parsing.</summary>
        private bool AnswersPending(ReadOnlySpan<byte> span)
        {
            if (_pending.Count == 0) return false;
            while (true)
            {
                var at = span.IndexOf(ToolUseIdKey);
                if (at < 0) return false;
                span = span[(at + ToolUseIdKey.Length)..];
                var end = span.IndexOf((byte)'"');
                if (end > 0 && _pending.ContainsKey(Encoding.UTF8.GetString(span[..end]))) return true;
            }
        }

        public override void Line(ReadOnlyMemory<byte> bytes)
        {
            var span = bytes.Span;
            var call = false;
            foreach (var marker in ClaudeToolMarkers) call |= span.IndexOf(marker) >= 0;
            if (!call && !AnswersPending(span)) return;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(bytes, Json); }
            catch (JsonException) { return; }

            using var _ = doc;
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object
                && message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                var at = Time(root);
                foreach (var block in content.EnumerateArray())
                {
                    if (block.ValueKind != JsonValueKind.Object) continue;
                    var type = Text(block, "type");
                    if (type == "tool_use" && Text(block, "name") is { } name && ClaudeTools.Contains(name)
                        && Text(block, "id") is { } id && block.TryGetProperty("input", out var input)
                        && input.ValueKind == JsonValueKind.Object)
                    {
                        var file = Text(input, "file_path") ?? Text(input, "notebook_path");
                        if (file is null) continue;
                        // Read when the result comes; the line's bytes will be gone by then.
                        _pending[id] = new Pending(name, file, at, input.Clone());
                    }
                    else if (type == "tool_result" && Text(block, "tool_use_id") is { } done
                             && _pending.Remove(done, out var pending))
                    {
                        var failed = block.TryGetProperty("is_error", out var error) && error.ValueKind == JsonValueKind.True;
                        var recorded = root.TryGetProperty("toolUseResult", out var r) && r.ValueKind == JsonValueKind.Object
                            ? r : (JsonElement?)null;
                        Add(Build(pending, failed, recorded));
                    }
                }
            }
        }

        public override IReadOnlyList<FileChange> Finish()
        {
            // Calls whose result was never written: shown from what they asked for.
            foreach (var pending in _pending.Values) Add(Build(pending, failed: false, recorded: null));
            _pending.Clear();
            return Changes.OrderBy(c => c.At ?? DateTimeOffset.MaxValue).ToList();
        }

        private static FileChange Build(Pending call, bool failed, JsonElement? recorded)
        {
            var path = recorded is { } r && Text(r, "filePath") is { Length: > 0 } p ? p : call.Path;
            var created = recorded is { } rc && Text(rc, "type") == "create";
            // What the tool itself recorded: hunks with line numbers.
            if (!failed && recorded is { } result && result.TryGetProperty("structuredPatch", out var patch)
                && patch.ValueKind == JsonValueKind.Array && patch.GetArrayLength() > 0)
                return MakeChange(path, created ? ChangeKind.Added : ChangeKind.Updated, call.At, call.Tool, true, Hunks(patch));

            // Otherwise the change as asked for.
            var input = call.Input;
            var lines = new List<DiffLine>();
            switch (call.Tool)
            {
                case "Write":
                    foreach (var text in SplitLines(Text(input, "content") ?? "")) lines.Add(new DiffLine('+', text));
                    return MakeChange(path, created ? ChangeKind.Added : ChangeKind.Updated, call.At, call.Tool, !failed, lines);
                case "Edit":
                    lines.AddRange(LineDiff.Compare(Text(input, "old_string") ?? "", Text(input, "new_string") ?? ""));
                    break;
                case "MultiEdit":
                    if (input.TryGetProperty("edits", out var edits) && edits.ValueKind == JsonValueKind.Array)
                        foreach (var edit in edits.EnumerateArray())
                        {
                            if (edit.ValueKind != JsonValueKind.Object) continue;
                            if (lines.Count > 0) lines.Add(new DiffLine('@', "…"));
                            lines.AddRange(LineDiff.Compare(Text(edit, "old_string") ?? "", Text(edit, "new_string") ?? ""));
                        }
                    break;
                case "NotebookEdit":
                    foreach (var text in SplitLines(Text(input, "new_source") ?? "")) lines.Add(new DiffLine('+', text));
                    break;
            }
            return MakeChange(path, ChangeKind.Updated, call.At, call.Tool, !failed, lines);
        }

        /// <summary>Claude's structuredPatch: hunks of prefixed lines, numbered from each hunk's start.</summary>
        private static List<DiffLine> Hunks(JsonElement patch)
        {
            var lines = new List<DiffLine>();
            foreach (var hunk in patch.EnumerateArray())
            {
                if (hunk.ValueKind != JsonValueKind.Object || !hunk.TryGetProperty("lines", out var body)
                    || body.ValueKind != JsonValueKind.Array)
                    continue;
                int oldAt = Number(hunk, "oldStart"), newAt = Number(hunk, "newStart");
                lines.Add(new DiffLine('@',
                    $"@@ -{oldAt},{Number(hunk, "oldLines")} +{newAt},{Number(hunk, "newLines")} @@"));
                foreach (var item in body.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String) continue;
                    var text = item.GetString() ?? "";
                    Numbered(lines, text, ref oldAt, ref newAt);
                }
            }
            return lines;
        }
    }

    // ================================================================== Codex

    private sealed class CodexCall
    {
        public required string? Id;
        public required DateTimeOffset? At;
        public required string Tool;
        public List<string> Patches = [];
        public string? Cwd;
        public List<FileChange>? Applied;
        public bool? Success;
    }

    private sealed class CodexReader : LineReader
    {
        private readonly List<CodexCall> _calls = [];
        private readonly Dictionary<string, CodexCall> _byId = new(StringComparer.Ordinal);
        private string? _cwd;

        public override bool MayHoldChange(ReadOnlySpan<byte> head)
        {
            if (head.IndexOf(PatchApplyEnd) >= 0) return true;
            foreach (var marker in CodexCallMarkers)
                if (head.IndexOf(marker) >= 0) return true;
            return false;
        }

        public override void Line(ReadOnlyMemory<byte> bytes)
        {
            var span = bytes.Span;
            var isEnd = span.IndexOf(PatchApplyEnd) >= 0;
            var isPatch = !isEnd && span.IndexOf(BeginPatch) >= 0;
            var isPlace = !isEnd && !isPatch && (span.IndexOf(TurnContext) >= 0 || span.IndexOf(SessionMeta) >= 0);
            if (!isEnd && !isPatch && !isPlace) return;
            try
            {
                using var doc = JsonDocument.Parse(bytes, Json);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("payload", out var payload)
                    || payload.ValueKind != JsonValueKind.Object)
                    return;
                if (isPlace)
                {
                    if (Text(payload, "cwd") is { Length: > 0 } cwd) _cwd = cwd;
                    return;
                }
                var at = Time(root);
                if (isEnd) Ended(payload, at);
                else Called(payload, at);
            }
            catch (JsonException)
            {
            }
        }

        private void Called(JsonElement payload, DateTimeOffset? at)
        {
            var type = Text(payload, "type");
            string? body = type switch
            {
                "custom_tool_call" => Text(payload, "input"),
                "function_call" => Text(payload, "arguments"),
                _ => null,
            };
            if (body is null) return;
            var call = new CodexCall
            {
                Id = Text(payload, "call_id"),
                At = at,
                Tool = Text(payload, "name") ?? "apply_patch",
                Cwd = _cwd,
            };
            // A function call's arguments are JSON; its strings are where a patch would be.
            var texts = type == "function_call" ? Strings(body) : [body];
            foreach (var text in texts)
                call.Patches.AddRange(PatchText.Find(text));
            if (call.Patches.Count == 0) return;
            if (call.Id is not null && _byId.TryGetValue(call.Id, out var ended))
            {
                ended.Patches = call.Patches;       // the end came first: keep it, with the text
                return;
            }
            _calls.Add(call);
            if (call.Id is not null) _byId[call.Id] = call;
        }

        /// <summary>What Codex applied: per file, the unified diff (or the whole content added or deleted).</summary>
        private void Ended(JsonElement payload, DateTimeOffset? at)
        {
            var id = Text(payload, "call_id");
            if (id is null || !_byId.TryGetValue(id, out var call))
            {
                call = new CodexCall { Id = id, At = at, Tool = "apply_patch", Cwd = _cwd };
                _calls.Add(call);
                if (id is not null) _byId[id] = call;
            }
            call.Success = !payload.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.False;
            if (!payload.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Object) return;
            var applied = new List<FileChange>();
            foreach (var file in changes.EnumerateObject())
            {
                var change = file.Value;
                if (change.ValueKind != JsonValueKind.Object) continue;
                var kind = Text(change, "type");
                if (kind is "update" or "modify")
                {
                    var moved = Text(change, "move_path");
                    applied.Add(MakeChange(file.Name, ChangeKind.Updated, call.At ?? at, call.Tool, call.Success.Value,
                        UnifiedDiff(Text(change, "unified_diff") ?? ""), moved));
                }
                else if (kind is "add" or "delete")
                {
                    var sign = kind == "add" ? '+' : '-';
                    applied.Add(MakeChange(file.Name, kind == "add" ? ChangeKind.Added : ChangeKind.Deleted, call.At ?? at, call.Tool,
                        call.Success.Value, [.. SplitLines(Text(change, "content") ?? "").Select(l => new DiffLine(sign, l))]));
                }
            }
            if (applied.Count > 0) call.Applied = applied;
        }

        public override IReadOnlyList<FileChange> Finish()
        {
            foreach (var call in _calls)
            {
                if (call.Applied is { } applied)
                {
                    foreach (var change in applied) Add(change);
                    continue;
                }
                foreach (var patch in call.Patches)
                    foreach (var change in PatchText.Parse(patch, call.At, call.Tool, call.Success ?? true, call.Cwd))
                        Add(change);
            }
            return Changes;
        }

        /// <summary>Every string in a JSON document, however deep.</summary>
        private static List<string> Strings(string json)
        {
            var found = new List<string>();
            try
            {
                using var doc = JsonDocument.Parse(json, Json);
                Collect(doc.RootElement);
            }
            catch (JsonException)
            {
                found.Add(json);
            }
            return found;

            void Collect(JsonElement element)
            {
                switch (element.ValueKind)
                {
                    case JsonValueKind.String:
                        found.Add(element.GetString() ?? "");
                        break;
                    case JsonValueKind.Array:
                        foreach (var item in element.EnumerateArray()) Collect(item);
                        break;
                    case JsonValueKind.Object:
                        foreach (var property in element.EnumerateObject()) Collect(property.Value);
                        break;
                }
            }
        }
    }

    /// <summary>A unified diff's hunks: <c>@@ -a,b +c,d @@</c> headings, then prefixed lines.</summary>
    internal static List<DiffLine> UnifiedDiff(string diff)
    {
        var lines = new List<DiffLine>();
        int oldAt = 0, newAt = 0;
        var inHunk = false;
        foreach (var text in SplitLines(diff))
        {
            if (text.StartsWith("@@", StringComparison.Ordinal))
            {
                (oldAt, newAt) = HunkStart(text);
                lines.Add(new DiffLine('@', text));
                inHunk = true;
                continue;
            }
            // File headers (---, +++, diff, index) come before the first hunk; inside one, a line
            // "--- x" is a removed "-- x" and "+++i" an added "++i".
            if (!inHunk) continue;
            Numbered(lines, text, ref oldAt, ref newAt);
        }
        return lines;
    }

    private static (int Old, int New) HunkStart(string heading)
    {
        // "@@ -509,9 +509,9 @@ optional context"
        int Start(char sign)
        {
            var at = heading.IndexOf(sign, 2);
            if (at < 0) return 0;
            var end = at + 1;
            while (end < heading.Length && char.IsAsciiDigit(heading[end])) end++;
            return int.TryParse(heading.AsSpan(at + 1, end - at - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
        }
        return (Start('-'), Start('+'));
    }

    /// <summary>A prefixed line of a hunk, numbered on the side(s) it belongs to.</summary>
    private static void Numbered(List<DiffLine> lines, string text, ref int oldAt, ref int newAt)
    {
        if (text.Length == 0)
        {
            lines.Add(new DiffLine(' ', "", oldAt > 0 ? oldAt++ : null, newAt > 0 ? newAt++ : null));
            return;
        }
        switch (text[0])
        {
            case '+':
                lines.Add(new DiffLine('+', text[1..], null, newAt > 0 ? newAt++ : null));
                break;
            case '-':
                lines.Add(new DiffLine('-', text[1..], oldAt > 0 ? oldAt++ : null, null));
                break;
            case '\\':
                lines.Add(new DiffLine('…', text.TrimStart('\\', ' ')));
                break;
            default:
                lines.Add(new DiffLine(' ', text[0] == ' ' ? text[1..] : text, oldAt > 0 ? oldAt++ : null, newAt > 0 ? newAt++ : null));
                break;
        }
    }

    /// <summary>Counts the lines, then keeps the first ones: a file written whole can be long.</summary>
    private static FileChange MakeChange(string path, ChangeKind kind, DateTimeOffset? at, string tool, bool succeeded,
        List<DiffLine> lines, string? movedTo = null)
    {
        var added = lines.Count(l => l.Kind == '+');
        var removed = lines.Count(l => l.Kind == '-');
        if (lines.Count > MaxLinesPerChange)
        {
            var more = lines.Count - MaxLinesPerChange;
            lines = lines.GetRange(0, MaxLinesPerChange);
            lines.Add(new DiffLine('…', $"（还有 {more:N0} 行没有显示）"));
        }
        return new FileChange(path, kind, at, tool, succeeded, lines, added, removed, movedTo);
    }

    internal static IEnumerable<string> SplitLines(string text)
    {
        if (text.Length == 0) yield break;
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var count = lines.Length;
        if (count > 1 && lines[^1].Length == 0) count--;       // a final newline ends the last line
        for (var i = 0; i < count; i++) yield return lines[i];
    }

    private static string? Text(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int Number(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) ? n : 0;

    private static DateTimeOffset? Time(JsonElement root) =>
        Text(root, "timestamp") is { } stamp && DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var at) ? at : null;

    // ============================================================ patch text

    /// <summary>
    /// Codex's own patch format — <c>*** Begin Patch</c>, a section per file, <c>*** End Patch</c> —
    /// found as written, or inside a script's string literal.
    /// </summary>
    internal static class PatchText
    {
        private const string Begin = "*** Begin Patch";
        private const string End = "*** End Patch";

        /// <summary>Each patch in a text: raw, or the contents of the string literal it starts.</summary>
        public static List<string> Find(string text)
        {
            var found = new List<string>();
            var from = 0;
            while ((from = text.IndexOf(Begin, from, StringComparison.Ordinal)) >= 0)
            {
                var quote = from > 0 ? text[from - 1] : '\0';
                if (quote is '"' or '\'' or '`')
                {
                    var (patch, next) = Literal(text, from - 1);
                    found.Add(patch);
                    from = Math.Max(next, from + Begin.Length);
                }
                else
                {
                    var end = text.IndexOf(End, from, StringComparison.Ordinal);
                    var stop = end < 0 ? text.Length : end + End.Length;
                    found.Add(text[from..stop]);
                    from = stop;
                }
            }
            return found;
        }

        /// <summary>A JavaScript string literal from its opening quote: its text, and where it ended.</summary>
        private static (string Text, int Next) Literal(string text, int open)
        {
            var quote = text[open];
            var sb = new StringBuilder();
            var i = open + 1;
            while (i < text.Length)
            {
                var c = text[i];
                if (c == quote) return (sb.ToString(), i + 1);
                if (c != '\\' || i + 1 >= text.Length)
                {
                    sb.Append(c);
                    i++;
                    continue;
                }
                var e = text[i + 1];
                i += 2;
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case '0': sb.Append('\0'); break;
                    case 'u' when i + 4 <= text.Length
                                  && int.TryParse(text.AsSpan(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var u):
                        sb.Append((char)u);
                        i += 4;
                        break;
                    case 'x' when i + 2 <= text.Length
                                  && int.TryParse(text.AsSpan(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var x):
                        sb.Append((char)x);
                        i += 2;
                        break;
                    case '\n': break;                        // a continued line
                    default: sb.Append(e); break;            // \\ \" \' \` and the rest stand for themselves
                }
            }
            return (sb.ToString(), text.Length);
        }

        /// <summary>The file sections of a patch. Paths without a drive or root are taken from <paramref name="cwd"/>.</summary>
        public static List<FileChange> Parse(string patch, DateTimeOffset? at, string tool, bool succeeded, string? cwd)
        {
            var changes = new List<FileChange>();
            string? path = null, movedTo = null;
            var kind = ChangeKind.Updated;
            var lines = new List<DiffLine>();

            void Close()
            {
                if (path is not null) changes.Add(MakeChange(Rooted(path, cwd), kind, at, tool, succeeded, lines,
                    movedTo is null ? null : Rooted(movedTo, cwd)));
                path = movedTo = null;
                lines = [];
            }

            foreach (var text in SplitLines(patch))
            {
                if (text.StartsWith("*** ", StringComparison.Ordinal))
                {
                    if (Header(text, "*** Add File: ") is { } added) { Close(); (path, kind) = (added, ChangeKind.Added); }
                    else if (Header(text, "*** Update File: ") is { } updated) { Close(); (path, kind) = (updated, ChangeKind.Updated); }
                    else if (Header(text, "*** Delete File: ") is { } deleted) { Close(); (path, kind) = (deleted, ChangeKind.Deleted); }
                    else if (Header(text, "*** Move to: ") is { } moved) movedTo = moved;
                    else if (text.StartsWith(End, StringComparison.Ordinal)) Close();
                    continue;                                // Begin Patch, End of File
                }
                if (path is null) continue;
                if (text.StartsWith("@@", StringComparison.Ordinal))
                {
                    lines.Add(new DiffLine('@', text));
                    continue;
                }
                if (text.Length == 0) { lines.Add(new DiffLine(' ', "")); continue; }
                lines.Add(text[0] switch
                {
                    '+' => new DiffLine('+', text[1..]),
                    '-' => new DiffLine('-', text[1..]),
                    ' ' => new DiffLine(' ', text[1..]),
                    _ => new DiffLine(' ', text),
                });
            }
            Close();
            return changes;
        }

        private static string? Header(string text, string prefix) =>
            text.StartsWith(prefix, StringComparison.Ordinal) ? text[prefix.Length..].Trim() : null;

        private static string Rooted(string path, string? cwd)
        {
            if (string.IsNullOrEmpty(cwd) || Path.IsPathRooted(path)) return path;
            try { return Path.GetFullPath(Path.Combine(cwd, path)); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
        }
    }
}

/// <summary>A line-by-line comparison of two texts, for an edit recorded only as its before and after.</summary>
internal static class LineDiff
{
    /// <summary>Beyond this many line pairs the texts are shown as removed, then added, without matching.</summary>
    private const long MaxCells = 4_000_000;

    public static List<DiffLine> Compare(string before, string after)
    {
        var a = ChangeScanner.SplitLines(before).ToArray();
        var b = ChangeScanner.SplitLines(after).ToArray();
        var lines = new List<DiffLine>();

        // Common beginning and end first: most edits change a few lines in the middle.
        var start = 0;
        while (start < a.Length && start < b.Length && a[start] == b[start]) start++;
        int endA = a.Length, endB = b.Length;
        while (endA > start && endB > start && a[endA - 1] == b[endB - 1]) { endA--; endB--; }

        for (var i = 0; i < start; i++) lines.Add(new DiffLine(' ', a[i]));
        var n = endA - start;
        var m = endB - start;
        if ((long)n * m > MaxCells)
        {
            for (var i = start; i < endA; i++) lines.Add(new DiffLine('-', a[i]));
            for (var j = start; j < endB; j++) lines.Add(new DiffLine('+', b[j]));
        }
        else
        {
            // Longest common subsequence over the middle.
            var lcs = new int[n + 1, m + 1];
            for (var i = n - 1; i >= 0; i--)
                for (var j = m - 1; j >= 0; j--)
                    lcs[i, j] = a[start + i] == b[start + j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            int x = 0, y = 0;
            while (x < n || y < m)
            {
                if (x < n && y < m && a[start + x] == b[start + y]) { lines.Add(new DiffLine(' ', a[start + x])); x++; y++; }
                // Removed before added, as diffs are read.
                else if (x < n && (y == m || lcs[x + 1, y] >= lcs[x, y + 1])) { lines.Add(new DiffLine('-', a[start + x])); x++; }
                else { lines.Add(new DiffLine('+', b[start + y])); y++; }
            }
        }
        for (var i = endA; i < a.Length; i++) lines.Add(new DiffLine(' ', a[i]));
        return lines;
    }
}
