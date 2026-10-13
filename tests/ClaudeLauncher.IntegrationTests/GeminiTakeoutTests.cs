using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ClaudeLauncher.Core.Catalog;
using ClaudeLauncher.Core.Contracts;
using ClaudeLauncher.Infrastructure.Backends;
using ClaudeLauncher.Infrastructure.Catalog;
using ClaudeLauncher.Infrastructure.Discovery;
using ClaudeLauncher.Infrastructure.Usage;

namespace ClaudeLauncher.IntegrationTests;

/// <summary>
/// Gemini's web history from Google Takeout: what is read out of an export (and what is not),
/// how records make conversations, that the export is only read, that a later import adds to
/// the store rather than replacing it, and that the list, the reader and the sync take it in.
/// </summary>
public sealed class GeminiTakeoutTests : IDisposable
{
    private const string ConvA = "611f669baabc4573";
    private const string ConvB = "4d64a966ff3c9d67";

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 7, 7, 7];

    private readonly string _dir;
    private readonly GeminiTakeoutStore _store;

    public GeminiTakeoutTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "launcher-takeout", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
        _store = new GeminiTakeoutStore(Path.Combine(_dir, "data", "gemini-web"));
    }

    private static object Prompt(string time, string prompt, string? conversation, string? html = "<p>好的</p>",
        object[]? subtitles = null, string[]? attached = null, string? imageFile = null) => new
    {
        header = "Gemini Apps",
        title = "Prompted " + prompt,
        time,
        products = new[] { "Gemini Apps" },
        details = conversation is null ? null : new[] { new { name = "https://gemini.google.com/app/" + conversation, url = "https://gemini.google.com/app/" + conversation } },
        activityControls = new[] { "Gemini Apps Activity" },
        safeHtmlItem = html is null ? null : new[] { new { html } },
        subtitles,
        attachedFiles = attached,
        imageFile,
    };

    private static object Cleared(string time) => new
    {
        header = "Gemini Apps", title = "Cleared Gemini Apps", time, products = new[] { "Gemini Apps" },
        activityControls = new[] { "Gemini Apps Activity" },
    };

    private static readonly JsonSerializerOptions Options = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    /// <summary>An export as Takeout makes it: the activity of each product in its own folder, files beside it.</summary>
    private string Export(string name, object[] records, Dictionary<string, byte[]>? files = null, string? html = null)
    {
        var path = Path.Combine(_dir, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Add(string entry, byte[] bytes)
        {
            using var stream = zip.CreateEntry(entry).Open();
            stream.Write(bytes);
        }
        var folder = "Takeout/My Activity/Gemini Apps/";
        if (html is not null) Add(folder + "MyActivity.html", Encoding.UTF8.GetBytes(html));
        else Add(folder + "MyActivity.json", JsonSerializer.SerializeToUtf8Bytes(records, Options));
        foreach (var (file, bytes) in files ?? []) Add(folder + file, bytes);
        // Neither of these is Gemini's conversation history.
        Add("Takeout/My Activity/Search/MyActivity.json", Encoding.UTF8.GetBytes("[{\"header\":\"Search\",\"title\":\"Searched for x\",\"time\":\"2026-10-01T08:00:00.000Z\"}]"));
        Add("Takeout/Gemini/gem-1.json", Encoding.UTF8.GetBytes("{\"name\":\"A gem\"}"));
        return path;
    }

    private static readonly object[] Standard =
    [
        Prompt("2026-10-02T09:00:00.000Z", "第二段对话的问题", ConvB, "<h3>标题</h3><p>回答 <strong>重点</strong></p>"),
        Cleared("2026-10-01T12:00:00.000Z"),
        Prompt("2026-10-01T08:05:00.000Z", "看看这张图", ConvA, "<p>图里是一张表</p>",
            subtitles:
            [
                new { name = "Attached 1 file." },
                new { name = "-  chart.png" },
                new { name = "我的网页", url = "https://example.com/page" },
                new { name = "1 generated image." },
                new { name = "https://lh3.googleusercontent.com/rd-gg/abc", url = "https://lh3.googleusercontent.com/rd-gg/abc" },
                new { name = "Used skill: 画图" },
            ],
            attached: ["chart.png", "photo.jpg", "notes.pdf"], imageFile: "chart.png"),
        Prompt("2026-10-01T08:00:00.000Z", "第一段对话的第一个问题", ConvA),
    ];

    [Fact]
    public void AnExportBecomesConversationsByTheirLinks()
    {
        var export = Export("takeout-1.zip", Standard, new() { ["chart.png"] = Png, ["photo.png"] = Png });
        var before = File.ReadAllBytes(export);

        var result = _store.Import([export]);

        Assert.Equal(3, result.Found);
        Assert.Equal(3, result.Added);
        Assert.Equal(2, result.Conversations);
        Assert.Equal(1, result.Skipped);                    // the cleared history
        Assert.Equal(before, File.ReadAllBytes(export));   // the export is only read

        var rows = GeminiMetadataReader.ForTakeout(_store).Read().Rows.ToDictionary(r => r.Key.NativeSessionId);
        Assert.Equal([ConvB, ConvA], rows.Keys.Order(StringComparer.Ordinal).ToArray());
        var a = rows[ConvA];
        Assert.Equal(ConversationKey.GeminiWeb, a.Key.Backend);
        Assert.Equal("第一段对话的第一个问题", a.Title);           // the oldest prompt names it
        Assert.Null(a.CliSessionId);
        Assert.Null(a.ProjectRef);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 8, 5, 0, TimeSpan.Zero), a.LastActivityUtc);

        var entries = GeminiSession.ReadEntries(a.NativeOpenRef!, keepImages: true);
        Assert.Equal(
        [
            MessageRole.User, MessageRole.Assistant,
            MessageRole.Attachment, MessageRole.Attachment, MessageRole.Attachment, MessageRole.Attachment, MessageRole.User,
            MessageRole.Note, MessageRole.Assistant,
        ], entries.Select(e => e.Role).ToArray());
        Assert.Equal(Png, entries[2].Image);                           // kept from the export, shown in the reader
        Assert.StartsWith("（图片 · chart.png", entries[2].Text);
        Assert.Equal(Png, entries[3].Image);                           // a JPEG Takeout kept as a PNG of its name
        Assert.StartsWith("（图片 · photo.jpg", entries[3].Text);
        Assert.Equal("（附件 · 不在导出里 · notes.pdf）", entries[4].Text);
        Assert.Equal("（附件 · 网页 · 我的网页  https://example.com/page）", entries[5].Text);
        Assert.Equal("提示 · 用了技能：画图", entries[7].Text);
        Assert.Equal("图里是一张表\n\n[生成的图片 1（在网页上看）](https://lh3.googleusercontent.com/rd-gg/abc)", entries[8].Text);
        Assert.Null(GeminiSession.ReadEntries(a.NativeOpenRef!)[2].Image);    // pictures only for the reader

        var b = GeminiSession.ReadDialogue(rows[ConvB].NativeOpenRef!);
        Assert.Equal("### 标题\n\n回答 **重点**", b[1].Text);
    }

    [Fact]
    public void ALaterImportAddsToWhatIsKept()
    {
        _store.Import([Export("takeout-1.zip", Standard, new() { ["chart.png"] = Png })]);
        var file = Path.Combine(_store.ConversationsFolder, $"session-{ConvB}.json");
        var written = File.GetLastWriteTimeUtc(file);

        var same = _store.Import([Export("takeout-again.zip", Standard, new() { ["chart.png"] = Png })]);
        Assert.Equal(0, same.Added);
        Assert.Equal(written, File.GetLastWriteTimeUtc(file));        // nothing changed: not written again

        // Google has since let the oldest prompt go, and a new one was asked.
        var later = _store.Import([Export("takeout-2.zip",
        [
            Prompt("2026-10-03T10:00:00.000Z", "后来又问", ConvA),
            Standard[0], Standard[2],
        ])]);
        Assert.Equal(3, later.Found);
        Assert.Equal(1, later.Added);
        Assert.Equal(4, later.Total);
        var a = GeminiSession.ReadDialogue(Path.Combine(_store.ConversationsFolder, $"session-{ConvA}.json"));
        Assert.Equal(["第一段对话的第一个问题", "好的", "看看这张图", "图里是一张表", "后来又问", "好的"],
            a.Select(e => e.Text.Split('\n')[0]).ToArray());
    }

    [Fact]
    public void RecordsWithoutALinkArePutTogetherByTime()
    {
        _store.Import([Export("takeout-1.zip",
        [
            Prompt("2026-10-01T08:00:00.000Z", "一", null),
            Prompt("2026-10-01T08:20:00.000Z", "二", null),        // within half an hour of the one before
            Prompt("2026-10-01T09:30:00.000Z", "三", null),        // not
        ])]);

        var rows = GeminiMetadataReader.ForTakeout(_store).Read().Rows.OrderBy(r => r.LastActivityUtc).ToList();
        Assert.Equal(["一", "三"], rows.Select(r => r.Title).ToArray());
        Assert.All(rows, r => Assert.StartsWith("t", r.Key.NativeSessionId));
        Assert.All(rows, r => Assert.False(GeminiTakeout.IsConversationId(r.Key.NativeSessionId)));
        Assert.Equal(4, GeminiSession.ReadDialogue(rows[0].NativeOpenRef!).Count);
    }

    [Fact]
    public void AnExportWithoutGeminisHistoryChangesNothing()
    {
        var none = _store.Import([Export("takeout-search.zip", [])]);
        Assert.Equal(0, none.Found);
        Assert.False(_store.Exists);

        var page = _store.Import([Export("takeout-page.zip", [], html: "<html><body>Prompted …</body></html>")]);
        Assert.Equal(0, page.Found);
        Assert.False(page.Unreadable);                 // no activity cards at all: not an activity page
        Assert.False(_store.Exists);

        // Cards, but with a time this reader does not know: said so, nothing written.
        var odd = _store.Import([Export("takeout-odd.zip", [], html: Page(Card("Prompted\u00a0x", [], "sometime", "<p>y</p>", ConvA)))]);
        Assert.Equal(0, odd.Found);
        Assert.True(odd.Unreadable);
        Assert.False(_store.Exists);
    }

    /// <summary>One record as the HTML export lays it out: a card with the title, a line per thing that came with it, the time, the answer.</summary>
    private static string Card(string title, string[] lines, string time, string? answer, string? conversation, string? image = null) =>
        "<div class=\"outer-cell mdl-cell mdl-cell--12-col mdl-shadow--2dp\"><div class=\"mdl-grid\">"
        + "<div class=\"header-cell mdl-cell mdl-cell--12-col\"><p class=\"mdl-typography--title\">Gemini Apps<br></p></div>"
        + "<div class=\"content-cell mdl-cell mdl-cell--6-col mdl-typography--body-1\">"
        + System.Net.WebUtility.HtmlEncode(title).Replace("\u00a0", "&nbsp;") + "<br>"
        + string.Concat(lines.Select(l => l + "<br>")) + time + "<br>" + (answer is null ? "" : answer + "<br>") + "</div>"
        + "<div class=\"content-cell mdl-cell mdl-cell--6-col mdl-typography--body-1 mdl-typography--text-right\">"
        + (image is null ? "" : $"<img src=\"{image}\" class=\"image-preview\">") + "</div>"
        + "<div class=\"content-cell mdl-cell mdl-cell--12-col mdl-typography--caption\"><b>Products:</b><br>&emsp;Gemini Apps<br>"
        + "<b>Why is this here?</b><br>&emsp;This activity was saved to your Google Account because the following settings were on:&nbsp;"
        + "<a href=\"https://myaccount.google.com/activitycontrols\">Gemini Apps Activity</a>.<br>"
        + (conversation is null ? "" : $"<b>Details:</b><br>&emsp;<a href=\"https://gemini.google.com/app/{conversation}\">https://gemini.google.com/app/{conversation}</a><br>")
        + "</div></div></div>";

    private static string Page(params string[] cards) =>
        "<html><head><meta charset=\"utf-8\"><title>My Activity</title></head><body><div class=\"mdl-grid\">"
        + string.Concat(cards) + "</div></body></html>";

    /// <summary>The <see cref="Standard"/> records as the HTML export of the same moment has them: times in EDT.</summary>
    private static readonly string StandardPage = Page(
        Card("Prompted\u00a0第二段对话的问题", [], "Oct 2, 2026, 5:00:00\u202fAM EDT", "<h3>标题</h3><p>回答 <strong>重点</strong></p>", ConvB),
        Card("Cleared Gemini Apps", [], "Oct 1, 2026, 8:00:00\u202fAM EDT", null, null),
        Card("Prompted\u00a0看看这张图",
        [
            "Attached 1 file.", "-  chart.png", "<a href=\"https://example.com/page\">我的网页</a>", "1 generated image.",
            "<a href=\"https://lh3.googleusercontent.com/rd-gg/abc\">https://lh3.googleusercontent.com/rd-gg/abc</a>", "Used skill: 画图",
        ], "Oct 1, 2026, 4:05:00\u202fAM EDT", "<p>图里是一张表</p>", ConvA, image: "chart.png"),
        Card("Prompted\u00a0第一段对话的第一个问题", [], "Oct 1, 2026, 4:00:00\u202fAM EDT", "<p>好的</p>", ConvA));

    [Fact]
    public void TheHtmlExportReadsAsTheJsonOneDoes()
    {
        var result = _store.Import([Export("takeout-html.zip", [], new() { ["chart.png"] = Png }, html: StandardPage)]);
        Assert.Equal(3, result.Found);
        Assert.Equal(2, result.Conversations);
        Assert.Equal(1, result.Skipped);

        var rows = GeminiMetadataReader.ForTakeout(_store).Read().Rows.ToDictionary(r => r.Key.NativeSessionId);
        var a = rows[ConvA];
        Assert.Equal("第一段对话的第一个问题", a.Title);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 8, 5, 0, TimeSpan.Zero), a.LastActivityUtc);    // EDT read as UTC−4
        var entries = GeminiSession.ReadEntries(a.NativeOpenRef!, keepImages: true);
        Assert.Equal(["第一段对话的第一个问题", "好的",
                      "（图片 · chart.png · 1 KB）", "（附件 · 网页 · 我的网页  https://example.com/page）", "看看这张图",
                      "提示 · 用了技能：画图", "图里是一张表\n\n[生成的图片 1（在网页上看）](https://lh3.googleusercontent.com/rd-gg/abc)"],
            entries.Select(e => e.Text).ToArray());
        Assert.Equal(Png, entries[2].Image);
        Assert.Equal("### 标题\n\n回答 **重点**", GeminiSession.ReadDialogue(rows[ConvB].NativeOpenRef!)[1].Text);

        // The JSON export of the same moment adds nothing: the same records, to the second.
        var json = _store.Import([Export("takeout-json.zip", Standard, new() { ["chart.png"] = Png, ["photo.png"] = Png })]);
        Assert.Equal(0, json.Added);
        Assert.Equal(3, json.Total);
    }

    [Theory]
    [InlineData("Oct 1, 2026, 4:05:00\u202fAM EDT", "2026-10-01T08:05:00Z")]
    [InlineData("Dec 2, 2025, 12:30:15 PM EST", "2025-12-02T17:30:15Z")]
    [InlineData("Jan 5, 2026, 12:00:00 AM GMT", "2026-01-05T00:00:00Z")]
    [InlineData("1 Oct 2026, 09:05:00 GMT+01:00", "2026-10-01T08:05:00Z")]
    [InlineData("2026年10月1日 16:05:00 GMT+08:00", "2026-10-01T08:05:00Z")]
    [InlineData("Oct 1, 2026, 13:05:00 PM EDT", null)]
    [InlineData("Feb 30, 2026, 1:00:00 PM EDT", null)]
    [InlineData("Prompted 10:00:00", null)]
    public void CardTimesAreReadInTheirOwnZone(string text, string? utc) =>
        Assert.Equal(utc is null ? null : DateTimeOffset.Parse(utc, System.Globalization.CultureInfo.InvariantCulture),
            GeminiTakeout.CardTimeOf(text.Replace("\\u202f", "\u202f")));

    [Fact]
    public void AKeptFileIsOnlyEverReadFromTheStoresOwnFolder()
    {
        _store.Import([Export("takeout-1.zip", Standard, new() { ["chart.png"] = Png })]);
        var outside = Path.Combine(_dir, "data", "secret.png");
        File.WriteAllBytes(outside, Png);
        var forged = Path.Combine(_store.ConversationsFolder, "session-0000000000000000.json");
        File.WriteAllText(forged, """
            {"sessionId":"0000000000000000","messages":[{"id":"u","timestamp":"2026-10-01T08:00:00.000Z","type":"user",
             "content":[{"text":"x"},{"launcherFile":{"name":"secret.png","mimeType":"image/png","path":"../../secret.png"}}]}]}
            """);

        var entries = GeminiSession.ReadEntries(forged, keepImages: true);
        Assert.Null(entries[0].Image);
        Assert.Equal("（附件 · secret.png · 文件不在了）", entries[0].Text);
    }

    [Fact]
    public void TheSyncListsTheImportAndForgetsItWhenRemoved()
    {
        using var catalog = new SqliteCatalog(Path.Combine(_dir, "catalog.sqlite"));
        catalog.Initialize(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "CatalogSchema.sql")));
        var config = Path.Combine(_dir, "claude");
        Directory.CreateDirectory(Path.Combine(config, "projects"));
        var claude = new ClaudeInstallation { Found = false };
        var codex = CodexPaths.FromHome(Path.Combine(_dir, "codex"));
        var sync = new CatalogSyncService(catalog, config, geminiWeb: _store);
        List<SessionMetadata> Listed() =>
            [.. catalog.Page(Visibility.Active, null, null, 100, GroupFilter.All, BackendFilter.Gemini).Rows
                .Where(r => r.Key.Backend == ConversationKey.GeminiWeb)];

        var nothing = sync.Sync(claude, codex);
        Assert.DoesNotContain(nothing.Coverage.Sources, s => s.Backend == ConversationKey.GeminiWeb);

        _store.Import([Export("takeout-1.zip", Standard, new() { ["chart.png"] = Png })]);
        var first = sync.Sync(claude, codex);
        Assert.Equal(2, Assert.Single(first.Coverage.Sources, s => s.Backend == ConversationKey.GeminiWeb).Records);
        Assert.Equal(2, Listed().Count);
        var row = Listed().Single(r => r.Key.NativeSessionId == ConvA);
        Assert.Equal(row.NativeOpenRef, TranscriptLocator.Find(row, config, codex));
        Assert.Equal(AppStanding.Imported, OrderingRules.Standing(row.Key.Backend, row.NativeArchiveState));

        // No tokens are recorded for the web: nothing to count.
        using (var ledger = new UsageLedger(Path.Combine(_dir, "usage.sqlite")))
            Assert.Empty(ledger.SourcesFor(Listed(), config, codex));

        // Gone from the store underneath the list: kept, marked as gone, like any app's record.
        Assert.Equal(2, _store.Remove());
        var removed = sync.Sync(claude, codex);
        Assert.Equal(2, removed.RowsRemoved);
        Assert.All(Listed(), r => Assert.NotNull(r.SourceGoneUtc));

        // Removed on purpose (what the import window does): the rows go with it.
        Assert.Equal(2, catalog.Forget(ConversationKey.GeminiWeb));
        Assert.Empty(Listed());
    }

    [Fact]
    public void ImportedRowsSortWhereTheirLabelsSay()
    {
        using var catalog = new SqliteCatalog(Path.Combine(_dir, "catalog.sqlite"));
        catalog.Initialize(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "CatalogSchema.sql")));
        SessionMetadata Row(string backend, string id, NativeArchiveState state) => new()
        {
            Key = new ConversationKey(backend, "acc", "org", id),
            Title = id, SearchKey = id, SourceRevision = "1",
            LastActivityUtc = DateTimeOffset.UtcNow, NativeArchiveState = state, SourceCoverage = SourceCoverage.Resolved,
        };
        catalog.UpsertRange(
        [
            Row(ConversationKey.GeminiWeb, "w", NativeArchiveState.Unsupported),
            Row(ConversationKey.Gemini, "g", NativeArchiveState.Unsupported),
            Row(ConversationKey.Codex, "c", NativeArchiveState.Active),
            Row(ConversationKey.DesktopCode, "d", NativeArchiveState.Unknown),
            Row(ConversationKey.CliCode, "t", NativeArchiveState.Unsupported),
        ]);
        List<SessionMetadata> Sorted(SortField field) =>
            [.. catalog.Page(Visibility.Active, null, null, 100, GroupFilter.All, BackendFilter.All, new SortOrder(field, false)).Rows];

        var bySource = Sorted(SortField.Source).Select(r => OrderingRules.SourceRank(r.Key.Backend)).ToList();
        Assert.Equal([1, 2, 3, 4, 5], bySource);
        var byStanding = Sorted(SortField.AppState)
            .Select(r => (int)OrderingRules.Standing(r.Key.Backend, r.NativeArchiveState, r.ContinuedIn is not null)).ToList();
        Assert.Equal(byStanding.Order(), byStanding);
        Assert.Equal("导入", OrderingRules.Label(AppStanding.Imported));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
    }
}
