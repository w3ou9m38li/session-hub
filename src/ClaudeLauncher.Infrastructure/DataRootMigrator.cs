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
    /// <summary>Nothing was moved; the old root is still the one that would be used.</summary>
    Failed,
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

    /// <summary>Migrates if needed, then makes sure the current root's folders exist.</summary>
    public static MigrationReport PrepareDataRoot()
    {
        MigrationReport report;
        try
        {
            report = Migrate(LauncherPaths.LegacyDataRoot, LauncherPaths.DataRoot);
        }
        catch (Exception ex)
        {
            // A failed migration must not stop the app: the old root is untouched and the
            // new one simply starts empty.
            report = new MigrationReport(MigrationOutcome.Failed, 0,
                LauncherPaths.LegacyDataRoot, LauncherPaths.DataRoot, ex.Message);
        }

        LauncherPaths.EnsureCreated();
        return report;
    }

    /// <summary>
    /// Copies <paramref name="legacyRoot"/> into <paramref name="newRoot"/> and removes the
    /// original. Exposed so the whole path can be exercised against temporary folders.
    /// </summary>
    public static MigrationReport Migrate(string legacyRoot, string newRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(newRoot);

        var from = Path.TrimEndingDirectorySeparator(Path.GetFullPath(legacyRoot));
        var to = Path.TrimEndingDirectorySeparator(Path.GetFullPath(newRoot));

        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
            return new MigrationReport(MigrationOutcome.NothingToDo, 0, from, to, null);

        if (!Directory.Exists(from))
            return new MigrationReport(MigrationOutcome.NothingToDo, 0, from, to, null);

        // An existing catalog in the new root is the one in use. Merging two catalogs would
        // have to pick a winner for every preference, so the old folder is left for the user.
        if (File.Exists(Path.Combine(to, CatalogFileName)))
            return new MigrationReport(MigrationOutcome.NothingToDo, 0, from, to,
                $"新数据目录已在使用，旧目录未动：{from}");

        // Fold the write-ahead log into the database first, so one file carries every commit
        // even if the -wal is left behind.
        TryCheckpoint(Path.Combine(from, CatalogFileName));

        var copied = CopyTree(from, to);
        var missing = Verify(from, to);
        if (missing is not null)
            return new MigrationReport(MigrationOutcome.Failed, copied, from, to,
                $"复制校验失败（{missing}），旧目录保持原样");

        try
        {
            Directory.Delete(from, recursive: true);
            return new MigrationReport(MigrationOutcome.Moved, copied, from, to, null);
        }
        catch (Exception ex)
        {
            return new MigrationReport(MigrationOutcome.CopiedOldKept, copied, from, to, ex.Message);
        }
    }

    /// <summary>
    /// Files that belong to the running database rather than to its content. The shared-memory
    /// file is rebuilt by SQLite and a stale copy of it can only confuse the new location.
    /// </summary>
    private static bool IsTransient(string file) =>
        file.EndsWith("-shm", StringComparison.OrdinalIgnoreCase);

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

    private static int CopyTree(string from, string to)
    {
        var copied = 0;
        Directory.CreateDirectory(to);

        foreach (var directory in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, directory)));

        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            if (IsTransient(file)) continue;
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
            copied++;
        }
        return copied;
    }

    /// <summary>Returns the first file that did not arrive intact, or null when all did.</summary>
    private static string? Verify(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            if (IsTransient(file)) continue;
            var relative = Path.GetRelativePath(from, file);
            var target = new FileInfo(Path.Combine(to, relative));
            if (!target.Exists || target.Length != new FileInfo(file).Length) return relative;
        }
        return null;
    }
}
