using Microsoft.Data.Sqlite;

namespace ClaudeLauncher.Infrastructure;

public enum MigrationOutcome
{
    /// <summary>No old folder, or the new root is already the one in use.</summary>
    NothingToDo,
    /// <summary>Everything was copied over and the old folder is gone.</summary>
    Moved,
    /// <summary>Everything was copied over, but the old folder could not be removed.</summary>
    CopiedOldKept,
    /// <summary>
    /// The move stopped short: the old folder is untouched, this run keeps using it, and the next
    /// start takes the move up again.
    /// </summary>
    Failed,
    /// <summary>
    /// A move is due, but this run only works beside the window (<c>--draft</c>, <c>--export</c>) and
    /// never moves anything: it uses the old folder, where the window works until it has moved it.
    /// </summary>
    Pending,
}

/// <param name="Files">How many files were carried over.</param>
public sealed record MigrationReport(
    MigrationOutcome Outcome, int Files, string? From, string? To, string? Message);

/// <summary>
/// Moves this tool's own data folder to wherever <see cref="LauncherPaths"/> now resolves.
///
/// <para>Only this tool's folder is ever touched: the catalog, exports, drafts and evidence.
/// Claude's own data, transcripts included, is never read here and never moved.</para>
///
/// <para>The old folder is deleted only after every file has been copied and checked, and a
/// failure at any point leaves it exactly where it was — an incomplete move that lost the
/// user's archive preferences would be worse than not moving at all.</para>
/// </summary>
public static class DataRootMigrator
{
    private const string CatalogFileName = "catalog.sqlite";

    /// <summary>
    /// Left in the new root from before the first file is copied until the last is checked, and
    /// while what a move cut short copied cannot all be removed again. What sits beside it is that
    /// move's own partial copy — nothing else writes there meanwhile (see <see cref="OldRootKept"/>) —
    /// cleared before the next start copies the old folder whole.
    /// </summary>
    public const string MarkerFileName = ".migrating";

    /// <summary>Room left on the new root's drive besides what the move takes.</summary>
    private const long SpareBytes = 64L * 1024 * 1024;

    private static MigrationReport? _prepared;

    /// <summary>
    /// Migrates if needed, then makes sure the current root's folders exist. Once a run: later
    /// calls return what the first one found.
    /// </summary>
    /// <param name="mayMove">
    /// False for a run beside the window (<c>--draft</c>, <c>--export</c>): it neither copies nor
    /// deletes, and while a move is due it uses the old folder the window works in. Only the window,
    /// holding the single-instance name, moves — a side run that took the move up could delete the
    /// folder under it.
    /// </param>
    public static MigrationReport PrepareDataRoot(bool mayMove = true) =>
        PrepareDataRoot(mayMove, LauncherPaths.LegacyDataRoot);

    /// <param name="legacyRoot">The old folder: a test's own, so that it never meets this machine's.</param>
    internal static MigrationReport PrepareDataRoot(bool mayMove, string legacyRoot)
    {
        if (_prepared is { } prepared) return prepared;

        MigrationReport report;
        try
        {
            // A root handed down by the window (LauncherPaths.UseDataRoot) is used as it is: the
            // window has made of the two folders what there was to make, and a child that judged
            // again could end up in the other one.
            report = LauncherPaths.DataRootGiven
                ? new MigrationReport(MigrationOutcome.NothingToDo, 0, null, LauncherPaths.DataRoot, null)
                : mayMove
                    ? Migrate(legacyRoot, LauncherPaths.DataRoot, LauncherPaths.DataRootFromVariable)
                    : Due(legacyRoot, LauncherPaths.DataRoot, LauncherPaths.DataRootFromVariable);
        }
        catch (Exception ex)
        {
            // A failed migration must not stop the app: the old folder is untouched, this run
            // keeps to it, and the next start takes the move up again.
            report = new MigrationReport(MigrationOutcome.Failed, 0,
                legacyRoot, LauncherPaths.DataRoot, ex.Message);
        }

        if (OldRootKept(report) is { } old) LauncherPaths.KeepOldRoot(old);
        LauncherPaths.EnsureCreated();
        return _prepared = report;
    }

    /// <summary>
    /// The folder this run uses instead of the new root: the old one, when a move into the new root
    /// stopped short or is not this run's to make. Until the move is done the old folder is the
    /// only whole copy, and a run on the new root would put data there that a later move clears.
    /// Null when the new root is the one to use.
    /// </summary>
    public static string? OldRootKept(MigrationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return report.Outcome is MigrationOutcome.Failed or MigrationOutcome.Pending
               && report.From is { } from && Directory.Exists(from)
            ? from : null;
    }

    /// <summary>
    /// What a run that never moves makes of the two folders: <see cref="MigrationOutcome.Pending"/>
    /// when <see cref="Migrate"/> would move the old folder (or take an unfinished move up), else
    /// <see cref="MigrationOutcome.NothingToDo"/>. Nothing is written. A root named by the variable
    /// that holds an unfinished move's marker is not used either: what a run wrote there would be
    /// cleared when the move is taken up.
    /// </summary>
    public static MigrationReport Due(string legacyRoot, string newRoot, bool newRootFromVariable = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(newRoot);

        var from = Path.TrimEndingDirectorySeparator(Path.GetFullPath(legacyRoot));
        var to = Path.TrimEndingDirectorySeparator(Path.GetFullPath(newRoot));
        var due = !string.Equals(from, to, StringComparison.OrdinalIgnoreCase)
                  && Directory.Exists(from)
                  && (File.Exists(Path.Combine(to, MarkerFileName)) || (!newRootFromVariable && !InUse(to)));
        return new MigrationReport(due ? MigrationOutcome.Pending : MigrationOutcome.NothingToDo, 0, from, to, null);
    }

    /// <summary>
    /// Copies <paramref name="legacyRoot"/> into <paramref name="newRoot"/> and removes the
    /// original. Exposed so the whole path can be exercised against temporary folders.
    /// </summary>
    /// <param name="newRootFromVariable">
    /// The new root was named by <see cref="LauncherPaths.DataRootVariable"/>: a copy run beside the
    /// everyday one, often in a folder thrown away afterwards. Nothing is moved into it — on a
    /// machine without the data drive, the old folder is the everyday one.
    /// </param>
    public static MigrationReport Migrate(string legacyRoot, string newRoot, bool newRootFromVariable = false) =>
        Migrate(legacyRoot, newRoot, newRootFromVariable, FreeSpace);

    /// <param name="freeSpace">Bytes free on the drive of a folder, or null when that cannot be asked.</param>
    internal static MigrationReport Migrate(string legacyRoot, string newRoot, bool newRootFromVariable,
        Func<string, long?> freeSpace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(newRoot);

        var from = Path.TrimEndingDirectorySeparator(Path.GetFullPath(legacyRoot));
        var to = Path.TrimEndingDirectorySeparator(Path.GetFullPath(newRoot));

        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
            return new MigrationReport(MigrationOutcome.NothingToDo, 0, from, to, null);

        var marker = Path.Combine(to, MarkerFileName);
        if (!Directory.Exists(from))
        {
            // Nothing left to move: the old folder was deleted after a move stopped short. Its
            // marker goes too, or a later folder of that name would be moved over data in use.
            try { File.Delete(marker); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* tried again next start */ }
            return new MigrationReport(MigrationOutcome.NothingToDo, 0, from, to, null);
        }

        // Never moved into; but while a move into the same folder is unfinished, not used either.
        if (newRootFromVariable) return Due(from, to, newRootFromVariable: true);

        // Anything already in the new root is data in use — a catalog, or copies and imports
        // without one — unless an earlier attempt at this move left it. Merging two catalogs
        // would have to pick a winner for every preference, and copying over the rest would
        // replace newer files with older ones, so the old folder is left for the user.
        var resuming = File.Exists(marker);
        if (!resuming && InUse(to))
            return new MigrationReport(MigrationOutcome.NothingToDo, 0, from, to,
                $"新数据目录已在使用，旧目录未动：{from}");

        // What a move cut short left is its own partial copy of files the old folder has since
        // changed or dropped (an import removed meanwhile would come back): the new root starts
        // over empty, so it ends up exactly the old folder.
        var setAside = resuming ? ClearLeftovers(to, marker) : null;

        // Fold the write-ahead log into the database first, so one file carries every commit
        // even if the -wal is left behind.
        TryCheckpoint(Path.Combine(from, CatalogFileName));

        var files = Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories).Where(f => !IsTransient(f)).ToList();
        var catalog = files.Where(f => IsCatalog(from, f)).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var rest = files.Where(f => !IsCatalog(from, f)).ToList();

        // Not begun on a drive that cannot take it: a move that fills the drive fails at every
        // start, each time after copying gigabytes before the window shows.
        if (Shortfall(to, files, freeSpace) is { } shortfall)
            return new MigrationReport(MigrationOutcome.Failed, 0, from, to, shortfall);

        // Marked before the first file is copied and unmarked once the last is checked: a move cut
        // short by the process ending is cleared and taken up again on the next start.
        Directory.CreateDirectory(to);
        File.WriteAllText(marker, from);

        var placed = new List<string>();        // every file this attempt put in the new root
        var made = new List<string>();          // every folder it made there
        var copied = 0;
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(to, Path.GetRelativePath(from, directory));
                if (Directory.Exists(target)) continue;
                Directory.CreateDirectory(target);
                made.Add(target);
            }
            copied += CopyFiles(from, to, rest, placed);
            var missing = Verify(from, to, rest);
            if (missing is null)
            {
                // The catalog last, once everything it refers to is in place: a move that stops
                // short leaves no catalog behind that passes for the old one carried over.
                copied += CopyFiles(from, to, catalog, placed);
                missing = Verify(from, to, catalog);
            }
            if (missing is not null)
            {
                Undo(marker, placed, made);
                return new MigrationReport(MigrationOutcome.Failed, copied, from, to, $"复制校验失败：{missing}");
            }
            File.Delete(marker);
        }
        catch
        {
            // The new root goes back to how this attempt found it, so the next start begins anew.
            Undo(marker, placed, made);
            throw;
        }

        try
        {
            Directory.Delete(from, recursive: true);
            return new MigrationReport(MigrationOutcome.Moved, copied, from, to, AsideNote(setAside));
        }
        catch (Exception ex)
        {
            return new MigrationReport(MigrationOutcome.CopiedOldKept, copied, from, to,
                AsideNote(setAside) is { } note ? ex.Message + "；" + note : ex.Message);
        }
    }

    /// <summary>
    /// Whether a folder holds a file of its own anywhere: not the probe that proved it writable,
    /// not a move's marker, not the empty folders a run makes before it writes anything, and not
    /// what an earlier attempt set aside (left there for the user, it does not hold the move off).
    /// </summary>
    private static bool InUse(string folder) =>
        Directory.Exists(folder)
        && Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Any(file => !IsOwnMark(file) && !IsAside(folder, file));

    private static string? AsideNote(string? setAside) =>
        setAside is null ? null : $"上次迁移没做完之后写进新目录的文件挪到了 {setAside}";

    private static long? FreeSpace(string folder)
    {
        try
        {
            return new DriveInfo(Path.GetPathRoot(folder)!).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;    // a share, say: copying finds out
        }
    }

    /// <summary>A move's marker, or a probe — any process's, its own name or the fixed one older versions wrote.</summary>
    private static bool IsOwnMark(string file) =>
        Path.GetFileName(file) is var name
        && (name.StartsWith(LauncherPaths.WriteProbeFileName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, MarkerFileName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Where files found in a marked root that the move did not put there are kept.</summary>
    private const string AsidePrefix = ".before-move-";

    /// <summary>
    /// Clears what a move cut short left in <paramref name="to"/>. Its copies keep the old files'
    /// write times, from before the marker was written: those go. A file written there since was
    /// not the move's — some run used this root after all — and is moved aside whole, never
    /// deleted. Throws while any of it cannot go, and the move waits for the next start.
    /// </summary>
    /// <returns>The folder files were moved into, or null when none were.</returns>
    private static string? ClearLeftovers(string to, string marker)
    {
        var since = File.GetLastWriteTimeUtc(marker);
        var aside = Path.Combine(to, AsidePrefix + DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture));
        foreach (var file in Directory.EnumerateFiles(to, "*", SearchOption.AllDirectories)
                     .Where(f => !IsOwnMark(f) && !IsAside(to, f)).ToList())
        {
            if (File.GetLastWriteTimeUtc(file) <= since)
            {
                File.Delete(file);
                continue;
            }
            var kept = Path.Combine(aside, Path.GetRelativePath(to, file));
            Directory.CreateDirectory(Path.GetDirectoryName(kept)!);
            File.Move(file, kept);
        }
        foreach (var directory in Directory.EnumerateDirectories(to, "*", SearchOption.AllDirectories)
                     .Where(d => !IsAside(to, d)).OrderByDescending(d => d.Length).ToList())
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        return Directory.Exists(aside) ? aside : null;
    }

    private static bool IsAside(string root, string path) =>
        Path.GetRelativePath(root, path).StartsWith(AsidePrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Why the new root's drive cannot take <paramref name="files"/>, or null when it can (or cannot be asked).</summary>
    private static string? Shortfall(string to, List<string> files, Func<string, long?> freeSpace)
    {
        if (freeSpace(to) is not { } free) return null;
        var need = files.Sum(f => new FileInfo(f).Length);
        return need + SpareBytes <= free
            ? null
            : $"{Path.GetPathRoot(to)} 剩余空间不够：要复制 {need / 1048576.0:0} MB，剩 {free / 1048576.0:0} MB";
    }

    /// <summary>
    /// Files that belong to the running database rather than to its content. The shared-memory
    /// file is rebuilt by SQLite and a stale copy of it can only confuse the new location.
    /// </summary>
    private static bool IsTransient(string file) =>
        file.EndsWith("-shm", StringComparison.OrdinalIgnoreCase);

    /// <summary>The catalog and what goes with it: catalog.sqlite* at the top of the folder.</summary>
    private static bool IsCatalog(string root, string file) =>
        string.Equals(Path.GetDirectoryName(file), root, StringComparison.OrdinalIgnoreCase)
        && Path.GetFileName(file).StartsWith(CatalogFileName, StringComparison.OrdinalIgnoreCase);

    private static void TryCheckpoint(string databasePath)
    {
        if (!File.Exists(databasePath)) return;
        try
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Cache = SqliteCacheMode.Private,
            };
            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // Locked or damaged: copy the files as they are and let the catalog's own
            // rebuild-on-corruption path deal with it at the new location.
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    /// <param name="placed">Each target noted before it is written, so a copy cut short is known too.</param>
    private static int CopyFiles(string from, string to, List<string> files, List<string> placed)
    {
        foreach (var file in files)
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            // The new root held nothing of its own when the move began; a file there now is not
            // this move's to replace, nor to remove again.
            if (File.Exists(target)) throw new IOException($"新数据目录里已经有 {Path.GetRelativePath(from, file)}");
            placed.Add(target);
            File.Copy(file, target, overwrite: false);
        }
        return files.Count;
    }

    /// <summary>
    /// Takes back what an attempt put in the new root. The marker goes only once all of it is gone;
    /// what cannot go yet is cleared by the next start, which finds the marker.
    /// </summary>
    private static void Undo(string marker, List<string> placed, List<string> made)
    {
        var whole = true;
        foreach (var file in placed)
        {
            try { File.Delete(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { whole = false; }
        }
        foreach (var directory in made.OrderByDescending(d => d.Length))
        {
            try { if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* left empty, harmless */ }
        }
        if (!whole) return;
        try { File.Delete(marker); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* cleared next start */ }
    }

    /// <summary>Returns the first file that did not arrive intact, or null when all did.</summary>
    private static string? Verify(string from, string to, List<string> files)
    {
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(from, file);
            var target = new FileInfo(Path.Combine(to, relative));
            if (!target.Exists || target.Length != new FileInfo(file).Length) return relative;
        }
        return null;
    }
}
