using System.Text;
using ClaudeLauncher.Infrastructure.Backends;

namespace ClaudeLauncher.IntegrationTests;

/// <summary>
/// This tool's packed copies: Zstandard frames cut at line ends, with a seek table any decoder
/// passes over — read, from any position, as the plain bytes they hold; grown at the end, and
/// left as they were when growing fails; and, when damaged, a file that cannot be read rather
/// than wrong words.
/// </summary>
public sealed class PackedCopyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "launcher-packed", Guid.NewGuid().ToString("n"));

    public PackedCopyTests() => Directory.CreateDirectory(_dir);

    /// <summary>Lines of many lengths, now and then a long one — as pictures make some lines of a transcript.</summary>
    private static byte[] Lines(int count, int seed = 1)
    {
        var random = new Random(seed);
        var text = new StringBuilder();
        for (var i = 0; i < count; i++)
        {
            text.Append("{\"n\":").Append(i).Append(",\"text\":\"");
            var length = random.Next(1, i % 17 == 0 ? 3000 : 200);
            for (var j = 0; j < length; j++) text.Append((char)('a' + random.Next(26)));
            text.Append("\"}\n");
        }
        return Encoding.UTF8.GetBytes(text.ToString());
    }

    private string Pack(byte[] plain, int frameBytes = 1024)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("n") + ".jsonl" + PackedCopy.Suffix);
        PackedCopy.Write(new MemoryStream(plain), path, wholeLines: false, frameBytes, CancellationToken.None);
        return path;
    }

    private static List<PackedCopy.Frame> Frames(string path)
    {
        using var file = File.OpenRead(path);
        return PackedCopy.ReadTable(file)!;
    }

    private static byte[] ReadAll(Stream stream)
    {
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ACopyReadsAsThePlainBytesFromAnyPosition(bool wholeFrames)
    {
        var plain = Lines(400);
        var path = Pack(plain);
        Assert.True(Frames(path).Count > 10);

        using var stream = PackedCopy.Open(path, wholeFrames)!;
        Assert.Equal(plain.Length, stream.Length);
        Assert.Equal(plain, ReadAll(stream));

        // Forward, backward, within a frame and across frames.
        var random = new Random(7);
        var buffer = new byte[5000];
        for (var i = 0; i < 300; i++)
        {
            var at = random.Next(plain.Length);
            stream.Position = at;
            var read = stream.ReadAtLeast(buffer, Math.Min(buffer.Length, plain.Length - at), throwOnEndOfStream: false);
            Assert.Equal(plain.AsSpan(at, read).ToArray(), buffer.AsSpan(0, read).ToArray());
        }
        stream.Seek(-10, SeekOrigin.End);
        Assert.Equal(plain[^10..], ReadAll(stream));

        Assert.Equal(plain.Length, PackedCopy.Stat(path)!.Value.Length);
        Assert.Equal(plain.Length, TranscriptFiles.Stat(path)!.Value.Length);
        using var seekable = TranscriptFiles.OpenSeekable(path);
        Assert.Equal(plain.Length, seekable.Length);
    }

    [Fact]
    public void FramesEndAtLineBreaksAndALineLongerThanTwoFramesIsCutAtTheLimit()
    {
        var plain = Encoding.UTF8.GetBytes("{\"a\":1}\n" + new string('x', 5000) + "\n{\"b\":2}\n" + new string('y', 900) + "\n");
        var path = Pack(plain, frameBytes: 1024);

        var frames = Frames(path);
        Assert.True(frames.Count >= 3);
        long start = 0;
        foreach (var frame in frames)
        {
            Assert.InRange(frame.Plain, 1u, 2048u);
            start += frame.Plain;
            Assert.True(plain[start - 1] == (byte)'\n' || frame.Plain == 2048);
        }
        Assert.Equal(plain.Length, start);
        using var stream = PackedCopy.Open(path)!;
        Assert.Equal(plain, ReadAll(stream));
    }

    [Fact]
    public void AnyZstandardDecoderReadsACopyWhole()
    {
        var plain = Lines(300);
        var path = Pack(plain);
        using var decoder = new ZstdSharp.DecompressionStream(File.OpenRead(path));
        Assert.Equal(plain, ReadAll(decoder));
    }

    [Fact]
    public void WholeLinesLeaveOutALineStillBeingWritten()
    {
        var plain = Encoding.UTF8.GetBytes("{\"a\":1}\n{\"b\":2}\n{\"c\":");
        var path = Path.Combine(_dir, "w.jsonl" + PackedCopy.Suffix);

        // Read a few bytes at a time and without seeking, as from a log being unpacked.
        var (length, tail) = PackedCopy.Write(new Trickle(plain), path, wholeLines: true);

        Assert.Equal(16, length);
        using var stream = PackedCopy.Open(path)!;
        Assert.Equal("{\"a\":1}\n{\"b\":2}\n", Encoding.UTF8.GetString(ReadAll(stream)));
        Assert.Equal(PackedCopy.TailHashOf(new MemoryStream(plain), 16), tail);
    }

    [Fact]
    public void AnEmptyTranscriptMakesAnEmptyCopyThatGrows()
    {
        var path = Pack([]);
        using (var stream = PackedCopy.Open(path)!)
        {
            Assert.Equal(0, stream.Length);
            Assert.Equal(-1, stream.ReadByte());
        }

        var more = Lines(3);
        var (added, _) = PackedCopy.Append(path, new MemoryStream(more), wholeLines: false, 1024, CancellationToken.None);
        Assert.Equal(more.Length, added);
        using var grown = PackedCopy.Open(path)!;
        Assert.Equal(more, ReadAll(grown));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GrowingACopyAddsToItsEndAndTheTailHashIsTheTranscripts(bool tailFromTheTranscript)
    {
        var plain = Lines(200);
        var path = Pack(plain, frameBytes: 4096);
        var all = new MemoryStream();
        all.Write(plain);

        for (var i = 0; i < 30; i++)
        {
            var more = Lines(1 + i % 6, seed: 100 + i);
            PackedCopy.TailHashOf(all, all.Length, out var tail);
            var (added, hash) = PackedCopy.Append(path, new MemoryStream(more), wholeLines: false, 4096, CancellationToken.None,
                tailFromTheTranscript ? tail : null);
            Assert.Equal(more.Length, added);
            all.Write(more);
            Assert.Equal(PackedCopy.TailHashOf(all, all.Length), hash);
        }

        using (var stream = PackedCopy.Open(path)!)
            Assert.Equal(all.ToArray(), ReadAll(stream));
        Assert.False(File.Exists(path + PackedCopy.UndoSuffix));            // kept only while growing
        // Growing a little at a time does not leave a string of tiny frames: a small last frame
        // is written again together with what is added.
        Assert.All(Frames(path).SkipLast(1), f => Assert.True(f.Plain >= 4096 / 4));
    }

    [Fact]
    public void GrowingACopyTakesItsTailFromTheTranscriptRatherThanUnpackingIt()
    {
        var plain = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 20).Select(i => "{\"n\":" + i + ",\"text\":\"" + new string('a', 80) + "\"}\n")));
        var path = Pack(plain, frameBytes: 4096);
        var frame = Assert.Single(Frames(path));
        Assert.True(frame.Plain >= 4096 / 4);                              // grown after, not written again
        // The frame the tail is in, unreadable: growing the copy does not unpack it.
        var bytes = File.ReadAllBytes(path);
        bytes[frame.Compressed / 2] ^= 0xFF;
        File.WriteAllBytes(path, bytes);

        var more = Lines(3, seed: 9);
        var (added, hash) = PackedCopy.Append(path, new MemoryStream(more), wholeLines: false, 4096, CancellationToken.None,
            plain[^Math.Min(PackedCopy.TailBytes, plain.Length)..]);

        Assert.Equal(more.Length, added);
        Assert.Equal(PackedCopy.TailHashOf(new MemoryStream([.. plain, .. more]), plain.Length + more.Length), hash);
    }

    [Fact]
    public void GrowingThatFailsPartWayLeavesTheCopyAsItWas()
    {
        var plain = Lines(100);
        var path = Pack(plain, frameBytes: 4096);
        var before = File.ReadAllBytes(path);

        Assert.Throws<IOException>(() =>
            PackedCopy.Append(path, new Trickle(Lines(80, seed: 3), failAfter: 3000), wholeLines: false, 4096, CancellationToken.None));

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + PackedCopy.UndoSuffix));            // put back: nothing left to put back later
        using var stream = PackedCopy.Open(path)!;
        Assert.Equal(plain, ReadAll(stream));
    }

    [Fact]
    public void GrowingStoppedBeforeItBeganWritesNothing()
    {
        var path = Pack(Lines(100), frameBytes: 4096);
        var before = File.ReadAllBytes(path);
        var written = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, written);
        // Standing where what growing writes over would be kept beside the copy: as it was after, when nothing was kept.
        var undo = path + PackedCopy.UndoSuffix;
        byte[] standIn = [1, 2, 3];
        File.WriteAllBytes(undo, standIn);
        using var stop = new CancellationTokenSource();
        stop.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            PackedCopy.Append(path, new MemoryStream(Lines(5, seed: 4)), wholeLines: false, 4096, stop.Token));

        // Not cut short and written back as it was: not written to at all.
        Assert.Equal(written, File.GetLastWriteTimeUtc(path));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(standIn, File.ReadAllBytes(undo));
    }

    /// <summary>
    /// A copy as a growing that stopped part way leaves it — the app closed, the machine went off:
    /// what it was to write over kept beside it, its table gone, half a frame after its frames.
    /// </summary>
    internal static void StoppedGrowing(string path)
    {
        var bytes = File.ReadAllBytes(path);
        long dataEnd;
        using (var file = File.OpenRead(path)) dataEnd = PackedCopy.ReadTable(file)!.Sum(f => (long)f.Compressed);
        PackedCopy.SaveUndo(path, dataEnd, bytes.AsSpan((int)dataEnd));
        File.WriteAllBytes(path, bytes[..(int)dataEnd].Concat(new byte[] { 0x28, 0xB5, 0x2F, 0xFD, 0x04, 0x58, 0x7A }).ToArray());
    }

    [Fact]
    public void AGrowingThatStoppedPartWayIsPutBackFromWhatWasKeptBesideIt()
    {
        var plain = Lines(100);
        var path = Pack(plain, frameBytes: 4096);
        var before = File.ReadAllBytes(path);
        StoppedGrowing(path);
        Assert.Null(PackedCopy.Open(path));

        Assert.True(PackedCopy.Restore(path));

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + PackedCopy.UndoSuffix));
        using (var stream = PackedCopy.Open(path)!)
            Assert.Equal(plain, ReadAll(stream));
        Assert.False(PackedCopy.Restore(path));                             // nothing more to put back
    }

    [Fact]
    public void WhatWasKeptHalfWrittenIsDroppedAndTheCopyLeftAsItIs()
    {
        var path = Pack(Lines(100), frameBytes: 4096);
        var before = File.ReadAllBytes(path);
        long dataEnd;
        using (var file = File.OpenRead(path)) dataEnd = PackedCopy.ReadTable(file)!.Sum(f => (long)f.Compressed) / 2;
        PackedCopy.SaveUndo(path, dataEnd, before.AsSpan((int)dataEnd));
        var undo = path + PackedCopy.UndoSuffix;
        File.WriteAllBytes(undo, File.ReadAllBytes(undo)[..^10]);         // stopped while it was being written

        Assert.True(PackedCopy.Restore(path));

        Assert.Equal(before, File.ReadAllBytes(path));                     // not touched yet when it stopped
        Assert.False(File.Exists(undo));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ADamagedCopyIsAFileThatCannotBeReadNotWrongWords(bool wholeFrames)
    {
        var plain = Lines(300);
        var path = Pack(plain, frameBytes: 4096);
        var bytes = File.ReadAllBytes(path);
        bytes[bytes.Length / 2] ^= 0xFF;
        File.WriteAllBytes(path, bytes);

        using var stream = PackedCopy.Open(path, wholeFrames)!;
        Assert.Throws<IOException>(() => ReadAll(stream));
    }

    [Fact]
    public void ReadAtRandomAFrameIsUnpackedOnceForAllItsReads()
    {
        // Opened as the transcript view opens a transcript: at its end, then wider and wider.
        var plain = Lines(2000);
        var path = Pack(plain, frameBytes: 64 * 1024);
        using var stream = TranscriptFiles.OpenSeekable(path, 1, FileOptions.RandomAccess);
        foreach (var window in new[] { 2_000, 20_000, 60_000, 200_000 })
        {
            var start = Math.Max(0, stream.Length - window);
            var bytes = new byte[stream.Length - start];
            stream.Position = start;
            stream.ReadExactly(bytes);
            Assert.Equal(plain[(int)start..], bytes);
        }
    }

    [Fact]
    public void AFileWithoutItsTableIsNotACopy()
    {
        var path = Pack(Lines(50));
        File.WriteAllBytes(path, File.ReadAllBytes(path)[..^1]);           // cut short

        Assert.Null(PackedCopy.Open(path));
        Assert.Null(PackedCopy.Stat(path));
    }

    [Fact]
    public void ACopyIsReadAsTheKindOfTranscriptItHolds()
    {
        Assert.True(GeminiSession.IsSessionFile(@"C:\data\archive\copies\gemini\g.json" + PackedCopy.Suffix));
        Assert.False(GeminiSession.IsSessionFile(@"C:\data\archive\copies\claude\a.jsonl" + PackedCopy.Suffix));
        Assert.False(WholeTranscript.Is(@"C:\data\archive\copies\codex\c.jsonl" + PackedCopy.Suffix));
        Assert.True(WholeTranscript.Is(@"C:\data\archive\copies\dsh\s" + DshSession.CopySuffix + PackedCopy.Suffix));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
    }

    /// <summary>A stream read a few bytes at a time and never sought in; failing part way, when asked to.</summary>
    private sealed class Trickle(byte[] bytes, int failAfter = int.MaxValue) : Stream
    {
        private int _at;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_at >= failAfter) throw new IOException("读到一半出错");
            var n = Math.Min(Math.Min(count, 7), bytes.Length - _at);
            Array.Copy(bytes, _at, buffer, offset, n);
            _at += n;
            return n;
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
    }
}
