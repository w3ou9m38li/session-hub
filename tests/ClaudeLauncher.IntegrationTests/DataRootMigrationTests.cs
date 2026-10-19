using System.Text;
using ClaudeLauncher.Core.Contracts;
using ClaudeLauncher.Infrastructure;
using ClaudeLauncher.Infrastructure.Catalog;

namespace ClaudeLauncher.IntegrationTests;

/// <summary>
/// The move carries the user's own catalog — their groups and archive choices. A half-done
/// move that deleted the original would lose all of it, so each guard is pinned here.
/// </summary>
public sealed class DataRootMigrationTests : IDisposable
{
    private readonly string _dir;
    private readonly string _legacy;
    private readonly string _target;

    public DataRootMigrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "launcher-migrate", Guid.NewGuid().ToString("n"));
        _legacy = Path.Combine(_dir, "old");
        _target = Path.Combine(_dir, "new");
        Directory.CreateDirectory(_legacy);
    }

    private void WriteLegacy(string relative, string content)
    {
        var path = Path.Combine(_legacy, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    [Fact]
    public void EverythingIsCarriedOverAndTheOldFolderGoes()
    {
        WriteLegacy("catalog.sqlite", "not really a database");
        WriteLegacy("export/全部对话.csv", "标题\n会话");
        WriteLegacy("memory-drafts/abc.md", "# 草稿");

        var report = DataRootMigrator.Migrate(_legacy, _target);

        Assert.Equal(MigrationOutcome.Moved, report.Outcome);
        Assert.Equal(3, report.Files);
        Assert.True(File.Exists(Path.Combine(_target, "catalog.sqlite")));
        Assert.Equal("标题\n会话", File.ReadAllText(Path.Combine(_target, "export", "全部对话.csv")));
        Assert.False(Directory.Exists(_legacy));
    }

    [Fact]
    public void AnExistingCatalogAtTheDestinationIsNeverMergedInto()
    {
        WriteLegacy("catalog.sqlite", "old");
        Directory.CreateDirectory(_target);
        File.WriteAllText(Path.Combine(_target, "catalog.sqlite"), "already in use");

        var report = DataRootMigrator.Migrate(_legacy, _target);

        Assert.Equal(MigrationOutcome.NothingToDo, report.Outcome);
        Assert.Equal("already in use", File.ReadAllText(Path.Combine(_target, "catalog.sqlite")));
        Assert.True(Directory.Exists(_legacy));          // left for the user to look at
        Assert.NotNull(report.Message);
    }

    [Fact]
    public void ARootNamedByTheVariableIsNeverMovedInto()
    {
        // A copy run beside the everyday one, in a folder thrown away afterwards. On a machine
        // without the data drive the old folder is the everyday data: moved there, it would go too.
        WriteLegacy("catalog.sqlite", "everyday");
        WriteLegacy("archive/copies/codex/a.jsonl.zst", "a copy");

        var report = DataRootMigrator.Migrate(_legacy, _target, newRootFromVariable: true);

        Assert.Equal(MigrationOutcome.NothingToDo, report.Outcome);
        Assert.Equal("everyday", File.ReadAllText(Path.Combine(_legacy, "catalog.sqlite")));
        Assert.True(File.Exists(Path.Combine(_legacy, "archive", "copies", "codex", "a.jsonl.zst")));
        Assert.False(File.Exists(Path.Combine(_target, "catalog.sqlite")));
    }

    [Fact]
    public void ARootNamedByTheVariableIsNotUsedWhileAMoveIntoItIsUnfinished()
    {
        // A move into the folder was cut short, then the variable named that folder. What a run wrote
        // there would be cleared when the everyday window takes the move up: until then it works in the
        // old folder, which that move carries over — and nothing here is touched.
        WriteLegacy("catalog.sqlite", "the old catalog");
        Directory.CreateDirectory(_target);
        var marker = Path.Combine(_target, DataRootMigrator.MarkerFileName);
        File.WriteAllText(marker, _legacy);
        File.WriteAllText(Path.Combine(_target, "catalog.sqlite"), "half a catalog");

        foreach (var report in new[]
                 {
                     DataRootMigrator.Due(_legacy, _target, newRootFromVariable: true),
                     DataRootMigrator.Migrate(_legacy, _target, newRootFromVariable: true),
                 })
        {
            Assert.Equal(MigrationOutcome.Pending, report.Outcome);
            Assert.Equal(_legacy, DataRootMigrator.OldRootKept(report));
        }
        Assert.True(File.Exists(marker));
        Assert.Equal("half a catalog", File.ReadAllText(Path.Combine(_target, "catalog.sqlite")));
        Assert.Equal("the old catalog", File.ReadAllText(Path.Combine(_legacy, "catalog.sqlite")));
    }

    [Fact]
    public void WhatWasSetAsideDoesNotHoldTheMoveOff()
    {
        // An attempt that set files aside and then failed leaves only those behind: the next start
        // moves again, and the files set aside stay where they are, for the user.
        WriteLegacy("catalog.sqlite", "the user's catalog");
        var aside = Path.Combine(_target, ".before-move-20261015-010203", "export", "清单.csv");
        Directory.CreateDirectory(Path.GetDirectoryName(aside)!);
        File.WriteAllText(aside, "written since");

        Assert.Equal(MigrationOutcome.Pending, DataRootMigrator.Due(_legacy, _target).Outcome);
        Assert.Equal(MigrationOutcome.Moved, DataRootMigrator.Migrate(_legacy, _target).Outcome);
        Assert.Equal("the user's catalog", File.ReadAllText(Path.Combine(_target, "catalog.sqlite")));
        Assert.Equal("written since", File.ReadAllText(aside));
    }

    [Fact]
    public void ANewRootHoldingDataWithoutACatalogIsNotCopiedOver()
    {
        // The catalog was deleted, to be rebuilt; the copies beside it are newer than the old folder's.
        WriteLegacy("catalog.sqlite", "old");
        WriteLegacy("archive/copies/codex/a.jsonl.zst", "old copy");
        var newer = Path.Combine(_target, "archive", "copies", "codex", "a.jsonl.zst");
        Directory.CreateDirectory(Path.GetDirectoryName(newer)!);
        File.WriteAllText(newer, "the newer, longer copy");

        var report = DataRootMigrator.Migrate(_legacy, _target);

        Assert.Equal(MigrationOutcome.NothingToDo, report.Outcome);
        Assert.Equal("the newer, longer copy", File.ReadAllText(newer));
        Assert.False(File.Exists(Path.Combine(_target, "catalog.sqlite")));
        Assert.True(Directory.Exists(_legacy));          // left for the user to look at
    }

    [Fact]
    public void AProbeLeftByTheWritableCheckIsNotDataInUse()
    {
        WriteLegacy("catalog.sqlite", "db");
        Directory.CreateDirectory(_target);
        File.WriteAllText(Path.Combine(_target, ".write-probe"), "");

        Assert.Equal(MigrationOutcome.Moved, DataRootMigrator.Migrate(_legacy, _target).Outcome);
    }

    [Fact]
    public void AProbeAnotherProcessLeftIsNotDataInUseEither()
    {
        WriteLegacy("catalog.sqlite", "db");
        Directory.CreateDirectory(_target);
        File.WriteAllText(Path.Combine(_target, ".write-probe-4242"), "");

        Assert.Equal(MigrationOutcome.Pending, DataRootMigrator.Due(_legacy, _target).Outcome);
        Assert.Equal(MigrationOutcome.Moved, DataRootMigrator.Migrate(_legacy, _target).Outcome);
    }

    [Fact]
    public void ProcessesStartingTogetherEachProbeTheirOwnFile()
    {
        Assert.Equal(".write-probe-" + Environment.ProcessId, LauncherPaths.WriteProbeName);

        // Another process in the middle of its probe, and an older version in the middle of its own.
        Directory.CreateDirectory(_target);
        using var other = new FileStream(Path.Combine(_target, ".write-probe-4242"), FileMode.Create, FileAccess.Write, FileShare.None);
        using var older = new FileStream(Path.Combine(_target, ".write-probe"), FileMode.Create, FileAccess.Write, FileShare.None);

        Assert.True(LauncherPaths.CanHostData(_target));
        Assert.False(File.Exists(Path.Combine(_target, LauncherPaths.WriteProbeName)));     // its own is gone again
    }

    [Fact]
    public void AMoveCutShortIsTakenBackAndBegunAnew()
    {
        WriteLegacy("archive/copies/codex/a.jsonl.zst", "a copy");
        WriteLegacy("chatgpt-web/conversations/session-1.json", "an import");
        WriteLegacy("catalog.sqlite", "the user's catalog");
        WriteLegacy("catalog.sqlite-wal", "its log");

        // Its log cannot be read: everything else is in place by then, and goes again.
        using (new FileStream(Path.Combine(_legacy, "catalog.sqlite-wal"), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.ThrowsAny<IOException>(() => DataRootMigrator.Migrate(_legacy, _target));

        Assert.Empty(Directory.EnumerateFileSystemEntries(_target));     // as the attempt found it
        Assert.Equal("the user's catalog", File.ReadAllText(Path.Combine(_legacy, "catalog.sqlite")));

        var again = DataRootMigrator.Migrate(_legacy, _target);

        Assert.Equal(MigrationOutcome.Moved, again.Outcome);
        Assert.Equal("the user's catalog", File.ReadAllText(Path.Combine(_target, "catalog.sqlite")));
        Assert.Equal("a copy", File.ReadAllText(Path.Combine(_target, "archive", "copies", "codex", "a.jsonl.zst")));
        Assert.False(File.Exists(Path.Combine(_target, DataRootMigrator.MarkerFileName)));
        Assert.False(Directory.Exists(_legacy));
    }

    [Fact]
    public void WhatAMoveCutOffLeftIsClearedBeforeItIsTakenUp()
    {
        // The process ended part way: its marker and its partial copy are still there. The old
        // folder went on meanwhile — an import removed, the archive's log folded in.
        WriteLegacy("archive/archive.sqlite", "the archive, its log folded in, and more");
        WriteLegacy("catalog.sqlite", "the user's catalog");
        Directory.CreateDirectory(Path.Combine(_target, "archive"));
        Directory.CreateDirectory(Path.Combine(_target, "chatgpt-web", "conversations"));
        var marker = Path.Combine(_target, DataRootMigrator.MarkerFileName);
        File.WriteAllText(marker, _legacy);
        var copies = new[]
        {
            Path.Combine(_target, "archive", "archive.sqlite"),
            Path.Combine(_target, "archive", "archive.sqlite-wal"),
            Path.Combine(_target, "chatgpt-web", "conversations", "session-1.json"),
        };
        File.WriteAllText(copies[0], "the archive");
        File.WriteAllText(copies[1], "its log, not yet folded in");
        File.WriteAllText(copies[2], "an import since removed");
        // A copy keeps its original's write time, from before the move began.
        foreach (var copy in copies) File.SetLastWriteTimeUtc(copy, File.GetLastWriteTimeUtc(marker).AddHours(-1));
        // Written after the move began: not the move's, so kept, out of the way.
        var later = Path.Combine(_target, "export", "清单.csv");
        Directory.CreateDirectory(Path.GetDirectoryName(later)!);
        File.WriteAllText(later, "written since");
        File.SetLastWriteTimeUtc(later, File.GetLastWriteTimeUtc(marker).AddMinutes(1));

        var report = DataRootMigrator.Migrate(_legacy, _target);

        Assert.Equal(MigrationOutcome.Moved, report.Outcome);
        Assert.Contains(".before-move-", report.Message);     // the window says where they went
        // The old folder, and the later file set aside: the removed import does not come back, and
        // SQLite finds no old log to replay over the newer archive.
        var entries = Directory.EnumerateFileSystemEntries(_target).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(3, entries.Count);
        Assert.Equal(["archive", "catalog.sqlite"], entries.Skip(1));
        Assert.StartsWith(".before-move-", entries[0]);
        Assert.Equal("written since", File.ReadAllText(Path.Combine(_target, entries[0]!, "export", "清单.csv")));
        Assert.Equal(["archive.sqlite"], Directory.EnumerateFileSystemEntries(Path.Combine(_target, "archive")).Select(Path.GetFileName));
        Assert.Equal("the archive, its log folded in, and more",
            File.ReadAllText(Path.Combine(_target, "archive", "archive.sqlite")));
    }

    [Fact]
    public void LeftoversThatCannotGoYetWaitForTheNextStart()
    {
        WriteLegacy("catalog.sqlite", "the user's catalog");
        Directory.CreateDirectory(_target);
        File.WriteAllText(Path.Combine(_target, DataRootMigrator.MarkerFileName), _legacy);
        var leftover = Path.Combine(_target, "catalog.sqlite");
        File.WriteAllText(leftover, "half a catalog");

        using (new FileStream(leftover, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.ThrowsAny<IOException>(() => DataRootMigrator.Migrate(_legacy, _target));
        Assert.True(File.Exists(Path.Combine(_target, DataRootMigrator.MarkerFileName)));
        Assert.Equal("the user's catalog", File.ReadAllText(Path.Combine(_legacy, "catalog.sqlite")));

        Assert.Equal(MigrationOutcome.Moved, DataRootMigrator.Migrate(_legacy, _target).Outcome);
        Assert.Equal("the user's catalog", File.ReadAllText(leftover));
    }

    [Fact]
    public void ADriveWithoutRoomIsNotStartedOn()
    {
        // Gigabytes copied at every start before the window shows, only to fill the drive again.
        WriteLegacy("catalog.sqlite", "the user's catalog");
        WriteLegacy("archive/copies/codex/a.jsonl.zst", new string('x', 4096));

        var report = DataRootMigrator.Migrate(_legacy, _target, newRootFromVariable: false, freeSpace: _ => 1024);

        Assert.Equal(MigrationOutcome.Failed, report.Outcome);
        Assert.Equal(0, report.Files);
        Assert.False(Directory.Exists(_target) && Directory.EnumerateFileSystemEntries(_target).Any());
        Assert.Equal(_legacy, DataRootMigrator.OldRootKept(report));
        Assert.True(File.Exists(Path.Combine(_legacy, "archive", "copies", "codex", "a.jsonl.zst")));

        Assert.Equal(MigrationOutcome.Moved,
            DataRootMigrator.Migrate(_legacy, _target, newRootFromVariable: false, freeSpace: _ => null).Outcome);
    }

    [Fact]
    public void EmptyFoldersAreNotDataInUse()
    {
        // A run that only made its folders, writing nothing yet, does not hold the move off.
        WriteLegacy("catalog.sqlite", "the user's catalog");
        Directory.CreateDirectory(Path.Combine(_target, "export"));
        Directory.CreateDirectory(Path.Combine(_target, "memory-drafts"));

        Assert.Equal(MigrationOutcome.Moved, DataRootMigrator.Migrate(_legacy, _target).Outcome);
        Assert.Equal("the user's catalog", File.ReadAllText(Path.Combine(_target, "catalog.sqlite")));
    }

    [Fact]
    public void ARunBesideTheWindowNeverMovesAndUsesTheFolderTheWindowDoes()
    {
        WriteLegacy("catalog.sqlite", "the user's catalog");

        // Not moved yet, or a move cut short: the window works on the old folder until it has moved.
        var due = DataRootMigrator.Due(_legacy, _target);
        Assert.Equal(MigrationOutcome.Pending, due.Outcome);
        Assert.Equal(_legacy, DataRootMigrator.OldRootKept(due));
        Directory.CreateDirectory(_target);
        File.WriteAllText(Path.Combine(_target, DataRootMigrator.MarkerFileName), _legacy);
        File.WriteAllText(Path.Combine(_target, "catalog.sqlite"), "half a catalog");
        Assert.Equal(MigrationOutcome.Pending, DataRootMigrator.Due(_legacy, _target).Outcome);
        Assert.Equal("half a catalog", File.ReadAllText(Path.Combine(_target, "catalog.sqlite")));
        Assert.Equal("the user's catalog", File.ReadAllText(Path.Combine(_legacy, "catalog.sqlite")));

        // The new root in use, or one named by the variable: that is where it reads.
        File.Delete(Path.Combine(_target, DataRootMigrator.MarkerFileName));
        Assert.Equal(MigrationOutcome.NothingToDo, DataRootMigrator.Due(_legacy, _target).Outcome);
        Assert.Equal(MigrationOutcome.NothingToDo, DataRootMigrator.Due(_legacy, Path.Combine(_dir, "empty"), newRootFromVariable: true).Outcome);
        Assert.Null(DataRootMigrator.OldRootKept(DataRootMigrator.Due(_legacy, _target)));
    }

    [Fact]
    public void AMoveCutShortKeepsThisRunOnTheOldFolder()
    {
        // Until the next start takes the move up again, the old folder is the only whole copy. A run
        // on the new root would write there what that attempt then copies the old files over.
        WriteLegacy("catalog.sqlite", "the user's catalog");
        var failed = new MigrationReport(MigrationOutcome.Failed, 0, _legacy, _target, "磁盘已满");

        Assert.Equal(_legacy, DataRootMigrator.OldRootKept(failed));
        Assert.Equal(_legacy, DataRootMigrator.OldRootKept(failed with { Outcome = MigrationOutcome.Pending }));
        foreach (var outcome in new[] { MigrationOutcome.NothingToDo, MigrationOutcome.Moved, MigrationOutcome.CopiedOldKept })
            Assert.Null(DataRootMigrator.OldRootKept(new MigrationReport(outcome, 1, _legacy, _target, null)));

        // No old folder to keep to: the new root it is.
        Directory.Delete(_legacy, recursive: true);
        Assert.Null(DataRootMigrator.OldRootKept(failed));
    }

    [Fact]
    public void ARealCatalogArrivesWithEveryCommitAndNoStaleSidecar()
    {
        // The catalog is written ahead of the database file, so a move that copied only
        // catalog.sqlite could land at the far end missing the user's most recent groups.
        var row = new SessionMetadata
        {
            Key = new ConversationKey(ConversationKey.CliCode, "acc", "org", "session-1"),
            Title = "会话", SearchKey = "会话", SourceRevision = "1:1",
            LastActivityUtc = DateTimeOffset.UtcNow,
            NativeArchiveState = NativeArchiveState.Unsupported,
            SourceCoverage = SourceCoverage.Resolved,
        };
        using (var catalog = new SqliteCatalog(Path.Combine(_legacy, "catalog.sqlite")))
        {
            catalog.Initialize(CatalogSchema());
            catalog.UpsertRange([row]);
            catalog.AssignGroup([row], "小麦");
        }

        var report = DataRootMigrator.Migrate(_legacy, _target);

        Assert.Equal(MigrationOutcome.Moved, report.Outcome);
        Assert.False(File.Exists(Path.Combine(_target, "catalog.sqlite-shm")));

        using var moved = new SqliteCatalog(Path.Combine(_target, "catalog.sqlite"));
        moved.Initialize(CatalogSchema());
        var carried = moved.Page(Visibility.Active).Rows;
        Assert.Single(carried);
        Assert.Equal("小麦", carried[0].LocalGroup);
    }

    private static string CatalogSchema()
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

    [Fact]
    public void NoOldFolderIsNotAFailure()
    {
        var report = DataRootMigrator.Migrate(Path.Combine(_dir, "missing"), _target);
        Assert.Equal(MigrationOutcome.NothingToDo, report.Outcome);
    }

    [Fact]
    public void AMarkerWithNothingLeftToMoveGoes()
    {
        // A move stopped short, then the old folder was deleted by hand. A folder of that name
        // made later must not be moved over the data the new root has held since.
        Directory.CreateDirectory(_target);
        File.WriteAllText(Path.Combine(_target, DataRootMigrator.MarkerFileName), _legacy);
        File.WriteAllText(Path.Combine(_target, "catalog.sqlite"), "in use since");
        Directory.Delete(_legacy);

        Assert.Equal(MigrationOutcome.NothingToDo, DataRootMigrator.Migrate(_legacy, _target).Outcome);
        Assert.False(File.Exists(Path.Combine(_target, DataRootMigrator.MarkerFileName)));

        WriteLegacy("catalog.sqlite", "a later, emptier one");
        Assert.Equal(MigrationOutcome.NothingToDo, DataRootMigrator.Migrate(_legacy, _target).Outcome);
        Assert.Equal("in use since", File.ReadAllText(Path.Combine(_target, "catalog.sqlite")));
    }

    [Fact]
    public void MigratingOntoItselfDoesNothing()
    {
        WriteLegacy("catalog.sqlite", "db");

        var report = DataRootMigrator.Migrate(_legacy, _legacy + Path.DirectorySeparatorChar);

        Assert.Equal(MigrationOutcome.NothingToDo, report.Outcome);
        Assert.True(File.Exists(Path.Combine(_legacy, "catalog.sqlite")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
    }
}
