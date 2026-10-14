using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ClaudeLauncher.Infrastructure.Backends;

/// <summary>
/// Where DeepSeek Harness (the <c>dsh</c> agent) keeps its sessions: under its home — the folder
/// <c>DSH_HOME</c> names, else <c>~\.dsh</c> — as <c>sessions\&lt;workspace&gt;\session-&lt;id&gt;\session.vN.jsonl.zstd</c>.
/// A deployment that sets <c>DSH_HOME</c> only in its own start script cannot be found from
/// here, so the homes the user adds are listed as well. Only the session logs are ever read: a
/// home also holds the model credentials, which are never opened.
/// </summary>
public sealed record DshPaths(IReadOnlyList<string> Homes)
{
    public const string HomeVariable = "DSH_HOME";

    public static DshPaths None { get; } = new([]);

    /// <summary>
    /// The homes on this computer: the folders the user added, the one <c>DSH_HOME</c> names (for
    /// this process, the user or the machine) and <c>~\.dsh</c> — each only when it holds sessions.
    /// </summary>
    public static DshPaths Discover(IEnumerable<string>? added = null)
    {
        var candidates = new List<string>();
        if (added is not null) candidates.AddRange(added);
        foreach (var target in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
        {
            try
            {
                if (Environment.GetEnvironmentVariable(HomeVariable, target) is { } named && !string.IsNullOrWhiteSpace(named))
                    candidates.Add(named);
            }
            catch (System.Security.SecurityException) { }
        }
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh"));

        var homes = new List<string>();
        foreach (var candidate in candidates)
            if (HomeOf(candidate) is { } home && !homes.Contains(home, StringComparer.OrdinalIgnoreCase)) homes.Add(home);
        return new DshPaths(homes);
    }

    /// <summary>
    /// The home a folder is or holds: the folder itself when it has a <c>sessions</c> folder, or its
    /// <c>data</c> folder when that has one (a local deployment keeps its home there). Null otherwise.
    /// </summary>
    public static string? HomeOf(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;
        try
        {
            var text = folder.Trim().Trim('"');
            if (text == "~" || text.StartsWith("~/", StringComparison.Ordinal) || text.StartsWith("~\\", StringComparison.Ordinal))
                text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), text.Length > 2 ? text[2..] : "");
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(text));
            if (Directory.Exists(Path.Combine(full, "sessions"))) return full;
            var data = Path.Combine(full, "data");
            return Directory.Exists(Path.Combine(data, "sessions")) ? data : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    public bool Exists => Homes.Count > 0;

    /// <summary>
    /// The sessions dsh's own list has archived, from the home's workspace registry
    /// (<c>storages\workspace.json</c>, <c>global.archivedSessionIds</c>); null when it cannot be read.
    /// </summary>
    public static HashSet<string>? ArchivedIn(string home)
    {
        var file = Path.Combine(home, "storages", "workspace.json");
        try
        {
            if (!File.Exists(file)) return [];
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            var archived = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("global", out var global) && global.ValueKind == JsonValueKind.Object
                && global.TryGetProperty("archivedSessionIds", out var ids) && ids.ValueKind == JsonValueKind.Array)
                foreach (var id in ids.EnumerateArray())
                    if (id.ValueKind == JsonValueKind.String && id.GetString() is { Length: > 0 } value) archived.Add(value);
            return archived;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Every session's log — the newest generation in its folder — with the home it is in.</summary>
    public IEnumerable<(string File, string Home)> SessionFiles()
    {
        foreach (var home in Homes)
        {
            List<string> workspaces;
            try { workspaces = Directory.EnumerateDirectories(Path.Combine(home, "sessions")).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var workspace in workspaces)
            {
                List<string> sessions;
                try { sessions = Directory.EnumerateDirectories(workspace, "session-*").ToList(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                foreach (var session in sessions)
                    if (LogOf(session) is { } log) yield return (log, home);
            }
        }
    }

    /// <summary>
    /// The log of a session folder: <c>session.jsonl</c> for the first format, <c>session.vN.jsonl</c>
    /// after it, either one compressed (<c>.zstd</c>). An older generation is left behind when dsh
    /// upgrades a session, so the highest one is the conversation as it stands.
    /// </summary>
    public static string? LogOf(string sessionFolder)
    {
        string? best = null;
        var bestGeneration = -1;
        var bestWritten = DateTime.MinValue;
        try
        {
            foreach (var file in new DirectoryInfo(sessionFolder).EnumerateFiles("session*.jsonl*"))
            {
                if (DshSession.GenerationOf(file.Name) is not { } generation) continue;
                if (generation < bestGeneration || (generation == bestGeneration && file.LastWriteTimeUtc <= bestWritten)) continue;
                (best, bestGeneration, bestWritten) = (file.FullName, generation, file.LastWriteTimeUtc);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return best;
    }
}

/// <summary>What the catalog needs from one session log: its id, times, folder and words to call it by.</summary>
/// <param name="DelegationDepth">0 for a conversation the user started; more for a subagent's.</param>
public sealed record DshSessionHeader(
    string SessionId, DateTimeOffset Created, DateTimeOffset LastActivity, string? Title, string? FirstUserWords,
    string? Cwd, int DelegationDepth, int Messages);

/// <summary>One model call a session recorded: its message id names it, so a copy in another session counts once.</summary>
public sealed record DshCall(string Id, DateTimeOffset When, string Model, long Input, long CacheRead, long CacheWrite, long Output);

/// <summary>
/// A DeepSeek Harness session log. One event per line — <c>{type, seq, time, data}</c> — and the
/// lines compressed a few at a time: the file is a run of Zstandard frames, one per append, so
/// it only grows. It is small and its events refer back to each other (a tool's result to its
/// call), so it is always read whole.
///
/// <para>The first line is the session itself: <c>{type:"session", id, createdAt, cwd, delegationDepth}</c>.
/// After it: <c>user/message</c> — what the user typed (<c>source.kind</c> "user"), or context the
/// harness put in (runtime context, the skill catalog), which is folded; <c>assistant/message</c>,
/// whose content is text, reasoning and tool calls, with the call's <c>usage</c>;
/// <c>tool/result</c>; <c>session/title</c>, the latest of which names the session; and
/// bookkeeping (turns, steps, requests) that is not shown.</para>
///
/// <para>Token counts are disjoint, as dsh documents them: <c>inputTokens</c> is the prompt that
/// did not come from the cache, <c>cacheReadTokens</c> and <c>cacheWriteTokens</c> the rest of it,
/// and <c>reasoningTokens</c> part of <c>outputTokens</c>.</para>
/// </summary>
public static class DshSession
{
    /// <summary>The name this tool gives its own copy of a session log (unpacked).</summary>
    public const string CopySuffix = ".dsh.jsonl";

    /// <summary>Largest log read: far beyond any session, small enough to hold in memory.</summary>
    private const long MaxTextBytes = 512L * 1024 * 1024;

    private static readonly JsonDocumentOptions Json = new() { MaxDepth = 256 };

    /// <summary>
    /// The format generation a log's file name carries: 0 for <c>session.jsonl</c>, N for
    /// <c>session.vN.jsonl</c>, either one optionally <c>.zstd</c>; null for any other name.
    /// </summary>
    public static int? GenerationOf(string name)
    {
        var stem = name.EndsWith(".zstd", StringComparison.OrdinalIgnoreCase) ? name[..^5] : name;
        if (!stem.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) return null;
        stem = stem[..^6];
        if (stem == "session") return 0;
        if (stem.Length > 9 && stem.StartsWith("session.v", StringComparison.Ordinal) && stem[9] != '0'
            && int.TryParse(stem.AsSpan(9), NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            return n;
        return null;
    }

    /// <summary>Whether a transcript path is a dsh session log, or this tool's copy of one.</summary>
    public static bool IsSessionFile(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        if (path.EndsWith(CopySuffix, StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(CopySuffix + PackedCopy.Suffix, StringComparison.OrdinalIgnoreCase)) return true;
        var name = Path.GetFileName(path);
        return name.StartsWith("session", StringComparison.Ordinal) && GenerationOf(name) is not null;
    }

    /// <summary>Whether a session log is stored compressed: read through a decompressor, copied unpacked.</summary>
    public static bool IsCompressed(string? path) =>
        path is not null && path.EndsWith(".zstd", StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ reading

    /// <summary>
    /// The complete lines of a log, unpacked. dsh appends a frame at a time, so the last one can
    /// be caught half written: everything before it stands, and the next read finds it whole.
    /// </summary>
    internal static List<string> ReadLines(string path, CancellationToken cancellation = default)
    {
        // A dsh log (.zstd), this tool's packed copy of one, or a plain one.
        using var input = TranscriptFiles.OpenRead(path);
        var bytes = new MemoryStream();
        try
        {
            var buffer = new byte[256 * 1024];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellation.ThrowIfCancellationRequested();
                bytes.Write(buffer, 0, read);
                if (bytes.Length > MaxTextBytes) break;
            }
        }
        catch (Exception ex) when (ex is ZstdSharp.ZstdException or InvalidDataException or EndOfStreamException)
        {
            // A frame still being written: the lines before it are complete.
        }

        var data = bytes.GetBuffer().AsSpan(0, (int)Math.Min(bytes.Length, int.MaxValue));
        var end = data.LastIndexOf((byte)'\n');
        var lines = new List<string>();
        if (end < 0) return lines;
        foreach (var line in Encoding.UTF8.GetString(data[..end]).Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.Length > 0) lines.Add(trimmed);
        }
        return lines;
    }

    /// <summary>The <c>type</c> of an event line, read off its start where dsh puts it; parsed only when it is not there.</summary>
    internal static string? TypeOf(string line)
    {
        const string Prefix = "{\"type\":\"";
        if (line.StartsWith(Prefix, StringComparison.Ordinal))
        {
            var end = line.IndexOf('"', Prefix.Length);
            if (end > Prefix.Length && end - Prefix.Length < 64) return line[Prefix.Length..end];
        }
        try
        {
            using var doc = JsonDocument.Parse(line, Json);
            return Text(doc.RootElement, "type");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>An event's time (milliseconds since 1970), read off its start without parsing it; null when it is not there.</summary>
    internal static long? TimeOf(string line)
    {
        const string Key = "\"time\":";
        var at = line.IndexOf(Key, 0, Math.Min(line.Length, 160), StringComparison.Ordinal);
        if (at < 0) return null;
        var start = at + Key.Length;
        var end = start;
        while (end < line.Length && end - start < 16 && char.IsAsciiDigit(line[end])) end++;
        return end > start && long.TryParse(line.AsSpan(start, end - start), NumberStyles.None, CultureInfo.InvariantCulture, out var ms)
            ? ms
            : null;
    }

    private static JsonDocument? Parse(string line)
    {
        try
        {
            var doc = JsonDocument.Parse(line, Json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object) return doc;
            doc.Dispose();
        }
        catch (JsonException) { }
        return null;
    }

    /// <summary>The header of a session; null when the log does not start as one (no session line, no id).</summary>
    public static DshSessionHeader? ReadHeader(string path, CancellationToken cancellation = default)
    {
        var lines = ReadLines(path, cancellation);
        if (lines.Count == 0) return null;

        string? id = null, cwd = null, title = null, first = null;
        var depth = 0;
        long created = 0, last = 0;
        var messages = 0;
        foreach (var line in lines)
        {
            cancellation.ThrowIfCancellationRequested();
            if (TimeOf(line) is { } time && time > last) last = time;
            switch (TypeOf(line))
            {
                case "session" when id is null:
                    using (var doc = Parse(line))
                    {
                        if (doc is null) return null;
                        var root = doc.RootElement;
                        id = Text(root, "id");
                        cwd = Text(root, "cwd");
                        created = Number(root, "createdAt");
                        depth = (int)Math.Min(int.MaxValue, Number(root, "delegationDepth"));
                    }
                    break;
                case "session/title":
                    using (var doc = Parse(line))
                        if (doc is not null && doc.RootElement.TryGetProperty("data", out var data)
                            && Text(data, "title") is { } named && !string.IsNullOrWhiteSpace(named))
                            title = named.Trim();
                    break;
                case "user/message":
                    using (var doc = Parse(line))
                    {
                        if (doc is null || !doc.RootElement.TryGetProperty("data", out var message) || !IsTyped(message)) break;
                        messages++;
                        if (first is null && ContentText(message) is { Length: > 0 } words && !string.IsNullOrWhiteSpace(words)) first = words;
                    }
                    break;
                case "assistant/message":
                    messages++;
                    break;
            }
        }
        if (string.IsNullOrWhiteSpace(id)) return null;
        var start = created > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(created) : DateTimeOffset.FromUnixTimeMilliseconds(last);
        var end = last > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(last) : start;
        return new DshSessionHeader(id, start, end < start ? start : end, title, first,
            string.IsNullOrWhiteSpace(cwd) ? null : cwd, depth, messages);
    }

    /// <summary>
    /// The session's entries for display, in order: what the two sides said; the model's
    /// reasoning, a tool's call and output, and what the harness put in, each folded; a
    /// slash command, an approval and an error as notes.
    /// </summary>
    public static List<TranscriptEntry> ReadEntries(string path, bool keepImages = false, CancellationToken cancellation = default)
    {
        _ = keepImages;     // pictures are kept in dsh's attachment store, not in the log: they are named, never shown
        var entries = new List<TranscriptEntry>();
        var lines = ReadLines(path, cancellation);
        for (var index = 0; index < lines.Count; index++)
        {
            cancellation.ThrowIfCancellationRequested();
            var line = lines[index];
            var type = TypeOf(line);
            if (type is not ("user/message" or "assistant/message" or "tool/result" or "command/run" or "command/done"
                or "approval/asked" or "approval/decided" or "turn/end")) continue;
            using var doc = Parse(line);
            if (doc is null || !doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) continue;
            var when = Stamp(Number(doc.RootElement, "time"));
            switch (type)
            {
                case "user/message":
                    ReadUser(data, index, when, entries);
                    break;
                case "assistant/message" when data.TryGetProperty("message", out var message):
                    ReadAssistant(message, index, when, entries);
                    break;
                case "tool/result" when data.TryGetProperty("message", out var result):
                    var output = ContentText(result);
                    var failed = result.TryGetProperty("isError", out var error) && error.ValueKind == JsonValueKind.True;
                    entries.Add(new TranscriptEntry(index, MessageRole.ToolResult,
                        output.Length == 0 ? (failed ? "工具出错（无输出）" : "工具输出（空）")
                            : $"{(failed ? "工具出错" : "工具输出")}（{output.Length:N0} 字符）",
                        Bound(output), when));
                    break;
                case "command/run":
                    var command = "/" + (Text(data, "name") ?? "");
                    var args = Text(data, "args");
                    entries.Add(new TranscriptEntry(index, MessageRole.Note,
                        "命令 · " + Flatten(string.IsNullOrWhiteSpace(args) ? command : command + " " + args, 110), null, when));
                    break;
                case "command/done" when Text(data, "text") is { Length: > 0 } said:
                    entries.Add(new TranscriptEntry(index, MessageRole.Note, "命令结果 · " + Flatten(said, 110), Bound(said), when));
                    break;
                case "approval/asked":
                    var reason = Text(data, "reason");
                    entries.Add(new TranscriptEntry(index, MessageRole.Note, "请求批准 · " + (Text(data, "toolName") ?? "操作"),
                        string.IsNullOrWhiteSpace(reason) ? null : Bound(reason), when));
                    break;
                case "approval/decided":
                    entries.Add(new TranscriptEntry(index, MessageRole.Note, "批准结果 · " + Outcome(Text(data, "outcome")), null, when));
                    break;
                case "turn/end" when data.TryGetProperty("reason", out var why):
                    switch (Text(why, "kind"))
                    {
                        case "error":
                            var problem = why.TryGetProperty("error", out var e) ? Text(e, "message") ?? "" : "";
                            var code = why.TryGetProperty("error", out var c) ? Text(c, "code") : null;
                            entries.Add(new TranscriptEntry(index, MessageRole.Note,
                                "出错 · " + (problem.Length > 0 ? Flatten(problem, 110) : code ?? "这一轮没有完成"),
                                problem.Length > 0 ? Bound(code is null ? problem : $"{problem}\n\n（{code}）") : null, when));
                            break;
                        case "aborted":
                            entries.Add(new TranscriptEntry(index, MessageRole.Note, "已中止 · 这一轮被停下了", null, when));
                            break;
                    }
                    break;
            }
        }
        return entries;
    }

    /// <summary>What the two sides said, and nothing else: for the search index and the side panel.</summary>
    public static List<TranscriptEntry> ReadDialogue(string path, CancellationToken cancellation = default) =>
        ReadEntries(path, keepImages: false, cancellation)
            .Where(e => e.Role is MessageRole.User or MessageRole.Assistant).ToList();

    /// <summary>Every model call with its token counts.</summary>
    public static List<DshCall> ReadCalls(string path, CancellationToken cancellation = default)
    {
        var calls = new List<DshCall>();
        var model = "";
        foreach (var line in ReadLines(path, cancellation))
        {
            cancellation.ThrowIfCancellationRequested();
            var type = TypeOf(line);
            if (type is not ("assistant/message" or "model/selection")) continue;
            using var doc = Parse(line);
            if (doc is null || !doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) continue;
            if (type == "model/selection")
            {
                model = Text(data, "model") ?? model;
                continue;
            }
            if (!data.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) continue;
            if (!data.TryGetProperty("message", out var message) || Text(message, "id") is not { Length: > 0 } id) continue;
            var time = Number(doc.RootElement, "time");
            if (time <= 0) continue;
            var by = message.TryGetProperty("source", out var source) ? Text(source, "model") : null;
            calls.Add(new DshCall(id, DateTimeOffset.FromUnixTimeMilliseconds(time), by ?? model,
                Number(usage, "inputTokens"), Number(usage, "cacheReadTokens"), Number(usage, "cacheWriteTokens"),
                Number(usage, "outputTokens")));
        }
        return calls;
    }

    // ------------------------------------------------------------------ messages

    /// <summary>A message the user typed, as opposed to context the harness put in under the user's role.</summary>
    private static bool IsTyped(JsonElement message) =>
        !message.TryGetProperty("source", out var source) || Text(source, "kind") is null or "user";

    private static void ReadUser(JsonElement message, int index, string? when, List<TranscriptEntry> entries)
    {
        var text = ContentText(message);
        if (!IsTyped(message))
        {
            var kind = message.TryGetProperty("source", out var source) ? Text(source, "kind") : null;
            if (text.Trim().Length > 0)
                entries.Add(new TranscriptEntry(index, MessageRole.Note, "注入的上下文 · " + InjectedName(kind), Bound(text), when));
            return;
        }
        AddText(entries, index, when, MessageRole.User, text);
        if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            foreach (var block in content.EnumerateArray())
                if (Text(block, "type") is { } type && type != "text")
                    entries.Add(new TranscriptEntry(index, MessageRole.Attachment,
                        type == "image" ? "（图片 · 存在 DeepSeek Harness 的附件里，这里不显示）" : $"（附件 · {type}）", null, when));
    }

    private static void ReadAssistant(JsonElement message, int index, string? when, List<TranscriptEntry> entries)
    {
        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return;
        foreach (var block in content.EnumerateArray())
        {
            switch (Text(block, "type"))
            {
                case "text":
                    AddText(entries, index, when, MessageRole.Assistant, Text(block, "text") ?? "");
                    break;
                case "reasoning" or "thinking":
                    var thought = Text(block, "text") ?? Text(block, "thinking") ?? "";
                    if (thought.Trim().Length > 0)
                        entries.Add(new TranscriptEntry(index, MessageRole.Note, "思考 · " + Flatten(thought, 80), Bound(thought), when));
                    break;
                case "tool-call":
                    var name = Text(block, "name") ?? "tool";
                    var arguments = Text(block, "arguments") ?? (block.TryGetProperty("arguments", out var raw) ? raw.GetRawText() : "");
                    var hint = Hint(arguments);
                    entries.Add(new TranscriptEntry(index, MessageRole.ToolCall,
                        hint.Length == 0 ? name : $"{name}  {Flatten(hint, 110)}", Bound(arguments), when));
                    break;
            }
        }
    }

    private static string InjectedName(string? kind) => kind switch
    {
        "runtime-context" => "运行环境与权限",
        "skill-catalog" => "可用技能列表",
        null or "" => "应用",
        var other => other,
    };

    private static string Outcome(string? outcome) => outcome switch
    {
        null or "" => "已处理",
        _ when outcome.Contains("approve", StringComparison.OrdinalIgnoreCase) || outcome.Contains("allow", StringComparison.OrdinalIgnoreCase) => "允许（" + outcome + "）",
        _ when outcome.Contains("deny", StringComparison.OrdinalIgnoreCase) || outcome.Contains("reject", StringComparison.OrdinalIgnoreCase) => "拒绝（" + outcome + "）",
        _ => outcome,
    };

    /// <summary>The text blocks of a message's content, joined.</summary>
    private static string ContentText(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content)) return "";
        if (content.ValueKind == JsonValueKind.String) return JsonText.Of(content) ?? "";
        if (content.ValueKind != JsonValueKind.Array) return "";
        var sb = new StringBuilder();
        foreach (var block in content.EnumerateArray())
            if (Text(block, "type") == "text" && Text(block, "text") is { } text)
            {
                if (sb.Length > 0) sb.Append("\n\n");
                sb.Append(text);
            }
        return sb.ToString();
    }

    /// <summary>A short hint about what a tool call did, from its arguments (a JSON object written as a string).</summary>
    private static string Hint(string arguments)
    {
        if (arguments.Length is 0 or > 1024 * 1024 || arguments[0] != '{') return "";
        try
        {
            using var doc = JsonDocument.Parse(arguments, Json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return "";
            foreach (var key in new[] { "description", "command", "script", "file_path", "path", "pattern", "query", "url", "question", "title" })
                if (Text(doc.RootElement, key) is { Length: > 0 } value) return value;
        }
        catch (JsonException) { }
        return "";
    }

    private static void AddText(List<TranscriptEntry> entries, int index, string? when, MessageRole role, string value)
    {
        var text = TranscriptText.Clean(value).Trim();
        if (text.Length > 0) entries.Add(new TranscriptEntry(index, role, text, null, when));
    }

    private static string Flatten(string s, int max)
    {
        var flat = string.Join(' ', TranscriptText.Clean(s).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length > max ? flat[..max] + "…" : flat;
    }

    private static string Bound(string s) =>
        s.Length <= TranscriptReader.MaxDetailChars ? s
            : s[..TranscriptReader.MaxDetailChars] + $"\n…（已截断，共 {s.Length:N0} 字符；原文件未修改）";

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String
            ? JsonText.Of(v)
            : null;

    private static long Number(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var v)
        && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) && n > 0 ? n : 0;

    private static string? Stamp(long ms) =>
        ms > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) : null;
}
