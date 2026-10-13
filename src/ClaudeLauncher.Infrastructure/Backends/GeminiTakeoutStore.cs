using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace ClaudeLauncher.Infrastructure.Backends;

/// <summary>What an import did.</summary>
/// <param name="Found">Prompts in the export.</param>
/// <param name="Added">Of those, the ones not imported before.</param>
/// <param name="Total">Prompts kept now, from every import.</param>
/// <param name="Conversations">The conversations they make up.</param>
/// <param name="ActivityFiles">Activity files read; 0 when the export had no Gemini activity.</param>
/// <param name="Unreadable">Gemini's activity was there, but in a layout or with dates this reader does not know.</param>
public sealed record GeminiTakeoutImport(
    int Found, int Added, int Total, int Conversations, int ActivityFiles, int Skipped, bool Unreadable);

/// <summary>
/// This tool's own copy of Gemini's web history, in its data root: every prompt from every
/// import (<c>entries.json</c>), the files sent with them (<c>files\</c>), and one document per
/// conversation (<c>conversations\session-&lt;id&gt;.json</c>) in the form Gemini CLI keeps its
/// sessions, so the list, the reader, the search index and the exporter read it as they read
/// those. A later import adds to it rather than replacing it: what Google has since let go of
/// stays here until the import is removed.
/// </summary>
public sealed class GeminiTakeoutStore(string root)
{
    public string Root { get; } = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    public string ConversationsFolder => Path.Combine(Root, "conversations");

    public string FilesFolder => Path.Combine(Root, "files");

    private string EntriesFile => Path.Combine(Root, "entries.json");

    public bool Exists => Directory.Exists(ConversationsFolder);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

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

    public List<GeminiTakeoutEntry> LoadEntries()
    {
        if (!File.Exists(EntriesFile)) return [];
        using var stream = new FileStream(EntriesFile, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            return JsonSerializer.Deserialize<Saved>(stream, Json)?.Entries ?? [];
        }
        catch (JsonException ex)
        {
            throw new IOException("导入的 Gemini 网页记录已损坏：" + EntriesFile, ex);
        }
    }

    /// <summary>Reads the exports and adds what they hold to the store; the exports themselves are only read.</summary>
    public GeminiTakeoutImport Import(IReadOnlyList<string> exports, CancellationToken cancellation = default)
    {
        var read = GeminiTakeout.Read(exports, KeepFile, cancellation);
        if (read.ActivityFiles == 0 || read.Entries.Count == 0)
            return new GeminiTakeoutImport(0, 0, 0, 0, read.ActivityFiles, read.Skipped, read.Unreadable);

        // Keyed afresh, so a record imported before — from the other format, or by an earlier
        // version that kept milliseconds in the key — is the same record now.
        var merged = new Dictionary<string, GeminiTakeoutEntry>(StringComparer.Ordinal);
        foreach (var kept in LoadEntries())
        {
            var key = GeminiTakeout.KeyOf(kept.Time, kept.Conversation, kept.Prompt);
            merged[key] = kept with { Key = key };
        }
        var added = 0;
        foreach (var entry in read.Entries)
        {
            if (!merged.ContainsKey(entry.Key)) added++;
            merged[entry.Key] = entry;
        }
        var all = merged.Values.OrderBy(e => e.Time).ToList();
        Directory.CreateDirectory(Root);
        WriteAtomically(EntriesFile, JsonSerializer.SerializeToUtf8Bytes(new Saved(1, all), Json));
        var conversations = WriteConversations(all, cancellation);
        return new GeminiTakeoutImport(read.Entries.Count, added, all.Count, conversations, read.ActivityFiles, read.Skipped, false);
    }

    /// <summary>What is imported now, for the import window: conversations, prompts, and when the last import was.</summary>
    public (int Conversations, int Prompts, DateTime? LastImport) Describe()
    {
        if (!Exists) return (0, 0, null);
        var entries = File.Exists(EntriesFile) ? LoadEntries().Count : 0;
        return (SessionFiles().Count(), entries, File.Exists(EntriesFile) ? File.GetLastWriteTime(EntriesFile) : null);
    }

    /// <summary>Removes everything imported; the number of conversations it held.</summary>
    public int Remove()
    {
        var count = SessionFiles().Count();
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        return count;
    }

    private sealed record Saved(int Version, List<GeminiTakeoutEntry> Entries);

    /// <summary>A file sent with a prompt, kept once under the hash of what it holds.</summary>
    private string? KeepFile(string name, Func<Stream> open)
    {
        byte[] bytes;
        using (var stream = open())
        using (var copy = new MemoryStream())
        {
            stream.CopyTo(copy);
            bytes = copy.ToArray();
        }
        var extension = Path.GetExtension(name).ToLowerInvariant();
        if (extension.Length > 8 || extension.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '.')) extension = "";
        var stored = GeminiTakeout.Hex(SHA256.HashData(bytes), 24) + extension;
        Directory.CreateDirectory(FilesFolder);
        var path = Path.Combine(FilesFolder, stored);
        if (!File.Exists(path)) WriteAtomically(path, bytes);
        return stored;
    }

    /// <summary>
    /// One document per conversation. A document that comes out the same is left as it was, so
    /// nothing that reads it — the list, the index — sees a change; one whose conversation is
    /// gone is removed.
    /// </summary>
    private int WriteConversations(List<GeminiTakeoutEntry> entries, CancellationToken cancellation)
    {
        Directory.CreateDirectory(ConversationsFolder);
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var conversations = GeminiTakeout.Conversations(entries);
        foreach (var (id, list) in conversations)
        {
            cancellation.ThrowIfCancellationRequested();
            var path = Path.Combine(ConversationsFolder, $"session-{id}.json");
            written.Add(path);
            var bytes = Document(id, list);
            if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) continue;
            WriteAtomically(path, bytes);
        }
        foreach (var stale in SessionFiles().Where(f => !written.Contains(f)))
        {
            try { File.Delete(stale); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* next import */ }
        }
        return conversations.Count;
    }

    /// <summary>A conversation as a Gemini CLI session document: prompts as the user's, answers as Gemini's, in Markdown.</summary>
    private static byte[] Document(string id, List<GeminiTakeoutEntry> entries)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            w.WriteStartObject();
            w.WriteString("sessionId", id);
            w.WriteString("startTime", Stamp(entries[0].Time));
            w.WriteString("lastUpdated", Stamp(entries[^1].Time));
            w.WriteString("kind", "google-takeout");
            if (GeminiTakeout.IsConversationId(id)) w.WriteString("link", "https://gemini.google.com/app/" + id);
            w.WriteStartArray("messages");
            foreach (var e in entries)
            {
                var at = Stamp(e.Time);
                w.WriteStartObject();
                w.WriteString("id", e.Key + "-u");
                w.WriteString("timestamp", at);
                w.WriteString("type", "user");
                w.WriteStartArray("content");
                if (e.Prompt.Length > 0)
                {
                    w.WriteStartObject();
                    w.WriteString("text", e.Prompt);
                    w.WriteEndObject();
                }
                foreach (var file in e.Files)
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
                        w.WriteString("mimeType", "不在导出里");
                        w.WriteString("fileUri", file.Name);
                    }
                    w.WriteEndObject();
                    w.WriteEndObject();
                }
                foreach (var page in e.Pages)
                {
                    w.WriteStartObject();
                    w.WriteStartObject("fileData");
                    w.WriteString("mimeType", "网页");
                    w.WriteString("fileUri", page.Name == page.Url ? page.Url : $"{page.Name}  {page.Url}");
                    w.WriteEndObject();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();

                foreach (var note in e.Notes)
                {
                    w.WriteStartObject();
                    w.WriteString("id", e.Key + "-n");
                    w.WriteString("timestamp", at);
                    w.WriteString("type", "info");
                    w.WriteString("content", note);
                    w.WriteEndObject();
                }

                var answer = HtmlMarkdown.ToMarkdown(e.ResponseHtml);
                if (e.GeneratedImages.Count > 0)
                    answer = (answer + "\n\n" + string.Join("\n\n", e.GeneratedImages.Select((url, i) =>
                        $"[生成的图片 {i + 1}（在网页上看）]({url})"))).Trim();
                if (answer.Length > 0)
                {
                    w.WriteStartObject();
                    w.WriteString("id", e.Key + "-m");
                    w.WriteString("timestamp", at);
                    w.WriteString("type", "gemini");
                    w.WriteString("content", answer);
                    w.WriteEndObject();
                }
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return buffer.ToArray();
    }

    private static string Stamp(DateTimeOffset time) =>
        time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static void WriteAtomically(string path, byte[] bytes)
    {
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }
}
