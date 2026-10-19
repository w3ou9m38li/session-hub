using System.Buffers;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace ClaudeLauncher.Infrastructure.Backends;

/// <summary>An export found among the downloads: where it is, when it was last written (UTC), how big it is.</summary>
public sealed record FoundExport(string Path, DateTime SavedUtc, long Bytes);

/// <summary>
/// Whose export a file is. A web export with neither app's marker files and a first
/// conversation that does not tell is <see cref="ChatGpt"/> and <see cref="Claude"/> both.
/// </summary>
[Flags]
public enum ExportKinds
{
    None = 0,
    ChatGpt = 1,
    Claude = 2,
    GeminiTakeout = 4,
}

/// <summary>
/// Tells exports apart — ChatGPT's or Claude's data export, a Google Takeout with Gemini's
/// activity in it — by the names of the files a zip lists, and where those leave ChatGPT's and
/// Claude's undecided, by the fields of the first conversation in it; nothing else of what the
/// files hold is read. Asked when an import window opens, for files dropped on the window, and
/// after a sync through <see cref="DownloadsScan"/> — in the background, at low priority, and
/// only for zips that arrived since it last looked.
/// </summary>
public static class ExportFinder
{
    /// <summary>How many of the newest zips are looked into.</summary>
    private const int Look = 40;

    private const ExportKinds Web = ExportKinds.ChatGpt | ExportKinds.Claude;

    /// <summary>The Downloads folder — where the user moved it, else the one in the profile.</summary>
    public static string DownloadsFolder =>
        KnownDownloadsFolder() ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    /// <summary>
    /// The zip in <paramref name="folder"/> (not its subfolders) that arrived last and is an export
    /// of <paramref name="kind"/>; with <paramref name="savedAfter"/>, only one that arrived after it
    /// — an export imported already is not offered again. A zip kept only online (OneDrive) is not
    /// opened: that would download it. Null when there is none.
    /// </summary>
    /// <param name="passOver">A zip not to offer — found to hold nothing, say: the next older one may be.</param>
    /// <param name="importedAlready">
    /// A zip imported already: the look ends there. An older export is not offered in its place —
    /// it holds less, not what the newer one lacked.
    /// </param>
    public static FoundExport? Newest(ExportKinds kind, string folder, DateTime? savedAfter = null,
        Func<FoundExport, bool>? passOver = null, Func<FoundExport, bool>? importedAlready = null)
    {
        List<FileInfo> zips;
        try
        {
            if (!Directory.Exists(folder)) return null;
            zips = new DirectoryInfo(folder).EnumerateFiles("*.zip", SearchOption.TopDirectoryOnly)
                .OrderByDescending(ArrivalOf).Take(Look).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        foreach (var zip in zips)
        {
            if (savedAfter is { } after && ArrivalOf(zip) <= after.ToUniversalTime()) break;
            if (OnlineOnly(zip.Attributes)) continue;
            var found = new FoundExport(zip.FullName, zip.LastWriteTimeUtc, zip.Length);
            if (importedAlready?.Invoke(found) == true) return null;
            if (passOver?.Invoke(found) == true) continue;
            if ((KindOf(zip.FullName) & kind) != 0) return found;
        }
        return null;
    }

    /// <summary>
    /// Whose export a file dropped on the window is: a zip; a list of conversations
    /// (<c>conversations*.json</c>) or the folder it was unpacked into; Takeout's activity record
    /// (<c>MyActivity.json</c> / <c>.html</c>, under whatever name). <see cref="ExportKinds.None"/>
    /// for anything else, and for what cannot be read.
    /// </summary>
    public static ExportKinds KindOf(string path)
    {
        try
        {
            if (Directory.Exists(path)) return OfFolder(path);
            var name = Path.GetFileName(path);
            if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using var stream = OpenShared(path);
                return Classify(stream);
            }
            if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                using var stream = OpenShared(path);
                if (IsConversationList(name)) return ConversationKind(stream) is var kind and not ExportKinds.None ? kind : Web;
                return IsGeminiActivity(PeekFirst(stream), GeminiTakeout.InGeminiFolder(path)) ? ExportKinds.GeminiTakeout : ExportKinds.None;
            }
            if (name.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))
                return IsActivityPage(path) ? ExportKinds.GeminiTakeout : ExportKinds.None;
            return ExportKinds.None;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                       or NotSupportedException or JsonException or ArgumentException)
        {
            return ExportKinds.None;
        }
    }

    /// <summary>
    /// Whose export a zip is, from one read of its list of files — names only, no entry objects —
    /// and, when that leaves ChatGPT's and Claude's undecided, from the first conversation it holds.
    /// </summary>
    /// <exception cref="InvalidDataException">Not a zip, or a damaged one.</exception>
    /// <exception cref="IOException">The zip could not be read just now (still downloading, locked).</exception>
    internal static ExportKinds Classify(Stream zip)
    {
        var lists = new List<string>();
        var kinds = Classify(ZipNames.Of(zip), lists);
        if ((kinds & Web) != Web) return kinds;
        // Undecided: a list of conversations and nothing else that tells — a small zip, opened as one.
        zip.Position = 0;
        using var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);
        return (kinds & ~Web) | Resolve(ExportPackage.ConversationFilesOf(lists), name =>
            archive.Entries.FirstOrDefault(e => e.FullName.Replace('\\', '/') == name)?.Open()
            ?? throw new InvalidDataException("压缩包的文件列表已损坏"));
    }

    /// <summary>Whose export the names a zip lists are: a zip that could be either app's is both.</summary>
    internal static ExportKinds Classify(IEnumerable<string> names) => Classify(names, null);

    /// <param name="lists">Given, collects the names of the lists of conversations.</param>
    private static ExportKinds Classify(IEnumerable<string> names, List<string>? lists)
    {
        bool takeout = false, conversations = false, chatGpt = false, claude = false;
        foreach (var name in names)
        {
            var slash = name.LastIndexOf('/');
            var file = name.AsSpan(slash + 1);
            if (!takeout && (file.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                && slash > 0 && InTakeoutsGeminiFolder(name.AsSpan(0, slash)))
                takeout = true;
            if (IsConversationList(file))
            {
                conversations = true;
                lists?.Add(name);
            }
            // ChatGPT's comes with its page of the conversations, the feedback given and the account; Claude's with its users and projects.
            chatGpt |= file.Equals("chat.html", StringComparison.OrdinalIgnoreCase)
                       || file.Equals("message_feedback.json", StringComparison.OrdinalIgnoreCase)
                       || file.Equals("user.json", StringComparison.OrdinalIgnoreCase)
                       || file.Equals("shared_conversations.json", StringComparison.OrdinalIgnoreCase);
            claude |= file.Equals("users.json", StringComparison.OrdinalIgnoreCase)
                      || file.Equals("projects.json", StringComparison.OrdinalIgnoreCase);
        }
        var kinds = takeout ? ExportKinds.GeminiTakeout : ExportKinds.None;
        if (!conversations) return kinds;
        if (chatGpt || !claude) kinds |= ExportKinds.ChatGpt;
        if (claude || !chatGpt) kinds |= ExportKinds.Claude;
        return kinds;
    }

    /// <summary>
    /// Folders that are Gemini's (or Bard's) inside a Takeout: <c>Takeout/My Activity/Gemini Apps</c>,
    /// the middle one named in the export's language. Takeout puts everything under its own
    /// <c>Takeout</c> folder, kept when the zip was unpacked and zipped again; without it, a folder
    /// named for Gemini is any project's — Gemini CLI's own sources, say.
    /// </summary>
    private static bool InTakeoutsGeminiFolder(ReadOnlySpan<char> folders)
    {
        var takeout = false;
        foreach (var range in folders.Split('/'))
        {
            var folder = folders[range];
            if (takeout && (folder.Contains("Gemini", StringComparison.OrdinalIgnoreCase) || folder.Contains("Bard", StringComparison.OrdinalIgnoreCase)))
                return true;
            takeout |= folder.Equals("Takeout", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    /// <summary>
    /// Kept only online — OneDrive's files on demand: offline, or fetched when opened or read.
    /// Opening one would download it, so it is passed over.
    /// </summary>
    internal static bool OnlineOnly(FileAttributes attributes) =>
        (attributes & (FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess)) != 0;

    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    /// <summary>
    /// When a zip came into its folder (UTC): a download is created then; a copy or a move keeps the
    /// time its original was written, so the later of the two.
    /// </summary>
    internal static DateTime ArrivalOf(FileInfo file) =>
        file.CreationTimeUtc > file.LastWriteTimeUtc ? file.CreationTimeUtc : file.LastWriteTimeUtc;

    /// <summary>Opened to be read as it is: the browser may still rename or delete it meanwhile.</summary>
    internal static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 16 * 1024);

    private static bool IsConversationList(ReadOnlySpan<char> file) =>
        file.StartsWith("conversations", StringComparison.OrdinalIgnoreCase) && file.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    /// <summary>A folder an export was unpacked into: a web export's only (a Takeout is imported from its zip or its activity file).</summary>
    private static ExportKinds OfFolder(string folder)
    {
        // A folder full of other things (Downloads itself): only a few levels down, and not every file.
        var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true };
        var names = Directory.EnumerateFiles(folder, "*", options).Take(20_000)
            .Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/')).ToList();
        var kinds = Classify(names) & Web;
        return kinds == Web
            ? Resolve(ExportPackage.ConversationFilesOf(names), name => OpenShared(Path.Combine(folder, name)))
            : kinds;
    }

    /// <summary>Whose conversations the first of the lists holds — ChatGPT's or Claude's; both when none of them tells.</summary>
    private static ExportKinds Resolve(IReadOnlyList<string> lists, Func<string, Stream> open)
    {
        foreach (var list in lists.Take(3))
        {
            try
            {
                using var stream = open(list);
                if (ConversationKind(stream) is var kind and not ExportKinds.None) return kind;
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                // Not a list this tool reads: the next one may tell.
            }
        }
        return Web;
    }

    /// <summary>
    /// Whose the first conversation of a list is, by the same fields the importers tell them
    /// apart by: ChatGPT's has its <c>mapping</c>, Claude's its <c>chat_messages</c>. None when it has neither.
    /// </summary>
    internal static ExportKinds ConversationKind(Stream stream)
    {
        var first = PeekFirst(stream);
        return first.Mapping ? ExportKinds.ChatGpt : first.ChatMessages ? ExportKinds.Claude : ExportKinds.None;
    }

    /// <summary>Takeout's record of one thing done: a title and a time — Gemini's when it says so or sits in Gemini's folder.</summary>
    private static bool IsGeminiActivity(FirstRecord first, bool inGeminiFolder) =>
        first.Title && first.Time && (inGeminiFolder || first.Gemini);

    /// <summary>Takeout's page of activity: its cards, Gemini's by the folder or the header of the first ones.</summary>
    private static bool IsActivityPage(string path)
    {
        var buffer = new byte[64 * 1024];
        int held;
        using (var stream = OpenShared(path)) held = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        var start = Encoding.UTF8.GetString(buffer, 0, held);
        return start.Contains("<div class=\"outer-cell", StringComparison.Ordinal)
               && (GeminiTakeout.InGeminiFolder(path) || start.Contains("Gemini", StringComparison.Ordinal)
                                                       || start.Contains("Bard", StringComparison.Ordinal));
    }

    /// <summary>What the first record of a JSON list says about itself — the fields it has, and whether it names Gemini.</summary>
    private readonly record struct FirstRecord(bool Mapping, bool ChatMessages, bool Title, bool Time, bool Gemini);

    /// <summary>
    /// The first record of a list — <c>[{…}, …]</c>, <c>{"conversations": [{…}, …]}</c>, or one
    /// <c>{…}</c> — read as it streams past and no further: an export's list runs to hundreds of
    /// megabytes, and the fields that tell are near its start.
    /// </summary>
    private static FirstRecord PeekFirst(Stream stream)
    {
        const long MaxRead = 32L * 1024 * 1024;
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            var state = new JsonReaderState(new JsonReaderOptions { MaxDepth = 256 });
            int held = 0, fields = -1;
            long total = 0;
            bool mapping = false, chat = false, title = false, time = false, gemini = false;
            bool list = false, bom = true;
            string? within = null;      // header, products: the field whose value is being read
            while (true)
            {
                if (held == buffer.Length)
                {
                    // One value longer than the buffer (a long title): room for all of it.
                    var larger = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
                    buffer.AsSpan(0, held).CopyTo(larger);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = larger;
                }
                var read = stream.Read(buffer, held, buffer.Length - held);
                held += read;
                total += read;
                var final = read == 0 || total > MaxRead;
                if (bom)
                {
                    if (held < 3 && !final) continue;
                    bom = false;
                    if (buffer.AsSpan(0, held).StartsWith("\uFEFF"u8))
                    {
                        buffer.AsSpan(3, held - 3).CopyTo(buffer);
                        held -= 3;
                    }
                }
                var reader = new Utf8JsonReader(buffer.AsSpan(0, held), final, state);
                while (reader.Read())
                {
                    var depth = reader.CurrentDepth;
                    switch (reader.TokenType)
                    {
                        case JsonTokenType.StartArray when depth == 0:
                            fields = 2;
                            break;
                        case JsonTokenType.StartObject when depth == 0:
                            fields = 1;         // one record — or one holding the list
                            break;
                        case JsonTokenType.StartArray when depth == 1 && list:
                            fields = 3;
                            mapping = chat = title = time = gemini = false;
                            break;
                        case JsonTokenType.PropertyName when depth == fields:
                            list = fields == 1 && reader.ValueTextEquals("conversations"u8);
                            within = reader.ValueTextEquals("header"u8) ? "header" : reader.ValueTextEquals("products"u8) ? "products" : null;
                            if (reader.ValueTextEquals("mapping"u8)) mapping = true;
                            else if (reader.ValueTextEquals("chat_messages"u8)) chat = true;
                            else if (reader.ValueTextEquals("title"u8)) title = true;
                            else if (reader.ValueTextEquals("time"u8)) time = true;
                            if (mapping || chat) return new FirstRecord(mapping, chat, title, time, gemini);
                            break;
                        case JsonTokenType.String when within is not null && (depth == fields || depth == fields + 1):
                            var text = TextOf(ref reader);
                            gemini |= text.Contains("Gemini", StringComparison.OrdinalIgnoreCase)
                                      || text.Contains("Bard", StringComparison.OrdinalIgnoreCase);
                            break;
                        case JsonTokenType.EndObject when depth == fields - 1:
                        case JsonTokenType.EndArray when depth == fields - 2:
                            return new FirstRecord(mapping, chat, title, time, gemini);    // the first record, or the list, has ended
                    }
                }
                if (final) return new FirstRecord(mapping, chat, title, time, gemini);
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

    /// <summary>A string's text, bytes that are not UTF-8 read as U+FFFD: the reader would refuse the whole file over them.</summary>
    private static string TextOf(ref Utf8JsonReader reader)
    {
        try
        {
            return reader.GetString() ?? "";
        }
        catch (InvalidOperationException)
        {
            return Encoding.UTF8.GetString(reader.ValueSpan);
        }
    }

    // --------------------------------------------------------------- the Downloads known folder

    private static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");

    private static string? KnownDownloadsFolder()
    {
        var path = IntPtr.Zero;
        try
        {
            return SHGetKnownFolderPath(DownloadsId, 0, IntPtr.Zero, out path) == 0 ? Marshal.PtrToStringUni(path) : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            Marshal.FreeCoTaskMem(path);        // whether it succeeded or not, as the shell asks
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(in Guid id, uint flags, IntPtr token, out IntPtr path);
}
