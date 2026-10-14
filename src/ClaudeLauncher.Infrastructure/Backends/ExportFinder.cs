using System.IO.Compression;

namespace ClaudeLauncher.Infrastructure.Backends;

/// <summary>An export found among the downloads: where it is, when it was saved, how big it is.</summary>
public sealed record FoundExport(string Path, DateTime Saved, long Bytes);

/// <summary>
/// Looks in the Downloads folder for the newest export of one kind — ChatGPT's or Claude's data
/// export, a Google Takeout with Gemini's activity in it — so an import window can offer it
/// without the user going to find it. Only names are read: the zips' own, and the names of the
/// files a zip lists, never what is in those files. Looked for when an import window opens,
/// never in the background.
/// </summary>
public static class ExportFinder
{
    public enum Kind { ChatGpt, Claude, GeminiTakeout }

    /// <summary>How many of the newest zips are looked into.</summary>
    private const int Look = 40;

    /// <summary>The Downloads folder in the user's profile.</summary>
    public static string DownloadsFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    /// <summary>
    /// The newest zip in <paramref name="folder"/> (not its subfolders) that is an export of
    /// <paramref name="kind"/>; with <paramref name="savedAfter"/>, only one saved after it — an
    /// export imported already is not offered again. Null when there is none.
    /// </summary>
    public static FoundExport? Newest(Kind kind, string folder, DateTime? savedAfter = null)
    {
        List<FileInfo> zips;
        try
        {
            if (!Directory.Exists(folder)) return null;
            zips = new DirectoryInfo(folder).EnumerateFiles("*.zip", SearchOption.TopDirectoryOnly)
                .OrderByDescending(f => f.LastWriteTimeUtc).Take(Look).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        foreach (var zip in zips)
        {
            if (savedAfter is { } after && zip.LastWriteTime <= after) break;
            if (Names(zip.FullName) is { } names && Is(kind, names))
                return new FoundExport(zip.FullName, zip.LastWriteTime, zip.Length);
        }
        return null;
    }

    /// <summary>The names of the files a zip holds; null when it cannot be opened (half downloaded, not a zip).</summary>
    private static List<string>? Names(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            return [.. zip.Entries.Where(e => e.Name.Length > 0).Select(e => e.FullName.Replace('\\', '/'))];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Whether the names a zip lists are those of an export of this kind.</summary>
    internal static bool Is(Kind kind, IReadOnlyCollection<string> names)
    {
        static string FileOf(string name) => name[(name.LastIndexOf('/') + 1)..];
        bool Has(string file) => names.Any(n => string.Equals(FileOf(n), file, StringComparison.OrdinalIgnoreCase));

        if (kind == Kind.GeminiTakeout)
            return names.Any(n =>
            {
                var parts = n.Split('/');
                var file = parts[^1];
                return (file.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                       && parts.Take(parts.Length - 1).Any(p => p.Contains("Gemini", StringComparison.OrdinalIgnoreCase)
                                                                || p.Contains("Bard", StringComparison.OrdinalIgnoreCase));
            });

        var conversations = names.Any(n =>
        {
            var file = FileOf(n);
            return file.StartsWith("conversations", StringComparison.OrdinalIgnoreCase)
                   && file.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
        });
        if (!conversations) return false;
        // ChatGPT's comes with its page of the conversations, the feedback given and the account; Claude's with its users and projects.
        var chatGpt = Has("chat.html") || Has("message_feedback.json") || Has("user.json") || Has("shared_conversations.json");
        var claude = Has("users.json") || Has("projects.json");
        return kind == Kind.ChatGpt ? chatGpt || !claude : claude || !chatGpt;
    }
}
