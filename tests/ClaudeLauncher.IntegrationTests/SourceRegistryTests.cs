using System.Reflection;
using ClaudeLauncher.Core.Catalog;
using ClaudeLauncher.Core.Contracts;
using ClaudeLauncher.Infrastructure.Backends;
using ClaudeLauncher.Infrastructure.Catalog;

namespace ClaudeLauncher.IntegrationTests;

/// <summary>
/// Every source is described once (<see cref="Sources"/>), and the catalog's SQL, the app filter,
/// the copies and the windows all take it from there. These hold the description complete and
/// the SQL written from it in step with the rules the columns show.
/// </summary>
public sealed class SourceRegistryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "launcher-sources", Guid.NewGuid().ToString("n"));

    private SqliteCatalog Catalog()
    {
        Directory.CreateDirectory(_dir);
        var catalog = new SqliteCatalog(Path.Combine(_dir, "catalog.sqlite"));
        catalog.Initialize(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "CatalogSchema.sql")));
        return catalog;
    }

    private static IEnumerable<string> BackendIds() =>
        typeof(ConversationKey).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!);

    [Fact]
    public void EveryBackendIdIsDescribedOnce()
    {
        foreach (var backend in BackendIds())
            Assert.True(Sources.Of(backend) is not null, $"{backend} has no description in Sources");
        Assert.Equal(Sources.All.Count, Sources.All.Select(s => s.Backend).Distinct().Count());
        Assert.Equal(BackendIds().Order(), Sources.All.Select(s => s.Backend).Order());
    }

    [Fact]
    public void TheSourcesReadSortInDistinctPlacesAndEveryFamilyHasOne()
    {
        var read = Sources.Read;
        Assert.Equal(read.Count, read.Select(s => s.Rank).Distinct().Count());
        Assert.All(read, s => Assert.True(s.Rank < OrderingRules.UnknownRank));
        Assert.Equal(read.Select(s => s.Rank).Order(), read.Select(s => s.Rank));     // listed in their order
        Assert.All(Sources.Families, f => Assert.NotEmpty(Sources.BackendsOf(f.Family)));
        Assert.All(read, s => Assert.Contains(Sources.Families, f => f.Family == s.Family));
        // The families' numbers are what the usage ledger stores: never renumbered.
        Assert.Equal([0, 1, 2, 3], Sources.Families.Select(f => (int)f.Family));
    }

    [Fact]
    public void EachSourceSaysWhatItCannotDo()
    {
        foreach (var s in Sources.Read)
        {
            Assert.False(string.IsNullOrWhiteSpace(s.ColumnName));
            Assert.False(string.IsNullOrWhiteSpace(s.ReportName));
            Assert.False(string.IsNullOrWhiteSpace(s.ExportName));
            Assert.False(string.IsNullOrWhiteSpace(s.SectionName));
            Assert.False(string.IsNullOrWhiteSpace(s.AppName));
            Assert.False(string.IsNullOrWhiteSpace(s.MissingText));
            if (s.Open is OpenRoute.None or OpenRoute.WebLink or OpenRoute.AppOnly)
                Assert.True(s.OpenWords is not null, $"{s.Backend} opens without a link of Claude's or ChatGPT's: its words are needed");
            if (s.Resume == ResumeRoute.None)
            {
                Assert.False(string.IsNullOrWhiteSpace(s.NoResumeLabel), s.Backend);
                Assert.False(string.IsNullOrWhiteSpace(s.NoResumeTooltip), s.Backend);
            }
            if (s.Changes == ChangesSupport.None)
            {
                Assert.False(string.IsNullOrWhiteSpace(s.NoChangesSummary), s.Backend);
                Assert.False(string.IsNullOrWhiteSpace(s.NoChangesText), s.Backend);
            }
            // Words for each standing its rule can give.
            AppStanding[] standings = s.Standing switch
            {
                StandingRule.ArchivedOrListed => [AppStanding.Active, AppStanding.Archived],
                StandingRule.TerminalTranscript => [AppStanding.EarlierPart, AppStanding.TerminalOnly],
                StandingRule.Imported => [AppStanding.Imported],
                _ => [],
            };
            Assert.All(standings, st => Assert.True(s.StateTexts.ContainsKey(st), $"{s.Backend}: no words for {st}"));
            if (s.Standing == StandingRule.AppState) Assert.NotEmpty(s.StateTexts);
            Assert.Equal(s.CopyFamily is null, s.CopyKey == CopyKey.None);
        }
        // A source a newer version wrote is named by its id and offers nothing.
        var unknown = Sources.Describe("future-app");
        Assert.Equal("future-app", unknown.ColumnName);
        Assert.Equal(OpenRoute.None, unknown.Open);
        Assert.Equal(ResumeRoute.None, unknown.Resume);
    }

    private static SessionMetadata Row(string backend, int i, NativeArchiveState state, bool continued = false) => new()
    {
        Key = new ConversationKey(backend, "acc", "org", $"{backend}-{i:D3}"),
        Title = $"对话 {i}",
        SearchKey = $"对话 {i}",
        SourceRevision = "1",
        LastActivityUtc = new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero).AddMinutes(i),
        NativeArchiveState = state,
        ContinuedIn = continued ? "desktop-1" : null,
    };

    [Fact]
    public void TheCatalogSortsEverySourceAndStandingAsTheColumnsShow()
    {
        using var catalog = Catalog();
        var rows = new List<SessionMetadata>();
        var i = 0;
        foreach (var source in Sources.Read)
            foreach (var state in Enum.GetValues<NativeArchiveState>())
                foreach (var continued in new[] { false, true })
                    rows.Add(Row(source.Backend, i++, state, continued));
        catalog.UpsertRange(rows);

        List<SessionMetadata> Sorted(SortField field) =>
            catalog.Page(Visibility.Active, null, null, 1000, sort: new SortOrder(field, false)).Rows.ToList();

        var bySource = Sorted(SortField.Source).Select(r => OrderingRules.SourceRank(r.Key.Backend)).ToList();
        Assert.Equal(rows.Count, bySource.Count);
        Assert.Equal(bySource.Order(), bySource);
        var byStanding = Sorted(SortField.AppState)
            .Select(r => (int)OrderingRules.Standing(r.Key.Backend, r.NativeArchiveState, r.ContinuedIn is not null)).ToList();
        Assert.Equal(rows.Count, byStanding.Count);
        Assert.Equal(byStanding.Order(), byStanding);
        Assert.Equal([1, 2, 3, 4, 5, 6], byStanding.Distinct().Order());       // every standing, from every source's rule
    }

    [Fact]
    public void TheAppFilterTakesEachSourceInExactlyOneFamily()
    {
        using var catalog = Catalog();
        catalog.UpsertRange(Sources.Read.Select((s, i) => Row(s.Backend, i, NativeArchiveState.Active)).ToList());
        IEnumerable<string> Listed(BackendFilter filter) =>
            catalog.Page(Visibility.Active, null, null, 1000, backend: filter).Rows.Select(r => r.Key.Backend);

        var seen = new List<string>();
        foreach (var family in Sources.Families)
        {
            var listed = Listed(new BackendFilter(family.Family)).Order().ToList();
            Assert.Equal(Sources.BackendsOf(family.Family).Order(), listed);
            seen.AddRange(listed);
        }
        Assert.Equal(Sources.Read.Select(s => s.Backend).Order(), seen.Order());
        Assert.Equal(Sources.Read.Count, Listed(BackendFilter.All).Count());
        Assert.Equal("b:All", BackendFilter.All.Fingerprint);                  // remembered names stay as they were
        Assert.Equal("Gemini", BackendFilter.Gemini.Name);
    }

    [Fact]
    public void CopiesAndMissingTextsComeFromTheSource()
    {
        SessionMetadata Of(string backend, string? cli = null) => new()
        {
            Key = new ConversationKey(backend, "acc", "org", "conv-1"),
            CliSessionId = cli,
            Title = "t",
            SearchKey = "t",
            SourceRevision = "1",
        };
        Assert.Equal(("claude", "cli-9"), TranscriptArchive.IdentityOf(Of(ConversationKey.DesktopCode, "cli-9")));
        Assert.Null(TranscriptArchive.IdentityOf(Of(ConversationKey.DesktopCode)));       // no terminal session: nothing kept
        Assert.Equal(("claude", "conv-1"), TranscriptArchive.IdentityOf(Of(ConversationKey.CliCode)));
        Assert.Equal(("codex", "conv-1"), TranscriptArchive.IdentityOf(Of(ConversationKey.Codex)));
        Assert.Equal(("dsh", "conv-1"), TranscriptArchive.IdentityOf(Of(ConversationKey.DeepSeek)));
        Assert.Null(TranscriptArchive.IdentityOf(Of(ConversationKey.GeminiWeb)));         // an import is this tool's own already

        // Deleted: the desktop row picked, and the terminal transcripts the delete took with it.
        var desktop = Of(ConversationKey.DesktopCode, "cli-9");
        var twin = new ConversationKey(ConversationKey.CliCode, "acc", "org", "cli-9");
        var resumed = new ConversationKey(ConversationKey.CliCode, "acc", "org", "cli-7");
        Assert.Equal(new[] { ("claude", "cli-9"), ("claude", "cli-7") },
            TranscriptArchive.IdentitiesOf([desktop], [desktop.Key, twin, resumed]));
        Assert.Equal(new[] { ("codex", "conv-1") }, TranscriptArchive.IdentitiesOf([Of(ConversationKey.Codex)], []));

        foreach (var s in Sources.Read.Where(s => s.Backend != ConversationKey.DesktopCode))
            Assert.Equal(s.MissingText, TranscriptLocator.ExplainMissing(Of(s.Backend)));
        Assert.Contains("没有关联的 CLI transcript", TranscriptLocator.ExplainMissing(Of(ConversationKey.DesktopCode)));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
    }
}
