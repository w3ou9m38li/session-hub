using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using ClaudeLauncher.Core.Contracts;
using ClaudeLauncher.Infrastructure.Launch;

namespace ClaudeLauncher.Infrastructure.Backends;

/// <summary>Who a turn of an exported conversation is from.</summary>
public enum ExportedRole { User, Assistant, Note }

/// <summary>A file of a turn: kept in the store's <c>files</c> folder (<see cref="Stored"/>), or only named when the export did not have it.</summary>
public sealed record ExportedFile(string Name, string? MimeType, string? Stored);

/// <summary>A step of the model's thinking.</summary>
public sealed record ExportedThought(string? Subject, string Text);

/// <summary>A tool the model called, what it was given, and — once it came — what it returned.</summary>
public sealed class ExportedToolCall(string name, string arguments, string? id)
{
    public string Name { get; } = name;
    public string Arguments { get; } = arguments;
    public string? Id { get; } = id;
    public string? Result { get; set; }
}

/// <summary>One turn: the user's words and files; the model's thinking, words, files and tool calls, in that order; or a note.</summary>
public sealed class ExportedTurn(ExportedRole role, DateTimeOffset? time)
{
    public ExportedRole Role { get; } = role;
    public DateTimeOffset? Time { get; } = time;
    public string Text { get; set; } = "";
    public List<ExportedFile> Files { get; } = [];
    public List<ExportedThought> Thoughts { get; } = [];
    public List<ExportedToolCall> Calls { get; } = [];
}

/// <summary>A conversation as a chat app's export holds it.</summary>
public sealed record ExportedConversation(string Id, string? Title, DateTimeOffset? Created, DateTimeOffset? Updated,
    IReadOnlyList<ExportedTurn> Turns);

/// <summary>Reads the conversations of one kind of export.</summary>
internal interface IExportReader
{
    /// <summary>The conversation an element of the export's conversation list holds; null when it is not one this reader knows.</summary>
    ExportedConversation? Read(JsonElement conversation);

    /// <summary>Whether the element is a conversation of the other app's export: the wrong file was chosen.</summary>
    bool IsOtherKind(JsonElement conversation);
}

/// <summary>
/// Puts what an export says into turns, in the order it was said. A model's turn shows its
/// thinking, then its words and the files it made, then the tools it called (each with what it
/// returned): anything that came later than that order allows starts a turn of its own.
/// </summary>
internal sealed class TurnBuilder
{
    private readonly List<ExportedTurn> _turns = [];
    private ExportedTurn? _model;

    public List<ExportedTurn> Turns
    {
        get
        {
            _model = null;
            return _turns;
        }
    }

    public void User(DateTimeOffset? time, string text, IEnumerable<ExportedFile> files)
    {
        _model = null;
        var turn = new ExportedTurn(ExportedRole.User, time) { Text = text };
        turn.Files.AddRange(files);
        if (turn.Text.Length > 0 || turn.Files.Count > 0) _turns.Add(turn);
    }

    public void Note(DateTimeOffset? time, string text)
    {
        _model = null;
        if (text.Length > 0) _turns.Add(new ExportedTurn(ExportedRole.Note, time) { Text = text });
    }

    public void Thought(DateTimeOffset? time, string? subject, string text)
    {
        if (text.Length == 0 && subject is null) return;
        Model(time, t => t.Text.Length == 0 && t.Files.Count == 0 && t.Calls.Count == 0)
            .Thoughts.Add(new ExportedThought(subject, text));
    }

    /// <summary>Words of the model: an answer given in several pieces with nothing between them stays one answer.</summary>
    public void Text(DateTimeOffset? time, string text)
    {
        if (text.Length == 0) return;
        if (_model is { } open && open.Text.Length > 0 && open.Files.Count == 0 && open.Calls.Count == 0)
        {
            open.Text += "\n\n" + text;
            return;
        }
        Model(time, t => t.Text.Length == 0 && t.Files.Count == 0 && t.Calls.Count == 0).Text = text;
    }

    public void File(DateTimeOffset? time, ExportedFile file) => Model(time, t => t.Calls.Count == 0).Files.Add(file);

    public ExportedToolCall Call(DateTimeOffset? time, string name, string arguments, string? id = null)
    {
        var call = new ExportedToolCall(name, arguments, id);
        Model(time, _ => true).Calls.Add(call);
        return call;
    }

    /// <summary>What a tool returned: to the call it answers — by its id, else the last one still waiting — or as a call of its own.</summary>
    public void Result(DateTimeOffset? time, string? name, string output, string? id = null)
    {
        ExportedToolCall? call = null;
        for (var i = _turns.Count - 1; i >= 0 && i >= _turns.Count - 4 && call is null; i--)
        {
            if (_turns[i].Role != ExportedRole.Assistant) break;
            call = _turns[i].Calls.LastOrDefault(c => id is not null ? c.Id == id : c.Result is null);
        }
        call ??= Call(time, name ?? "工具", "", id);
        call.Result = call.Result is null ? output : call.Result + "\n\n" + output;
    }

    private ExportedTurn Model(DateTimeOffset? time, Func<ExportedTurn, bool> fits)
    {
        if (_model is null || !fits(_model))
        {
            _model = new ExportedTurn(ExportedRole.Assistant, time);
            _turns.Add(_model);
        }
        return _model;
    }
}

/// <summary>
/// An export the user downloaded, read where it is: the zip as it came, or — a JSON file of it
/// chosen — the folder it was unpacked into. Only ever read.
/// </summary>
public sealed class ExportPackage : IDisposable
{
    private readonly ZipArchive? _zip;
    private readonly Dictionary<string, ZipArchiveEntry>? _entries;
    private readonly string? _folder;

    /// <summary>Every file in it, by its path inside the export, with <c>/</c> between folders.</summary>
    public IReadOnlyList<string> Names { get; }

    private ExportPackage(ZipArchive zip)
    {
        _zip = zip;
        _entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (var entry in zip.Entries)
            if (entry.Name.Length > 0) _entries.TryAdd(entry.FullName.Replace('\\', '/'), entry);
        Names = [.. _entries.Keys];
    }

    private ExportPackage(string folder)
    {
        _folder = folder;
        // An export unpacked into a folder full of other things (Downloads): only a few levels down.
        var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true };
        Names = [.. Directory.EnumerateFiles(folder, "*", options).Take(200_000)
            .Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/'))];
    }

    /// <summary>A zip, or the folder of a JSON file chosen in it.</summary>
    public static ExportPackage At(string path)
    {
        if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return new ExportPackage(ZipFile.OpenRead(path));
        if (Directory.Exists(path)) return new ExportPackage(Path.GetFullPath(path));
        if (File.Exists(path)) return new ExportPackage(Path.GetDirectoryName(Path.GetFullPath(path))!);
        throw new FileNotFoundException("找不到这个导出文件", path);
    }

    /// <summary>The files that list the conversations — conversations.json, numbered ones in a large export — nearest the top first.</summary>
    public IReadOnlyList<string> ConversationFiles()
    {
        var all = Names.Where(n =>
            {
                var file = n[(n.LastIndexOf('/') + 1)..];
                return file.StartsWith("conversations", StringComparison.OrdinalIgnoreCase)
                       && file.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
            })
            .ToList();
        if (all.Count == 0) return [];
        var depth = all.Min(n => n.Count(c => c == '/'));
        return [.. all.Where(n => n.Count(c => c == '/') == depth).Order(StringComparer.OrdinalIgnoreCase)];
    }

    public Stream Open(string name)
    {
        if (_entries is not null)
            return _entries.TryGetValue(name, out var entry) ? entry.Open() : throw new FileNotFoundException("导出里没有这个文件", name);
        var full = Path.GetFullPath(Path.Combine(_folder!, name));
        if (!full.StartsWith(_folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("导出里没有这个文件", name);
        return new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
    }

    /// <summary>A file's size, when it is known without reading it.</summary>
    public long? Length(string name)
    {
        if (_entries is not null) return _entries.TryGetValue(name, out var entry) ? entry.Length : null;
        try
        {
            return new FileInfo(Path.Combine(_folder!, name)).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The conversations a file lists, one at a time: an array of them read as it is reached —
    /// an export of years of conversations runs to hundreds of megabytes — or an object holding one.
    /// </summary>
    public IEnumerable<JsonElement> Conversations(string name, CancellationToken cancellation)
    {
        int first;
        using (var peek = Open(name)) first = FirstByte(peek);
        if (first == '[')
        {
            using var stream = Open(name);
            foreach (var element in JsonSerializer.DeserializeAsyncEnumerable<JsonElement>(stream, Json, cancellation)
                         .ToBlockingEnumerable(cancellation))
                yield return element;
        }
        else if (first == '{')
        {
            using var stream = Open(name);
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 256 });
            var root = document.RootElement;
            if (root.TryGetProperty("conversations", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var element in list.EnumerateArray())
                    yield return element.Clone();
            else
                yield return root.Clone();
        }
    }

    private static readonly JsonSerializerOptions Json = new() { MaxDepth = 256 };

    /// <summary>The first byte that is not white space or the byte-order mark.</summary>
    private static int FirstByte(Stream stream)
    {
        int b;
        while ((b = stream.ReadByte()) >= 0)
            if (b is not (' ' or '\t' or '\r' or '\n' or 0xEF or 0xBB or 0xBF)) return b;
        return -1;
    }

    public void Dispose() => _zip?.Dispose();
}

/// <summary>What an import of a web export did.</summary>
/// <param name="Found">Conversations read from the export.</param>
/// <param name="Added">Of those, the ones not imported before.</param>
/// <param name="Updated">The ones imported before that have changed since.</param>
/// <param name="Total">Conversations kept now, from every import.</param>
/// <param name="Unreadable">Conversations in the export this reader could not make out.</param>
/// <param name="ConversationFiles">Files listing conversations found in the export; 0 when it had none.</param>
/// <param name="OtherKind">The export is the other app's: the wrong file was chosen.</param>
public sealed record WebExportImport(int Found, int Added, int Updated, int Total, int Unreadable, int ConversationFiles,
    bool OtherKind);

/// <summary>
/// This tool's own copy of one chat app's conversations on the web, from the exports the user
/// downloads (ChatGPT's, Claude's): one document per conversation
/// (<c>conversations\session-&lt;id&gt;.json</c>), in the form Gemini CLI keeps its sessions, so
/// the list, the reader, the search index and the exporter read it as they read those; the
/// pictures sent and made in <c>files\</c>. A later export replaces a conversation it has again
/// and adds the new ones; one it no longer has — deleted on the web since — stays, until the
/// import is removed.
/// </summary>
public sealed class WebExportStore
{
    public WebExportStore(string backend, string root)
    {
        if (backend is not (ConversationKey.ChatGptWeb or ConversationKey.ClaudeWeb))
            throw new ArgumentOutOfRangeException(nameof(backend), backend, "不是网页导出的来源");
        Backend = backend;
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }

    /// <summary>ChatGPT's and Claude's, in this tool's data root.</summary>
    public static IReadOnlyList<WebExportStore> InDataRoot() =>
    [
        new(ConversationKey.ChatGptWeb, LauncherPaths.ChatGptWebStore),
        new(ConversationKey.ClaudeWeb, LauncherPaths.ClaudeWebStore),
    ];

    public static WebExportStore InDataRoot(string backend) => InDataRoot().First(s => s.Backend == backend);

    public string Backend { get; }

    public string Root { get; }

    public string ConversationsFolder => Path.Combine(Root, "conversations");

    public string FilesFolder => Path.Combine(Root, "files");

    public bool Exists => Directory.Exists(ConversationsFolder);

    /// <summary>A file larger than this is named, not kept: an export can carry whole videos.</summary>
    private const long MaxKeptFile = 64L * 1024 * 1024;

    /// <summary>One file per conversation, as the sync lists them.</summary>
    public IEnumerable<string> SessionFiles()
    {
        if (!Exists) return [];
        try
        {
            return Directory.EnumerateFiles(ConversationsFolder, "session-*.json", SearchOption.TopDirectoryOnly).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Reads the exports and adds what they hold to the store; the exports themselves are only read.</summary>
    /// <param name="progress">Conversations read so far.</param>
    public WebExportImport Import(IReadOnlyList<string> exports, IProgress<int>? progress = null,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(exports);
        int found = 0, added = 0, updated = 0, unreadable = 0, files = 0;
        var otherKind = false;
        foreach (var export in exports)
        {
            using var package = ExportPackage.At(export);
            var lists = package.ConversationFiles();
            files += lists.Count;
            IExportReader reader = Backend == ConversationKey.ChatGptWeb
                ? new ChatGptExport(package, Keeper(package))
                : new ClaudeExport();
            foreach (var list in lists)
                foreach (var element in package.Conversations(list, cancellation))
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (reader.IsOtherKind(element))
                    {
                        otherKind = true;
                        continue;
                    }
                    ExportedConversation? conversation;
                    try
                    {
                        conversation = reader.Read(element);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or FormatException or KeyNotFoundException)
                    {
                        conversation = null;       // a shape this reader does not know: counted, not imported
                    }
                    if (conversation is null)
                    {
                        unreadable++;
                        continue;
                    }
                    found++;
                    switch (Write(conversation))
                    {
                        case Written.Added: added++; break;
                        case Written.Updated: updated++; break;
                    }
                    progress?.Report(found);
                }
        }
        return new WebExportImport(found, added, updated, SessionFiles().Count(), unreadable, files, otherKind && found == 0);
    }

    /// <summary>What is imported now, for the import window: conversations, and when the last import was.</summary>
    public (int Conversations, DateTime? LastImport) Describe()
    {
        if (!Exists) return (0, null);
        var sessions = SessionFiles().ToList();
        return (sessions.Count, sessions.Count == 0 ? null : sessions.Max(File.GetLastWriteTime));
    }

    /// <summary>Removes everything imported; the number of conversations it held.</summary>
    public int Remove()
    {
        var count = SessionFiles().Count();
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        return count;
    }

    private enum Written { Same, Added, Updated }

    /// <summary>A conversation's document, written only when it says something new: what reads it sees no change otherwise.</summary>
    private Written Write(ExportedConversation conversation)
    {
        Directory.CreateDirectory(ConversationsFolder);
        var path = Path.Combine(ConversationsFolder, $"session-{FileNameOf(conversation.Id)}.json");
        var bytes = SessionDocument.Write(conversation, Backend == ConversationKey.ChatGptWeb ? "chatgpt-export" : "claude-export",
            WebLinks.Build(Backend, conversation.Id));
        var existed = File.Exists(path);
        if (existed && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) return Written.Same;
        WriteAtomically(path, bytes);
        return existed ? Written.Updated : Written.Added;
    }

    /// <summary>An id as a file name: as it is when it is plain letters, digits and dashes, else a hash of it.</summary>
    private static string FileNameOf(string id) =>
        id.Length is > 0 and <= 80 && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
            ? id
            : Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id)))[..32].ToLowerInvariant();

    /// <summary>Keeps a file of the export once, under the hash of what it holds; null when it cannot be read.</summary>
    private Func<string, string?, string?> Keeper(ExportPackage package) => (name, fileName) =>
    {
        try
        {
            if (package.Length(name) is > MaxKeptFile) return null;
            byte[] bytes;
            using (var stream = package.Open(name))
            using (var copy = new MemoryStream())
            {
                stream.CopyTo(copy);
                bytes = copy.ToArray();
            }
            var extension = Path.GetExtension(fileName ?? name).ToLowerInvariant();
            if (extension.Length > 8 || extension.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '.')) extension = "";
            var stored = Convert.ToHexString(SHA256.HashData(bytes))[..48].ToLowerInvariant() + extension;
            Directory.CreateDirectory(FilesFolder);
            var path = Path.Combine(FilesFolder, stored);
            if (!File.Exists(path)) WriteAtomically(path, bytes);
            return stored;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    };

    private static void WriteAtomically(string path, byte[] bytes)
    {
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>
/// A conversation as a Gemini CLI session document — the form this tool keeps every imported
/// conversation in: the user's turns with their files, the model's with its thinking, words,
/// files and tool calls, notes as Gemini's own notes.
/// </summary>
internal static class SessionDocument
{
    public static byte[] Write(ExportedConversation conversation, string kind, string? link)
    {
        var times = conversation.Turns.Where(t => t.Time is not null).Select(t => t.Time!.Value).ToList();
        var started = conversation.Created ?? (times.Count > 0 ? times.Min() : DateTimeOffset.UnixEpoch);
        var updated = conversation.Updated ?? (times.Count > 0 ? times.Max() : started);
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            w.WriteStartObject();
            w.WriteString("sessionId", conversation.Id);
            w.WriteString("startTime", Stamp(started));
            w.WriteString("lastUpdated", Stamp(updated));
            w.WriteString("kind", kind);
            if (link is not null) w.WriteString("link", link);
            if (!string.IsNullOrWhiteSpace(conversation.Title)) w.WriteString("summary", conversation.Title.Trim());
            w.WriteStartArray("messages");
            var n = 0;
            foreach (var turn in conversation.Turns)
            {
                w.WriteStartObject();
                w.WriteString("id", $"{conversation.Id}-{n++}");
                w.WriteString("timestamp", Stamp(turn.Time ?? updated));
                switch (turn.Role)
                {
                    case ExportedRole.User:
                        w.WriteString("type", "user");
                        w.WriteStartArray("content");
                        if (turn.Text.Length > 0)
                        {
                            w.WriteStartObject();
                            w.WriteString("text", turn.Text);
                            w.WriteEndObject();
                        }
                        foreach (var file in turn.Files) WriteFile(w, file);
                        w.WriteEndArray();
                        break;

                    case ExportedRole.Assistant:
                        w.WriteString("type", "gemini");
                        if (turn.Thoughts.Count > 0)
                        {
                            w.WriteStartArray("thoughts");
                            foreach (var thought in turn.Thoughts)
                            {
                                w.WriteStartObject();
                                if (thought.Subject is not null) w.WriteString("subject", thought.Subject);
                                w.WriteString("description", thought.Text);
                                w.WriteEndObject();
                            }
                            w.WriteEndArray();
                        }
                        if (turn.Files.Count == 0) w.WriteString("content", turn.Text);
                        else
                        {
                            w.WriteStartArray("content");
                            if (turn.Text.Length > 0)
                            {
                                w.WriteStartObject();
                                w.WriteString("text", turn.Text);
                                w.WriteEndObject();
                            }
                            foreach (var file in turn.Files) WriteFile(w, file);
                            w.WriteEndArray();
                        }
                        if (turn.Calls.Count > 0)
                        {
                            w.WriteStartArray("toolCalls");
                            foreach (var call in turn.Calls)
                            {
                                w.WriteStartObject();
                                w.WriteString("name", call.Name);
                                w.WriteString("args", call.Arguments);
                                if (call.Result is not null) w.WriteString("resultDisplay", call.Result);
                                w.WriteEndObject();
                            }
                            w.WriteEndArray();
                        }
                        break;

                    default:
                        w.WriteString("type", "info");
                        w.WriteString("content", turn.Text);
                        break;
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return buffer.ToArray();
    }

    private static void WriteFile(Utf8JsonWriter w, ExportedFile file)
    {
        w.WriteStartObject();
        if (file.Stored is not null)
        {
            w.WriteStartObject("launcherFile");
            w.WriteString("name", file.Name);
            if (file.MimeType is not null) w.WriteString("mimeType", file.MimeType);
            w.WriteString("path", "../files/" + file.Stored);
        }
        else
        {
            w.WriteStartObject("fileData");
            w.WriteString("mimeType", file.MimeType ?? "不在导出里");
            w.WriteString("fileUri", file.Name);
        }
        w.WriteEndObject();
        w.WriteEndObject();
    }

    private static string Stamp(DateTimeOffset time) =>
        time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}

/// <summary>Small readers of the JSON the exports are made of: a missing or mistyped value is null, never an exception.</summary>
internal static class ExportJson
{
    public static JsonElement Prop(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    public static string? Text(JsonElement element, string name) =>
        Prop(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    public static bool Bool(JsonElement element, string name) => Prop(element, name).ValueKind == JsonValueKind.True;

    public static IEnumerable<JsonElement> Items(JsonElement element, string name) =>
        Prop(element, name) is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray() : [];

    /// <summary>A time given in seconds since 1970, as ChatGPT writes it; null when there is none.</summary>
    public static DateTimeOffset? Seconds(JsonElement element, string name)
    {
        var value = Prop(element, name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var seconds) || !(seconds > 0) || seconds > 1e11)
            return null;
        return DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(seconds * 1000));
    }

    /// <summary>A time written as ISO 8601, as Claude writes it; null when there is none.</summary>
    public static DateTimeOffset? Iso(JsonElement element, string name) =>
        Text(element, name) is { Length: > 0 } text
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)
            ? time
            : null;

    /// <summary>Anything readable: a string as it is, anything else as compact JSON.</summary>
    public static string Readable(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? "",
        JsonValueKind.Undefined or JsonValueKind.Null => "",
        _ => element.GetRawText(),
    };

    /// <summary>The image types a file name names; null for anything else.</summary>
    public static string? ImageType(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        _ => null,
    };
}
