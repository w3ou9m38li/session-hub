using System.IO.Compression;
using ClaudeLauncher.Infrastructure.Backends;

namespace ClaudeLauncher.IntegrationTests;

/// <summary>
/// The export an import window offers from the Downloads folder: told apart by the names its zip
/// lists — ChatGPT's, Claude's, a Takeout with Gemini's activity — the newest first, only one
/// saved after the last import, and a half-downloaded or broken zip passed over.
/// </summary>
public sealed class ExportFinderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "launcher-finder", Guid.NewGuid().ToString("n"));

    public ExportFinderTests() => Directory.CreateDirectory(_dir);

    private string Zip(string name, DateTime saved, params string[] entries)
    {
        var path = Path.Combine(_dir, name);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            foreach (var entry in entries)
                using (var stream = zip.CreateEntry(entry).Open())
                    stream.WriteByte((byte)'{');
        File.SetLastWriteTime(path, saved);
        return path;
    }

    [Theory]
    [InlineData(true, false, false, "conversations.json", "chat.html", "user.json", "file-AbC-x.png")]
    [InlineData(false, true, false, "conversations.json", "users.json", "projects.json")]
    [InlineData(true, true, false, "conversations.json")]                           // could be either: offered to both
    [InlineData(true, false, false, "export/conversations-000.json", "export/conversations-001.json", "export/message_feedback.json")]
    [InlineData(false, false, true, "Takeout/My Activity/Gemini Apps/MyActivity.json")]
    [InlineData(false, false, true, "Takeout/我的活动/Gemini Apps/我的活动记录.html")]
    [InlineData(false, false, false, "photos/a.png", "notes.txt")]
    public void AnExportIsToldApartByTheNamesItsZipLists(bool chatGpt, bool claude, bool takeout, params string[] names)
    {
        Assert.Equal(chatGpt, ExportFinder.Is(ExportFinder.Kind.ChatGpt, names));
        Assert.Equal(claude, ExportFinder.Is(ExportFinder.Kind.Claude, names));
        Assert.Equal(takeout, ExportFinder.Is(ExportFinder.Kind.GeminiTakeout, names));
    }

    [Fact]
    public void TheNewestExportSavedAfterTheLastImportIsOfferedAndABrokenZipPassedOver()
    {
        var now = DateTime.Now;
        Zip("old-chatgpt.zip", now.AddDays(-9), "conversations.json", "chat.html");
        var newer = Zip("new-chatgpt.zip", now.AddDays(-2), "conversations.json", "chat.html");
        Zip("claude.zip", now.AddDays(-1), "conversations.json", "users.json");
        File.WriteAllBytes(Path.Combine(_dir, "half.zip"), [1, 2, 3, 4]);                // still downloading, or not a zip
        File.SetLastWriteTime(Path.Combine(_dir, "half.zip"), now);
        Directory.CreateDirectory(Path.Combine(_dir, "unpacked"));
        Zip(Path.Combine("unpacked", "deeper.zip"), now, "conversations.json", "chat.html");   // only the folder itself is looked in

        var found = ExportFinder.Newest(ExportFinder.Kind.ChatGpt, _dir)!;
        Assert.Equal(newer, found.Path);
        Assert.True(found.Bytes > 0);

        Assert.Equal(newer, ExportFinder.Newest(ExportFinder.Kind.ChatGpt, _dir, savedAfter: now.AddDays(-5))!.Path);
        Assert.Null(ExportFinder.Newest(ExportFinder.Kind.ChatGpt, _dir, savedAfter: now.AddDays(-1.5)));   // imported since
        Assert.EndsWith("claude.zip", ExportFinder.Newest(ExportFinder.Kind.Claude, _dir)!.Path);
        Assert.Null(ExportFinder.Newest(ExportFinder.Kind.GeminiTakeout, _dir));
        Assert.Null(ExportFinder.Newest(ExportFinder.Kind.ChatGpt, Path.Combine(_dir, "nowhere")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
    }
}
