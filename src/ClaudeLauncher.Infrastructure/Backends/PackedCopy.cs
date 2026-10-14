using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using ZstdSharp;
using ZstdSharp.Unsafe;

namespace ClaudeLauncher.Infrastructure.Backends;

/// <summary>
/// This tool's compressed copy of a transcript: Zstandard frames of about
/// <see cref="FrameBytes"/> each, cut at line ends where it can, compressed independently, with a
/// seek table after them in Zstandard's seekable format (a skippable frame any decoder passes
/// over). Most of a transcript's bytes are pictures sent inline, which compress poorly on their
/// own but repeat: long-distance matching over a frame finds the repeats.
///
/// <para>Reading one is reading a plain file (<see cref="PackedCopyStream"/>): the table says
/// which frame holds a position, and only that frame is decompressed — so the transcript view,
/// the search index and the usage count read a copy at an offset just as they read a transcript.</para>
/// </summary>
public static class PackedCopy
{
    /// <summary>The suffix of a packed copy's name, after the transcript's own extension.</summary>
    public const string Suffix = ".zst";

    /// <summary>
    /// Plain bytes per frame: large enough for the repeats to be found, small enough to seek in.
    /// A frame grows past it to end at a line break, but never to twice its size.
    /// </summary>
    public const int FrameBytes = 32 * 1024 * 1024;

    /// <summary>The window: one frame, so a repeat anywhere in it is found.</summary>
    internal const int WindowLog = 25;

    internal const int Level = 6;

    private const uint SkippableMagic = 0x184D2A5E;
    private const uint SeekableMagic = 0x8F92EAB1;
    private const int FooterBytes = 9;
    private const int EntryBytes = 8;

    /// <summary>One frame: its compressed and plain sizes.</summary>
    public readonly record struct Frame(uint Compressed, uint Plain);

    /// <summary>Whether a path names a packed copy (by its name; <see cref="ReadTable"/> says whether it is one).</summary>
    public static bool HasSuffix(string? path) =>
        path is not null && path.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether <paramref name="packed"/> is the packed copy of the plain copy <paramref name="plain"/>,
    /// as the archive packs the copies it made before copies were packed: the same name, with
    /// <see cref="Suffix"/> added. What was read from the plain one holds for the packed one.
    /// </summary>
    public static bool IsPackingOf(string packed, string plain) =>
        HasSuffix(packed) && !HasSuffix(plain)
        && string.Equals(Path.GetFileName(packed), Path.GetFileName(plain) + Suffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The frames of a packed copy, from the seek table at its end; null when the file has none (a
    /// Zstandard file someone else wrote, or one cut short).
    /// </summary>
    public static List<Frame>? ReadTable(Stream file)
    {
        var length = file.Length;
        if (length < FooterBytes + 8) return null;
        Span<byte> footer = stackalloc byte[FooterBytes];
        file.Position = length - FooterBytes;
        file.ReadExactly(footer);
        if (BinaryPrimitives.ReadUInt32LittleEndian(footer[5..]) != SeekableMagic) return null;
        var count = BinaryPrimitives.ReadUInt32LittleEndian(footer);
        if ((footer[4] & 0x7C) != 0) return null;                   // reserved bits
        var entryBytes = (footer[4] & 0x80) != 0 ? EntryBytes + 4 : EntryBytes;
        var tableBytes = (long)count * entryBytes + FooterBytes;
        if (tableBytes + 8 > length) return null;
        var table = new byte[tableBytes + 8];
        file.Position = length - table.Length;
        file.ReadExactly(table);
        if (BinaryPrimitives.ReadUInt32LittleEndian(table) != SkippableMagic
            || BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(4)) != tableBytes) return null;
        var frames = new List<Frame>((int)count);
        long compressed = 0;
        for (var i = 0; i < count; i++)
        {
            var at = 8 + i * entryBytes;
            var frame = new Frame(BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(at)),
                BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(at + 4)));
            frames.Add(frame);
            compressed += frame.Compressed;
        }
        // The frames end where the table begins: anything else is not a table of this file.
        return compressed == length - table.Length ? frames : null;
    }

    /// <summary>Writes the seek table for the frames, at the stream's position.</summary>
    internal static void WriteTable(Stream file, IReadOnlyList<Frame> frames)
    {
        var tableBytes = frames.Count * EntryBytes + FooterBytes;
        var buffer = new byte[8 + tableBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, SkippableMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), (uint)tableBytes);
        for (var i = 0; i < frames.Count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8 + i * EntryBytes), frames[i].Compressed);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(12 + i * EntryBytes), frames[i].Plain);
        }
        var footer = buffer.AsSpan(8 + frames.Count * EntryBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(footer, (uint)frames.Count);
        footer[4] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(footer[5..], SeekableMagic);
        file.Write(buffer);
    }

    /// <summary>A packed copy's plain length and last write; null when the file is not one.</summary>
    public static (long Length, long Written)? Stat(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096,
            FileOptions.RandomAccess);
        if (ReadTable(file) is not { } frames) return null;
        return (frames.Sum(f => (long)f.Plain), File.GetLastWriteTimeUtc(path).Ticks);
    }

    /// <summary>
    /// Writes <paramref name="plain"/> as a new packed copy at <paramref name="path"/>. With
    /// <paramref name="wholeLines"/>, only up to its last line break: a line still being written
    /// waits for the next pass. The plain length written, and the hash of its last
    /// <see cref="TailBytes"/> bytes (what a copy grown later must still end with).
    /// </summary>
    public static (long Length, string TailHash) Write(Stream plain, string path, bool wholeLines,
        CancellationToken cancellation = default) =>
        Write(plain, path, wholeLines, FrameBytes, cancellation);

    /// <param name="frameBytes">Plain bytes per frame: small ones in tests, to have many.</param>
    internal static (long Length, string TailHash) Write(Stream plain, string path, bool wholeLines, int frameBytes,
        CancellationToken cancellation)
    {
        using var output = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1024 * 1024);
        using var writer = new PackedCopyWriter(output, frameBytes);
        var length = writer.Feed(plain, wholeLines, cancellation);
        writer.Finish();
        // On the disk before anything is renamed to it or deleted for it.
        output.Flush(flushToDisk: true);
        return (length, writer.TailHash());
    }

    /// <summary>How much of a copy's end is hashed to check that it still ends as its transcript does.</summary>
    public const int TailBytes = 4096;

    /// <summary>The hash of a stream's bytes [length - TailBytes, length): what <see cref="Write"/> returns for the same bytes.</summary>
    public static string TailHashOf(Stream plain, long length)
    {
        var size = (int)Math.Min(TailBytes, length);
        var bytes = new byte[size];
        plain.Position = length - size;
        plain.ReadExactly(bytes);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    /// <summary>
    /// Adds <paramref name="more"/> to the end of a packed copy, up to its last line break when
    /// <paramref name="wholeLines"/>: as a frame of its own, or — when the copy's last frame is
    /// small — rewritten together with that frame, so a copy that grows a little at a time does
    /// not end as a string of tiny frames. The bytes added, and the copy's new tail hash.
    /// </summary>
    public static (long Added, string TailHash) Append(string path, Stream more, bool wholeLines,
        CancellationToken cancellation = default) =>
        Append(path, more, wholeLines, FrameBytes, cancellation);

    internal static (long Added, string TailHash) Append(string path, Stream more, bool wholeLines, int frameBytes,
        CancellationToken cancellation)
    {
        // Nobody reads it while it changes: a reader opening it now waits for the next pass, as with any file held.
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1024 * 1024);
        var frames = ReadTable(file) ?? throw new InvalidDataException("副本的索引表不完整：" + Path.GetFileName(path));
        long dataEnd = frames.Sum(f => (long)f.Compressed);
        var plainEnd = frames.Sum(f => (long)f.Plain);
        byte[]? carried = null;
        byte[] seed;
        using (var stream = new PackedCopyStream(file, frames, leaveOpen: true))
        {
            if (frames.Count > 0 && frames[^1].Plain < frameBytes / 4)
            {
                // The last frame is small: read it back and write it again with what is added.
                var last = frames[^1];
                dataEnd -= last.Compressed;
                plainEnd -= last.Plain;
                carried = new byte[last.Plain];
                stream.Position = plainEnd;
                stream.ReadExactly(carried);
                frames.RemoveAt(frames.Count - 1);
            }
            // What the copy keeps just before where writing starts: part of the tail when what follows is short.
            seed = new byte[(int)Math.Min(TailBytes, plainEnd)];
            stream.Position = plainEnd - seed.Length;
            stream.ReadExactly(seed);
        }
        // What is written over — the small last frame and the table — is kept, to put back should writing fail.
        var replaced = new byte[file.Length - dataEnd];
        file.Position = dataEnd;
        file.ReadExactly(replaced);
        try
        {
            file.SetLength(dataEnd);
            file.Position = dataEnd;
            using var writer = new PackedCopyWriter(file, frameBytes, frames);
            writer.Seed(seed);
            if (carried is not null) writer.Write(carried);
            var before = writer.Length;
            var length = writer.Feed(more, wholeLines, cancellation);
            writer.Finish();
            file.Flush(flushToDisk: true);
            return (length - before, writer.TailHash());
        }
        catch
        {
            file.SetLength(dataEnd);
            file.Position = dataEnd;
            file.Write(replaced);
            file.Flush();
            throw;
        }
    }

    /// <summary>Opens a packed copy for reading as the plain transcript it holds; null when the file is not one.</summary>
    /// <param name="wholeFrames">
    /// For reading here and there rather than straight through — a transcript opened at its end,
    /// then read further back: each frame is unpacked whole and kept while it is read.
    /// </param>
    public static PackedCopyStream? Open(string path, bool wholeFrames = false)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024,
            FileOptions.RandomAccess);
        try
        {
            if (ReadTable(file) is { } frames) return new PackedCopyStream(file, frames, leaveOpen: false, wholeFrames);
            file.Dispose();
            return null;
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    internal static Compressor NewCompressor()
    {
        var compressor = new Compressor(Level);
        compressor.SetParameter(ZSTD_cParameter.ZSTD_c_windowLog, WindowLog);
        compressor.SetParameter(ZSTD_cParameter.ZSTD_c_enableLongDistanceMatching, 1);
        compressor.SetParameter(ZSTD_cParameter.ZSTD_c_checksumFlag, 1);
        return compressor;
    }
}

/// <summary>
/// Writes frames of a packed copy to a stream positioned where they go, and the seek table after
/// them. A frame closes once it holds its size (<see cref="PackedCopy.FrameBytes"/>) at the next
/// line break — or at twice that on a line longer than that.
/// </summary>
internal sealed class PackedCopyWriter : IDisposable
{
    private readonly Stream _output;
    private readonly int _frameBytes;
    private readonly int _maxFrameBytes;
    private readonly Compressor _compressor = PackedCopy.NewCompressor();
    private readonly List<PackedCopy.Frame> _frames;
    private readonly byte[] _out = new byte[1024 * 1024];
    private readonly byte[] _tail = new byte[PackedCopy.TailBytes];
    private int _tailFill;
    private long _frameCompressed;
    private long _framePlain;
    private long _plain;

    public PackedCopyWriter(Stream output, int frameBytes, List<PackedCopy.Frame>? framesBefore = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(frameBytes, 16);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(frameBytes, PackedCopy.FrameBytes);
        _output = output;
        _frameBytes = frameBytes;
        _maxFrameBytes = 2 * frameBytes;
        _frames = framesBefore ?? [];
        _plain = _frames.Sum(f => (long)f.Plain);
    }

    /// <summary>Plain bytes in the copy so far.</summary>
    public long Length => _plain;

    /// <summary>
    /// Copies a stream in. With <paramref name="wholeLines"/>, what follows its last line break is
    /// left out: it is held back until the end, then dropped. The copy's plain length after.
    /// </summary>
    public long Feed(Stream input, bool wholeLines, CancellationToken cancellation)
    {
        var buffer = new byte[1024 * 1024];
        var pending = new MemoryStream();          // bytes after the last line break seen, when lines are whole
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            var data = buffer.AsSpan(0, read);
            if (!wholeLines)
            {
                Write(data);
                continue;
            }
            var newline = data.LastIndexOf((byte)'\n');
            if (newline < 0)
            {
                pending.Write(data);
                // A line longer than a frame can hold is written as it is: it still ends somewhere.
                if (pending.Length >= _maxFrameBytes) FlushPending(pending);
                continue;
            }
            FlushPending(pending);
            Write(data[..(newline + 1)]);
            pending.Write(data[(newline + 1)..]);
        }
        return _plain;
    }

    private void FlushPending(MemoryStream pending)
    {
        if (pending.Length == 0) return;
        Write(pending.GetBuffer().AsSpan(0, (int)pending.Length));
        pending.SetLength(0);
    }

    /// <summary>Adds bytes to the current frame, closing it at a line break once it is full.</summary>
    public void Write(ReadOnlySpan<byte> data)
    {
        while (data.Length > 0)
        {
            var room = _frameBytes - _framePlain;
            ReadOnlySpan<byte> part;
            var close = false;
            if (room > 0)
                part = data[..(int)Math.Min(room, data.Length)];
            else
            {
                // Full: close at the next line break in reach, or at the hard limit.
                var limit = (int)Math.Min(_maxFrameBytes - _framePlain, data.Length);
                var newline = data[..limit].IndexOf((byte)'\n');
                part = newline >= 0 ? data[..(newline + 1)] : data[..limit];
                close = newline >= 0 || _framePlain + limit >= _maxFrameBytes;
            }
            Compress(part, final: false);
            Remember(part);
            _framePlain += part.Length;
            _plain += part.Length;
            data = data[part.Length..];
            if (close) EndFrame();
        }
    }

    private void Compress(ReadOnlySpan<byte> source, bool final)
    {
        while (true)
        {
            var status = _compressor.WrapStream(source, _out, out var consumed, out var written, final);
            if (written > 0)
            {
                _output.Write(_out, 0, written);
                _frameCompressed += written;
            }
            source = source[consumed..];
            if (status == System.Buffers.OperationStatus.InvalidData) throw new InvalidDataException("压缩副本时出错");
            // Mid-frame, taking the input is enough: what the compressor holds comes out later.
            // At the frame's end, everything has to be out.
            if (final ? status == System.Buffers.OperationStatus.Done && source.IsEmpty : source.IsEmpty) return;
            if (consumed == 0 && written == 0 && status != System.Buffers.OperationStatus.DestinationTooSmall)
                throw new InvalidDataException("压缩副本时没有进展");
        }
    }

    private void EndFrame()
    {
        if (_framePlain == 0) return;
        Compress([], final: true);
        _frames.Add(new PackedCopy.Frame((uint)_frameCompressed, (uint)_framePlain));
        _compressor.ResetStream();
        _frameCompressed = 0;
        _framePlain = 0;
    }

    /// <summary>The copy's bytes just before where this writer starts: the tail when what it adds is short.</summary>
    public void Seed(ReadOnlySpan<byte> before) => Remember(before);

    /// <summary>The last <see cref="PackedCopy.TailBytes"/> plain bytes, kept as they pass.</summary>
    private void Remember(ReadOnlySpan<byte> part)
    {
        if (part.Length >= _tail.Length)
        {
            part[^_tail.Length..].CopyTo(_tail);
            _tailFill = _tail.Length;
            return;
        }
        var keep = Math.Min(_tailFill, _tail.Length - part.Length);
        _tail.AsSpan(_tailFill - keep, keep).CopyTo(_tail);
        part.CopyTo(_tail.AsSpan(keep));
        _tailFill = keep + part.Length;
    }

    /// <summary>Closes the last frame and writes the table.</summary>
    public void Finish()
    {
        EndFrame();
        PackedCopy.WriteTable(_output, _frames);
        _output.Flush();
    }

    /// <summary>
    /// The hash of the copy's last bytes as <see cref="PackedCopy.TailHashOf"/> computes it. Bytes
    /// carried over from before this writer (an append) are part of the tail when it is short.
    /// </summary>
    public string TailHash() => Convert.ToHexString(SHA256.HashData(_tail.AsSpan(0, _tailFill)));

    public void Dispose() => _compressor.Dispose();
}

/// <summary>
/// A packed copy read as the plain bytes it holds: seekable, the plain length its
/// <see cref="Stream.Length"/>, one frame decompressed at a time — only the one a position falls in.
///
/// <para>Read straight through, a frame is decompressed as it is read, and a position further on
/// in it is reached by decompressing up to it. Read here and there (whole frames), the frame is
/// unpacked whole into memory the decoder would otherwise keep as its window, and read again from
/// there at no cost: opening a transcript at its end and then widening the view decompresses its
/// last frame once, not once a step.</para>
/// </summary>
public sealed class PackedCopyStream : Stream
{
    private readonly Stream _file;
    private readonly bool _leaveOpen;
    private readonly bool _wholeFrames;
    private NativeBuffer? _unpacked;        // whole frames: the open frame's plain bytes
    private NativeBuffer? _packed;          // whole frames: its compressed bytes
    private readonly long[] _plainStart;
    private readonly long[] _compressedStart;
    private readonly PackedCopy.Frame[] _frames;
    private readonly Decompressor _decompressor = new();
    private DecompressionStream? _frame;
    private int _frameIndex = -1;
    private long _framePosition;            // plain position the open frame's decoder has reached
    private long _position;
    private byte[]? _skip;

    internal PackedCopyStream(Stream file, IReadOnlyList<PackedCopy.Frame> frames, bool leaveOpen, bool wholeFrames = false)
    {
        _file = file;
        _leaveOpen = leaveOpen;
        _wholeFrames = wholeFrames;
        _frames = [.. frames];
        _plainStart = new long[_frames.Length + 1];
        _compressedStart = new long[_frames.Length + 1];
        for (var i = 0; i < _frames.Length; i++)
        {
            _plainStart[i + 1] = _plainStart[i] + _frames[i].Plain;
            _compressedStart[i + 1] = _compressedStart[i] + _frames[i].Compressed;
        }
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _plainStart[^1];

    public override long Position
    {
        get => _position;
        set => _position = Math.Clamp(value, 0, Length);
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        try
        {
            return ReadFrames(buffer);
        }
        catch (Exception ex) when (ex is ZstdException or InvalidDataException or EndOfStreamException)
        {
            // Damaged on disk: read as any file that cannot be read now, which every caller handles.
            _frame?.Dispose();
            _frame = null;
            _frameIndex = -1;
            throw new IOException("副本损坏，读不出来", ex);
        }
    }

    private int ReadFrames(Span<byte> buffer)
    {
        if (buffer.IsEmpty || _position >= Length) return 0;
        var index = FrameOf(_position);
        if (_wholeFrames)
        {
            if (_frameIndex != index) Unpack(index);
            var at = (int)(_position - _plainStart[index]);
            var count = (int)Math.Min(buffer.Length, _frames[index].Plain - at);
            _unpacked!.View(at, count).CopyTo(buffer);
            _position += count;
            return count;
        }
        if (_frame is null || _frameIndex != index || _framePosition > _position) OpenFrame(index);
        // Forward to the position within the frame.
        while (_framePosition < _position)
        {
            _skip ??= new byte[64 * 1024];
            var n = _frame!.Read(_skip, 0, (int)Math.Min(_skip.Length, _position - _framePosition));
            if (n <= 0) throw new InvalidDataException("副本的一帧比索引表说的短");
            _framePosition += n;
        }
        var wanted = (int)Math.Min(buffer.Length, _plainStart[index + 1] - _position);
        var read = _frame!.Read(buffer[..wanted]);
        if (read <= 0) throw new InvalidDataException("副本的一帧比索引表说的短");
        _framePosition += read;
        _position += read;
        return read;
    }

    private int FrameOf(long position)
    {
        var at = Array.BinarySearch(_plainStart, 0, _frames.Length, position);
        return at >= 0 ? at : ~at - 1;
    }

    /// <summary>Unpacks a whole frame in one go: straight into its buffer, which serves as the decoder's window.</summary>
    private void Unpack(int index)
    {
        _frameIndex = -1;
        var frame = _frames[index];
        var packed = (_packed ??= new NativeBuffer()).Grow((int)frame.Compressed);
        var unpacked = (_unpacked ??= new NativeBuffer()).Grow((int)frame.Plain);
        _file.Position = _compressedStart[index];
        _file.ReadExactly(packed);
        var written = _decompressor.Unwrap(packed, unpacked);
        if (written != frame.Plain) throw new InvalidDataException("副本的一帧比索引表说的短");
        _frameIndex = index;
    }

    private void OpenFrame(int index)
    {
        _frame?.Dispose();
        _decompressor.ResetStream();
        var slice = new StreamSlice(_file, _compressedStart[index], _frames[index].Compressed);
        _frame = new DecompressionStream(slice, _decompressor, 64 * 1024, checkEndOfStream: true,
            preserveDecompressor: true, leaveOpen: false);
        _frameIndex = index;
        _framePosition = _plainStart[index];
    }

    public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
    {
        SeekOrigin.Begin => offset,
        SeekOrigin.Current => _position + offset,
        _ => Length + offset,
    };

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _frame?.Dispose();
            _decompressor.Dispose();
            _unpacked?.Dispose();
            _packed?.Dispose();
            if (!_leaveOpen) _file.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// Memory outside the managed heap, as the decoder's own window is: a frame read whole takes tens
/// of megabytes, and is handed back the moment its copy is closed rather than at some later
/// collection.
/// </summary>
internal sealed unsafe class NativeBuffer : IDisposable
{
    private byte* _bytes;
    private int _size;

    /// <summary>The first <paramref name="length"/> bytes, grown to that first when smaller (what it held is not kept).</summary>
    public Span<byte> Grow(int length)
    {
        ObjectDisposedException.ThrowIf(_size < 0, this);
        if (length > _size)
        {
            NativeMemory.Free(_bytes);
            _bytes = null;
            _size = 0;
            _bytes = (byte*)NativeMemory.Alloc((nuint)length);
            _size = length;
        }
        return new Span<byte>(_bytes, length);
    }

    /// <summary>Bytes it holds.</summary>
    public ReadOnlySpan<byte> View(int start, int length)
    {
        ObjectDisposedException.ThrowIf(_size < 0, this);
        ArgumentOutOfRangeException.ThrowIfGreaterThan((long)start + length, _size);
        return new ReadOnlySpan<byte>(_bytes + start, length);
    }

    public void Dispose()
    {
        NativeMemory.Free(_bytes);
        _bytes = null;
        _size = -1;
        GC.SuppressFinalize(this);
    }

    ~NativeBuffer() => NativeMemory.Free(_bytes);
}

/// <summary>A window of a stream, read-only: one frame's compressed bytes, or the part of a transcript a copy has not got yet.</summary>
internal sealed class StreamSlice(Stream inner, long start, long length) : Stream
{
    private long _read;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position { get => _read; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_read >= length || count == 0) return 0;
        inner.Position = start + _read;
        var n = inner.Read(buffer, offset, (int)Math.Min(count, length - _read));
        if (n <= 0) throw new EndOfStreamException("文件比读到的位置短：它在读的时候变了");
        _read += n;
        return n;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
