using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ClaudeLauncher.Infrastructure.Backends;

/// <summary>
/// Where Gemini CLI keeps its sessions: <c>~\.gemini\tmp\&lt;project&gt;\chats\session-*.json</c>,
/// each project folder naming its working directory in a <c>.project_root</c> file. Only those
/// are ever read — the folder also holds the user's sign-in (<c>oauth_creds.json</c>), which is
/// never opened.
/// </summary>
public sealed record GeminiPaths(string Home, string ProjectsRoot)
{
    public static GeminiPaths Discover() =>
        FromHome(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini"));

    public static GeminiPaths FromHome(string home)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(home);
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(home));
        return new GeminiPaths(full, Path.Combine(full, "tmp"));
    }

    public bool Exists => Directory.Exists(ProjectsRoot);

    /// <summary>Every session file, with the project folder it is in.</summary>
    public IEnumerable<(string File, string ProjectFolder)> SessionFiles()
    {
        if (!Exists) yield break;
        IEnumerable<string> projects;
        try { projects = Directory.EnumerateDirectories(ProjectsRoot).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }
        foreach (var project in projects)
        {
            var chats = Path.Combine(project, "chats");
            if (!Directory.Exists(chats)) continue;
            List<string> files;
            try { files = Directory.EnumerateFiles(chats, "session-*.json", SearchOption.TopDirectoryOnly).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var file in files) yield return (file, project);
        }
    }

    /// <summary>The working directory a project folder belongs to, from its <c>.project_root</c>; null when it does not say.</summary>
    public static string? ProjectRootOf(string projectFolder)
    {
        try
        {
            var file = Path.Combine(projectFolder, ".project_root");
            if (!File.Exists(file)) return null;
            var root = File.ReadAllText(file, Encoding.UTF8).Trim();
            return root.Length is > 0 and < 1024 ? root : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>What the catalog needs from one session file: its id, times, and words to call it by.</summary>
public sealed record GeminiSessionHeader(
    string SessionId, DateTimeOffset? Started, DateTimeOffset? LastUpdated, string? Summary,
    string? FirstUserWords, int Messages);

/// <summary>One model call a session recorded: its message id names it, so it counts once.</summary>
public sealed record GeminiCall(string Id, DateTimeOffset When, string Model, long Input, long Cached, long Output, long Thoughts, long Tool);

/// <summary>
/// A Gemini CLI session. Unlike a Claude or Codex transcript it is one JSON document, written
/// again whole as the conversation goes on, so it is always read whole: never from an offset.
///
/// <para><c>{sessionId, projectHash, startTime, lastUpdated, summary?, messages:[…]}</c>; a message
/// is <c>{id, timestamp, type:user|gemini|info|error|warning, content, …}</c>. A user message's
/// content is a list of parts — text, a tool's response, a file sent inline — and the model's is
/// its text, with <c>thoughts</c>, <c>tokens</c>, <c>model</c> and, in newer versions,
/// <c>toolCalls</c> beside it.</para>
/// </summary>
public static class GeminiSession
{
    /// <summary>Whether a transcript path is a Gemini session (one JSON document), or this tool's packed copy of one.</summary>
    public static bool IsSessionFile(string? path) =>
        path is not null && (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                             || path.EndsWith(".json" + PackedCopy.Suffix, StringComparison.OrdinalIgnoreCase));

    private static readonly JsonDocumentOptions Json = new() { MaxDepth = 256 };

    /// <summary>
    /// The document, parsed. Gemini writes the file again whole, so it can be caught half
    /// written: that reads as an I/O failure like any other file that cannot be read right now,
    /// and every caller already tries again on the next pass.
    /// </summary>
    private static JsonDocument Open(string path)
    {
        using var stream = TranscriptFiles.OpenRead(path);
        try
        {
            return JsonDocument.Parse(stream, Json);
        }
        catch (JsonException ex)
        {
            throw new IOException("Gemini 会话文件正在写入或已损坏：" + Path.GetFileName(path), ex);
        }
    }

    /// <summary>The header of a session; null when the file is not one (no id, no messages).</summary>
    public static GeminiSessionHeader? ReadHeader(string path) => QuickHeader(path) ?? ReadHeaderWhole(path);

    /// <summary>The header, from the whole document.</summary>
    internal static GeminiSessionHeader? ReadHeaderWhole(string path)
    {
        using var doc = Open(path);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || Text(root, "sessionId") is not { Length: > 0 } id) return null;
        if (!root.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array) return null;

        string? first = null;
        var count = 0;
        foreach (var message in messages.EnumerateArray())
        {
            count++;
            if (first is null && Text(message, "type") == "user")
            {
                var words = PartsText(message);
                if (!string.IsNullOrWhiteSpace(words)) first = words;
            }
        }
        if (count == 0) return null;
        return new GeminiSessionHeader(id, Time(root, "startTime"), Time(root, "lastUpdated"),
            Text(root, "summary") is { Length: > 0 } summary ? summary.Trim() : null, first, count);
    }

    /// <summary>The kinds of document this tool writes itself, for what it imports: every header field comes before the messages.</summary>
    private static readonly HashSet<string> OwnKinds = new(StringComparer.Ordinal) { "google-takeout", "chatgpt-export", "claude-export" };

    private const int QuickBytes = 64 * 1024;

    /// <summary>
    /// The header of a document this tool wrote itself — an import — from its first kilobytes, up
    /// to the first thing the user said: a sync lists thousands of them, some megabytes long.
    /// Null for anything else, a Gemini CLI session or a first question further in than that,
    /// which is then read whole.
    /// </summary>
    private static GeminiSessionHeader? QuickHeader(string path)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(QuickBytes);
        try
        {
            int read;
            using (var stream = TranscriptFiles.OpenRead(path))
                read = stream.ReadAtLeast(buffer.AsSpan(0, QuickBytes), QuickBytes, throwOnEndOfStream: false);
            return QuickHeader(buffer.AsSpan(0, read), isFinalBlock: read < QuickBytes);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>The header from a document's first bytes; null when they do not hold it, or the document is not one of this tool's own.</summary>
    internal static GeminiSessionHeader? QuickHeader(ReadOnlySpan<byte> bytes, bool isFinalBlock)
    {
        try
        {
            return QuickHeaderOf(bytes, isFinalBlock);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;            // not as expected — a value cut off by the first bytes' end: read whole
        }
    }

    private static GeminiSessionHeader? QuickHeaderOf(ReadOnlySpan<byte> bytes, bool isFinalBlock)
    {
        var reader = new Utf8JsonReader(bytes, isFinalBlock, default);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;
        string? id = null, summary = null;
        DateTimeOffset? started = null, updated = null;
        var own = false;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var name = reader.GetString();
            if (!reader.Read()) return null;
            var text = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            switch (name)
            {
                case "sessionId": id = text; break;
                case "startTime": started = ParseTime(text); break;
                case "lastUpdated": updated = ParseTime(text); break;
                case "summary": summary = text; break;
                case "kind": own = text is not null && OwnKinds.Contains(text); break;
                case "messages":
                    if (!own || string.IsNullOrEmpty(id) || reader.TokenType != JsonTokenType.StartArray) return null;
                    var count = 0;
                    while (reader.Read())
                    {
                        if (reader.TokenType == JsonTokenType.EndArray)
                            return count == 0 ? null : Header(id, started, updated, summary, null, count);
                        if (reader.TokenType != JsonTokenType.StartObject) return null;
                        using var message = JsonDocument.ParseValue(ref reader);
                        count++;
                        if (Text(message.RootElement, "type") == "user" && PartsText(message.RootElement) is var words
                            && !string.IsNullOrWhiteSpace(words))
                            return Header(id, started, updated, summary, words, count);
                    }
                    return null;        // the first kilobytes ran out before the first question
                default:
                    if (!reader.TrySkip()) return null;
                    break;
            }
        }
        return null;
    }

    private static GeminiSessionHeader Header(string id, DateTimeOffset? started, DateTimeOffset? updated, string? summary,
        string? first, int count) =>
        new(id, started, updated, summary is { Length: > 0 } words ? words.Trim() : null, first, count);

    private static DateTimeOffset? ParseTime(string? iso) =>
        iso is { Length: > 0 and < 64 }
        && DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var when)
            ? when
            : null;

    /// <summary>
    /// The session's entries for display, in order: what the two sides said, a tool's call and
    /// response as folded lines, the model's thinking folded too, files sent inline as
    /// attachments — pictures kept only when <paramref name="keepImages"/>.
    /// </summary>
    public static List<TranscriptEntry> ReadEntries(string path, bool keepImages = false, CancellationToken cancellation = default)
    {
        using var doc = Open(path);
        var entries = new List<TranscriptEntry>();
        if (!doc.RootElement.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
            return entries;

        var index = 0;
        long imageBytes = 0;
        var folder = Path.GetDirectoryName(Path.GetFullPath(path));
        foreach (var message in messages.EnumerateArray())
        {
            cancellation.ThrowIfCancellationRequested();
            var when = Stamp(Text(message, "timestamp"));
            switch (Text(message, "type"))
            {
                case "user":
                    ReadUser(message, index, when, folder, keepImages, ref imageBytes, entries);
                    break;
                case "gemini":
                    ReadModel(message, index, when, folder, keepImages, ref imageBytes, entries);
                    break;
                case "info" or "warning" or "error":
                    if (PartsText(message) is { Length: > 0 } note)
                        entries.Add(new TranscriptEntry(index, MessageRole.Note,
                            (Text(message, "type") == "error" ? "错误 · " : "提示 · ") + Flatten(note, 80), Bound(note), when));
                    break;
            }
            index++;
        }
        return entries;
    }

    /// <summary>What the two sides said, and nothing else: for the search index and the side panel.</summary>
    public static List<TranscriptEntry> ReadDialogue(string path, CancellationToken cancellation = default) =>
        ReadEntries(path, keepImages: false, cancellation)
            .Where(e => e.Role is MessageRole.User or MessageRole.Assistant).ToList();

    /// <summary>Every model call with its token counts.</summary>
    public static List<GeminiCall> ReadCalls(string path, CancellationToken cancellation = default)
    {
        using var doc = Open(path);
        var calls = new List<GeminiCall>();
        if (!doc.RootElement.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
            return calls;
        foreach (var message in messages.EnumerateArray())
        {
            cancellation.ThrowIfCancellationRequested();
            if (Text(message, "type") != "gemini" || !message.TryGetProperty("tokens", out var tokens)
                || tokens.ValueKind != JsonValueKind.Object) continue;
            if (Text(message, "id") is not { Length: > 0 } id || Time(message, "timestamp") is not { } when) continue;
            calls.Add(new GeminiCall(id, when, Text(message, "model") ?? "",
                Number(tokens, "input"), Number(tokens, "cached"), Number(tokens, "output"),
                Number(tokens, "thoughts"), Number(tokens, "tool")));
        }
        return calls;
    }

    private static void ReadUser(JsonElement message, int index, string? when, string? folder, bool keepImages,
        ref long imageBytes, List<TranscriptEntry> entries)
    {
        if (!message.TryGetProperty("content", out var content)) return;
        if (content.ValueKind == JsonValueKind.String)
        {
            AddText(entries, index, when, MessageRole.User, JsonText.Of(content) ?? "");
            return;
        }
        if (content.ValueKind != JsonValueKind.Array) return;

        var words = new StringBuilder();
        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.String)
            {
                words.Append(JsonText.Of(part));
                continue;
            }
            if (part.ValueKind != JsonValueKind.Object) continue;
            if (Text(part, "text") is { } text) words.Append(text);
            else if (part.TryGetProperty("functionResponse", out var response))
            {
                var output = response.TryGetProperty("response", out var body) ? Readable(body) : "";
                var name = Text(response, "name");
                entries.Add(new TranscriptEntry(index, MessageRole.ToolResult,
                    (name is null ? "工具输出" : "工具输出 · " + name) + $"（{output.Length:N0} 字符）", Bound(output), when));
            }
            else if (part.TryGetProperty("inlineData", out var inline))
                entries.Add(Attachment(inline, index, when, keepImages, ref imageBytes));
            else if (part.TryGetProperty("launcherFile", out var kept))
                entries.Add(KeptFile(kept, folder, index, when, keepImages, ref imageBytes));
            else if (part.TryGetProperty("fileData", out var file))
                entries.Add(new TranscriptEntry(index, MessageRole.Attachment,
                    "（附件 · " + (Text(file, "mimeType") ?? "文件") + " · " + (Text(file, "fileUri") ?? "") + "）", null, when));
        }
        if (words.Length > 0) AddText(entries, index, when, MessageRole.User, words.ToString());
    }

    private static void ReadModel(JsonElement message, int index, string? when, string? folder, bool keepImages,
        ref long imageBytes, List<TranscriptEntry> entries)
    {
        if (message.TryGetProperty("thoughts", out var thoughts) && thoughts.ValueKind == JsonValueKind.Array)
        {
            var detail = new StringBuilder();
            string? first = null;
            foreach (var thought in thoughts.EnumerateArray())
            {
                var subject = Text(thought, "subject");
                first ??= subject;
                if (detail.Length > 0) detail.Append("\n\n");
                if (subject is not null) detail.Append("**").Append(subject).Append("**\n");
                detail.Append(Text(thought, "description"));
            }
            if (detail.Length > 0)
                entries.Add(new TranscriptEntry(index, MessageRole.Note,
                    first is null ? "思考" : "思考 · " + Flatten(first, 80), Bound(detail.ToString()), when));
        }

        if (message.TryGetProperty("content", out var content))
        {
            var text = content.ValueKind == JsonValueKind.String ? JsonText.Of(content) ?? "" : PartsText(message);
            AddText(entries, index, when, MessageRole.Assistant, text);
            // Pictures the model made, kept beside an imported conversation.
            if (content.ValueKind == JsonValueKind.Array)
                foreach (var part in content.EnumerateArray())
                    if (part.ValueKind == JsonValueKind.Object && part.TryGetProperty("launcherFile", out var kept))
                        entries.Add(KeptFile(kept, folder, index, when, keepImages, ref imageBytes));
                    else if (part.ValueKind == JsonValueKind.Object && part.TryGetProperty("fileData", out var named))
                        entries.Add(new TranscriptEntry(index, MessageRole.Attachment,
                            "（附件 · " + (Text(named, "mimeType") ?? "文件") + " · " + (Text(named, "fileUri") ?? "") + "）", null, when));
        }

        if (message.TryGetProperty("toolCalls", out var calls) && calls.ValueKind == JsonValueKind.Array)
            foreach (var call in calls.EnumerateArray())
            {
                var name = Text(call, "displayName") ?? Text(call, "name") ?? "tool";
                var arguments = call.TryGetProperty("args", out var args) ? Readable(args) : "";
                var hint = Text(call, "description") ?? Hint(args);
                entries.Add(new TranscriptEntry(index, MessageRole.ToolCall,
                    string.IsNullOrEmpty(hint) ? name : $"{name}  {Flatten(hint, 110)}", Bound(arguments), when));
                var output = Text(call, "resultDisplay") is { Length: > 0 } shown ? shown
                    : call.TryGetProperty("result", out var result) ? Readable(result) : null;
                if (output is not null)
                    entries.Add(new TranscriptEntry(index, MessageRole.ToolResult,
                        output.Length == 0 ? "工具输出（空）" : $"工具输出（{output.Length:N0} 字符）", Bound(output), when));
            }
    }

    private static TranscriptEntry Attachment(JsonElement inline, int index, string? when, bool keepImages, ref long imageBytes)
    {
        var mime = Text(inline, "mimeType") ?? "文件";
        var data = inline.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() ?? "" : "";
        var size = (long)data.Length * 3 / 4;
        var sizeText = SizeText(size);
        if (!mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return new TranscriptEntry(index, MessageRole.Attachment, $"（附件 · {mime} · {sizeText}）", null, when);

        var label = $"（图片 · {sizeText}）";
        if (!keepImages) return new TranscriptEntry(index, MessageRole.Attachment, label, null, when);
        if (size > TranscriptReader.MaxImageBytes || imageBytes + size > 256L * 1024 * 1024)
            return new TranscriptEntry(index, MessageRole.Attachment, label + "太大，未显示", null, when);
        try
        {
            var bytes = Convert.FromBase64String(data);
            imageBytes += bytes.Length;
            return new TranscriptEntry(index, MessageRole.Attachment, label, null, when) { Image = bytes };
        }
        catch (FormatException)
        {
            return new TranscriptEntry(index, MessageRole.Attachment, "（图片数据无法读取）", null, when);
        }
    }

    /// <summary>
    /// A file this tool kept beside its own copy of a conversation — an import of Gemini's web
    /// history keeps what was sent in a <c>files</c> folder next to its conversations. It is only
    /// ever read from there: a path that leads anywhere else is named, never opened.
    /// </summary>
    private static TranscriptEntry KeptFile(JsonElement kept, string? folder, int index, string? when, bool keepImages,
        ref long imageBytes)
    {
        var name = Text(kept, "name") ?? "文件";
        var mime = Text(kept, "mimeType");
        FileInfo? file = null;
        if (folder is not null && Text(kept, "path") is { Length: > 0 } relative)
        {
            var files = Path.GetFullPath(Path.Combine(folder, "..", "files")) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(Path.Combine(folder, relative));
            if (full.StartsWith(files, StringComparison.OrdinalIgnoreCase) && File.Exists(full)) file = new FileInfo(full);
        }
        var image = mime?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true;
        if (file is null)
            return new TranscriptEntry(index, MessageRole.Attachment, $"（附件 · {name} · 文件不在了）", null, when);
        var label = image ? $"（图片 · {name} · {SizeText(file.Length)}）" : $"（附件 · {name} · {SizeText(file.Length)}）";
        if (!image || !keepImages) return new TranscriptEntry(index, MessageRole.Attachment, label, null, when);
        if (file.Length > TranscriptReader.MaxImageBytes || imageBytes + file.Length > 256L * 1024 * 1024)
            return new TranscriptEntry(index, MessageRole.Attachment, label + "太大，未显示", null, when);
        try
        {
            var bytes = File.ReadAllBytes(file.FullName);
            imageBytes += bytes.Length;
            return new TranscriptEntry(index, MessageRole.Attachment, label, null, when) { Image = bytes };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new TranscriptEntry(index, MessageRole.Attachment, label + "读不出来", null, when);
        }
    }

    private static string SizeText(long size) =>
        size >= 1024 * 1024 ? $"{size / (1024.0 * 1024):0.#} MB" : $"{Math.Max(1, size / 1024):N0} KB";

    private static void AddText(List<TranscriptEntry> entries, int index, string? when, MessageRole role, string value)
    {
        var text = TranscriptText.Clean(value).Trim();
        if (text.Length > 0) entries.Add(new TranscriptEntry(index, role, text, null, when));
    }

    /// <summary>The text parts of a message's content, joined; a string content as it is.</summary>
    private static string PartsText(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content)) return "";
        if (content.ValueKind == JsonValueKind.String) return JsonText.Of(content) ?? "";
        if (content.ValueKind != JsonValueKind.Array) return "";
        var sb = new StringBuilder();
        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.String) sb.Append(JsonText.Of(part));
            else if (Text(part, "text") is { } text) sb.Append(text);
        }
        return sb.ToString();
    }

    /// <summary>A tool's arguments or output, readable: a string as it is, anything else as JSON.</summary>
    private static string Readable(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => JsonText.Of(value) ?? "",
        JsonValueKind.Object when value.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.String =>
            JsonText.Of(output) ?? "",
        JsonValueKind.Undefined or JsonValueKind.Null => "",
        _ => value.GetRawText(),
    };

    private static string Hint(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) return "";
        foreach (var key in new[] { "description", "command", "file_path", "absolute_path", "path", "pattern", "query", "url" })
            if (args.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                return JsonText.Of(v) ?? "";
        return "";
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
        element.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) && n > 0 ? n : 0;

    private static DateTimeOffset? Time(JsonElement element, string property) =>
        Text(element, property) is { Length: > 0 and < 64 } iso
        && DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var when)
            ? when
            : null;

    private static string? Stamp(string? iso) =>
        iso is { Length: > 0 and < 64 }
        && DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var when)
            ? when.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture)
            : null;
}
