using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using static ClaudeLauncher.Infrastructure.Backends.ExportJson;

namespace ClaudeLauncher.Infrastructure.Backends;

/// <summary>
/// Reads ChatGPT's data export — the zip ChatGPT mails a link to, from Settings → Data controls →
/// Export data. <c>conversations.json</c> (split into numbered files in a large export) holds
/// every conversation as a tree of messages: every edited question and regenerated answer is a
/// branch of it, and the conversation as it was last seen is the branch that ends at
/// <c>current_node</c>. The model's thinking, its calls to tools (code, search, pictures) and what
/// they returned are messages of their own; the app's own instructions are hidden ones. Pictures
/// sent or made are files beside it, named after the id a message points to them by.
/// </summary>
internal sealed partial class ChatGptExport(ExportPackage package, Func<string, string?, string?> keep) : IExportReader
{
    private Dictionary<string, string>? _files;

    public bool IsOtherKind(JsonElement conversation) =>
        conversation.ValueKind == JsonValueKind.Object && conversation.TryGetProperty("chat_messages", out _);

    public ExportedConversation? Read(JsonElement conversation)
    {
        if (Prop(conversation, "mapping") is not { ValueKind: JsonValueKind.Object } mapping) return null;
        var id = Text(conversation, "conversation_id") ?? Text(conversation, "id");
        if (string.IsNullOrWhiteSpace(id)) return null;
        var created = Seconds(conversation, "create_time");
        var turns = new TurnBuilder();
        foreach (var node in Branch(mapping, Text(conversation, "current_node")))
            if (Prop(node, "message") is { ValueKind: JsonValueKind.Object } message)
                Add(message, turns, created);
        return new ExportedConversation(id, Text(conversation, "title"), created, Seconds(conversation, "update_time"),
            turns.Turns);
    }

    /// <summary>
    /// The nodes from the root to <paramref name="current"/>, following each node's parent; without
    /// a current node, from the root down through the last child each time — the latest answer.
    /// </summary>
    internal static List<JsonElement> Branch(JsonElement mapping, string? current)
    {
        var path = new List<JsonElement>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (current is not null && mapping.TryGetProperty(current, out _))
        {
            var at = current;
            while (at is not null && seen.Add(at) && mapping.TryGetProperty(at, out var node))
            {
                path.Add(node);
                at = Text(node, "parent");
            }
            path.Reverse();
            return path;
        }
        string? down = null;
        foreach (var node in mapping.EnumerateObject())
            if (Text(node.Value, "parent") is null)
            {
                down = node.Name;
                break;
            }
        while (down is not null && seen.Add(down) && mapping.TryGetProperty(down, out var node))
        {
            path.Add(node);
            down = Items(node, "children").LastOrDefault() is { ValueKind: JsonValueKind.String } child ? child.GetString() : null;
        }
        return path;
    }

    private void Add(JsonElement message, TurnBuilder turns, DateTimeOffset? fallback)
    {
        var metadata = Prop(message, "metadata");
        if (Bool(metadata, "is_visually_hidden_from_conversation")) return;
        var author = Prop(message, "author");
        var content = Prop(message, "content");
        var type = Text(content, "content_type") ?? "text";
        if (type is "user_editable_context" or "model_editable_context") return;      // custom instructions, memory
        var time = Seconds(message, "create_time") ?? fallback;

        switch (Text(author, "role"))
        {
            case "user":
            {
                var files = new List<ExportedFile>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var words = Words(content, files, seen);
                files.AddRange(Attachments(metadata, seen));
                turns.User(time, words.Trim(), files);
                break;
            }
            case "assistant":
            {
                var recipient = Text(message, "recipient") ?? "all";
                if (type == "thoughts")
                {
                    foreach (var thought in Items(content, "thoughts"))
                        turns.Thought(time, Text(thought, "summary"), (Text(thought, "content") ?? "").Trim());
                }
                else if (type == "reasoning_recap")
                {
                    // 「Thought for 12s」: what the thinking above already shows.
                }
                else if (recipient != "all")
                {
                    turns.Call(time, recipient, Words(content, null, null).Trim());
                }
                else
                {
                    var files = new List<ExportedFile>();
                    var words = Words(content, files, new HashSet<string>(StringComparer.Ordinal));
                    if (type == "code")
                    {
                        var language = Text(content, "language") is { Length: > 0 } named && named != "unknown" ? named : "";
                        words = $"```{language}\n{words}\n```";
                    }
                    turns.Text(time, Clean(words).Trim());
                    foreach (var file in files) turns.File(time, file);
                }
                break;
            }
            case "tool":
            {
                var files = new List<ExportedFile>();
                var output = Words(content, files, new HashSet<string>(StringComparer.Ordinal)).Trim();
                if (output.Length > 0 || files.Count == 0) turns.Result(time, Text(author, "name"), output);
                // Pictures a tool made are part of the answer.
                foreach (var file in files) turns.File(time, file);
                break;
            }
            // The app's own system messages are its instructions to the model: not shown.
        }
    }

    /// <summary>
    /// What a message's content says, as text. Pictures among its parts go to <paramref name="files"/>
    /// (when given), each once.
    /// </summary>
    private string Words(JsonElement content, List<ExportedFile>? files, HashSet<string>? seen)
    {
        switch (Text(content, "content_type"))
        {
            case "text" or "multimodal_text" or null:
            {
                var text = new StringBuilder();
                foreach (var part in Items(content, "parts"))
                {
                    if (part.ValueKind == JsonValueKind.String)
                    {
                        if (text.Length > 0) text.Append('\n');
                        text.Append(part.GetString());
                    }
                    else if (Text(part, "content_type") is "image_asset_pointer")
                    {
                        if (files is not null && Picture(Text(part, "asset_pointer"), seen) is { } picture) files.Add(picture);
                    }
                    else if (Text(part, "text") is { Length: > 0 } said)      // a spoken message's transcript
                    {
                        if (text.Length > 0) text.Append('\n');
                        text.Append(said);
                    }
                }
                return text.ToString();
            }
            case "code" or "execution_output":
                return Text(content, "text") ?? "";
            case "tether_browsing_display":
                return Text(content, "result") ?? Text(content, "summary") ?? "";
            case "tether_quote":
                return string.Join("\n", new[] { Text(content, "title"), Text(content, "url"), Text(content, "text") }
                    .Where(s => !string.IsNullOrWhiteSpace(s)));
            case "system_error":
                return "出错：" + string.Join(" ", new[] { Text(content, "name"), Text(content, "text") }.Where(s => !string.IsNullOrWhiteSpace(s)));
            default:
                // A kind this reader does not know: its words, when it has any.
                return Text(content, "text") ?? Text(content, "result")
                    ?? string.Join("\n", Items(content, "parts").Where(p => p.ValueKind == JsonValueKind.String).Select(p => p.GetString()));
        }
    }

    /// <summary>A picture a message points to: kept from the export, or — when the export does not have it — named.</summary>
    private ExportedFile? Picture(string? pointer, HashSet<string>? seen)
    {
        var id = PointerId(pointer);
        if (id is null || seen?.Add(id) == false) return null;
        if (!Files().TryGetValue(id, out var name)) return new ExportedFile("图片", "图片，不在导出里", null);
        var file = name[(name.LastIndexOf('/') + 1)..];
        var type = ImageType(file);
        return new ExportedFile(file, type ?? "image/png", keep(name, type is null ? file + ".png" : file));
    }

    /// <summary>Files sent with a message that are not among its pictures: kept when the export has them, named when not.</summary>
    private IEnumerable<ExportedFile> Attachments(JsonElement metadata, HashSet<string> seen)
    {
        foreach (var attachment in Items(metadata, "attachments"))
        {
            var id = Text(attachment, "id");
            var name = Text(attachment, "name") ?? id ?? "附件";
            if (id is not null && !seen.Add(id)) continue;
            var mime = Text(attachment, "mime_type") ?? Text(attachment, "mimeType") ?? ImageType(name);
            if (id is not null && Files().TryGetValue(id, out var inExport))
                yield return new ExportedFile(name, mime, keep(inExport, name));
            else
                yield return new ExportedFile(name, "不在导出里", null);
        }
    }

    /// <summary>The export's files by the id at the start of their names: <c>file-AbC…-name.png</c>, <c>file_00ab…-uuid.png</c>.</summary>
    private Dictionary<string, string> Files()
    {
        if (_files is not null) return _files;
        _files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in package.Names)
            if (FileId(name[(name.LastIndexOf('/') + 1)..]) is { } id) _files.TryAdd(id, name);
        return _files;
    }

    /// <summary>The id a file of the export is named by; null for a file not named that way.</summary>
    internal static string? FileId(string fileName)
    {
        if (fileName.Length <= 5 || !(fileName.StartsWith("file-", StringComparison.Ordinal)
                                      || fileName.StartsWith("file_", StringComparison.Ordinal)))
            return null;
        var cut = fileName.IndexOfAny(['-', '.'], 5);
        return cut < 0 ? fileName : fileName[..cut];
    }

    /// <summary>The file id an asset pointer names: <c>file-service://file-AbC</c>, <c>sediment://file_00ab</c>.</summary>
    internal static string? PointerId(string? pointer)
    {
        if (string.IsNullOrWhiteSpace(pointer)) return null;
        var at = pointer.IndexOf("://", StringComparison.Ordinal);
        var rest = at < 0 ? pointer : pointer[(at + 3)..];
        var end = rest.IndexOfAny(['?', '#', '/']);
        return FileId(end < 0 ? rest : rest[..end]);
    }

    /// <summary>
    /// The answer without the markers ChatGPT's search leaves in its text: private-use characters
    /// around citations (<c>citeturn0search3</c>) and 【4†source】 notes.
    /// </summary>
    internal static string Clean(string text) =>
        text.Contains('') || text.Contains('【')
            ? PrivateUse().Replace(CitationMarks().Replace(text, ""), "")
            : text;

    [GeneratedRegex("[^]{0,400}|【\\d+(?::\\d+)?†[^】]{0,80}】")]
    private static partial Regex CitationMarks();

    [GeneratedRegex("[-]")]
    private static partial Regex PrivateUse();
}
