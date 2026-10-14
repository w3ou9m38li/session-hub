namespace ClaudeLauncher.Infrastructure.Backends;

/// <summary>
/// Opening a transcript whichever way it is stored: as written; compressed with Zstandard
/// (<c>rollout-*.jsonl.zst</c>, as a maintenance tool for Codex leaves archived tasks, or a
/// DeepSeek Harness log); or as this tool's packed copy (<see cref="PackedCopy"/>), which reads —
/// and seeks — as the plain transcript it holds. Always read-only, and never in a way that stops
/// the app writing to the file.
/// </summary>
public static class TranscriptFiles
{
    public const string ZstdExtension = ".zst";

    public static bool IsCompressed(string? path) =>
        path is not null && path.EndsWith(ZstdExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The file read from the start: decompressed on the way when it is compressed — a packed
    /// copy, a Codex rollout (<c>.zst</c>) or a DeepSeek Harness log (<c>.zstd</c>).
    /// </summary>
    public static Stream OpenRead(string path)
    {
        if (PackedCopy.HasSuffix(path) && PackedCopy.Open(path) is { } packed) return packed;
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024,
            FileOptions.SequentialScan);
        if (!IsCompressed(path) && !DshSession.IsCompressed(path)) return file;
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
    /// A transcript opened for reading at any position: the file itself, or — for a packed copy —
    /// the plain bytes it holds, its length and positions those of the plain transcript. A
    /// transcript compressed some other way is never read this way: the locator hands out this
    /// tool's packed copy of it instead. Opened for random access, a packed copy keeps the frame
    /// it reads unpacked whole, for reading it again.
    /// </summary>
    public static Stream OpenSeekable(string path, int bufferSize = 64 * 1024, FileOptions options = FileOptions.SequentialScan)
    {
        if (PackedCopy.HasSuffix(path)
            && PackedCopy.Open(path, wholeFrames: (options & FileOptions.RandomAccess) != 0) is { } packed) return packed;
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize, options);
    }

    /// <summary>
    /// What a transcript holds and when it was last written: for a packed copy, the length of the
    /// plain transcript in it. Null when there is no such file.
    /// </summary>
    public static (long Length, long Written)? Stat(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return null;
        if (PackedCopy.HasSuffix(path) && PackedCopy.Stat(path) is { } packed) return packed;
        return (info.Length, info.LastWriteTimeUtc.Ticks);
    }

    /// <summary>
    /// The name a compressed transcript has without its compression: rollout-….jsonl.zst →
    /// rollout-….jsonl. Anything else as it is.
    /// </summary>
    public static string WithoutCompression(string path) =>
        IsCompressed(path) ? path[..^ZstdExtension.Length] : path;
}
