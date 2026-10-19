using System.IO.Compression;
using System.Text;
using ClaudeLauncher.Infrastructure.Backends;
using ClaudeLauncher.Infrastructure.Settings;

namespace ClaudeLauncher.IntegrationTests;

/// <summary>
/// The look in Downloads after a sync: nothing listed or opened while the folder's time says
/// nothing came; each new zip opened once and every kind read from that one look; one that could
/// not be opened asked again; nothing offered twice, nothing older in place of one set aside,
/// nothing that arrived before the last import or long ago, nothing kept only online.
/// </summary>
public sealed class DownloadsScanTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "launcher-downloads", Guid.NewGuid().ToString("n"));
    private readonly List<string> _opens = [];
    private readonly HashSet<string> _locked = new(StringComparer.OrdinalIgnoreCase);
    private readonly DateTime _now = DateTime.UtcNow;
    private DateTime _folderTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public DownloadsScanTests() => Directory.CreateDirectory(_dir);

    private const string ChatGptList = """[{"title":"引物设计","create_time":1760000000,"mapping":{"r":{"id":"r"}}}]""";
    private const string ClaudeList = """[{"uuid":"c1","name":"论文","chat_messages":[]}]""";
    private const string Activity = """[{"header":"Gemini Apps","title":"Prompted hi","time":"2026-10-01T08:00:00.000Z","products":["Gemini Apps"]}]""";

    private static readonly Dictionary<string, string> ChatGpt = new() { ["conversations.json"] = ChatGptList, ["chat.html"] = "<html/>" };
    private static readonly Dictionary<string, string> Claude = new() { ["conversations.json"] = ClaudeList, ["users.json"] = "[]" };
    private static readonly Dictionary<string, string> Takeout = new() { ["Takeout/My Activity/Gemini Apps/MyActivity.json"] = Activity };

    /// <summary>A zip that came into the folder <paramref name="daysAgo"/> days ago.</summary>
    private string Zip(string name, Dictionary<string, string> files, double daysAgo = 0.1)
    {
        var path = Path.Combine(_dir, name);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            foreach (var (entry, text) in files)
                using (var stream = zip.CreateEntry(entry).Open())
                    stream.Write(Encoding.UTF8.GetBytes(text));
        var at = _now.AddDays(-daysAgo);
        File.SetCreationTimeUtc(path, at);
        File.SetLastWriteTimeUtc(path, at);
        return path;
    }

    /// <summary>The folder's time moved on, as a download finishing moves it.</summary>
    private void Touch()
    {
        _folderTime = _folderTime.AddMinutes(1);
        Directory.SetLastWriteTimeUtc(_dir, _folderTime);
    }

    /// <summary>The folder's time left as it was, whatever was written in it.</summary>
    private void Hold() => Directory.SetLastWriteTimeUtc(_dir, _folderTime);

    private Stream Open(string path)
    {
        _opens.Add(Path.GetFileName(path));
        if (_locked.Remove(Path.GetFileName(path))) throw new IOException("还在下载");
        return File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    }

    private DownloadsScan Scan(TimeSpan? relistAfter = null) =>
        new(_dir, Open) { RelistAfter = relistAfter ?? TimeSpan.FromDays(1) };

    private IReadOnlyList<DownloadsOffer> Check(DownloadsScan scan, Func<FoundExport, bool>? handled = null,
        Func<ExportKinds, DateTime>? notBefore = null) =>
        scan.Check(notBefore ?? (_ => DownloadsScan.NotBefore(null, _now)), handled ?? (_ => false));

    private static string[] Names(IReadOnlyList<DownloadsOffer> offers) =>
        [.. offers.Select(o => $"{Path.GetFileName(o.Export.Path)}:{o.Kind}")];

    [Fact]
    public void AFolderWhoseTimeIsUnchangedIsNeitherListedNorOpened()
    {
        Zip("chatgpt.zip", ChatGpt, daysAgo: 0.2);
        Touch();
        var scan = Scan();

        Assert.Equal(["chatgpt.zip:ChatGpt"], Names(Check(scan)));
        Assert.Equal(["chatgpt.zip"], _opens);

        // A zip written while the folder's time stays put is not seen: the time is the whole look.
        Zip("claude.zip", Claude);
        Hold();
        Assert.Equal(["chatgpt.zip:ChatGpt"], Names(Check(scan)));
        Assert.Equal(["chatgpt.zip"], _opens);

        // The time moved: listed again, and only the new zip opened.
        Touch();
        Assert.Equal(["claude.zip:Claude", "chatgpt.zip:ChatGpt"], Names(Check(scan)));
        Assert.Equal(["chatgpt.zip", "claude.zip"], _opens);
        Assert.Equal(2, Check(scan).Count);
        Assert.Equal(2, _opens.Count);
    }

    [Fact]
    public void AFolderIsListedAgainAfterAWhileWhateverItsTimeSays()
    {
        Zip("chatgpt.zip", ChatGpt, daysAgo: 0.2);
        Touch();
        var scan = Scan(relistAfter: TimeSpan.Zero);
        Check(scan);

        // A drive that keeps no folder times (FAT, a network share): seen all the same, on the next look.
        Zip("claude.zip", Claude);
        Hold();
        Assert.Equal(["claude.zip:Claude", "chatgpt.zip:ChatGpt"], Names(Check(scan)));
        Assert.Equal(["chatgpt.zip", "claude.zip"], _opens);        // what was known is not opened again
    }

    [Fact]
    public void AZipThatCannotBeOpenedYetIsAskedAgainNotKeptAsNotAnExport()
    {
        Zip("takeout-20261015.zip", Takeout);
        Touch();
        _locked.Add("takeout-20261015.zip");            // the browser still writing it
        var scan = Scan();

        Assert.Empty(Check(scan));
        // Asked again on the next look, though the folder's time did not move.
        Assert.Equal(["takeout-20261015.zip:GeminiTakeout"], Names(Check(scan)));
        Assert.Equal(2, _opens.Count);
        Check(scan);
        Assert.Equal(2, _opens.Count);
    }

    [Fact]
    public void AHalfWrittenZipIsLookedAtAgainOnceItIsWhole()
    {
        var path = Path.Combine(_dir, "chatgpt.zip");
        File.WriteAllBytes(path, [0x50, 0x4B, 3, 4, 1, 2, 3]);         // the start of a zip, its list of files not yet written
        File.SetLastWriteTimeUtc(path, _now.AddMinutes(-2));
        Touch();
        var scan = Scan();
        Assert.Empty(Check(scan));

        File.Delete(path);
        Zip("chatgpt.zip", ChatGpt);
        Touch();
        Assert.Equal(["chatgpt.zip:ChatGpt"], Names(Check(scan)));
        Assert.Equal(2, _opens.Count);
    }

    [Fact]
    public void AnExportHandledIsNotOfferedAgainNorAnOlderOneInItsPlace()
    {
        Zip("chatgpt-old.zip", ChatGpt, daysAgo: 3);
        var newer = Zip("chatgpt-new.zip", ChatGpt, daysAgo: 1);
        Touch();
        var scan = Scan();
        Assert.Equal(["chatgpt-new.zip:ChatGpt"], Names(Check(scan)));      // of each app, the one that came last

        var file = new FileInfo(newer);
        var set = HandledExport.Add([], new HandledExport(newer, file.Length, file.LastWriteTimeUtc, "ChatGpt", HandledExport.Dismissed));
        Assert.Empty(Check(scan, export => HandledExport.Contains(set, export)));

        // The same name downloaded anew is a new export.
        var again = HandledExport.Add([], new HandledExport(newer, file.Length, file.LastWriteTimeUtc.AddHours(-1), "ChatGpt",
            HandledExport.Imported));
        Assert.Equal(["chatgpt-new.zip:ChatGpt"], Names(Check(scan, export => HandledExport.Contains(again, export))));
    }

    [Fact]
    public void OnlyAnExportThatArrivedSinceItsAppsLastImportAndWithinTwoWeeksIsOffered()
    {
        Zip("chatgpt-long-ago.zip", ChatGpt, daysAgo: 20);
        Zip("claude.zip", Claude, daysAgo: 2);
        // Copied in today from somewhere else: written long ago, but it arrived now.
        var copied = Zip("chatgpt-copied.zip", ChatGpt, daysAgo: 0.01);
        File.SetLastWriteTimeUtc(copied, _now.AddDays(-30));
        Touch();
        var scan = Scan();

        Assert.Equal(["chatgpt-copied.zip:ChatGpt", "claude.zip:Claude"], Names(Check(scan)));
        Assert.DoesNotContain("chatgpt-long-ago.zip", _opens);               // not even opened

        // ChatGPT imported a few minutes ago: its export is not offered; Claude's still is.
        var imported = _now.AddMinutes(-5);
        Assert.Equal(["claude.zip:Claude"], Names(Check(scan, notBefore: kind =>
            DownloadsScan.NotBefore(kind == ExportKinds.ChatGpt ? imported : null, _now))));

        Assert.Equal(imported, DownloadsScan.NotBefore(imported, _now));
        Assert.Equal(_now - DownloadsScan.Window, DownloadsScan.NotBefore(_now.AddDays(-30), _now));
        Assert.Equal(_now - DownloadsScan.Window, DownloadsScan.NotBefore(null, _now));
    }

    [Fact]
    public void AZipKeptOnlyOnlineIsNotOpened()
    {
        var online = Zip("chatgpt.zip", ChatGpt);
        File.SetAttributes(online, File.GetAttributes(online) | FileAttributes.Offline);
        Touch();

        Assert.Empty(Check(Scan()));
        Assert.Empty(_opens);                     // opening it would download it
    }

    [Theory]
    [InlineData(FileAttributes.Offline, true)]
    [InlineData((FileAttributes)0x00040000, true)]         // recalled when opened
    [InlineData((FileAttributes)0x00400000, true)]         // recalled when read
    [InlineData(FileAttributes.Archive | FileAttributes.ReadOnly, false)]
    [InlineData(FileAttributes.Normal, false)]
    public void OneDrivesFilesOnDemandAreToldByTheirAttributes(FileAttributes attributes, bool online) =>
        Assert.Equal(online, ExportFinder.OnlineOnly(attributes));

    [Fact]
    public void EveryKindIsReadFromOneLookAndAZipIsOfferedOnce()
    {
        var both = new Dictionary<string, string>(Takeout) { ["conversations.json"] = ChatGptList, ["chat.html"] = "<html/>" };
        Zip("everything.zip", both);
        Touch();

        Assert.Equal(["everything.zip:ChatGpt"], Names(Check(Scan())));
        Assert.Equal(["everything.zip"], _opens);
        Assert.Equal(ExportKinds.ChatGpt | ExportKinds.GeminiTakeout, ExportFinder.KindOf(Path.Combine(_dir, "everything.zip")));
    }

    [Fact]
    public void AListOfConversationsAloneIsToldByItsFirstConversation()
    {
        Zip("a.zip", new() { ["conversations.json"] = ChatGptList }, daysAgo: 3);
        Zip("b.zip", new() { ["conversations.json"] = ClaudeList }, daysAgo: 2);
        Zip("c.zip", new() { ["conversations.json"] = "[]" }, daysAgo: 1);     // tells nothing: offered as neither
        Touch();

        Assert.Equal(["b.zip:Claude", "a.zip:ChatGpt"], Names(Check(Scan())));
        Assert.Equal(ExportKinds.ChatGpt, ExportFinder.KindOf(Path.Combine(_dir, "a.zip")));
        Assert.Equal(ExportKinds.Claude, ExportFinder.KindOf(Path.Combine(_dir, "b.zip")));
        Assert.Equal(ExportKinds.ChatGpt | ExportKinds.Claude, ExportFinder.KindOf(Path.Combine(_dir, "c.zip")));
    }

    [Fact]
    public void AProjectNamedForGeminiIsNotOfferedAsATakeout()
    {
        Zip("takeout-20261001T010203Z-001.zip", Takeout, daysAgo: 2);
        // Gemini CLI's sources from GitHub: JSON and HTML in a folder named for Gemini, but no Takeout around it.
        Zip("gemini-cli-main.zip", new() { ["gemini-cli-main/package.json"] = "{}", ["gemini-cli-main/docs/index.html"] = "<html/>" });
        Touch();

        Assert.Equal(["takeout-20261001T010203Z-001.zip:GeminiTakeout"], Names(Check(Scan())));
    }

    [Fact]
    public void AFolderThatIsNotThereOffersNothing()
    {
        var scan = new DownloadsScan(Path.Combine(_dir, "nowhere"), Open);
        Assert.Empty(Check(scan));
        Assert.Empty(_opens);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
    }
}
