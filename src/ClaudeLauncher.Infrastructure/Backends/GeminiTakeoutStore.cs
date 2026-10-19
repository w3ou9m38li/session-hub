using System.Buffers;
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

    /// <summary>When an import last wrote the store — the time of its record of prompts, one look at it; null before the first.</summary>
    public DateTime? LastImportUtc
    {
        get
        {
            var entries = new FileInfo(EntriesFile);
            return entries.Exists ? entries.LastWriteTimeUtc : null;
        }
    }

    /// <summary>
    /// Reads the exports and adds what they hold to the store; the exports themselves are only read.
    /// Stopped while it reads, it leaves the store as it was; once it writes, it finishes — the
    /// record of prompts and the conversations made of it are written as one, never one without the other.
    /// </summary>
    /// <param name="progress">Activity records read so far.</param>
    public GeminiTakeoutImport Import(IReadOnlyList<string> exports, IProgress<int>? progress = null,
        CancellationToken cancellation = default)
    {
        // The files this import kept for its prompts: taken out again when it stops before writing.
        var keptNow = new List<string>();
        GeminiTakeoutRead read;
        List<GeminiTakeoutEntry> all;
        var added = 0;
        try
        {
            read = GeminiTakeout.Read(exports, (name, open) => KeepFile(name, open, keptNow), cancellation, progress);
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
            foreach (var entry in read.Entries)
            {
                if (merged.TryGetValue(entry.Key, out var kept)) merged[entry.Key] = WithKeptFiles(entry, kept);
                else
                {
                    added++;
                    merged[entry.Key] = entry;
                }
            }
            all = merged.Values.OrderBy(e => e.Time).ToList();
            cancellation.ThrowIfCancellationRequested();        // the last moment to stop with nothing changed
        }
        catch
        {
            foreach (var file in keptNow)
            {
                try { File.Delete(file); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* unused, and harmless */ }
            }
            throw;
        }
        Directory.CreateDirectory(Root);
        WriteAtomically(EntriesFile, JsonSerializer.SerializeToUtf8Bytes(new Saved(1, all), Json));
        var conversations = WriteConversations(all, CancellationToken.None);
        return new GeminiTakeoutImport(read.Entries.Count, added, all.Count, conversations, read.ActivityFiles, read.Skipped, false);
    }

    /// <summary>
    /// What is imported now, for the import window: conversations, prompts — null when their
    /// record cannot be read, which leaves the rest as it is — and when the last import was.
    /// </summary>
    public (int Conversations, int? Prompts, DateTime? LastImport) Describe()
    {
        if (!Exists) return (0, 0, null);
        var entries = new FileInfo(EntriesFile);
        return entries.Exists
            ? (SessionFiles().Count(), CountEntries(), entries.LastWriteTime)
            : (SessionFiles().Count(), 0, null);
    }

    /// <summary>
    /// How many prompts entries.json holds, counted as it streams past rather than read in: it
    /// holds every answer's page too, tens of megabytes for a long history. Null when it cannot be read.
    /// </summary>
    private int? CountEntries()
    {
        try
        {
            using var stream = new FileStream(EntriesFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                64 * 1024, FileOptions.SequentialScan);
            return CountEntries(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>The number of items in the <c>Entries</c> list of a saved store, none of them taken in.</summary>
    private static int CountEntries(Stream stream)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            var state = new JsonReaderState(new JsonReaderOptions { MaxDepth = 256 });
            int held = 0, count = 0;
            bool final = false, named = false, inEntries = false;
            while (true)
            {
                if (held == buffer.Length)
                {
                    // One value longer than the buffer (a long answer): room for all of it.
                    var larger = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
                    buffer.AsSpan(0, held).CopyTo(larger);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = larger;
                }
                var read = stream.Read(buffer, held, buffer.Length - held);
                held += read;
                final = read == 0;
                var reader = new Utf8JsonReader(buffer.AsSpan(0, held), final, state);
                while (reader.Read())
                {
                    if (inEntries)
                    {
                        if (reader.CurrentDepth == 1) return count;         // the list's end: what follows is not needed
                        if (reader.CurrentDepth == 2 && reader.TokenType is not (JsonTokenType.EndObject or JsonTokenType.EndArray))
                            count++;
                    }
                    else if (named)
                    {
                        if (reader.TokenType != JsonTokenType.StartArray) return 0;
                        inEntries = true;
                    }
                    else
                    {
                        named = reader.CurrentDepth == 1 && reader.TokenType == JsonTokenType.PropertyName
                                && reader.ValueTextEquals("Entries"u8);
                    }
                }
                if (final) return 0;        // a store without a list of entries holds none
                var consumed = (int)reader.BytesConsumed;
                buffer.AsSpan(consumed, held - consumed).CopyTo(buffer);
                held -= consumed;
                state = reader.CurrentState;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Removes everything imported; the number of conversations it held.</summary>
    public int Remove()
    {
        var count = SessionFiles().Count();
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        return count;
    }

    private sealed record Saved(int Version, List<GeminiTakeoutEntry> Entries);

    /// <summary>
    /// A record read again, keeping the copy an earlier import made of any file this export
    /// could not give (damaged, locked, or not in it): that copy is still in <c>files\</c>, and a
    /// later export must not take the picture out of a conversation that had it.
    /// </summary>
    private static GeminiTakeoutEntry WithKeptFiles(GeminiTakeoutEntry entry, GeminiTakeoutEntry kept)
    {
        if (entry.Files.TrueForAll(f => f.Stored is not null)) return entry;
        return entry with
        {
            Files = entry.Files.Select(f => f.Stored is null && kept.Files.Find(k => k.Name == f.Name)?.Stored is { } stored
                ? f with { Stored = stored }
                : f).ToList(),
        };
    }

    /// <summary>
    /// A file sent with a prompt, kept once under the hash of what it holds; null when the export
    /// cannot give it. One kept for the first time is added to <paramref name="keptNow"/>.
    /// </summary>
    private string? KeepFile(string name, Func<Stream> open, List<string> keptNow)
    {
        byte[] bytes;
        try
        {
            using var stream = open();
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            bytes = copy.ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // Damaged in the zip, locked, or online-only: named, not kept, and the prompts still come in.
            // Only the read: a store that cannot be written to still stops the import.
            return null;
        }
        var extension = Path.GetExtension(name).ToLowerInvariant();
        if (extension.Length > 8 || extension.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '.')) extension = "";
        var stored = GeminiTakeout.Hex(SHA256.HashData(bytes), 24) + extension;
        Directory.CreateDirectory(FilesFolder);
        var path = Path.Combine(FilesFolder, stored);
        if (!File.Exists(path))
        {
            WriteAtomically(path, bytes);
            keptNow.Add(path);
        }
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
