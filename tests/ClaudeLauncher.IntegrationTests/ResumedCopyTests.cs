using System.Text;
using System.Text.Json;
using ClaudeLauncher.Core.Contracts;
using ClaudeLauncher.Infrastructure.Backends;
using ClaudeLauncher.Infrastructure.Catalog;
using ClaudeLauncher.Infrastructure.Changes;
using ClaudeLauncher.Infrastructure.Search;

namespace ClaudeLauncher.IntegrationTests;

/// <summary>
/// Claude Code's resume starts a new transcript with a copy of the session's history, the
/// copied lines still under the old session's id. A copy nothing was said in is the same
/// conversation twice; one the conversation went on in holds the copy and its own part.
/// </summary>
public sealed class ResumedCopyTests : IDisposable
{
    private const string Origin = "aaaaaaaa-0000-4000-8000-000000000001";
    private const string Silent = "bbbbbbbb-0000-4000-8000-000000000002";
    private const string Talking = "cccccccc-0000-4000-8000-000000000003";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "launcher-resume", Guid.NewGuid().ToString("n"));
    private readonly string _projects;

    public ResumedCopyTests()
    {
        _projects = Directory.CreateDirectory(Path.Combine(_dir, "config", "projects", "G--demo")).FullName;
    }

    private static string Line(string type, string session, string text = "", string? tool = null) => JsonSerializer.Serialize(
        type is "user" or "assistant"
            ? new
            {
                type,
                sessionId = session,
                cwd = @"G:\demo",
                message = new
                {
                    role = type,
                    content = tool is null
                        ? new object[] { new { type = "text", text } }
                        : [new { type = "tool_use", id = tool, name = "Write", input = new { file_path = @"G:\demo\" + text, content = "x" } }],
                },
            }
            : (object)new { type, sessionId = session, customTitle = "同一段对话", mode = "normal" });

    /// <summary>Resuming <see cref="Origin"/>: its history copied under its own id, then what this session adds.</summary>
    private static string[] Copy(params string[] own) =>
    [
        Line("custom-title", Origin), Line("user", Origin, "第一问 KASP"), Line("assistant", Origin, "第一答"),
        .. own,
    ];

    private string Write(string id, params string[] lines)
    {
        var path = Path.Combine(_projects, id + ".jsonl");
        File.WriteAllText(path, string.Join('\n', lines) + '\n', new UTF8Encoding(false));
        return path;
    }

    private void WriteFamily()
    {
        Write(Origin, Line("custom-title", Origin), Line("user", Origin, "第一问 KASP"), Line("assistant", Origin, "第一答"));
        // Opened with --resume and left again: only the app's own bookkeeping under the new id.
        Write(Silent, Copy(Line("custom-title", Silent), Line("mode", Silent)));
        Write(Talking, Copy(Line("user", Talking, "第二问 引物"), Line("assistant", Talking, "第二答")));
    }

    private List<SessionMetadata> ReadRows() =>
        new CliMetadataReader(Path.Combine(_dir, "config")).Read("local", "default").ToList();

    [Fact]
    public void ACopyIsKnownByTheSessionItsHistoryCameFromAndByWhetherAnythingWasSaidInIt()
    {
        WriteFamily();
        var rows = ReadRows().ToDictionary(r => r.Key.NativeSessionId);

        Assert.Null(rows[Origin].ResumedFrom);
        Assert.Equal(Origin, rows[Silent].ResumedFrom);
        Assert.True(rows[Silent].ResumedSilently);
        Assert.Equal(Origin, rows[Talking].ResumedFrom);
        Assert.False(rows[Talking].ResumedSilently);
        Assert.Equal("同一段对话", rows[Silent].Title);
    }

    [Fact]
    public void ACopyNothingWasSaidInFoldsIntoTheOriginalAndOneWithMoreStaysListed()
    {
        WriteFamily();
        var rows = ReadRows();
        CatalogSyncService.FoldResumedCopies(rows);
        var byId = rows.ToDictionary(r => r.Key.NativeSessionId);

        Assert.Equal(Origin, byId[Silent].MergedInto);
        Assert.Null(byId[Talking].MergedInto);
        Assert.Equal(Origin, byId[Talking].ResumedFrom);
        Assert.Null(byId[Origin].MergedInto);
    }

    private static SessionMetadata Cli(string id, string? resumedFrom = null, bool silent = false) => new()
    {
        Key = new ConversationKey(ConversationKey.CliCode, "local", "default", id),
        CliSessionId = id,
        Title = "t",
        SearchKey = "t",
        SourceRevision = "1:1",
        ResumedFrom = resumedFrom,
        ResumedSilently = silent,
        NativeArchiveState = NativeArchiveState.Unsupported,
    };

    [Fact]
    public void AFoldedCopyGoesToTheRowThatShowsItsOriginalThroughCopiesOfCopies()
    {
        var desktop = new SessionMetadata
        {
            Key = new ConversationKey(ConversationKey.DesktopCode, "acc", "org", "local_d"),
            CliSessionId = "t0", Title = "t", SearchKey = "t", SourceRevision = "1:1",
        };
        var rows = new List<SessionMetadata>
        {
            desktop, Cli("t0"),
            Cli("s1", "t0", silent: true),          // a silent copy of the desktop session's transcript
            Cli("s2", "s1", silent: true),          // ... and a silent copy of that copy
            Cli("k1", "t0"),                         // one the conversation went on in
            Cli("s3", "k1", silent: true),          // a silent copy of that one
            Cli("o1", "gone", silent: true),        // its original is no longer on disk
        };
        CatalogSyncService.FoldTerminalTwins(rows);
        CatalogSyncService.FoldResumedCopies(rows);
        var byId = rows.ToDictionary(r => r.Key.NativeSessionId);

        Assert.Equal("local_d", byId["t0"].MergedInto);
        Assert.Equal("local_d", byId["s1"].MergedInto);
        Assert.Equal("local_d", byId["s2"].MergedInto);
        Assert.Null(byId["k1"].MergedInto);
        Assert.Equal("k1", byId["s3"].MergedInto);
        // All there is of that history now: a conversation of its own, not a copy of anything.
        Assert.Null(byId["o1"].MergedInto);
        Assert.Null(byId["o1"].ResumedFrom);
    }

    [Fact]
    public void TheCatalogLinksACopyAndItsOriginalAndHandsOverWhatWasSetOnAFoldedOne()
    {
        using var catalog = new SqliteCatalog(Path.Combine(_dir, "catalog.sqlite"));
        catalog.Initialize(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "CatalogSchema.sql")));

        // Before the copy was folded, the user had pinned it.
        catalog.UpsertRange([Cli(Origin), Cli(Silent)]);
        catalog.SetPinned([catalog.Page(Visibility.Active, null, null, 10).Rows.Single(r => r.Key.NativeSessionId == Silent)], true);

        var rows = new List<SessionMetadata> { Cli(Origin), Cli(Silent, Origin, silent: true), Cli(Talking, Origin) };
        CatalogSyncService.FoldResumedCopies(rows);
        catalog.UpsertRange(rows);
        Assert.Equal(1, catalog.AdoptMergedPreferences());

        var listed = catalog.Page(Visibility.Active, null, null, 10).Rows.ToDictionary(r => r.Key.NativeSessionId);
        Assert.Equal([Origin, Talking], listed.Keys.Order());
        Assert.True(listed[Origin].LocalPinned);

        var ofOrigin = catalog.Resumes(listed[Origin]);
        Assert.Null(ofOrigin.From);
        Assert.Equal([Talking], ofOrigin.Into.Select(r => r.Key.NativeSessionId));
        Assert.Equal(1, ofOrigin.Folded);

        var ofTalking = catalog.Resumes(listed[Talking]);
        Assert.Equal(Origin, ofTalking.From?.Key.NativeSessionId);
        Assert.Empty(ofTalking.Into);
        Assert.False(catalog.Resumes(Cli("x")).Any);
    }

    [Fact]
    public void OnlyWhatWasSaidInACopyIsIndexedUnderIt()
    {
        WriteFamily();
        using var index = new ContentIndex(Path.Combine(_dir, "index.sqlite"));
        var copy = Path.Combine(_projects, Talking + ".jsonl");

        index.Update([new ContentIndex.Source("copy", copy, new OwnPart(Talking, false))]);
        Assert.Empty(index.Hits("copy", "KASP"));                       // the copied part: the original's
        Assert.Single(index.Hits("copy", "引物"));                      // said here

        // Told to take the copy whole, it starts that conversation over.
        Assert.Equal(1, index.Update([new ContentIndex.Source("copy", copy)]).Indexed);
        Assert.Single(index.Hits("copy", "KASP"));
    }

    [Fact]
    public void OnlyTheChangesMadeInACopyAreItsChanges()
    {
        var path = Write(Talking, Copy(Line("assistant", Talking, "b.txt", tool: "t2")));
        File.AppendAllText(path, Line("assistant", Origin, "a.txt", tool: "t1") + '\n');

        Assert.Equal(2, ChangeScanner.Read(path, codex: false).Count);
        var mine = Assert.Single(ChangeScanner.Read(path, codex: false, part: new OwnPart(Talking, false)));
        Assert.EndsWith("b.txt", mine.Path, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
    }
}
