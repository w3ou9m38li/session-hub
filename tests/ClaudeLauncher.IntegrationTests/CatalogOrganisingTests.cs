using ClaudeLauncher.Core.Contracts;
using ClaudeLauncher.Infrastructure.Catalog;

namespace ClaudeLauncher.IntegrationTests;

/// <summary>
/// 0.8's ways of finding one's way through a long list: sections by date and a time limit,
/// the user's own pins, names and notes, and one row per conversation where Claude's desktop
/// session and its terminal transcript used to be listed twice.
/// </summary>
public sealed class CatalogOrganisingTests : IDisposable
{
    private readonly string _dir;
    private readonly SqliteCatalog _catalog;

    /// <summary>Noon, local time: every bucket edge is a local midnight before it.</summary>
    private static readonly DateTime Now = new(2026, 9, 23, 12, 0, 0, DateTimeKind.Local);

    public CatalogOrganisingTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "launcher-organising", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
        _catalog = new SqliteCatalog(Path.Combine(_dir, "catalog.sqlite"));
        _catalog.Initialize(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "CatalogSchema.sql")));
    }

    private static SessionMetadata Row(string id, string title, DateTime? localTime,
        string backend = ConversationKey.CliCode, string? cli = null) => new()
    {
        Key = new ConversationKey(backend, "acc", "org", id),
        CliSessionId = cli,
        Title = title,
        SearchKey = SearchKeys.Normalize(title),
        SourceRevision = "1:1",
        LastActivityUtc = localTime is { } t ? new DateTimeOffset(t) : null,
        NativeArchiveState = NativeArchiveState.Unsupported,
        SourceCoverage = SourceCoverage.Resolved,
    };

    private List<SessionMetadata> All(SortOrder sort = default, string? search = null, DateTimeOffset? since = null) =>
        _catalog.Page(Visibility.Active, search, null, SqliteCatalog.MaxPageSize, sort: sort, since: since).Rows.ToList();

    private SessionMetadata One(string id) => All().Single(r => r.Key.NativeSessionId == id);

    // ------------------------------------------------------------- size

    [Fact]
    public void SizeSortsLargestFirstInSizeSectionsWithNoFileLast()
    {
        const long MB = 1024 * 1024;
        _catalog.UpsertRange([
            Row("small", "小", Now) with { TranscriptBytes = 300 * 1024 },
            Row("huge", "巨", Now) with { TranscriptBytes = 420 * MB },
            Row("none", "无文件", Now),
            Row("mid", "中", Now) with { TranscriptBytes = 5 * MB },
            Row("large", "大", Now) with { TranscriptBytes = 40 * MB },
        ]);

        Assert.Equal(["huge", "large", "mid", "small", "none"],
            All(new SortOrder(SortField.Size, false)).Select(r => r.Key.NativeSessionId));
        Assert.Equal(["small", "mid", "large", "huge", "none"],
            All(new SortOrder(SortField.Size, true)).Select(r => r.Key.NativeSessionId));

        var counts = _catalog.SectionCounts(Visibility.Active, null, GroupFilter.All, BackendFilter.All, SortField.Size);
        Assert.Equal(All().GroupBy(r => SqliteCatalog.SectionKey(r, SortField.Size)!).ToDictionary(g => g.Key, g => g.Count())
                          .OrderBy(kv => kv.Key), counts.OrderBy(kv => kv.Key));
        Assert.Equal(SqliteCatalog.SizeHugeKey, SqliteCatalog.SectionKey(One("huge"), SortField.Size));
        Assert.Equal(SqliteCatalog.SizeNoneKey, SqliteCatalog.SectionKey(One("none"), SortField.Size));
        Assert.Equal(420 * MB, One("huge").TranscriptBytes);
    }

    [Fact]
    public void ADesktopRowIsAsBigAsTheTranscriptItWasFoldedWith()
    {
        var rows = new List<SessionMetadata>
        {
            Row("local_1", "桌面", Now, ConversationKey.DesktopCode, cli: "t1"),
            Row("t1", "终端", Now, ConversationKey.CliCode, cli: "t1") with { TranscriptBytes = 12345 },
        };
        CatalogSyncService.FoldTerminalTwins(rows);
        Assert.Equal(12345, rows.Single(r => r.Key.Backend == ConversationKey.DesktopCode).TranscriptBytes);
    }

    // ------------------------------------------------------------ dates

    [Fact]
    public void EachRowFallsInTheDayItHappened()
    {
        var dates = DateBuckets.At(Now);
        Assert.Equal(DateBuckets.TodayKey, dates.KeyOf(new DateTimeOffset(Now.Date)));
        Assert.Equal(DateBuckets.YesterdayKey, dates.KeyOf(new DateTimeOffset(Now.Date.AddTicks(-1))));
        Assert.Equal(DateBuckets.WeekKey, dates.KeyOf(new DateTimeOffset(Now.Date.AddDays(-6))));
        Assert.Equal(DateBuckets.MonthKey, dates.KeyOf(new DateTimeOffset(Now.Date.AddDays(-6).AddTicks(-1))));
        Assert.Equal(DateBuckets.EarlierKey, dates.KeyOf(new DateTimeOffset(Now.Date.AddDays(-30))));
        Assert.Equal(DateBuckets.NoTimeKey, dates.KeyOf(null));
    }

    [Fact]
    public void DateSectionsCountWhatTheRowsSay()
    {
        _catalog.UpsertRange([
            Row("a", "今天一", Now.AddHours(-1)), Row("b", "今天二", Now.Date.AddMinutes(5)),
            Row("c", "昨天", Now.AddDays(-1)), Row("d", "五天前", Now.AddDays(-5)),
            Row("e", "二十天前", Now.AddDays(-20)), Row("f", "很久以前", Now.AddDays(-400)),
            Row("g", "没有时间", null),
        ]);
        var dates = DateBuckets.At(Now);

        var counts = _catalog.SectionCounts(Visibility.Active, null, GroupFilter.All, BackendFilter.All,
            SortField.LastActivity, dates);
        var expected = All().GroupBy(r => SqliteCatalog.SectionKey(r, SortField.LastActivity, dates)!)
                            .ToDictionary(g => g.Key, g => g.Count());

        Assert.Equal(expected.OrderBy(kv => kv.Key), counts.OrderBy(kv => kv.Key));
        Assert.Equal(2, counts[DateBuckets.TodayKey]);
        Assert.Equal(1, counts[DateBuckets.NoTimeKey]);

        // Newest first, so the sections run in day order without coming back.
        var order = All().Select(r => SqliteCatalog.SectionKey(r, SortField.LastActivity, dates)).ToList();
        Assert.Equal(["0", "0", "1", "2", "3", "4", "5"], order);
    }

    [Fact]
    public void ATimeLimitKeepsOnlyWhatIsRecent()
    {
        _catalog.UpsertRange([Row("a", "新", Now.AddHours(-2)), Row("b", "旧", Now.AddDays(-40)), Row("c", "无时间", null)]);
        var since = new DateTimeOffset(Now.Date.AddDays(-6));

        Assert.Equal(["a"], All(since: since).Select(r => r.Key.NativeSessionId));
        Assert.Equal(1, _catalog.Count(Visibility.Active, since: since));

        // A cursor taken under one time limit is not stitched onto another.
        for (var i = 0; i < 12; i++) _catalog.UpsertRange([Row("n" + i, "新" + i, Now.AddMinutes(-i))]);
        var first = _catalog.Page(Visibility.Active, null, null, 5, since: since);
        Assert.Equal(PageOutcome.CursorExpired, _catalog.Page(Visibility.Active, null, first.Next, 5).Outcome);
    }

    // ------------------------------------------------- pins, names, notes

    [Fact]
    public void PinnedRowsLeadEveryOrderAndHaveTheirOwnSection()
    {
        _catalog.UpsertRange([Row("a", "阿", Now.AddHours(-1)), Row("b", "病", Now.AddHours(-2)), Row("c", "整", Now.AddHours(-3))]);
        var pinned = _catalog.SetPinned([One("c")], true);
        Assert.Equal(1, pinned.Applied);

        foreach (var sort in new[] { SortOrder.Default, new SortOrder(SortField.LastActivity, true),
                     new SortOrder(SortField.Title, false), new SortOrder(SortField.Title, true) })
            Assert.Equal("c", All(sort)[0].Key.NativeSessionId);

        var dates = DateBuckets.At(Now);
        Assert.Equal(SqliteCatalog.PinnedSection, SqliteCatalog.SectionKey(One("c"), SortField.LastActivity, dates));
        var counts = _catalog.SectionCounts(Visibility.Active, null, GroupFilter.All, BackendFilter.All,
            SortField.LastActivity, dates);
        Assert.Equal(1, counts[SqliteCatalog.PinnedSection]);
        Assert.Equal(2, counts[DateBuckets.TodayKey]);

        // Undone like any other organising action.
        Assert.Equal(1, _catalog.RevertPreferences(pinned.Changes).Applied);
        Assert.False(One("c").LocalPinned);
        Assert.Equal("a", All()[0].Key.NativeSessionId);
    }

    [Fact]
    public void AName_OfOnesOwnIsShownSortedAndSearched_TheSourcesTitleIsKept()
    {
        _catalog.UpsertRange([Row("a", "项目完善与功能精简", Now), Row("b", "项目完善与功能精简", Now.AddHours(-1))]);
        var named = _catalog.SetLocalTitle(One("b"), "  启动速度优化  ");

        var b = One("b");
        Assert.Equal("启动速度优化", b.DisplayTitle);
        Assert.Equal("项目完善与功能精简", b.Title);
        Assert.Equal(["b"], All(search: "启动速度").Select(r => r.Key.NativeSessionId));
        // "启" sorts before "项" by pinyin (qǐ < xiàng).
        Assert.Equal("b", All(new SortOrder(SortField.Title, false))[0].Key.NativeSessionId);

        _catalog.RevertPreferences(named.Changes);
        Assert.Null(One("b").LocalTitle);
        Assert.Empty(All(search: "启动速度"));
    }

    [Fact]
    public void ANoteIsSearchableAndAStaleUndoLeavesANewerChangeAlone()
    {
        _catalog.UpsertRange([Row("a", "GWAS 工具", Now)]);
        var first = _catalog.SetNote(One("a"), "等审稿意见回来再继续");
        Assert.Equal(["a"], All(search: "审稿意见").Select(r => r.Key.NativeSessionId));

        _catalog.SetNote(One("a"), "已投稿");
        var stale = _catalog.RevertPreferences(first.Changes);
        Assert.Equal(0, stale.Applied);
        Assert.Equal(1, stale.Conflict);
        Assert.Equal("已投稿", One("a").LocalNote);

        // Blank removes it.
        _catalog.SetNote(One("a"), "   ");
        Assert.Null(One("a").LocalNote);
        Assert.Empty(All(search: "已投稿"));
    }

    // ---------------------------------------------- one row per conversation

    [Fact]
    public void ADesktopSessionAndItsTerminalTranscriptAreOneRow()
    {
        var rows = new List<SessionMetadata>
        {
            Row("local_1", "GWAS SNP Gene Tools 优化", Now.AddHours(-3), ConversationKey.DesktopCode, cli: "cli-1"),
            Row("cli-1", "Optimize SNP tools", Now.AddHours(-1)),     // the terminal carried on
            Row("cli-2", "只在终端的会话", Now.AddHours(-2)),
        };
        CatalogSyncService.FoldTerminalTwins(rows);
        _catalog.UpsertRange(rows);

        var listed = All();
        Assert.Equal(["local_1", "cli-2"], listed.Select(r => r.Key.NativeSessionId));
        var desktop = listed[0];
        Assert.Equal(new DateTimeOffset(Now.AddHours(-1)), desktop.LastActivityUtc);    // the later of the two
        Assert.Equal(["local_1"], All(search: "optimize").Select(r => r.Key.NativeSessionId));
        Assert.Equal(new ViewCounts(2, 0), _catalog.CountViews());
        Assert.Equal(2, _catalog.Count(Visibility.Active));
        Assert.Equal(2, _catalog.Groups(Visibility.Active).Sum(g => g.Count));
    }

    [Fact]
    public void WhatWasSetOnTheTerminalRowMovesToTheMergedDesktopRow()
    {
        // Before 0.8 the two were separate rows, and the user may have named and pinned the terminal one.
        List<SessionMetadata> Rows() =>
        [
            Row("local_1", "桌面标题", Now.AddHours(-2), ConversationKey.DesktopCode, cli: "cli-1"),
            Row("cli-1", "终端标题", Now.AddHours(-1)),
        ];
        _catalog.UpsertRange(Rows());
        _catalog.SetLocalTitle(One("cli-1"), "我起的名字");
        _catalog.SetPinned([One("cli-1")], true);

        var folded = Rows();
        CatalogSyncService.FoldTerminalTwins(folded);
        _catalog.UpsertRange(folded);
        Assert.Equal(1, _catalog.AdoptMergedPreferences());

        var desktop = One("local_1");
        Assert.Equal("我起的名字", desktop.DisplayTitle);
        Assert.True(desktop.LocalPinned);
        Assert.Equal(["local_1"], All(search: "我起的名字").Select(r => r.Key.NativeSessionId));
        Assert.Equal(0, _catalog.AdoptMergedPreferences());          // handed over once

        // Clearing it on the desktop row sticks through the next sync.
        _catalog.SetLocalTitle(desktop, null);
        _catalog.UpsertRange(folded);
        _catalog.AdoptMergedPreferences();
        Assert.Null(One("local_1").LocalTitle);
    }

    [Fact]
    public void ATranscriptADesktopSessionWasContinuedFromStaysListedAndSaysSo()
    {
        // Claude continued the conversation in a new transcript and remembers the old one.
        List<SessionMetadata> Rows() =>
        [
            Row("local_1", "项目完善", Now.AddHours(-1), ConversationKey.DesktopCode, cli: "cli-new")
                with { PriorCliSessionIds = ["cli-old", "cli-new"] },
            Row("cli-new", "项目完善", Now.AddHours(-1)),
            Row("cli-old", "项目完善", Now.AddDays(-1)),
            Row("cli-other", "别的会话", Now.AddHours(-2)),
        ];
        var rows = Rows();
        CatalogSyncService.FoldTerminalTwins(rows);
        _catalog.UpsertRange(rows);

        // The current transcript folds into the desktop row; the earlier one keeps its own row, marked.
        Assert.Equal(["local_1", "cli-other", "cli-old"], All().Select(r => r.Key.NativeSessionId));
        Assert.Equal("local_1", One("cli-old").ContinuedIn);
        Assert.Null(One("cli-other").ContinuedIn);
        Assert.Null(One("local_1").ContinuedIn);

        // Nothing new: nothing written.
        var again = Rows();
        CatalogSyncService.FoldTerminalTwins(again);
        Assert.Equal(0, _catalog.UpsertRange(again));

        // Once Claude no longer lists it, the next sync takes the mark off.
        var later = Rows();
        later[0] = later[0] with { PriorCliSessionIds = [] };
        CatalogSyncService.FoldTerminalTwins(later);
        Assert.Equal(1, _catalog.UpsertRange(later));
        Assert.Null(One("cli-old").ContinuedIn);
    }

    [Fact]
    public void AContinuedSessionAndItsEarlierPartsLinkBothWays()
    {
        var rows = new List<SessionMetadata>
        {
            Row("local_1", "项目完善", Now.AddHours(-1), ConversationKey.DesktopCode, cli: "cli-3")
                with { PriorCliSessionIds = ["cli-2", "cli-1"] },
            Row("cli-3", "项目完善", Now.AddHours(-1)),
            Row("cli-2", "项目完善", Now.AddDays(-1)),
            Row("cli-1", "项目完善", Now.AddDays(-3)),
            Row("local_2", "没续接过", Now.AddHours(-2), ConversationKey.DesktopCode, cli: "cli-4"),
            Row("cli-4", "没续接过", Now.AddHours(-2)),
            Row("cli-9", "别的会话", Now.AddHours(-3)),
        };
        CatalogSyncService.FoldTerminalTwins(rows);
        _catalog.UpsertRange(rows);

        // The list knows which desktop session has earlier parts, and how many.
        Assert.Equal(2, One("local_1").EarlierParts);
        Assert.Equal(0, One("local_2").EarlierParts);
        Assert.Equal(0, One("cli-2").EarlierParts);

        // From the session and from each of its parts: one chain, in the order it happened.
        string[] expected = ["cli-1", "cli-2", "local_1"];
        foreach (var id in expected)
            Assert.Equal(expected, _catalog.ContinuationChain(One(id)).Select(r => r.Key.NativeSessionId));

        // Nothing to link for a session never continued, or for a conversation of its own.
        Assert.Empty(_catalog.ContinuationChain(One("local_2")));
        Assert.Empty(_catalog.ContinuationChain(One("cli-9")));
    }

    [Fact]
    public void ACatalogFromBeforeTheMarkGetsItsColumnOnOpen()
    {
        _catalog.UpsertRange([Row("cli-old", "项目完善", Now)]);
        using (var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(_dir, "catalog.sqlite")};Pooling=False"))
        {
            raw.Open();
            using var cmd = raw.CreateCommand();
            // As a catalog from before the column was: no column, and so no index on it either.
            cmd.CommandText = "DROP INDEX IF EXISTS sessions_continued_in; ALTER TABLE sessions DROP COLUMN continued_in;";
            cmd.ExecuteNonQuery();
        }

        _catalog.Initialize(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "CatalogSchema.sql")));

        Assert.Null(One("cli-old").ContinuedIn);
        _catalog.UpsertRange([Row("cli-old", "项目完善", Now) with { ContinuedIn = "local_1" }]);
        Assert.Equal("local_1", One("cli-old").ContinuedIn);
    }

    public void Dispose()
    {
        _catalog.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
    }
}
