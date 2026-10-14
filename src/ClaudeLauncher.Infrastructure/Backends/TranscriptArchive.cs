using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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
/// <para>Copies are packed (<see cref="PackedCopy"/>): Zstandard frames that read — and seek —
/// as the plain transcript, so everything that reads a transcript reads a copy the same way, in
/// about a third of the room. A transcript that only grows (Claude's, Codex's JSON lines) has
/// just its new complete lines added — after checking that the copy still ends the way the
/// transcript did, else it is copied again whole. One written again whole each time (a Gemini
/// CLI session) is copied once it has been left alone for a minute. A Zstandard-compressed
/// rollout is copied once; a DeepSeek Harness log, compressed a few lines at a time as it grows,
/// is copied whole whenever it has changed and then been left alone for a minute.</para>
///
/// <para>Copies made before copies were packed are plain files. A pass with time to spare packs
/// some of them, oldest first, and deletes a plain file only once its packed copy is known to
/// unpack to the very same bytes.</para>
///
/// <para>The originals are opened read-only, sharing every access, and never changed.</para>
/// </summary>
public sealed class TranscriptArchive : IDisposable
{
    /// <summary>A transcript to keep a copy of: which conversation of which app it holds.</summary>
    public sealed record Source(string Path, string Family, string Conversation);

    /// <param name="Pending">What was left for the next pass when this one ran out of time: transcripts to copy, and plain copies to pack.</param>
    /// <param name="Packed">Copies from before copies were packed, packed by this pass.</param>
    public sealed record UpdateRun(int Sources, int Copied, int Grown, long BytesWritten, int Pending, int Packed = 0)
    {
        public bool ChangedAnything => Copied + Grown + Packed > 0;
    }

    /// <summary>What the copies come to.</summary>
    /// <param name="Bytes">The transcripts they hold, as the apps wrote them.</param>
    /// <param name="Stored">The room they take on the drive.</param>
    /// <param name="Plain">Copies from before copies were packed, not packed yet.</param>
    public readonly record struct ArchiveSize(int Copies, long Bytes, long Stored, int Plain);

    /// <summary>A transcript written again whole is copied only once it has been left alone this long.</summary>
    private static readonly TimeSpan Settled = TimeSpan.FromMinutes(1);

    /// <summary>A copy being written, under its own name until it is complete.</summary>
    private const string PartSuffix = ".part";

    private readonly SqliteConnection _connection;
    private readonly Lock _gate = new();
    /// <summary>Held while a copy is written, packed or removed: never two at once on the same file.</summary>
    private readonly Lock _writing = new();
    private readonly HashSet<string> _folders = new(StringComparer.OrdinalIgnoreCase);
    private bool _swept;
    private bool _disposed;

    public string Root { get; }

    public string CopiesFolder => Path.Combine(Root, "copies");

    public TranscriptArchive(string root)
    {
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        Directory.CreateDirectory(CopiesFolder);
        // Packed copies gain nothing from the drive's compression, which earlier versions turned on here.
        NtfsCompression.TryDisable(CopiesFolder);
        _connection = new SqliteConnection($"Data Source={Path.Combine(Root, "archive.sqlite")};Pooling=False");
        try
        {
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
            // Since copies are packed: the hash a copy ends with, and the room it takes.
            AddColumn("tail_hash", "TEXT");
            AddColumn("stored_length", "INTEGER");
        }
        catch
        {
            _connection.Dispose();
            throw;
        }
    }

    private void AddColumn(string name, string type)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('copies') WHERE name=$n;";
        cmd.Parameters.AddWithValue("$n", name);
        if (Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) > 0) return;
        cmd.Parameters.Clear();
        cmd.CommandText = $"ALTER TABLE copies ADD COLUMN {name} {type};";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Which copy a listed row's transcript is kept as: its app and the id its transcript is
    /// named by. Null for a row whose transcript is this tool's own already (an import).
    /// </summary>
    public static (string Family, string Conversation)? IdentityOf(SessionMetadata row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var source = Sources.Of(row.Key.Backend);
        var family = source?.CopyFamily;
        var id = source?.CopyKey switch
        {
            CopyKey.Native => row.Key.NativeSessionId,
            CopyKey.CliSession => row.CliSessionId,
            CopyKey.CliSessionOrNative => row.CliSessionId ?? row.Key.NativeSessionId,
            _ => null,
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
            return cmd.ExecuteScalar() is string recorded && Located(family, recorded) is var copy && File.Exists(copy) ? copy : null;
        }
    }

    /// <summary>
    /// What to read for a transcript an app keeps compressed in a way that cannot be read from
    /// a position (a Codex rollout a maintenance tool compressed): its copy, made now if it has
    /// not been yet. Null when it cannot be read.
    /// </summary>
    public string? Readable(string compressed, string family, string conversation)
    {
        try
        {
            lock (_writing)
            {
                var existing = Known(family, conversation);
                if (existing is not null && existing.Source == compressed && File.Exists(existing.Path)) return existing.Path;
                var info = new FileInfo(compressed);
                if (!info.Exists) return null;
                Keep(new Source(compressed, family, conversation), info, existing, CancellationToken.None);
            }
            return CopyOf(family, conversation);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ZstdSharp.ZstdException)
        {
            return null;
        }
    }

    /// <summary>
    /// Brings the copies up to date, oldest transcripts first (the first an app cleans up), for
    /// at most <paramref name="budget"/>; what is left waits for the next pass. Time to spare
    /// goes to packing copies from before copies were packed.
    /// </summary>
    public UpdateRun Update(IReadOnlyList<Source> sources, TimeSpan budget,
        IProgress<(int Done, int Total)>? progress = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var clock = Stopwatch.StartNew();
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
        var plain = known.Where(k => !PackedCopy.HasSuffix(k.Value.Path))
            .OrderBy(k => k.Value.SourceWritten).Select(k => k.Key).ToList();
        var total = work.Count + plain.Count;

        int copied = 0, grown = 0, packed = 0, done = 0;
        long written = 0;
        foreach (var (source, info, before) in work.OrderBy(w => w.Info.LastWriteTimeUtc))
        {
            cancellation.ThrowIfCancellationRequested();
            if (clock.Elapsed > budget) break;
            progress?.Report((done, total));
            try
            {
                Kept kind;
                long bytes;
                lock (_writing) (kind, bytes) = Keep(source, info, before, cancellation);
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

        if (done == work.Count)
            foreach (var (family, conversation) in plain)
            {
                cancellation.ThrowIfCancellationRequested();
                if (clock.Elapsed > budget) break;
                progress?.Report((done, total));
                try
                {
                    lock (_writing)
                        if (Pack(family, conversation, cancellation)) packed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                               or ZstdSharp.ZstdException)
                {
                    // Held, or not packed exactly: the plain copy stays as it is, and the next pass tries again.
                }
                done++;
            }

        if (done == total) Sweep();
        progress?.Report((total, total));
        return new UpdateRun(sources.Count, copied, grown, written, total - done, packed);
    }

    /// <summary>Removes a conversation's copy, when the user deletes the conversation here.</summary>
    public void Remove(string family, string conversation)
    {
        lock (_writing)
        {
            var existing = Known(family, conversation);
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = "DELETE FROM copies WHERE family=$f AND conversation=$c;";
                cmd.Parameters.AddWithValue("$f", family);
                cmd.Parameters.AddWithValue("$c", conversation);
                cmd.ExecuteNonQuery();
            }
            if (existing is null || !IsOurs(existing.Path)) return;
            TryDelete(existing.Path);
            // A plain copy from before it was packed, should one have been left beside it.
            if (PackedCopy.HasSuffix(existing.Path)) TryDelete(existing.Path[..^PackedCopy.Suffix.Length]);
        }
    }

    /// <summary>How many copies there are, what they hold and the room they take.</summary>
    public ArchiveSize Size()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT COUNT(*), COALESCE(SUM(copied_length),0), COALESCE(SUM(COALESCE(stored_length, copied_length)),0),
                       COALESCE(SUM(CASE WHEN copy LIKE '%.zst' THEN 0 ELSE 1 END),0)
                FROM copies;
                """;
            using var reader = cmd.ExecuteReader();
            return reader.Read()
                ? new ArchiveSize(reader.GetInt32(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt32(3))
                : default;
        }
    }

    // ------------------------------------------------------------------ copying

    private sealed record CopyRecord(string Source, string Path, long SourceLength, long SourceWritten, long CopiedLength,
        string? TailHash);

    private enum Kept { Nothing, Copied, Grown }

    private (Kept Kind, long Bytes) Keep(Source source, FileInfo info, CopyRecord? before, CancellationToken cancellation)
    {
        // A DeepSeek Harness log is compressed a few lines at a time as it grows: it is copied
        // whole, once it has been left alone like a document written again whole.
        var dsh = DshSession.IsSessionFile(source.Path);
        var growingPacked = dsh && DshSession.IsCompressed(source.Path);
        var compressed = TranscriptFiles.IsCompressed(source.Path) || growingPacked;
        var wholeDocument = TranscriptFiles.WithoutCompression(source.Path).EndsWith(".json", StringComparison.OrdinalIgnoreCase);
        var copy = Path.Combine(FolderOf(source.Family),
            source.Conversation + (dsh ? DshSession.CopySuffix : wholeDocument ? ".json" : ".jsonl") + PackedCopy.Suffix);

        if ((wholeDocument || growingPacked) && DateTime.UtcNow - info.LastWriteTimeUtc < Settled) return (Kept.Nothing, 0);

        if (!compressed && !wholeDocument)
        {
            // Lines: copied up to the last complete one — a line still being written waits for the next pass.
            using var input = new FileStream(source.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                64 * 1024, FileOptions.SequentialScan);

            // Grown in place: only the new lines are added, once the copy is known to end as the transcript did.
            if (before is { TailHash: { } tail } && before.Source == source.Path && before.Path == copy
                && info.Length >= before.SourceLength && info.Length > before.CopiedLength
                && File.Exists(copy) && PackedCopy.Stat(copy) is { } held && held.Length == before.CopiedLength
                && PackedCopy.TailHashOf(input, before.CopiedLength) == tail)
            {
                var end = LineEnd(input, before.CopiedLength, info.Length);
                if (end == before.CopiedLength)
                {
                    Record(source, copy, info, before.CopiedLength, tail);
                    return (Kept.Nothing, 0);
                }
                try
                {
                    var (added, hash) = PackedCopy.Append(copy, new StreamSlice(input, before.CopiedLength, end - before.CopiedLength),
                        wholeLines: false, cancellation);
                    Record(source, copy, info, before.CopiedLength + added, hash);
                    return (Kept.Grown, added);
                }
                catch (IOException ex) when (ex.InnerException is ZstdSharp.ZstdException or InvalidDataException or EndOfStreamException)
                {
                    // The copy is damaged: made again whole below.
                }
            }

            var complete = LineEnd(input, 0, info.Length);
            input.Position = 0;
            return Replace(source, info, before, copy,
                temp => PackedCopy.Write(new StreamSlice(input, 0, complete), temp, wholeLines: false, cancellation));
        }

        // Compressed, or a document: unpacked on the way. A dsh log as far as its last complete line.
        return Replace(source, info, before, copy, temp =>
        {
            using var input = TranscriptFiles.OpenRead(source.Path);
            return PackedCopy.Write(input, temp, wholeLines: growingPacked, cancellation);
        });
    }

    /// <summary>
    /// Makes a conversation's copy anew: written under another name, put in place once complete,
    /// and the copy it replaces — a plain one from before copies were packed — deleted, when it
    /// is in this archive's folder.
    /// </summary>
    private (Kept Kind, long Bytes) Replace(Source source, FileInfo info, CopyRecord? before, string copy,
        Func<string, (long Length, string TailHash)> write)
    {
        var temp = copy + PartSuffix;
        (long Length, string TailHash) made;
        try
        {
            made = write(temp);
            File.Move(temp, copy, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
        Record(source, copy, info, made.Length, made.TailHash);
        if (before is not null && !string.Equals(before.Path, copy, StringComparison.OrdinalIgnoreCase) && IsOurs(before.Path))
            TryDelete(before.Path);
        return (Kept.Copied, made.Length);
    }

    /// <summary>
    /// Packs a copy made before copies were packed, into this archive's folder. Its plain file is
    /// deleted only once the packed copy is known to unpack to the very same bytes — until then it
    /// stays the copy — and only when it is this archive's own.
    /// </summary>
    private bool Pack(string family, string conversation, CancellationToken cancellation)
    {
        var record = Known(family, conversation);
        if (record is null || PackedCopy.HasSuffix(record.Path) || !File.Exists(record.Path)) return false;
        var copy = Path.Combine(FolderOf(family), Path.GetFileName(record.Path) + PackedCopy.Suffix);
        var temp = copy + PartSuffix;
        (long Length, string TailHash) made;
        try
        {
            byte[] digest;
            using (var file = new FileStream(record.Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                       64 * 1024, FileOptions.SequentialScan))
            using (var plain = new HashingStream(file))
            {
                made = PackedCopy.Write(plain, temp, wholeLines: false, cancellation);
                digest = plain.Digest();
            }
            using (var check = PackedCopy.Open(temp))
            {
                if (check is null || check.Length != made.Length || !HashOf(check, cancellation).AsSpan().SequenceEqual(digest))
                    throw new InvalidDataException("打包后的副本和原副本对不上：" + Path.GetFileName(record.Path));
            }
            File.Move(temp, copy, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
        Repoint(family, conversation, copy, made.Length, made.TailHash);
        if (IsOurs(record.Path)) TryDelete(record.Path);
        return true;
    }

    /// <summary>
    /// What earlier passes left behind: a copy half written when the app was closed, a plain copy
    /// that could not be deleted once packed. Looked for once, the first time a pass is done with
    /// everything else.
    /// </summary>
    private void Sweep()
    {
        if (_swept) return;
        _swept = true;
        var copies = Load().Values.Select(r => r.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        lock (_writing)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(CopiesFolder, "*", SearchOption.AllDirectories))
                    if (file.EndsWith(PartSuffix, StringComparison.OrdinalIgnoreCase)
                        || (!copies.Contains(file) && copies.Contains(file + PackedCopy.Suffix)))
                        TryDelete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>A family's folder of copies, made when missing.</summary>
    private string FolderOf(string family)
    {
        var folder = Path.Combine(CopiesFolder, family);
        Directory.CreateDirectory(folder);
        if (_folders.Add(family)) NtfsCompression.TryDisable(folder);
        return folder;
    }

    /// <summary>Whether a file is in this archive's folder: the only files it ever writes or deletes.</summary>
    private bool IsOurs(string path) =>
        path.StartsWith(CopiesFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Where a recorded copy is: a record written while the data root was somewhere else names
    /// the old place, and the copy moved here with the root.
    /// </summary>
    private string Located(string family, string recorded)
    {
        if (IsOurs(recorded)) return recorded;
        var here = Path.Combine(CopiesFolder, family, Path.GetFileName(recorded));
        return File.Exists(here) ? here : recorded;
    }

    /// <summary>Where the last complete line in [<paramref name="from"/>, <paramref name="to"/>) ends; <paramref name="from"/> when no line does.</summary>
    private static long LineEnd(Stream input, long from, long to)
    {
        var buffer = new byte[64 * 1024];
        for (var end = to; end > from;)
        {
            var size = (int)Math.Min(buffer.Length, end - from);
            input.Position = end - size;
            input.ReadExactly(buffer, 0, size);
            var newline = buffer.AsSpan(0, size).LastIndexOf((byte)'\n');
            if (newline >= 0) return end - size + newline + 1;
            end -= size;
        }
        return from;
    }

    private static byte[] HashOf(Stream stream, CancellationToken cancellation)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, read);
        }
        return hash.GetHashAndReset();
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void Record(Source source, string copy, FileInfo info, long copiedLength, string tailHash)
    {
        var stored = new FileInfo(copy).Length;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO copies(family, conversation, source, copy, source_length, source_written, copied_length,
                                              copied_utc, tail_hash, stored_length)
                VALUES($f, $c, $s, $copy, $length, $written, $copied, $at, $tail, $stored);
                """;
            cmd.Parameters.AddWithValue("$f", source.Family);
            cmd.Parameters.AddWithValue("$c", source.Conversation);
            cmd.Parameters.AddWithValue("$s", source.Path);
            cmd.Parameters.AddWithValue("$copy", copy);
            cmd.Parameters.AddWithValue("$length", info.Length);
            cmd.Parameters.AddWithValue("$written", info.LastWriteTimeUtc.Ticks);
            cmd.Parameters.AddWithValue("$copied", copiedLength);
            cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$tail", tailHash);
            cmd.Parameters.AddWithValue("$stored", stored);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Points a record at the packed copy of its plain one; the transcript it was copied from stays as recorded.</summary>
    private void Repoint(string family, string conversation, string to, long length, string tailHash)
    {
        var stored = new FileInfo(to).Length;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                UPDATE copies SET copy=$to, copied_length=$length, tail_hash=$tail, stored_length=$stored
                WHERE family=$f AND conversation=$c;
                """;
            cmd.Parameters.AddWithValue("$f", family);
            cmd.Parameters.AddWithValue("$c", conversation);
            cmd.Parameters.AddWithValue("$to", to);
            cmd.Parameters.AddWithValue("$length", length);
            cmd.Parameters.AddWithValue("$tail", tailHash);
            cmd.Parameters.AddWithValue("$stored", stored);
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
                SELECT source, copy, source_length, source_written, copied_length, tail_hash FROM copies WHERE family=$f AND conversation=$c;
                """;
            cmd.Parameters.AddWithValue("$f", family);
            cmd.Parameters.AddWithValue("$c", conversation);
            using var r = cmd.ExecuteReader();
            return r.Read() ? Read(r, 0, family) : null;
        }
    }

    private Dictionary<(string, string), CopyRecord> Load()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var all = new Dictionary<(string, string), CopyRecord>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT family, conversation, source, copy, source_length, source_written, copied_length, tail_hash FROM copies;
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read()) all[(r.GetString(0), r.GetString(1))] = Read(r, 2, r.GetString(0));
            return all;
        }
    }

    private CopyRecord Read(SqliteDataReader r, int at, string family) =>
        new(r.GetString(at), Located(family, r.GetString(at + 1)), r.GetInt64(at + 2), r.GetInt64(at + 3), r.GetInt64(at + 4),
            r.IsDBNull(at + 5) ? null : r.GetString(at + 5));

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _connection.Dispose();
        }
    }

    /// <summary>Reads through to a stream, hashing what passes.</summary>
    private sealed class HashingStream(Stream inner) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        public byte[] Digest() => _hash.GetHashAndReset();

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer);
            _hash.AppendData(buffer[..read]);
            return read;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _hash.Dispose();
            base.Dispose(disposing);
        }
    }
}

/// <summary>
/// The drive's own compression for a folder. Earlier versions turned it on for the copies; packed
/// copies are compressed already, so it is turned off for the files written from now on.
/// </summary>
internal static class NtfsCompression
{
    private const uint FsctlSetCompression = 0x9C040;
    private const ushort CompressionFormatNone = 0;

    /// <summary>Files made in the folder from now on are stored as they are; those in it already stay as they are.</summary>
    public static bool TryDisable(string folder)
    {
        try
        {
            if ((File.GetAttributes(folder) & FileAttributes.Compressed) == 0) return true;
            using var handle = CreateFile(folder, 0x80000000 | 0x40000000, 0x7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
            if (handle.IsInvalid) return false;
            var format = CompressionFormatNone;
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
