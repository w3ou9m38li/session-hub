using ClaudeLauncher.Core.Catalog;
using ClaudeLauncher.Core.Contracts;
using ClaudeLauncher.Infrastructure.Catalog;
using Microsoft.Data.Sqlite;

namespace ClaudeLauncher.IntegrationTests;

/// <summary>
/// Sorting happens in the catalog, page by page. The failures that matter are the quiet ones:
/// a row that never appears on any page, one that appears twice, an order that disagrees with
/// what the column shows, and blanks that jump to the top when the order is reversed.
/// </summary>
public sealed class CatalogSortTests : IDisposable
{
    private readonly string _dir;
    private readonly SqliteCatalog _catalog;

    public CatalogSortTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "launcher-sort", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
        _catalog = new SqliteCatalog(Path.Combine(_dir, "catalog.sqlite"));
        _catalog.Initialize(Schema());
    }

    private static string Schema()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "CatalogSchema.sql"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                         "src", "ClaudeLauncher.Infrastructure", "Catalog", "CatalogSchema.sql"),
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return File.ReadAllText(c);
        throw new FileNotFoundException("CatalogSchema.sql not found next to the tests.");
    }

    private static readonly string[] Backends = [ConversationKey.CliCode, ConversationKey.DesktopCode, ConversationKey.Codex];
    private static readonly NativeArchiveState[] States =
        [NativeArchiveState.Active, NativeArchiveState.Archived, NativeArchiveState.Unsupported, NativeArchiveState.Unknown];
    // "" as well as null: a source can record a missing working directory either way.
    private static readonly string?[] Projects = [@"G:\wheat GWAS 2", @"D:\ClaudeLauncher", null, @"g:\Wheat", @"F:\Subject", ""];
    private static readonly string[] Titles = ["整理小麦数据", "Alpha", "alpha", "病级提取", "zeta", "Äpfel", "１２３", "阿斯匹林"];

    /// <summary>A catalog with every awkward case: shared titles and timestamps, blanks, every backend and state.</summary>
    private List<SessionMetadata> Seed(int count = 157)
    {
        var rows = new List<SessionMetadata>();
        var start = new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < count; i++)
        {
            rows.Add(new SessionMetadata
            {
                Key = new ConversationKey(Backends[i % 3], "acc", "org", $"s-{i:D4}"),
                Title = Titles[i % Titles.Length],
                SearchKey = SearchKeys.Normalize(Titles[i % Titles.Length]),
                ProjectRef = Projects[i % Projects.Length],
                SidebarGroup = i % 11 == 0 ? "Claude 组" : null,
                SourceRevision = "1",
                // Every fourth row shares a timestamp; every thirteenth has none at all.
                LastActivityUtc = i % 13 == 0 ? null : start.AddMinutes(i % 4 == 0 ? 0 : i),
                NativeArchiveState = States[i % States.Length],
                SourceCoverage = SourceCoverage.Resolved,
                // Every other terminal transcript is one a desktop session was continued from.
                ContinuedIn = i % 6 == 0 ? "local_x" : null,
            });
        }
        _catalog.UpsertRange(rows);
        var stored = _catalog.Page(Visibility.Active, null, null, SqliteCatalog.MaxPageSize).Rows;
        _catalog.AssignGroup(stored.Where((_, i) => i % 5 == 1), "小麦");
        _catalog.AssignGroup(_catalog.Page(Visibility.Active, null, null, SqliteCatalog.MaxPageSize).Rows
                                     .Where((_, i) => i % 7 == 2), "Beta");
        return rows;
    }

    private List<SessionMetadata> ReadAll(SortOrder sort, int pageSize = 13, string? search = null)
    {
        var all = new List<SessionMetadata>();
        PageCursor? cursor = null;
        do
        {
            var page = _catalog.Page(Visibility.Active, search, cursor, pageSize, sort: sort);
            Assert.Equal(PageOutcome.Ok, page.Outcome);
            all.AddRange(page.Rows);
            cursor = page.Next;
        } while (cursor is not null);
        return all;
    }

    public static TheoryData<SortField, bool> EveryOrder()
    {
        var data = new TheoryData<SortField, bool>();
        foreach (var field in Enum.GetValues<SortField>())
        {
            data.Add(field, false);
            data.Add(field, true);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(EveryOrder))]
    public void EveryOrderPagesThroughEveryRowExactlyOnce(SortField field, bool reversed)
    {
        var seeded = Seed();
        var seen = ReadAll(new SortOrder(field, reversed)).Select(r => r.Key).ToList();

        Assert.Equal(seeded.Count, seen.Count);
        Assert.Equal(seeded.Count, seen.Distinct().Count());
    }

    [Theory]
    [MemberData(nameof(EveryOrder))]
    public void PagesJoinIntoTheSameOrderAsOneBigPage(SortField field, bool reversed)
    {
        Seed();
        var sort = new SortOrder(field, reversed);
        var paged = ReadAll(sort, pageSize: 7).Select(r => r.Key);
        var whole = ReadAll(sort, pageSize: SqliteCatalog.MaxPageSize).Select(r => r.Key);

        Assert.Equal(whole, paged);
    }

    [Fact]
    public void TitlesSortTheWayTheyReadIgnoringCaseAndWidth()
    {
        Seed(Titles.Length);
        var titles = ReadAll(new SortOrder(SortField.Title, false)).Select(r => r.Title).ToList();

        for (var i = 1; i < titles.Count; i++)
            Assert.True(UiCollation.Compare(titles[i - 1], titles[i]) <= 0, $"{titles[i - 1]} 应排在 {titles[i]} 前");
        // Chinese by pinyin: 阿 (ā) comes before 病 (bìng), which comes before 整 (zhěng).
        Assert.True(titles.IndexOf("阿斯匹林") < titles.IndexOf("病级提取"));
        Assert.True(titles.IndexOf("病级提取") < titles.IndexOf("整理小麦数据"));
    }

    [Fact]
    public void ReversingTheTitleOrderReversesIt()
    {
        Seed(Titles.Length);
        var forward = ReadAll(new SortOrder(SortField.Title, false)).Select(r => r.Key).ToList();
        var backward = ReadAll(new SortOrder(SortField.Title, true)).Select(r => r.Key).ToList();

        // Rows with equal titles keep newest-first in both, so compare the title sequence.
        Assert.Equal(
            forward.Select(k => Title(k)).Reverse().ToList(),
            backward.Select(k => Title(k)).ToList(),
            new CollationEquality());

        string Title(ConversationKey key) => ReadAll(default).Single(r => r.Key == key).Title;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BlanksStayLastInBothDirections(bool reversed)
    {
        Seed();

        var byGroup = ReadAll(new SortOrder(SortField.Group, reversed));
        var firstUngrouped = byGroup.FindIndex(r => GroupRules.Effective(r.LocalGroup, r.SidebarGroup) is null);
        Assert.All(byGroup.Skip(firstUngrouped), r => Assert.Null(GroupRules.Effective(r.LocalGroup, r.SidebarGroup)));

        var byProject = ReadAll(new SortOrder(SortField.Project, reversed));
        var firstBlank = byProject.FindIndex(r => string.IsNullOrEmpty(r.ProjectRef));
        Assert.All(byProject.Skip(firstBlank), r => Assert.True(string.IsNullOrEmpty(r.ProjectRef)));

        var byTime = ReadAll(new SortOrder(SortField.LastActivity, reversed));
        var firstUntimed = byTime.FindIndex(r => r.LastActivityUtc is null);
        Assert.All(byTime.Skip(firstUntimed), r => Assert.Null(r.LastActivityUtc));
    }

    [Fact]
    public void WithinACategoryTheNewestComesFirst()
    {
        Seed();
        var rows = ReadAll(new SortOrder(SortField.Source, false));

        foreach (var run in rows.GroupBy(r => r.Key.Backend))
        {
            var times = run.Where(r => r.LastActivityUtc is not null).Select(r => r.LastActivityUtc!.Value).ToList();
            Assert.Equal(times.OrderByDescending(t => t), times);
        }
    }

    [Fact]
    public void SourceAndStandingFollowTheSameRulesTheColumnsShow()
    {
        Seed();

        var bySource = ReadAll(new SortOrder(SortField.Source, false))
            .Select(r => OrderingRules.SourceRank(r.Key.Backend)).ToList();
        Assert.Equal(bySource.Order(), bySource);

        var byStanding = ReadAll(new SortOrder(SortField.AppState, false))
            .Select(r => (int)OrderingRules.Standing(r.Key.Backend, r.NativeArchiveState, r.ContinuedIn is not null)).ToList();
        Assert.Equal(byStanding.Order(), byStanding);
        // Every standing is actually present in the seed but an imported copy's (5), which GeminiTakeoutTests sorts.
        Assert.Equal([1, 2, 3, 4, 6], byStanding.Distinct().Order());
    }

    [Theory]
    [InlineData(SortField.Group)]
    [InlineData(SortField.Source)]
    [InlineData(SortField.Project)]
    [InlineData(SortField.AppState)]
    public void SectionCountsAgreeWithTheRowsInEachSection(SortField field)
    {
        Seed();
        var rows = ReadAll(new SortOrder(field, false));
        var counts = _catalog.SectionCounts(Visibility.Active, null, GroupFilter.All, BackendFilter.All, field);

        var expected = rows.GroupBy(r => SqliteCatalog.SectionKey(r, field)!)
                           .ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(expected.OrderBy(kv => kv.Key), counts.OrderBy(kv => kv.Key));

        // Sections are contiguous: once the list leaves a category it never comes back to it.
        var order = rows.Select(r => SqliteCatalog.SectionKey(r, field)).ToList();
        var runs = order.Where((key, i) => i == 0 || key != order[i - 1]).ToList();
        Assert.Equal(runs.Count, runs.Distinct().Count());
    }

    [Fact]
    public void PathsThatDifferOnlyInCaseKeepTheirSectionsContiguous()
    {
        // Two sources can record one folder with a different drive-letter case; with times
        // interleaved, a case-blind order would alternate between the two sections.
        var start = new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);
        _catalog.UpsertRange(Enumerable.Range(0, 12).Select(i => new SessionMetadata
        {
            Key = new ConversationKey(ConversationKey.CliCode, "acc", "org", $"c-{i:D2}"),
            Title = "t",
            SearchKey = "t",
            ProjectRef = i % 2 == 0 ? @"D:\ClaudeLauncher" : @"d:\claudelauncher",
            SourceRevision = "1",
            LastActivityUtc = start.AddMinutes(i),
            NativeArchiveState = NativeArchiveState.Unsupported,
            SourceCoverage = SourceCoverage.Resolved,
        }));

        var order = ReadAll(new SortOrder(SortField.Project, false)).Select(r => r.ProjectRef).ToList();
        var runs = order.Where((key, i) => i == 0 || key != order[i - 1]).ToList();

        Assert.Equal(2, runs.Count);
    }

    [Fact]
    public void SectionCountsRespectTheSearch()
    {
        Seed();
        var counts = _catalog.SectionCounts(Visibility.Active, "wheat", GroupFilter.All, BackendFilter.All, SortField.Project);

        Assert.All(counts.Keys, k => Assert.Contains("wheat", k, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ACursorCannotCrossToAnotherOrder()
    {
        Seed();
        var first = _catalog.Page(Visibility.Active, null, null, 10, sort: new SortOrder(SortField.Title, false));
        Assert.NotNull(first.Next);

        var crossed = _catalog.Page(Visibility.Active, null, first.Next, 10, sort: new SortOrder(SortField.Title, true));
        Assert.Equal(PageOutcome.CursorExpired, crossed.Outcome);
    }

    [Fact]
    public void TheDefaultOrderIsStillNewestFirst()
    {
        Seed();
        var times = ReadAll(default).Where(r => r.LastActivityUtc is not null).Select(r => r.LastActivityUtc!.Value).ToList();
        Assert.Equal(times.OrderByDescending(t => t), times);
    }

    private sealed class CollationEquality : IEqualityComparer<string>
    {
        public bool Equals(string? x, string? y) => UiCollation.Compare(x, y) == 0;
        public int GetHashCode(string obj) => 0;
    }

    public void Dispose()
    {
        _catalog.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
    }
}
