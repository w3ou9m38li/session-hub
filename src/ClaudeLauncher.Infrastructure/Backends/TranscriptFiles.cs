namespace ClaudeLauncher.Infrastructure.Backends;

/// <summary>
/// Opening a transcript whichever way it is stored: as written, or compressed with Zstandard
/// (<c>rollout-*.jsonl.zst</c>), as a maintenance tool for Codex leaves archived tasks. Always
/// read-only, and never in a way that stops the app writing to the file.
/// </summary>
public static class TranscriptFiles
{
    public const string ZstdExtension = ".zst";

    public static bool IsCompressed(string? path) =>
        path is not null && path.EndsWith(ZstdExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>The file read from the start: decompressed on the way when it is compressed.</summary>
    public static Stream OpenRead(string path)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024,
            FileOptions.SequentialScan);
        if (!IsCompressed(path)) return file;
        try
        {
            return new ZstdSharp.DecompressionStream(file, leaveOpen: false);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The name a compressed transcript has without its compression: rollout-….jsonl.zst →
    /// rollout-….jsonl. Anything else as it is.
    /// </summary>
    public static string WithoutCompression(string path) =>
        IsCompressed(path) ? path[..^ZstdExtension.Length] : path;
}
