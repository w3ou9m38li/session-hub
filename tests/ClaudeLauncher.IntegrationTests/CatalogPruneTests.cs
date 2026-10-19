using ClaudeLauncher.Core.Contracts;
using ClaudeLauncher.Infrastructure.Catalog;

namespace ClaudeLauncher.IntegrationTests;

/// <summary>
/// The list only ever grows by itself: a conversation whose record an app deleted, cleaned up
/// or moved stays listed, marked as gone from the app. It leaves the list only when the user
/// deletes it here — and then it stays deleted, even while the app still has it.
/// </summary>
public sealed class CatalogPruneTests : IDisposable
{
    private readonly string _dir;
    private readonly SqliteCatalog _catalog;

    public CatalogPruneTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "launcher-prune", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
        _catalog = new SqliteCatalog(Path.Combine(_dir, "catalog.sqlite"));
        _catalog.Initialize(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "CatalogSchema.sql")));
    }

    private static SessionMetadata Row(string backend, string id) => new()
    {
        Key = new ConversationKey(backend, "acc", "org", id),
        Title = id,
        SearchKey = id,
        SourceRevision = "1:1",
        LastActivityUtc = DateTimeOffset.UtcNow,
        NativeArchiveState = NativeArchiveState.Unsupported,
        SourceCoverage = SourceCoverage.Resolved,
    };

    private static ConversationKey Key(string backend, string id) => new(backend, "acc", "org", id);

    private List<SessionMetadata> Listed() =>
        [.. _catalog.Page(Visibility.Active, null, null, 100).Rows.OrderBy(r => r.Key.NativeSessionId)];

    [Fact]
    public void RowsAbsentFromAFullReadStayListedMarkedAsGone()
    {
        _catalog.UpsertRange([
            Row(ConversationKey.DesktopCode, "local_a"),
            Row(ConversationKey.DesktopCode, "local_b"),
            Row(ConversationKey.CliCode, "a"),
        ]);
        var before = _catalog.CurrentRevision();

        // The desktop source now contains only local_b; the CLI source is not being read.
        Assert.Equal(1, _catalog.MarkGone(ConversationKey.DesktopCode, [Key(ConversationKey.DesktopCode, "local_b")]));

        var rows = Listed();
        Assert.Equal(["a", "local_a", "local_b"], rows.Select(r => r.Key.NativeSessionId).ToArray());
        Assert.NotNull(rows.Single(r => r.Key.NativeSessionId == "local_a").SourceGoneUtc);
        Assert.All(rows.Where(r => r.Key.NativeSessionId != "local_a"), r => Assert.Null(r.SourceGoneUtc));
        Assert.True(_catalog.CurrentRevision() > before);                 // paging cursors learn of it

        // Found gone once: a second read that still lacks it changes nothing.
        var marked = _catalog.CurrentRevision();
        Assert.Equal(0, _catalog.MarkGone(ConversationKey.DesktopCode, [Key(ConversationKey.DesktopCode, "local_b")]));
        Assert.Equal(marked, _catalog.CurrentRevision());
    }

    [Fact]
    public void ARowSeenAgainIsNoLongerGone()
    {
        _catalog.UpsertRange([Row(ConversationKey.Codex, "t")]);
        _catalog.MarkGone(ConversationKey.Codex, []);
        Assert.NotNull(Listed().Single().SourceGoneUtc);

        Assert.Equal(1, _catalog.UpsertRange([Row(ConversationKey.Codex, "t")]));     // the same row, back
        Assert.Null(Listed().Single().SourceGoneUtc);
    }

    [Fact]
    public void KeysOfOtherBackendsInThePresentListDoNotCountAsPresent()
    {
        _catalog.UpsertRange([Row(ConversationKey.DesktopCode, "x")]);

        // A CLI key with the same native id is a different conversation.
        Assert.Equal(1, _catalog.MarkGone(ConversationKey.DesktopCode, [Key(ConversationKey.CliCode, "x")]));
        Assert.NotNull(Listed().Single().SourceGoneUtc);
    }

    [Fact]
    public void NothingMissingIsANoOpThatKeepsTheRevision()
    {
        _catalog.UpsertRange([Row(ConversationKey.CliCode, "a")]);
        var before = _catalog.CurrentRevision();

        Assert.Equal(0, _catalog.MarkGone(ConversationKey.CliCode, [Key(ConversationKey.CliCode, "a")]));
        Assert.Equal(before, _catalog.CurrentRevision());
    }

    /// <summary>
    /// As a sync keys them: a desktop session under the account and organization folders it was
    /// read from, its terminal transcript under the Claude Code home.
    /// </summary>
    private static SessionMetadata DesktopSession(string id, string cli) => Row(ConversationKey.DesktopCode, id) with
    {
        Key = new ConversationKey(ConversationKey.DesktopCode,
            "6d1f0c2e-0000-4000-8000-00000000acc1", "9b2e7d4a-0000-4000-8000-00000000a0a1", id),
        CliSessionId = cli,
    };

    private static SessionMetadata Terminal(string id) =>
        Row(ConversationKey.CliCode, id) with { Key = CatalogSyncService.CliKey(id), CliSessionId = id };

    [Fact]
    public void ADeletedConversationLeavesWithWhatWasFoldedIntoItAndIsNotListedAgain()
    {
        var desktop = DesktopSession("local_d", "cli-1");
        var folded = Terminal("cli-1") with { MergedInto = "local_d" };
        _catalog.UpsertRange([desktop, folded, Terminal("keep")]);

        var deleted = _catalog.Delete([desktop.Key]);

        Assert.Equal([desktop.Key, folded.Key], deleted.ToArray());
        Assert.Equal(["keep"], Listed().Select(r => r.Key.NativeSessionId).ToArray());
        // The app still has it: a sync offers it again, and it is not taken back.
        Assert.Equal(0, _catalog.UpsertRange([desktop, folded]));
        Assert.Equal(["keep"], Listed().Select(r => r.Key.NativeSessionId).ToArray());
        // Nor once the transcript is no longer folded: Claude carried the conversation on in a
        // new one, or a sync could not read the desktop record.
        Assert.Equal(0, _catalog.UpsertRange([folded with { MergedInto = null }]));
        Assert.Equal(0, _catalog.UpsertRange([folded with { MergedInto = null, ContinuedIn = "local_d" }]));
        Assert.Equal(["keep"], Listed().Select(r => r.Key.NativeSessionId).ToArray());
    }

    [Fact]
    public void ADeletedDesktopSessionTakesItsTerminalTranscriptAlongWhenItIsNotFoldedAtThatMoment()
    {
        // The last sync could not read the desktop record: the twin is listed on its own for now.
        var desktop = DesktopSession("local_e", "cli-2");
        var twin = Terminal("cli-2");
        var never = DesktopSession("local_f", "cli-3");          // its transcript was never read
        _catalog.UpsertRange([desktop, twin, never, Terminal("keep")]);

        var deleted = _catalog.Delete([desktop.Key, never.Key]);

        Assert.Equal([desktop.Key, twin.Key, never.Key], deleted.ToArray());
        Assert.Equal(["keep"], Listed().Select(r => r.Key.NativeSessionId).ToArray());
        Assert.Equal(0, _catalog.UpsertRange([desktop, twin with { MergedInto = "local_e" }, never, Terminal("cli-3")]));
        Assert.Equal(["keep"], Listed().Select(r => r.Key.NativeSessionId).ToArray());
    }

    [Fact]
    public void AnImportRemovedOnPurposeLeavesAndComesBackWithTheNextImport()
    {
        _catalog.UpsertRange([Row(ConversationKey.GeminiWeb, "w1"), Row(ConversationKey.GeminiWeb, "w2"), Row(ConversationKey.Codex, "c")]);

        Assert.Equal(2, _catalog.Forget(ConversationKey.GeminiWeb));
        Assert.Equal(["c"], Listed().Select(r => r.Key.NativeSessionId).ToArray());

        Assert.Equal(1, _catalog.UpsertRange([Row(ConversationKey.GeminiWeb, "w1")]));
        Assert.Equal(["c", "w1"], Listed().Select(r => r.Key.NativeSessionId).ToArray());
    }

    [Fact]
    public void ALauncherArchivedRowIsKeptLikeAnyOther()
    {
        var row = Row(ConversationKey.DesktopCode, "local_gone");
        _catalog.UpsertRange([row]);
        var stored = _catalog.Page(Visibility.Active, null, null, 10).Rows.Single();
        _catalog.SetVisibility(stored.Key, Visibility.ArchivedView, stored.PreferenceRevision, "op-1");

        Assert.Equal(1, _catalog.MarkGone(ConversationKey.DesktopCode, []));
        Assert.NotNull(Assert.Single(_catalog.Page(Visibility.ArchivedView, null, null, 10).Rows).SourceGoneUtc);
    }

    public void Dispose()
    {
        _catalog.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
