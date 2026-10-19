using System.IO.Compression;
using System.Text;
using ClaudeLauncher.Infrastructure.Backends;
using ClaudeLauncher.Infrastructure.Settings;

namespace ClaudeLauncher.IntegrationTests;

/// <summary>
/// The export an import window offers from the Downloads folder: told apart by the names its zip
/// lists — ChatGPT's, Claude's, a Takeout with Gemini's activity — the newest first, only one
/// saved after the last import and not imported already, and a half-downloaded or broken zip
/// passed over. A file dropped on the window: told apart by its name and its first record.
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
        File.SetCreationTime(path, saved);
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
    [InlineData(false, false, true, "takeout-20261001T010203Z-001/Takeout/My Activity/Gemini Apps/MyActivity.json")]   // unpacked and zipped again
    // A project named for Gemini is not a Takeout: Gemini CLI's sources, a folder that merely holds "bard".
    [InlineData(false, false, false, "gemini-cli-main/package.json", "gemini-cli-main/docs/index.html", "gemini-cli-main/tsconfig.json")]
    [InlineData(false, false, false, "bombardier/config.json")]
    [InlineData(false, false, false, "Gemini Apps/Takeout/MyActivity.json")]          // the folders the wrong way round
    [InlineData(false, false, false, "photos/a.png", "notes.txt")]
    public void AnExportIsToldApartByTheNamesItsZipLists(bool chatGpt, bool claude, bool takeout, params string[] names)
    {
        var kinds = ExportFinder.Classify(names);
        Assert.Equal(chatGpt, kinds.HasFlag(ExportKinds.ChatGpt));
        Assert.Equal(claude, kinds.HasFlag(ExportKinds.Claude));
        Assert.Equal(takeout, kinds.HasFlag(ExportKinds.GeminiTakeout));
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

        var found = ExportFinder.Newest(ExportKinds.ChatGpt, _dir)!;
        Assert.Equal(newer, found.Path);
        Assert.True(found.Bytes > 0);

        Assert.Equal(newer, ExportFinder.Newest(ExportKinds.ChatGpt, _dir, savedAfter: now.AddDays(-5))!.Path);
        Assert.Null(ExportFinder.Newest(ExportKinds.ChatGpt, _dir, savedAfter: now.AddDays(-1.5)));   // imported since
        Assert.EndsWith("claude.zip", ExportFinder.Newest(ExportKinds.Claude, _dir)!.Path);
        Assert.Null(ExportFinder.Newest(ExportKinds.GeminiTakeout, _dir));
        Assert.Null(ExportFinder.Newest(ExportKinds.ChatGpt, Path.Combine(_dir, "nowhere")));
    }

    [Fact]
    public void TheImportWindowPassesOverAnExportFoundEmptyButSuggestsNoneOlderThanOneImported()
    {
        var now = DateTime.Now;
        var older = Zip("older-chatgpt.zip", now.AddDays(-3), "conversations.json", "chat.html");
        var newer = Zip("newer-chatgpt.zip", now.AddDays(-1), "conversations.json", "chat.html");
        var stored = now.AddDays(-5);       // the last import, before both arrived (one that wrote nothing new leaves it there)
        List<HandledExport> Handled(string outcome, string kind = "ChatGpt") =>
            [new HandledExport(newer, new FileInfo(newer).Length, File.GetLastWriteTimeUtc(newer), kind, outcome)];

        Assert.Equal(newer, HandledExport.Suggest([], ExportKinds.ChatGpt, _dir, stored)!.Path);
        // Found to hold nothing, or the other app's: the next older one is the one.
        Assert.Equal(older, HandledExport.Suggest(Handled(HandledExport.NotImportable), ExportKinds.ChatGpt, _dir, stored)!.Path);
        Assert.Equal(File.GetLastWriteTimeUtc(older),
            HandledExport.Suggest(Handled(HandledExport.NotImportable), ExportKinds.ChatGpt, _dir, stored)!.SavedUtc);
        Assert.Equal(older, HandledExport.Suggest(Handled(HandledExport.Imported, "Claude"), ExportKinds.ChatGpt, _dir, stored)!.Path);
        // Set aside in the status bar: the window was asked for.
        Assert.Equal(newer, HandledExport.Suggest(Handled(HandledExport.Dismissed), ExportKinds.ChatGpt, _dir, stored)!.Path);
        // Imported: not an older one in its place, as the status bar does.
        Assert.Null(HandledExport.Suggest(Handled(HandledExport.Imported), ExportKinds.ChatGpt, _dir, stored));
        // The import removed since: the one imported before is the one to import again.
        Assert.Equal(newer, HandledExport.Suggest(Handled(HandledExport.Imported), ExportKinds.ChatGpt, _dir, lastImport: null)!.Path);

        Assert.Equal(older, ExportFinder.Newest(ExportKinds.ChatGpt, _dir, passOver: f => f.Path == newer)!.Path);
        Assert.Null(ExportFinder.Newest(ExportKinds.ChatGpt, _dir, importedAlready: f => f.Path == newer));
    }

    [Fact]
    public void AZipOfAProjectNamedForGeminiIsNotTakenForATakeout()
    {
        var now = DateTime.Now;
        var repo = Zip("gemini-cli-main.zip", now.AddHours(-1), "gemini-cli-main/package.json", "gemini-cli-main/docs/index.html");
        var takeout = Zip("takeout-20261001T010203Z-001.zip", now.AddDays(-1), "Takeout/My Activity/Gemini Apps/MyActivity.json");

        Assert.Equal(ExportKinds.None, ExportFinder.KindOf(repo));
        Assert.Equal(ExportKinds.GeminiTakeout, ExportFinder.KindOf(takeout));
        Assert.Equal(takeout, ExportFinder.Newest(ExportKinds.GeminiTakeout, _dir)!.Path);
    }

    [Fact]
    public void AnExportCopiedInIsAsNewAsTheMomentItArrived()
    {
        var now = DateTime.Now;
        Zip("chatgpt.zip", now.AddDays(-2), "conversations.json", "chat.html");
        // Copied in an hour ago from another computer: written a month ago, the time a copy keeps.
        var copied = Zip("copied-chatgpt.zip", now.AddHours(-1), "conversations.json", "chat.html");
        File.SetLastWriteTime(copied, now.AddDays(-30));

        Assert.Equal(copied, ExportFinder.Newest(ExportKinds.ChatGpt, _dir)!.Path);
        Assert.Equal(copied, ExportFinder.Newest(ExportKinds.ChatGpt, _dir, savedAfter: now.AddDays(-1))!.Path);    // imported since the other
        Assert.Null(ExportFinder.Newest(ExportKinds.ChatGpt, _dir, savedAfter: now.AddMinutes(-5)));
    }

    private string Write(string relative, string text, bool bom = false)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(bom));
        return path;
    }

    [Fact]
    public void AFileDroppedOnTheWindowIsToldApartByItsNameAndItsFirstRecord()
    {
        const string chatGpt = """[{"title":"引物","create_time":1760000000,"update_time":1760000600,"mapping":{"r":{"id":"r"}}}]""";
        const string claude = """[{"uuid":"c1","name":"论文","created_at":"2026-09-01T08:00:00Z","chat_messages":[{"text":"hi"}]}]""";
        const string activity = """[{"header":"Gemini Apps","title":"Prompted hi","time":"2026-10-01T08:00:00.000Z","products":["Gemini Apps"]}]""";
        const string search = """[{"header":"Search","title":"Searched for x","time":"2026-10-01T08:00:00.000Z","products":["Search"]}]""";
        const string page = """<html><body><div class="outer-cell mdl-cell"><div class="header-cell"><p class="mdl-typography--title">Gemini Apps<br></p></div></div></body></html>""";

        Assert.Equal(ExportKinds.ChatGpt, ExportFinder.KindOf(Write("a/conversations.json", chatGpt, bom: true)));
        Assert.Equal(ExportKinds.Claude, ExportFinder.KindOf(Write("b/conversations.json", claude)));
        // A list held in an object, and one conversation on its own.
        Assert.Equal(ExportKinds.Claude, ExportFinder.KindOf(Write("c/conversations-000.json", "{\"conversations\":" + claude + "}")));
        Assert.Equal(ExportKinds.ChatGpt, ExportFinder.KindOf(Write("d/conversations.json", chatGpt[1..^1])));
        Assert.Equal(ExportKinds.ChatGpt | ExportKinds.Claude, ExportFinder.KindOf(Write("e/conversations.json", "[]")));

        // Takeout's activity record, under whatever name: Gemini's by its folder, or by what it says.
        Assert.Equal(ExportKinds.GeminiTakeout, ExportFinder.KindOf(Write("Takeout/My Activity/Gemini Apps/我的活动记录.json", activity)));
        Assert.Equal(ExportKinds.GeminiTakeout, ExportFinder.KindOf(Write("MyActivity.json", activity)));
        Assert.Equal(ExportKinds.None, ExportFinder.KindOf(Write("f/MyActivity.json", search)));
        // A record that does not name Gemini: Gemini's in Gemini's folder only.
        const string unnamed = """[{"header":"Apps","title":"Prompted hi","time":"2026-10-01T08:00:00.000Z"}]""";
        Assert.Equal(ExportKinds.GeminiTakeout, ExportFinder.KindOf(Write("Takeout/My Activity/Gemini Apps/MyActivity.json", unnamed)));
        Assert.Equal(ExportKinds.None, ExportFinder.KindOf(Write("i/MyActivity.json", unnamed)));
        Assert.Equal(ExportKinds.GeminiTakeout, ExportFinder.KindOf(Write("g/MyActivity.html", page)));
        Assert.Equal(ExportKinds.None, ExportFinder.KindOf(Write("h/chat.html", "<html><body>chat</body></html>")));

        // The folder an export was unpacked into: its own markers, or its first conversation.
        Write("unpacked-chatgpt/conversations.json", "[]");
        Write("unpacked-chatgpt/chat.html", "<html/>");
        Assert.Equal(ExportKinds.ChatGpt, ExportFinder.KindOf(Path.Combine(_dir, "unpacked-chatgpt")));
        Write("unpacked-claude/conversations.json", claude);
        Assert.Equal(ExportKinds.Claude, ExportFinder.KindOf(Path.Combine(_dir, "unpacked-claude")));
        Assert.Equal(ExportKinds.None, ExportFinder.KindOf(Path.Combine(_dir, "Takeout")));        // a Takeout comes as its zip or its file

        Assert.Equal(ExportKinds.Claude, ExportFinder.KindOf(Zip("claude.zip", DateTime.Now, "conversations.json", "users.json")));
        Assert.Equal(ExportKinds.None, ExportFinder.KindOf(Write("notes.json", "{\"a\":1}")));
        Assert.Equal(ExportKinds.None, ExportFinder.KindOf(Write("photo.png", "png")));
        Assert.Equal(ExportKinds.None, ExportFinder.KindOf(Write("broken.zip", "not a zip")));
        Assert.Equal(ExportKinds.None, ExportFinder.KindOf(Write("broken/conversations.json", "[{\"title\":")));
        Assert.Equal(ExportKinds.None, ExportFinder.KindOf(Path.Combine(_dir, "gone.zip")));
    }

    [Fact]
    public void ARecordWithBytesThatAreNotUtf8IsStillToldApart()
    {
        // A damaged byte in what the record says: read around it, not thrown at the window that was dropped on.
        static byte[] Record(string before, string after) =>
            [.. Encoding.UTF8.GetBytes("[{\"header\":\"" + before), 0xFF, 0xFE, .. Encoding.UTF8.GetBytes(after + "\",\"title\":\"Prompted hi\",\"time\":\"2026-10-01T08:00:00.000Z\"}]")];
        var gemini = Path.Combine(_dir, "MyActivity.json");
        File.WriteAllBytes(gemini, Record("Gemini Apps ", ""));
        var other = Path.Combine(_dir, "other", "MyActivity.json");
        Directory.CreateDirectory(Path.GetDirectoryName(other)!);
        File.WriteAllBytes(other, Record("Sea", "rch"));

        Assert.Equal(ExportKinds.GeminiTakeout, ExportFinder.KindOf(gemini));
        Assert.Equal(ExportKinds.None, ExportFinder.KindOf(other));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
    }
}
