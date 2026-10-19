using System.IO.Compression;
using System.Text;
using ClaudeLauncher.Infrastructure.Backends;

namespace ClaudeLauncher.IntegrationTests;

/// <summary>
/// Whose export a zip is, told from its list of files read as it streams past: the same names a
/// ZipArchive lists, ZIP64's large lists included, a damaged list refused as a ZipArchive refuses
/// it — and a fraction of what a ZipArchive allocates for a Takeout of many files.
/// </summary>
public sealed class ZipNamesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "launcher-zipnames", Guid.NewGuid().ToString("n"));

    public ZipNamesTests() => Directory.CreateDirectory(_dir);

    private static List<string> ArchiveNames(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        return [.. zip.Entries.Where(e => e.Name.Length > 0).Select(e => e.FullName.Replace('\\', '/'))];
    }

    private static List<string> StreamedNames(string path)
    {
        using var stream = File.OpenRead(path);
        return [.. ZipNames.Of(stream)];
    }

    [Fact]
    public void TheNamesAreTheOnesAZipArchiveLists()
    {
        var path = Path.Combine(_dir, "takeout.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            zip.Comment = "Google Takeout";
            zip.CreateEntry("Takeout/");                                        // a folder: not a file
            zip.CreateEntry("Takeout/My Activity/");
            foreach (var (name, level) in new[]
                     {
                         ("Takeout/我的活动/Gemini Apps/我的活动记录.html", CompressionLevel.Optimal),
                         ("Takeout/My Activity/Gemini Apps/MyActivity.json", CompressionLevel.NoCompression),
                         (@"export\conversations.json", CompressionLevel.Fastest),
                         ("表情 😀.png", CompressionLevel.Optimal),
                     })
            {
                var entry = zip.CreateEntry(name, level);
                entry.Comment = "说明 " + name;                                    // passed over with the extra fields
                using var stream = entry.Open();
                stream.Write(Encoding.UTF8.GetBytes(new string('x', 3000)));
            }
        }

        Assert.Equal(ArchiveNames(path), StreamedNames(path));
        Assert.Equal(["Takeout/我的活动/Gemini Apps/我的活动记录.html", "Takeout/My Activity/Gemini Apps/MyActivity.json",
            "export/conversations.json", "表情 😀.png"], StreamedNames(path));
    }

    [Fact]
    public void ATakeoutOfManyFilesIsReadThroughItsZip64RecordForAFractionOfAZipArchive()
    {
        // More files than the plain end record can count: the count is in the ZIP64 record.
        var path = Path.Combine(_dir, "takeout-20261015T010203Z-001.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            for (var i = 0; i < 70_000; i++) zip.CreateEntry($"Takeout/Google Photos/Photos from 2025/IMG_{i:00000}.jpg", CompressionLevel.NoCompression);
            using var activity = zip.CreateEntry("Takeout/My Activity/Gemini Apps/MyActivity.json").Open();
            activity.Write("[]"u8);
        }
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(0x07064B50u, BitConverter.ToUInt32(bytes, bytes.Length - 22 - 20));     // it is ZIP64

        var names = StreamedNames(path);
        Assert.Equal(70_001, names.Count);
        Assert.Equal(ArchiveNames(path), names);

        long Allocated(Action work)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            work();
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
        var kinds = ExportKinds.None;
        var streamed = Allocated(() =>
        {
            using var stream = File.OpenRead(path);
            kinds = ExportFinder.Classify(stream);
        });
        var archive = Allocated(() =>
        {
            using var zip = ZipFile.OpenRead(path);
            _ = zip.Entries.Count;
        });
        Assert.Equal(ExportKinds.GeminiTakeout, kinds);
        Assert.True(streamed * 4 < archive, $"read as it streams: {streamed:N0} bytes; as a ZipArchive: {archive:N0} bytes");
    }

    [Fact]
    public void WhatIsNotAZipOrADamagedOneIsRefusedAsAZipArchiveRefusesIt()
    {
        var empty = Path.Combine(_dir, "empty.zip");
        File.WriteAllBytes(empty, []);
        var text = Path.Combine(_dir, "text.zip");
        File.WriteAllText(text, "not a zip at all");
        Assert.Throws<InvalidDataException>(() => StreamedNames(empty));
        Assert.Throws<InvalidDataException>(() => StreamedNames(text));

        // A list that says it holds one file more than it does.
        var path = Path.Combine(_dir, "damaged.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            for (var i = 0; i < 3; i++) zip.CreateEntry($"conversations-{i}.json");
        var bytes = File.ReadAllBytes(path);
        bytes[^(22 - 10)]++;                                              // the count of the whole list
        bytes[^(22 - 8)]++;                                               // and of this disk's part of it
        File.WriteAllBytes(path, bytes);
        Assert.Throws<InvalidDataException>(() => StreamedNames(path));

        // Where the list says an entry starts, something else.
        bytes[^(22 - 10)]--;
        bytes[^(22 - 8)]--;
        bytes[BitConverter.ToInt32(bytes, bytes.Length - 22 + 16)] ^= 0xFF;
        File.WriteAllBytes(path, bytes);
        Assert.Throws<InvalidDataException>(() => ArchiveNames(path));
        Assert.Throws<InvalidDataException>(() => StreamedNames(path));

        // Cut short inside its list: the end record survives, the list it points into does not.
        var cut = Path.Combine(_dir, "cut.zip");
        using (var zip = ZipFile.Open(cut, ZipArchiveMode.Create))
            for (var i = 0; i < 3; i++) zip.CreateEntry($"conversations-{i}.json");
        var whole = File.ReadAllBytes(cut);
        File.WriteAllBytes(cut, [.. whole.AsSpan(0, whole.Length - 22 - 30), .. whole.AsSpan(whole.Length - 22)]);
        Assert.Throws<InvalidDataException>(() => StreamedNames(cut));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
    }
}
