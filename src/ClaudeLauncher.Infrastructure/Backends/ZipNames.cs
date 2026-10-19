using System.Buffers.Binary;
using System.Text;

namespace ClaudeLauncher.Infrastructure.Backends;

/// <summary>
/// The names of the files a zip lists, read from its central directory as they stream past. A
/// Takeout of every Google product lists hundreds of thousands of files; opened as a ZipArchive it
/// costs about a kilobyte of objects for each, all kept until the last is read — only to learn
/// whose export it is. Here each name is a short-lived string, and nothing else is kept.
/// </summary>
internal static class ZipNames
{
    private const uint EndSignature = 0x06054B50;
    private const uint End64LocatorSignature = 0x07064B50;
    private const uint End64Signature = 0x06064B50;
    private const uint EntrySignature = 0x02014B50;

    /// <summary>The fixed part of the end record; its comment follows.</summary>
    private const int EndLength = 22;

    /// <summary>
    /// The files' names, '/' between folders, folders themselves left out; read as they are enumerated.
    /// Names are read as UTF-8 whatever the zip says: only the plain ASCII ones decide anything.
    /// </summary>
    /// <exception cref="InvalidDataException">Not a zip, or its list of files is damaged or cut short.</exception>
    /// <exception cref="IOException">It could not be read just now.</exception>
    public static IEnumerable<string> Of(Stream zip)
    {
        var (count, offset) = FindDirectory(zip);
        return Entries(zip, count, offset);
    }

    /// <summary>How many entries the zip lists, and where the list starts — ZIP64's larger numbers included.</summary>
    private static (long Count, long Offset) FindDirectory(Stream zip)
    {
        var length = zip.Length;
        // The end record is the last thing in the file, after at most a 64 KB comment.
        var tail = (int)Math.Min(length, EndLength + ushort.MaxValue);
        var buffer = new byte[tail];
        zip.Position = length - tail;
        Read(zip, buffer);
        for (var at = tail - EndLength; at >= 0; at--)
        {
            if (U32(buffer, at) != EndSignature || at + EndLength + U16(buffer, at + 20) > tail) continue;
            long count = U16(buffer, at + 10), offset = U32(buffer, at + 16);
            if ((count == ushort.MaxValue || offset == uint.MaxValue) && at >= 20 && U32(buffer, at - 20) == End64LocatorSignature)
            {
                // ZIP64: the real numbers are in a record of their own, its place given just before this one.
                var record = new byte[56];
                zip.Position = Checked((long)U64(buffer, at - 20 + 8), length - record.Length);
                Read(zip, record);
                if (U32(record, 0) != End64Signature) throw Damaged();
                count = (long)U64(record, 32);
                offset = (long)U64(record, 48);
            }
            // Each entry takes 46 bytes at least: a larger count is not a list of files.
            return (Checked(count, length / 46), Checked(offset, length));
        }
        throw new InvalidDataException("不是 zip 压缩包：找不到它的文件列表");
    }

    private static IEnumerable<string> Entries(Stream zip, long count, long offset)
    {
        zip.Position = offset;
        var header = new byte[46];
        var bytes = new byte[ushort.MaxValue];
        for (long i = 0; i < count; i++)
        {
            Read(zip, header);
            if (U32(header, 0) != EntrySignature) throw Damaged();
            var nameLength = U16(header, 28);
            Read(zip, bytes.AsSpan(0, nameLength));
            var name = Encoding.UTF8.GetString(bytes, 0, nameLength).Replace('\\', '/');
            // The extra fields and the comment: passed over, through the same buffer once the name is out of it.
            for (var skip = U16(header, 30) + U16(header, 32); skip > 0; skip -= Math.Min(skip, bytes.Length))
                Read(zip, bytes.AsSpan(0, Math.Min(skip, bytes.Length)));
            if (name.Length > 0 && !name.EndsWith('/')) yield return name;
        }
    }

    private static void Read(Stream zip, Span<byte> into)
    {
        try
        {
            zip.ReadExactly(into);
        }
        catch (EndOfStreamException)
        {
            throw Damaged();        // as a ZipArchive says of a list cut short
        }
    }

    private static long Checked(long value, long limit) => value >= 0 && value <= limit ? value : throw Damaged();

    private static InvalidDataException Damaged() => new("压缩包的文件列表已损坏");

    private static ushort U16(byte[] buffer, int at) => BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(at));

    private static uint U32(byte[] buffer, int at) => BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(at));

    private static ulong U64(byte[] buffer, int at) => BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(at));
}
