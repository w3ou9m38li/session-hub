using System.Buffers;

namespace ClaudeLauncher.Infrastructure.Backends;

/// <summary>
/// A line being put together from the blocks a transcript is read in, in memory borrowed from
/// a pool. Every file used to get arrays of its own — a megabyte to read into, and a
/// line buffer that doubled its way up to the longest line — and with hundreds of files read in
/// one pass that was gigabytes of large objects: each one a trip to the large-object heap, and
/// a full collection of the whole program every few of them, felt as stutter in the window.
/// Borrowed memory goes back when the reading is done and is lent out again to the next file.
/// </summary>
internal sealed class PooledBytes : IDisposable
{
    /// <summary>
    /// A pool of its own, and a small one: a few arrays of each size up to the read block. The
    /// shared pool keeps many large arrays per processor for good — a hundred megabytes left
    /// behind after one pass over every transcript. A line longer than a block, which is rare
    /// now that tool output is passed over unread, gets an array of its own and lets it go.
    /// </summary>
    private static readonly ArrayPool<byte> Pool = ArrayPool<byte>.Create(BlockSize, maxArraysPerBucket: 4);

    private const int BlockSize = 1024 * 1024;

    private byte[] _array;
    private int _count;

    public PooledBytes(int initial = 64 * 1024) => _array = Pool.Rent(initial);

    public int WrittenCount => _count;

    public ReadOnlySpan<byte> WrittenSpan => _array.AsSpan(0, _count);

    public ReadOnlyMemory<byte> WrittenMemory => _array.AsMemory(0, _count);

    public void Write(ReadOnlySpan<byte> bytes)
    {
        if (_count + bytes.Length > _array.Length)
        {
            var larger = Pool.Rent(Math.Max(_count + bytes.Length, _array.Length * 2));
            _array.AsSpan(0, _count).CopyTo(larger);
            Pool.Return(_array);
            _array = larger;
        }
        bytes.CopyTo(_array.AsSpan(_count));
        _count += bytes.Length;
    }

    public void ResetWrittenCount() => _count = 0;

    public void Dispose()
    {
        if (_array.Length == 0) return;
        Pool.Return(_array);
        _array = [];
        _count = 0;
    }

    /// <summary>A block to read a file into, borrowed; give it back with <see cref="Return"/>.</summary>
    public static byte[] RentBlock() => Pool.Rent(BlockSize);

    public static void Return(byte[] block) => Pool.Return(block);

    /// <summary>
    /// A line's first bytes: what is held of it so far and what just arrived, together — a line
    /// that began at the end of the previous block has only a few bytes held. Copied into
    /// <paramref name="scratch"/> only when the two have to be joined.
    /// </summary>
    public static ReadOnlySpan<byte> Head(ReadOnlySpan<byte> held, ReadOnlySpan<byte> piece, int size, Span<byte> scratch)
    {
        if (held.Length >= size) return held[..size];
        var take = Math.Min(piece.Length, size - held.Length);
        held.CopyTo(scratch);
        piece[..take].CopyTo(scratch[held.Length..]);
        return scratch[..(held.Length + take)];
    }
}
