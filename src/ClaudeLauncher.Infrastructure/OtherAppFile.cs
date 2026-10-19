using System.Text;

namespace ClaudeLauncher.Infrastructure;

/// <summary>
/// Opens a file another app owns the one way this tool may: read-only, sharing read, write and
/// delete — so the app can go on writing it, replace it with a renamed copy or delete it while
/// it is being read, and nothing it does fails because this tool happened to be looking.
/// </summary>
internal static class OtherAppFile
{
    public static FileStream Open(string path, int bufferSize = 4096, FileOptions options = FileOptions.None) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize, options);

    /// <summary>The whole file as text, by its byte-order mark or else as UTF-8 — what <see cref="File.ReadAllText(string)"/> reads.</summary>
    public static string ReadAllText(string path)
    {
        using var stream = Open(path, options: FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
