using System.Text;
using System.Text.Json;
using static ClaudeLauncher.Infrastructure.Backends.ExportJson;

namespace ClaudeLauncher.Infrastructure.Backends;

/// <summary>
/// Reads Claude's data export — the zip Claude mails a link to, from Settings → Privacy → Export
/// data. <c>conversations.json</c> holds every conversation with its messages, the person's
/// (<c>human</c>) and Claude's (<c>assistant</c>), each a list of blocks: text, thinking, a tool's
/// use and its result. Files sent are only named — the text Claude took out of a document comes
/// with it — and no picture is in the export. When messages say which one each follows, the
/// conversation is the branch last written to; edited questions left the others behind.
/// </summary>
internal sealed class ClaudeExport : IExportReader
{
    public bool IsOtherKind(JsonElement conversation) =>
        conversation.ValueKind == JsonValueKind.Object && conversation.TryGetProperty("mapping", out _);

    public ExportedConversation? Read(JsonElement conversation)
    {
        if (Prop(conversation, "chat_messages") is not { ValueKind: JsonValueKind.Array } messages) return null;
        var id = Text(conversation, "uuid") ?? Text(conversation, "id");
        if (string.IsNullOrWhiteSpace(id)) return null;
        var created = Iso(conversation, "created_at");
        var turns = new TurnBuilder();
        foreach (var message in Branch(messages))
        {
            var time = Iso(message, "created_at") ?? created;
            switch (Text(message, "sender"))
            {
                case "human":
                    User(message, time, turns);
                    break;
                case "assistant":
                    Assistant(message, time, turns);
                    break;
            }
        }
        return new ExportedConversation(id, Text(conversation, "name"), created, Iso(conversation, "updated_at"), turns.Turns);
    }

    /// <summary>
    /// The messages of the branch last written to, when they say which message each follows;
    /// otherwise every message, in the order the export lists them.
    /// </summary>
    internal static List<JsonElement> Branch(JsonElement messages)
    {
        var all = messages.EnumerateArray().Where(m => m.ValueKind == JsonValueKind.Object).ToList();
        if (all.Count == 0 || !all.Any(m => Text(m, "parent_message_uuid") is not null)) return all;
        var byId = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var message in all)
            if (Text(message, "uuid") is { } uuid) byId.TryAdd(uuid, message);
        // The leaf: the message written last — on the same time, the later one in the list.
        var leaf = all.Select((m, i) => (Message: m, Index: i))
            .OrderBy(x => Iso(x.Message, "created_at") ?? DateTimeOffset.MinValue).ThenBy(x => x.Index).Last().Message;
        var chain = new List<JsonElement>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        JsonElement? at = leaf;
        while (at is { } message && Text(message, "uuid") is { } uuid && seen.Add(uuid))
        {
            chain.Add(message);
            at = Text(message, "parent_message_uuid") is { } parent && byId.TryGetValue(parent, out var previous) ? previous : null;
        }
        chain.Reverse();
        return chain;
    }

    private static void User(JsonElement message, DateTimeOffset? time, TurnBuilder turns)
    {
        var words = Blocks(message, "text");
        if (words.Length == 0) words = Text(message, "text") ?? "";
        var files = new List<ExportedFile>();
        var contents = new List<(string Name, string Text)>();
        foreach (var attachment in Items(message, "attachments"))
        {
            var name = Text(attachment, "file_name") ?? "附件";
            var extracted = Text(attachment, "extracted_content");
            files.Add(new ExportedFile(name, string.IsNullOrEmpty(extracted) ? "不在导出里" : "取出的文字在下面", null));
            if (!string.IsNullOrEmpty(extracted)) contents.Add((name, extracted));
        }
        foreach (var file in Items(message, "files").Concat(Items(message, "files_v2")))
            if (Text(file, "file_name") is { Length: > 0 } name && !files.Any(f => f.Name == name))
                files.Add(new ExportedFile(name, ImageType(name) is null ? "文件，不在导出里" : "图片，不在导出里", null));
        turns.User(time, words.Trim(), files);
        foreach (var (name, text) in contents) turns.Note(time, $"附件「{name}」里的文字\n\n{text}");
    }

    private static void Assistant(JsonElement message, DateTimeOffset? time, TurnBuilder turns)
    {
        if (Prop(message, "content") is not { ValueKind: JsonValueKind.Array } blocks || blocks.GetArrayLength() == 0)
        {
            turns.Text(time, (Text(message, "text") ?? "").Trim());
            return;
        }
        foreach (var block in blocks.EnumerateArray())
        {
            var when = Iso(block, "start_timestamp") ?? time;
            switch (Text(block, "type"))
            {
                case "text":
                    turns.Text(when, (Text(block, "text") ?? "").Trim());
                    break;
                case "thinking":
                    var summary = Items(block, "summaries").Select(s => Text(s, "summary")).LastOrDefault(s => !string.IsNullOrWhiteSpace(s));
                    turns.Thought(when, summary, (Text(block, "thinking") ?? "").Trim());
                    break;
                case "tool_use":
                    turns.Call(when, Text(block, "name") ?? "工具", Readable(Prop(block, "input")), Text(block, "id"));
                    break;
                case "tool_result":
                    var output = Prop(block, "content") is { ValueKind: JsonValueKind.Array } parts
                        ? string.Join("\n\n", parts.EnumerateArray().Select(p => Text(p, "text") ?? Readable(p)))
                        : Readable(Prop(block, "content"));
                    turns.Result(when, Text(block, "name"), output.Trim(), Text(block, "tool_use_id"));
                    break;
                default:
                    if (Text(block, "text") is { Length: > 0 } text) turns.Text(when, text.Trim());
                    break;
            }
        }
    }

    /// <summary>The text of a message's blocks of one type, joined.</summary>
    private static string Blocks(JsonElement message, string type)
    {
        var text = new StringBuilder();
        foreach (var block in Items(message, "content"))
            if (Text(block, "type") == type && Text(block, "text") is { Length: > 0 } words)
            {
                if (text.Length > 0) text.Append("\n\n");
                text.Append(words);
            }
        return text.ToString();
    }
}
