using System.Globalization;
using System.Runtime.InteropServices;
using ClaudeLauncher.Core.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;

namespace ClaudeLauncher.Infrastructure.Backends;

/// <summary>
/// This tool's own copy of every transcript it lists, kept in its data root so a conversation
/// stays readable after the app deletes, cleans up or compresses away its record. Only ever
/// added to: a copy is grown as its transcript grows, replaced when the transcript is rewritten,
/// and removed only when the user deletes the conversation here.
///
/// <para>Copies are plain files, so everything that reads a transcript reads a copy the same
/// way; the folder is NTFS-compressed where the drive allows it. A transcript that only grows
/// (Claude's, Codex's JSON lines) has just its new complete lines appended — after checking
/// that the copy still ends the way the transcript did, else it is copied again whole. One
/// written again whole each time (a Gemini CLI session) is copied once it has been left alone
/// for a minute. A Zstandard-compressed rollout is unpacked into its copy once.</para>
///
/// <para>The originals are opened read-only, sharing every access, and never changed.</para>
/// </summary>
public sealed class TranscriptArchive : IDisposable
{
    /// <summary>A transcript to keep a copy of: which conversation of which app it holds.</summary>
    public sealed record Source(string Path, string Family, string Conversation);

    /// <param name="Pending">Transcripts left for the next pass when this one ran out of time.</param>
    public sealed record UpdateRun(int Sources, int Copied, int Grown, long BytesWritten, int Pending)
    {
        public bool ChangedAnything => Copied + Grown > 0;
    }

    /// <summary>A transcript written again whole is copied only once it has been left alone this long.</summary>
    private static readonly TimeSpan Settled = TimeSpan.FromMinutes(1);

    /// <summary>How much of the copy's end must match the transcript before new lines are appended to it.</summary>
    private const int TailCheck = 4096;

    private readonly SqliteConnection _connection;
    private readonly Lock _gate = new();
    private bool _disposed;

    public string Root { get; }

    public string CopiesFolder => Path.Combine(Root, "copies");

    public TranscriptArchive(string root)
    {
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        Directory.CreateDirectory(CopiesFolder);
        NtfsCompression.TryEnable(CopiesFolder);
        _connection = new SqliteConnection($"Data Source={Path.Combine(Root, "archive.sqlite")};Pooling=False");
        _connection.Open();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS copies (
              family TEXT NOT NULL,
              conversation TEXT NOT NULL,
              source TEXT NOT NULL,
              copy TEXT NOT NULL,
              source_length INTEGER NOT NULL,
              source_written INTEGER NOT NULL,
              copied_length INTEGER NOT NULL,
              copied_utc TEXT NOT NULL,
              PRIMARY KEY(family, conversation));
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Which copy a listed row's transcript is kept as: its app and the id its transcript is
    /// named by. Null for a row whose transcript is this tool's own already (an import).
    /// </summary>
    public static (string Family, string Conversation)? IdentityOf(SessionMetadata row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var (family, id) = row.Key.Backend switch
        {
            ConversationKey.CliCode => ("claude", row.CliSessionId ?? row.Key.NativeSessionId),
            ConversationKey.DesktopCode => ("claude", row.CliSessionId),
            ConversationKey.Codex => ("codex", row.Key.NativeSessionId),
            ConversationKey.Gemini => ("gemini", row.Key.NativeSessionId),
            _ => (null, null),
        };
        if (family is null || string.IsNullOrWhiteSpace(id) || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        return (family, id);
    }

    /// <summary>The copy of a conversation's transcript, when one has been made.</summary>
    public string? CopyOf(string family, string conversation)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT copy FROM copies WHERE family=$f AND conversation=$c;";
            cmd.Parameters.AddWithValue("$f", family);
            cmd.Parameters.AddWithValue("$c", conversation);
            return cmd.ExecuteScalar() is string copy && File.Exists(copy) ? copy : null;
        }
    }

    /// <summary>
    /// A readable form of a compressed transcript: its copy, unpacked now if it has not been
    /// yet. Null when it cannot be read.
    /// </summary>
    public string? Unpacked(string compressed, string family, string conversation)
    {
        var existing = Known(family, conversation);
        if (existing is not null && existing.Source == compressed && File.Exists(existing.Path)) return existing.Path;
        try
        {
            var info = new FileInfo(compressed);
            if (!info.Exists) return null;
            Keep(new Source(compressed, family, conversation), info, existing);
            return CopyOf(family, conversation);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ZstdSharp.ZstdException)
        {
            return null;
        }
    }

    /// <summary>
    /// Brings the copies up to date, oldest transcripts first (the first an app cleans up), for
    /// at most <paramref name="budget"/>; what is left waits for the next pass.
    /// </summary>
    public UpdateRun Update(IReadOnlyList<Source> sources, TimeSpan budget,
        IProgress<(int Done, int Total)>? progress = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var known = Load();
        var work = new List<(Source Source, FileInfo Info, CopyRecord? Before)>();
        foreach (var source in sources.DistinctBy(s => (s.Family, s.Conversation)))
        {
            cancellation.ThrowIfCancellationRequested();
            FileInfo info;
            try
            {
                info = new FileInfo(source.Path);
                if (!info.Exists) continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                continue;
            }
            known.TryGetValue((source.Family, source.Conversation), out var before);
            if (before is not null && before.Source == source.Path && before.SourceLength == info.Length
                && before.SourceWritten == info.LastWriteTimeUtc.Ticks && File.Exists(before.Path)) continue;
            work.Add((source, info, before));
        }

        var started = DateTime.UtcNow;
        int copied = 0, grown = 0, done = 0;
        long written = 0;
        foreach (var (source, info, before) in work.OrderBy(w => w.Info.LastWriteTimeUtc))
        {
            cancellation.ThrowIfCancellationRequested();
            if (DateTime.UtcNow - started > budget) break;
            progress?.Report((done, work.Count));
            try
            {
                var (kind, bytes) = Keep(source, info, before);
                if (kind == Kept.Copied) copied++;
                else if (kind == Kept.Grown) grown++;
                written += bytes;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                           or ZstdSharp.ZstdException)
            {
                // Held, or being written right now: the next pass tries again.
            }
            done++;
        }
        progress?.Report((work.Count, work.Count));
        return new UpdateRun(sources.Count, copied, grown, written, work.Count - done);
    }

    /// <summary>Removes a conversation's copy, when the user deletes the conversation here.</summary>
    public void Remove(string family, string conversation)
    {
        var existing = Known(family, conversation);
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM copies WHERE family=$f AND conversation=$c;";
            cmd.Parameters.AddWithValue("$f", family);
            cmd.Parameters.AddWithValue("$c", conversation);
            cmd.ExecuteNonQuery();
        }
        if (existing is not null)
        {
            try { File.Delete(existing.Path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>How many copies there are and how much they hold (before the drive's compression).</summary>
    public (int Copies, long Bytes) Size()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*), COALESCE(SUM(copied_length),0) FROM copies;";
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? (reader.GetInt32(0), reader.GetInt64(1)) : (0, 0);
        }
    }

    // ------------------------------------------------------------------ copying

    private sealed record CopyRecord(string Source, string Path, long SourceLength, long SourceWritten, long CopiedLength);

    private enum Kept { Nothing, Copied, Grown }

    private (Kept Kind, long Bytes) Keep(Source source, FileInfo info, CopyRecord? before)
    {
        var compressed = TranscriptFiles.IsCompressed(source.Path);
        var plainName = TranscriptFiles.WithoutCompression(source.Path);
        var wholeDocument = plainName.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
        var folder = Path.Combine(CopiesFolder, source.Family);
        Directory.CreateDirectory(folder);
        var copy = Path.Combine(folder, source.Conversation + (wholeDocument ? ".json" : ".jsonl"));

        if (wholeDocument && DateTime.UtcNow - info.LastWriteTimeUtc < Settled) return (Kept.Nothing, 0);

        // Grown in place: only the new complete lines are added, once the copy is known to end as the transcript did.
        if (!compressed && !wholeDocument && before is not null && before.Source == source.Path && before.Path == copy
            && File.Exists(copy) && info.Length > before.SourceLength && new FileInfo(copy).Length == before.CopiedLength
            && EndsAlike(source.Path, copy, before.CopiedLength))
        {
            var added = AppendLines(source.Path, copy, before.CopiedLength, info.Length);
            Record(source, copy, info, before.CopiedLength + added);
            return (added > 0 ? Kept.Grown : Kept.Nothing, added);
        }

        var temp = copy + ".part";
        long length;
        using (var input = TranscriptFiles.OpenRead(source.Path))
        using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 256 * 1024))
        {
            if (compressed || wholeDocument)
            {
                input.CopyTo(output, 1024 * 1024);
                length = output.Length;
            }
            else
            {
                // Up to the last complete line: a line still being written waits for the next pass.
                length = CopyLines(input, output, info.Length);
                output.SetLength(length);
            }
        }
        File.Move(temp, copy, overwrite: true);
        Record(source, copy, info, length);
        return (Kept.Copied, length);
    }

    /// <summary>Copies up to <paramref name="limit"/> bytes; how many of them end in a line break (the rest are cut off by the caller).</summary>
    private static long CopyLines(Stream input, Stream output, long limit)
    {
        var buffer = new byte[1024 * 1024];
        long total = 0, complete = 0;
        int read;
        while (total < limit && (read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, limit - total))) > 0)
        {
            output.Write(buffer, 0, read);
            var newline = buffer.AsSpan(0, read).LastIndexOf((byte)'\n');
            if (newline >= 0) complete = total + newline + 1;
            total += read;
        }
        return complete;
    }

    /// <summary>Appends what the transcript gained from <paramref name="from"/>, up to its last line break; the bytes added.</summary>
    private static long AppendLines(string source, string copy, long from, long to)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024);
        input.Seek(from, SeekOrigin.Begin);
        using var output = new FileStream(copy, FileMode.Open, FileAccess.Write, FileShare.Read, 256 * 1024);
        output.Seek(from, SeekOrigin.Begin);
        var written = CopyLines(input, output, to - from);
        output.SetLength(from + written);
        return written;
    }

    /// <summary>Whether the copy ends as the transcript does at that length: it only grew since.</summary>
    private static bool EndsAlike(string source, string copy, long length)
    {
        var size = (int)Math.Min(TailCheck, length);
        if (size == 0) return true;
        var a = new byte[size];
        var b = new byte[size];
        using (var s = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            s.Seek(length - size, SeekOrigin.Begin);
            if (s.ReadAtLeast(a, size, throwOnEndOfStream: false) != size) return false;
        }
        using (var c = new FileStream(copy, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            c.Seek(length - size, SeekOrigin.Begin);
            if (c.ReadAtLeast(b, size, throwOnEndOfStream: false) != size) return false;
        }
        return a.AsSpan().SequenceEqual(b);
    }

    private void Record(Source source, string copy, FileInfo info, long copiedLength)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO copies(family, conversation, source, copy, source_length, source_written, copied_length, copied_utc)
                VALUES($f, $c, $s, $copy, $length, $written, $copied, $at);
                """;
            cmd.Parameters.AddWithValue("$f", source.Family);
            cmd.Parameters.AddWithValue("$c", source.Conversation);
            cmd.Parameters.AddWithValue("$s", source.Path);
            cmd.Parameters.AddWithValue("$copy", copy);
            cmd.Parameters.AddWithValue("$length", info.Length);
            cmd.Parameters.AddWithValue("$written", info.LastWriteTimeUtc.Ticks);
            cmd.Parameters.AddWithValue("$copied", copiedLength);
            cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            cmd.ExecuteNonQuery();
        }
    }

    private CopyRecord? Known(string family, string conversation)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT source, copy, source_length, source_written, copied_length FROM copies WHERE family=$f AND conversation=$c;
                """;
            cmd.Parameters.AddWithValue("$f", family);
            cmd.Parameters.AddWithValue("$c", conversation);
            using var r = cmd.ExecuteReader();
            return r.Read() ? new CopyRecord(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4)) : null;
        }
    }

    private Dictionary<(string, string), CopyRecord> Load()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var all = new Dictionary<(string, string), CopyRecord>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT family, conversation, source, copy, source_length, source_written, copied_length FROM copies;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                all[(r.GetString(0), r.GetString(1))] = new CopyRecord(r.GetString(2), r.GetString(3), r.GetInt64(4), r.GetInt64(5), r.GetInt64(6));
            return all;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _connection.Dispose();
        }
    }
}

/// <summary>Turns on the drive's own compression for a folder: new files in it are stored compressed, read as usual.</summary>
internal static class NtfsCompression
{
    private const uint FsctlSetCompression = 0x9C040;
    private const ushort CompressionFormatDefault = 1;

    public static bool TryEnable(string folder)
    {
        try
        {
            if ((File.GetAttributes(folder) & FileAttributes.Compressed) != 0) return true;
            using var handle = CreateFile(folder, 0x80000000 | 0x40000000, 0x7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
            if (handle.IsInvalid) return false;
            var format = CompressionFormatDefault;
            return DeviceIoControl(handle, FsctlSetCompression, ref format, sizeof(ushort), IntPtr.Zero, 0, out _, IntPtr.Zero);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition,
        uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, ref ushort input, int inputSize,
        IntPtr output, int outputSize, out int returned, IntPtr overlapped);
}
