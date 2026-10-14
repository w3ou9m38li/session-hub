using System.Globalization;
using System.Text;
using ClaudeLauncher.Core.Catalog;
using ClaudeLauncher.Core.Contracts;
using ClaudeLauncher.Infrastructure.Search;
using Microsoft.Data.Sqlite;

namespace ClaudeLauncher.Infrastructure.Catalog;

public enum PageOutcome { Ok, CursorExpired }

/// <summary>
/// Opaque paging position. Carries the filter and order it was issued for, the full sort key
/// of the last row, and the catalog revision, so a page can never be stitched onto a
/// different query, a different order or a superseded snapshot.
/// </summary>
public sealed record PageCursor(
    string FilterFingerprint,
    long CatalogRevision,
    IReadOnlyList<object> SortKeys);

/// <summary>What the list can be ordered by: one per column.</summary>
public enum SortField { LastActivity, Title, Group, Source, Project, AppState, Size, Relevance }

/// <summary>
/// An order for the list. Every field has the direction a person expects first — newest
/// first for time, A→Z for text, Claude before ChatGPT, active before archived — and
/// <see cref="Reversed"/> flips it. The default value is newest first, the list's usual order.
///
/// <para>Rows without a value (no group, no working directory, no timestamp) stay at the end
/// in both directions: reversing an order is about the values, not about pushing blanks up.</para>
/// </summary>
public readonly record struct SortOrder(SortField Field, bool Reversed)
{
    public static SortOrder Default => default;

    public bool Descending => (Field is SortField.LastActivity or SortField.Size or SortField.Relevance) != Reversed;

    /// <summary>A category rather than a scale: the list shows it in sections.</summary>
    public bool IsCategorical => Field is SortField.Group or SortField.Source or SortField.Project or SortField.AppState;

    /// <summary>Shown in sections: a category, time — 今天、昨天、7 天内…… — size, or where a search's words were found.</summary>
    public bool HasSections => IsCategorical || Field is SortField.LastActivity or SortField.Size or SortField.Relevance;

    public string Fingerprint => $"s:{Field}:{(Reversed ? 1 : 0)}";
}

/// <summary>
/// The order a person reads text in: Chinese by pinyin, Latin alphabetically, case and
/// full/half width ignored. Registered on the catalog's connection as <c>UI</c> for sorting
/// only — keys, filters and uniqueness stay exact.
/// </summary>
public static class UiCollation
{
    public const string Name = "UI";

    private const CompareOptions Loose =
        CompareOptions.IgnoreCase | CompareOptions.IgnoreWidth | CompareOptions.IgnoreKanaType;

    private static readonly CompareInfo Culture = Create();

    /// <summary>
    /// Loose first, exact to break ties. Without the tie-break, <c>D:\Proj</c> and <c>d:\proj</c>
    /// would count as one value, their rows would interleave by time, and a list shown in
    /// sections would start the same section twice.
    /// </summary>
    public static int Compare(string? x, string? y)
    {
        var loose = Culture.Compare(x, y, Loose);
        return loose != 0 ? loose : string.CompareOrdinal(x, y);
    }

    private static CompareInfo Create()
    {
        try
        {
            return CultureInfo.GetCultureInfo("zh-CN").CompareInfo;
        }
        catch (CultureNotFoundException)
        {
            // Invariant-globalisation mode: still a stable order, just not a pinyin one.
            return CultureInfo.InvariantCulture.CompareInfo;
        }
    }
}

/// <summary>
/// Where 今天, 昨天, 7 天内 and 30 天内 begin, as of one local moment. Taken once per load, so the
/// query that counts each section and the code that labels a row use the very same edges.
/// </summary>
public sealed record DateBuckets(DateTimeOffset Today, DateTimeOffset Yesterday, DateTimeOffset Week, DateTimeOffset Month)
{
    public const string TodayKey = "0", YesterdayKey = "1", WeekKey = "2", MonthKey = "3", EarlierKey = "4", NoTimeKey = "5";

    public static DateBuckets At(DateTime localNow)
    {
        var today = localNow.Date;
        return new(Midnight(today), Midnight(today.AddDays(-1)), Midnight(today.AddDays(-6)), Midnight(today.AddDays(-29)));

        // The local offset of that very day, so a daylight-saving change in between is honoured.
        static DateTimeOffset Midnight(DateTime day) => new(DateTime.SpecifyKind(day, DateTimeKind.Local));
    }

    public string KeyOf(DateTimeOffset? when) => when switch
    {
        null => NoTimeKey,
        { } t when t >= Today => TodayKey,
        { } t when t >= Yesterday => YesterdayKey,
        { } t when t >= Week => WeekKey,
        { } t when t >= Month => MonthKey,
        _ => EarlierKey,
    };

    /// <summary><see cref="KeyOf"/> in SQL, over the stored UTC text; edges bound by <see cref="Bind"/>.</summary>
    internal const string Sql =
        "CASE WHEN last_activity_utc IS NULL THEN '5' WHEN last_activity_utc >= $d0 THEN '0' " +
        "WHEN last_activity_utc >= $d1 THEN '1' WHEN last_activity_utc >= $d2 THEN '2' " +
        "WHEN last_activity_utc >= $d3 THEN '3' ELSE '4' END";

    internal void Bind(SqliteCommand cmd)
    {
        cmd.Parameters.AddWithValue("$d0", SqliteCatalog.Utc(Today));
        cmd.Parameters.AddWithValue("$d1", SqliteCatalog.Utc(Yesterday));
        cmd.Parameters.AddWithValue("$d2", SqliteCatalog.Utc(Week));
        cmd.Parameters.AddWithValue("$d3", SqliteCatalog.Utc(Month));
    }
}

/// <summary>Which of the user's own settings for a conversation a write changed.</summary>
public enum PreferenceField { Pinned, Title, Note }

/// <summary>
/// One row's pin, name or note as a write changed it ("1"/"0" for a pin, null for none), and
/// the preference revision that write produced — an undo applies only while the row still
/// carries it.
/// </summary>
public sealed record PreferenceChange(ConversationKey Key, PreferenceField Field, string? Before, string? After, long Revision);

/// <param name="Applied">Rows that actually changed.</param>
/// <param name="Unchanged">Rows that already had the requested value.</param>
/// <param name="Conflict">Rows a newer change had moved on from; nothing was overwritten.</param>
public sealed record PreferenceWriteResult(int Applied, int Unchanged, int Conflict)
{
    public IReadOnlyList<PreferenceChange> Changes { get; init; } = [];
}

public sealed record CatalogPage(
    PageOutcome Outcome,
    IReadOnlyList<SessionMetadata> Rows,
    PageCursor? Next,
    long CatalogRevision);

public enum GroupFilterKind
{
    /// <summary>No group filtering at all.</summary>
    All,
    /// <summary>Only conversations with no effective group.</summary>
    Ungrouped,
    /// <summary>Only conversations whose effective group is <see cref="GroupFilter.Name"/>.</summary>
    Named,
}

/// <summary>
/// Which group the list is restricted to. The default value is <see cref="All"/>, so a caller
/// that does not care about groups can leave it out entirely.
/// </summary>
public readonly record struct GroupFilter(GroupFilterKind Kind, string? Name)
{
    public static GroupFilter All => new(GroupFilterKind.All, null);
    public static GroupFilter Ungrouped => new(GroupFilterKind.Ungrouped, null);
    public static GroupFilter Named(string name) => new(GroupFilterKind.Named, name);

    /// <summary>Part of the paging fingerprint: a cursor must never cross a filter change.</summary>
    public string Fingerprint => Kind switch
    {
        GroupFilterKind.Named => "g:" + Name,
        GroupFilterKind.Ungrouped => "g:<none>",
        _ => "g:*",
    };
}

/// <summary>One group and how many conversations are in it. Null name = ungrouped.</summary>
public sealed record GroupTally(string? Name, int Count);

/// <summary>
/// Which app's conversations the list shows: one app family (<see cref="AppFamily"/>) — Claude's
/// desktop and terminal sessions together, Gemini CLI and Gemini on the web together — or all of
/// them. The default value is <see cref="All"/>.
/// </summary>
public readonly record struct BackendFilter(AppFamily? Family)
{
    public static BackendFilter All => new(null);
    public static BackendFilter Claude => new(AppFamily.Claude);
    public static BackendFilter Codex => new(AppFamily.Codex);
    public static BackendFilter Gemini => new(AppFamily.Gemini);
    public static BackendFilter DeepSeek => new(AppFamily.DeepSeek);

    public bool IsAll => Family is null;

    /// <summary>The name it is remembered by in the settings: 「All」 or the family's.</summary>
    public string Name => Family?.ToString() ?? "All";

    public string Fingerprint => "b:" + Name;

    /// <summary>SQL predicate on the sessions table, or null for no restriction.</summary>
    internal string? Predicate => Family is { } family
        ? "backend IN (" + string.Join(",", Sources.BackendsOf(family).Select(b => "'" + b.Replace("'", "''") + "'")) + ")"
        : null;
}

/// <param name="Applied">Rows whose group actually changed.</param>
/// <param name="Unchanged">Rows that already carried the requested group.</param>
/// <param name="Conflict">Rows a newer preference had moved on from; nothing was overwritten.</param>
public sealed record GroupWriteResult(int Applied, int Unchanged, int Conflict)
{
    /// <summary>Exactly the rows this write changed, with what they held before — what an undo needs.</summary>
    public IReadOnlyList<GroupChange> Changes { get; init; } = [];
}

/// <summary>
/// One row's group as a write changed it. <paramref name="Revision"/> is the preference
/// revision the write produced: an undo applies only while the row still carries it.
/// </summary>
/// <param name="Before">The stored value before the write: null = no preference, "" = explicitly ungrouped.</param>
public sealed record GroupChange(ConversationKey Key, string? Before, string After, long Revision);

/// <summary>How many conversations each view holds, for the view switch to show.</summary>
public sealed record ViewCounts(int Active, int Archived);

/// <summary>
/// This launcher's own catalog. Every statement is parameterised; the database lives only
/// under this tool's data root and nothing here ever writes to Claude's own files.
/// </summary>
public sealed class SqliteCatalog : IDisposable
{
    public const int DefaultPageSize = 100;
    public const int MaxPageSize = 500;

    /// <summary>Sorts rows with no timestamp last without dropping them from the page.</summary>
    private const string NoTimeSentinel = "0001-01-01T00:00:00.0000000Z";
    private const string UtcFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    /// <summary>A moment as the catalog stores it: fixed-width UTC text, so text order is time order.</summary>
    internal static string Utc(DateTimeOffset when) =>
        when.UtcDateTime.ToString(UtcFormat, CultureInfo.InvariantCulture);

    /// <summary>
    /// The section key of pinned rows, whatever the order: they are listed first, under 置顶.
    /// Starts with a control character, so no group name or path can ever equal it.
    /// </summary>
    public const string PinnedSection = "\u0001pinned";

    /// <summary>
    /// The one place a row's columns are listed, so <see cref="Read"/>'s ordinals cannot
    /// drift apart from the two statements that feed it. The last one is counted, not stored:
    /// how many earlier transcripts a desktop session was continued from (only desktop rows
    /// look, and there are few of them).
    /// </summary>
    private const string RowColumns = """
        backend,account_context,organization_context,native_id,cli_session_id,
        title,search_key,project_ref,native_open_ref,last_activity_utc,
        source_revision,sidebar_group,title_is_placeholder,visibility,native_archive_state,
        source_coverage,local_pinned,preference_revision,local_group,
        local_title,local_note,merged_into,transcript_bytes,continued_in,resumed_from,source_gone_utc,
        CASE WHEN backend='desktop-code' THEN (SELECT COUNT(*) FROM sessions part
          WHERE part.continued_in=sessions.native_id AND part.merged_into IS NULL) ELSE 0 END
        """;

    /// <summary>
    /// Size sections, largest first: 100 MB and more, 10–100 MB, 1–10 MB, less, and no file.
    /// Keys sort in section order; <see cref="SizeSection"/> is the same rule in C#.
    /// </summary>
    private const string SizeSectionSql =
        "CASE WHEN transcript_bytes IS NULL THEN '4' WHEN transcript_bytes >= 104857600 THEN '0' " +
        "WHEN transcript_bytes >= 10485760 THEN '1' WHEN transcript_bytes >= 1048576 THEN '2' ELSE '3' END";

    public const string SizeHugeKey = "0", SizeLargeKey = "1", SizeMediumKey = "2", SizeSmallKey = "3", SizeNoneKey = "4";

    /// <summary>Relevance sections: every word in what the catalog names, or some only in what was said.</summary>
    public const string RelevanceNamedKey = "0", RelevanceSaidKey = "1";

    public static string SizeSection(long? bytes) => bytes switch
    {
        null => SizeNoneKey,
        >= 100L * 1024 * 1024 => SizeHugeKey,
        >= 10L * 1024 * 1024 => SizeLargeKey,
        >= 1024 * 1024 => SizeMediumKey,
        _ => SizeSmallKey,
    };

    /// <summary>
    /// The group a row counts as being in. Mirrors <c>GroupRules.Effective</c>: the user's own
    /// group wins, '' means they explicitly removed it, and NULL falls back to Claude's.
    /// Kept as one expression so filtering and tallying can never disagree.
    /// </summary>
    private const string EffectiveGroupSql =
        "CASE WHEN local_group IS NULL THEN NULLIF(sidebar_group,'') " +
        "WHEN local_group='' THEN NULL ELSE local_group END";

    /// <summary>
    /// The paging index's own expression, sentinel inlined: a parameter in its place would
    /// stop SQLite from recognising the index and make every page a full sort.
    /// </summary>
    private const string SortTimeSql = "COALESCE(last_activity_utc,'" + NoTimeSentinel + "')";

    /// <summary>Mirrors <c>OrderingRules.SourceRank</c>: written from the same sources' ranks.</summary>
    private static readonly string SourceRankSql =
        "CASE backend " + string.Concat(Sources.All.Where(src => src.Read).Select(src => $"WHEN '{src.Backend}' THEN {src.Rank} "))
        + $"ELSE {OrderingRules.UnknownRank} END";

    /// <summary>Mirrors <c>OrderingRules.Standing</c>, as the enum's numeric value: each source's rule, the app's state otherwise.</summary>
    private static readonly string StandingSql =
        "CASE " + string.Concat(Sources.All.Where(src => src.Read && src.Standing != StandingRule.AppState)
            .Select(src => $"WHEN backend='{src.Backend}' THEN {StandingOf(src.Standing)} "))
        + $"WHEN native_archive_state='Active' THEN {(int)AppStanding.Active} "
        + $"WHEN native_archive_state='Archived' THEN {(int)AppStanding.Archived} "
        + $"WHEN native_archive_state='Unsupported' THEN {(int)AppStanding.TerminalOnly} ELSE {(int)AppStanding.Unobserved} END";

    private static string StandingOf(StandingRule rule) => rule switch
    {
        StandingRule.ArchivedOrListed =>
            $"(CASE WHEN native_archive_state='Archived' THEN {(int)AppStanding.Archived} ELSE {(int)AppStanding.Active} END)",
        StandingRule.TerminalTranscript =>
            $"(CASE WHEN continued_in IS NOT NULL THEN {(int)AppStanding.EarlierPart} ELSE {(int)AppStanding.TerminalOnly} END)",
        StandingRule.Imported => $"{(int)AppStanding.Imported}",
        _ => throw new ArgumentOutOfRangeException(nameof(rule)),
    };

    private readonly SqliteConnection _connection;

    /// <summary>True once the full-text index is attached: a search then also matches what was said.</summary>
    private bool _contentSearch;

    /// <summary>
    /// Per search term, the temporary table holding the conversations in which it was said:
    /// looked up in the index once per search, not once per query — a page, its count, its
    /// section counts and every further page all ask. Dropped when the index changes.
    /// </summary>
    private readonly Dictionary<string, string> _saidTables = new(StringComparer.Ordinal);
    private const int MaxSaidTables = 32;
    private int _saidSequence;

    /// <summary>Bumped when the index changes, so a page cursor from before it expires.</summary>
    private long _contentGeneration;

    public SqliteCatalog(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
        };
        _connection = new SqliteConnection(builder.ToString());
        _connection.Open();
        _connection.CreateCollation(UiCollation.Name, UiCollation.Compare);
        Execute("PRAGMA journal_mode=WAL;");
        Execute("PRAGMA busy_timeout=5000;");
    }

    /// <summary>
    /// Opens the catalog, rebuilding it if the file is unreadable.
    ///
    /// <para>The catalog is a derived cache: every row can be re-read from Claude's own files,
    /// so a corrupt database is an inconvenience, not data loss — and crashing on it would be
    /// worse than rebuilding. What a rebuild does cost is this tool's own preferences (which
    /// rows you collapsed, what you pinned), so the damaged file is moved aside rather than
    /// deleted, and the caller is told.</para>
    /// </summary>
    public static SqliteCatalog OpenOrRebuild(string databasePath, string schemaSql, out string? rebuiltFrom)
    {
        rebuiltFrom = null;
        SqliteCatalog? attempt = null;
        try
        {
            attempt = new SqliteCatalog(databasePath);
            attempt.Initialize(schemaSql);
            attempt.VerifyIntegrity();
            var ok = attempt;
            attempt = null;         // handed to the caller; do not dispose below
            return ok;
        }
        catch (SqliteException)
        {
            // The failed connection still holds the file open. Without disposing it first the
            // move silently fails and the rebuild reopens the very same corrupt database —
            // which is exactly what happened before this line existed.
            attempt?.Dispose();
            attempt = null;
            SqliteConnection.ClearAllPools();

            rebuiltFrom = QuarantineDamagedFiles(databasePath);
            var rebuilt = new SqliteCatalog(databasePath);
            rebuilt.Initialize(schemaSql);
            return rebuilt;
        }
        finally
        {
            attempt?.Dispose();
        }
    }

    /// <summary>Moves a damaged database and its WAL/SHM aside. Never deletes them.</summary>
    private static string? QuarantineDamagedFiles(string databasePath)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string? moved = null;
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var file = databasePath + suffix;
            if (!File.Exists(file)) continue;
            var target = $"{databasePath}.damaged-{stamp}{suffix}";
            try
            {
                File.Move(file, target, overwrite: true);
                moved ??= target;
            }
            catch (IOException ex)
            {
                // Still locked. Rebuilding onto a file we could not move would reopen the
                // corrupt database, so this fails loudly instead of pretending it worked.
                throw new InvalidOperationException(
                    $"目录数据库损坏，但无法移开损坏文件（{file}）。"
                    + "请关闭其他启动器实例后重试。", ex);
            }
        }
        return moved;
    }

    /// <summary>
    /// Proves the catalog is actually usable.
    ///
    /// <para><c>quick_check</c> alone is not enough: it passed on a database whose paging
    /// index was corrupt, and the failure then surfaced mid-query. So this also runs the
    /// real paging read — the same statement and index the app uses — because the only
    /// check worth trusting is the one that does what the app does.</para>
    /// </summary>
    public void VerifyIntegrity()
    {
        using (var check = _connection.CreateCommand())
        {
            check.CommandText = "PRAGMA quick_check(1);";
            var result = check.ExecuteScalar() as string;
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                throw new SqliteException($"catalog integrity check failed: {result}", 11);
        }

        // The sentinel is inlined exactly as Page writes it: a bound value would not match the
        // index expression, and the check would read something other than what the app reads.
        using var smoke = _connection.CreateCommand();
        smoke.CommandText = $"""
            SELECT backend, native_id, {SortTimeSql} AS sort_time
            FROM sessions
            WHERE visibility=$visibility
            ORDER BY local_pinned DESC, sort_time DESC, backend ASC, account_context ASC,
                     organization_context ASC, native_id ASC
            LIMIT 1;
            """;
        smoke.Parameters.AddWithValue("$visibility", Visibility.Active.ToString());
        using var reader = smoke.ExecuteReader();
        while (reader.Read()) { /* touching the row is the point */ }
    }

    public void Initialize(string schemaSql)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaSql);
        using var tx = _connection.BeginTransaction();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = schemaSql;
            cmd.ExecuteNonQuery();
        }
        MigrateAddColumns(tx);
        CreateLinkIndexes(tx);
        BackfillProjectKeys(tx);
        tx.Commit();
    }

    /// <summary>
    /// Rows found by their links to other rows: a conversation by its id alone (whichever
    /// backend and account), the rows folded into it, the transcripts continued or resumed from
    /// it. Without them every such lookup scans the whole table once per row — eighteen seconds
    /// a sync at three thousand conversations. Partial where almost every row has no link: a
    /// list of the rows without one reads faster from the table than through an index of NULLs.
    /// Made after the columns are added, so a catalog from before them opens too.
    /// </summary>
    private void CreateLinkIndexes(SqliteTransaction tx)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            CREATE INDEX IF NOT EXISTS sessions_native ON sessions(native_id);
            CREATE INDEX IF NOT EXISTS sessions_merged_into ON sessions(merged_into) WHERE merged_into IS NOT NULL;
            CREATE INDEX IF NOT EXISTS sessions_continued_in ON sessions(continued_in) WHERE continued_in IS NOT NULL;
            CREATE INDEX IF NOT EXISTS sessions_resumed_from ON sessions(resumed_from) WHERE resumed_from IS NOT NULL;
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Adds columns introduced after a database was first created. CREATE TABLE IF NOT
    /// EXISTS leaves an existing table untouched, so a new column needs an explicit ALTER.
    /// Additive only — no column is ever dropped or retyped, so an older build still reads
    /// the database.
    /// </summary>
    private void MigrateAddColumns(SqliteTransaction tx)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var probe = _connection.CreateCommand())
        {
            probe.Transaction = tx;
            probe.CommandText = "PRAGMA table_info(sessions);";
            using var reader = probe.ExecuteReader();
            while (reader.Read()) existing.Add(reader.GetString(1));
        }

        (string Name, string Ddl)[] additive =
        [
            ("sidebar_group", "ALTER TABLE sessions ADD COLUMN sidebar_group TEXT;"),
            ("local_group", "ALTER TABLE sessions ADD COLUMN local_group TEXT;"),
            ("project_key", "ALTER TABLE sessions ADD COLUMN project_key TEXT;"),
            ("local_title", "ALTER TABLE sessions ADD COLUMN local_title TEXT;"),
            ("local_note", "ALTER TABLE sessions ADD COLUMN local_note TEXT;"),
            ("local_search_key", "ALTER TABLE sessions ADD COLUMN local_search_key TEXT;"),
            ("merged_into", "ALTER TABLE sessions ADD COLUMN merged_into TEXT;"),
            ("transcript_bytes", "ALTER TABLE sessions ADD COLUMN transcript_bytes INTEGER;"),
            ("continued_in", "ALTER TABLE sessions ADD COLUMN continued_in TEXT;"),
            ("resumed_from", "ALTER TABLE sessions ADD COLUMN resumed_from TEXT;"),
            ("source_gone_utc", "ALTER TABLE sessions ADD COLUMN source_gone_utc TEXT;"),
        ];

        foreach (var (name, ddl) in additive)
        {
            if (existing.Contains(name)) continue;
            using var alter = _connection.CreateCommand();
            alter.Transaction = tx;
            alter.CommandText = ddl;
            alter.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Fills the search key of the working directory for rows written before that column
    /// existed (or by an older build). Normalisation is done here rather than in SQL because
    /// SQLite's own lower() folds ASCII only, and a path is searched the same way a title is.
    /// A sync rewrites every key anyway; this only makes search right before the first one.
    /// </summary>
    private void BackfillProjectKeys(SqliteTransaction tx)
    {
        var pending = new List<(long RowId, string Key)>();
        using (var select = _connection.CreateCommand())
        {
            select.Transaction = tx;
            select.CommandText =
                "SELECT rowid, project_ref FROM sessions WHERE project_key IS NULL AND project_ref IS NOT NULL;";
            using var reader = select.ExecuteReader();
            while (reader.Read())
                pending.Add((reader.GetInt64(0), SearchKeys.Normalize(reader.GetString(1))));
        }
        if (pending.Count == 0) return;

        using var update = _connection.CreateCommand();
        update.Transaction = tx;
        update.CommandText = "UPDATE sessions SET project_key=$key WHERE rowid=$id;";
        foreach (var (rowId, key) in pending)
        {
            update.Parameters.Clear();
            update.Parameters.AddWithValue("$key", key);
            update.Parameters.AddWithValue("$id", rowId);
            update.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Makes the full-text index part of every search: a term then also matches a conversation
    /// in which it was said. The index is its own database, so the catalog itself never holds
    /// transcript text and the index can be deleted at any time.
    ///
    /// <para>It is attached only for the moment a search word is looked up in it, never left
    /// attached: while the index is being written — gigabytes, the first time — every query on a
    /// connection it is attached to slows down tenfold, those that never touch it included.</para>
    /// </summary>
    public void AttachContentIndex(string indexPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexPath);
        _contentIndexPath = indexPath;
        _contentSearch = true;
    }

    /// <summary>Stops searching what was said: the index is let go of, and nothing looked up in it is kept.</summary>
    public void DetachContentIndex()
    {
        if (!_contentSearch) return;
        DropSaidTables();
        _contentSearch = false;
        _contentIndexPath = null;
        _contentGeneration++;
    }

    private string? _contentIndexPath;

    /// <summary>
    /// The index has changed: what was looked up in it is looked up again, and a page cursor
    /// from before the change expires rather than stitching old results to new ones.
    /// </summary>
    public void ContentIndexChanged()
    {
        DropSaidTables();
        _contentGeneration++;
    }

    /// <summary>
    /// Makes sure every word of a search — those to find and those to leave out — has its table
    /// of the conversations in which it was said, with how many messages there say it.
    /// </summary>
    /// <param name="mineOnly">Only what the user said counts; a table of its own per word.</param>
    private IReadOnlyDictionary<string, string>? PrepareContentSearch(string? searchText, bool mineOnly = false)
    {
        if (!_contentSearch) return null;
        var words = SearchKeys.Parse(searchText)
            .Where(t => ContentIndex.TermCondition(t.Text, "$q") is not null)
            .Select(t => (t.Text, Key: SaidKey(t.Text, mineOnly)))
            .Distinct()
            .ToList();

        // A cache, not a history — but room is made before this search's tables are, and never
        // at the expense of a word this same search uses: that word's clause would silently drop.
        var missing = words.Count(w => !_saidTables.ContainsKey(w.Key));
        if (_saidTables.Count + missing > MaxSaidTables)
        {
            var wanted = words.Select(w => w.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var (stale, table) in _saidTables.Where(kv => !wanted.Contains(kv.Key)).ToList())
            {
                Execute($"DROP TABLE IF EXISTS temp.{table};");
                _saidTables.Remove(stale);
            }
        }

        if (missing == 0) return _saidTables;
        using (var attach = _connection.CreateCommand())
        {
            attach.CommandText = "ATTACH DATABASE $path AS idx;";
            attach.Parameters.AddWithValue("$path", _contentIndexPath);
            attach.ExecuteNonQuery();
        }
        try
        {
            foreach (var (text, key) in words)
            {
                if (_saidTables.ContainsKey(key)) continue;
                var condition = ContentIndex.TermCondition(text, "$q")!;
                var table = "said_" + (++_saidSequence).ToString(CultureInfo.InvariantCulture);
                Execute($"CREATE TEMP TABLE {table}(conversation TEXT PRIMARY KEY, hits INTEGER NOT NULL) WITHOUT ROWID;");
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = $"INSERT INTO temp.{table} SELECT conversation, COUNT(*) FROM idx.messages "
                                  + $"WHERE {condition}{(mineOnly ? " AND role='user'" : "")} GROUP BY conversation;";
                cmd.Parameters.AddWithValue("$q", ContentIndex.TermValue(text));
                cmd.ExecuteNonQuery();
                _saidTables[key] = table;
            }
        }
        finally
        {
            Execute("DETACH DATABASE idx;");
        }
        return _saidTables;
    }

    /// <summary>How a word's table is remembered: the same word said only by the user is another table.</summary>
    private static string SaidKey(string term, bool mineOnly) => mineOnly ? term + "\u0001mine" : term;

    /// <summary>
    /// For each word of a search, how many messages of each conversation say it (keyed like
    /// <see cref="ContentIndex.KeyOf"/>) — what the list shows beside a row found by content.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> ContentHitCounts(
        string? searchText, bool mineOnly = false)
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, int>>(StringComparer.Ordinal);
        if (PrepareContentSearch(searchText, mineOnly) is not { } said) return result;
        foreach (var term in SearchKeys.Terms(searchText))
        {
            if (!said.TryGetValue(SaidKey(term, mineOnly), out var table)) continue;
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $"SELECT conversation, hits FROM temp.{table};";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) counts[reader.GetString(0)] = reader.GetInt32(1);
            result[term] = counts;
        }
        return result;
    }

    private void DropSaidTables()
    {
        foreach (var table in _saidTables.Values) Execute($"DROP TABLE IF EXISTS temp.{table};");
        _saidTables.Clear();
    }

    /// <summary>
    /// The first search term this row was found by only through what was said in it — its
    /// title, name, note, folder and id do not hold it — or null. The same tests
    /// <see cref="AppendFilter"/> makes in SQL, applied to the row in hand.
    /// </summary>
    public static string? ContentOnlyTerm(SessionMetadata row, IReadOnlyList<string> terms)
    {
        ArgumentNullException.ThrowIfNull(row);
        string? local = null, project = null;
        foreach (var term in terms)
        {
            if (row.SearchKey.Contains(term, StringComparison.Ordinal)) continue;
            local ??= LocalSearchKey(row.LocalTitle, row.LocalNote) ?? string.Empty;
            if (local.Contains(term, StringComparison.Ordinal)) continue;
            project ??= SearchKeys.Normalize(row.ProjectRef);
            if (project.Contains(term, StringComparison.Ordinal)) continue;
            if (SearchKeys.LooksLikeIdFragment(term)
                && (row.Key.NativeSessionId.ToLowerInvariant().Contains(term, StringComparison.Ordinal)
                    || (row.CliSessionId?.ToLowerInvariant().Contains(term, StringComparison.Ordinal) ?? false)))
                continue;
            return term;
        }
        return null;
    }

    /// <summary>Every conversation listed on its own, in both views: what the full-text index should cover.</summary>
    public IReadOnlyList<SessionMetadata> ListedRows()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"SELECT {RowColumns} FROM sessions WHERE merged_into IS NULL;";
        var rows = new List<SessionMetadata>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) rows.Add(Read(reader));
        return rows;
    }

    /// <summary>Whether nothing has been listed yet: a first launch on this data root.</summary>
    public bool IsEmpty()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT NOT EXISTS (SELECT 1 FROM sessions);";
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    public long CurrentRevision()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT revision FROM catalog_version WHERE singleton_id=1;";
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    // ----------------------------------------------------------------- sync

    /// <summary>
    /// Writes a sync batch inside one transaction. Rows identical to what is stored are not
    /// rewritten, and the revision is bumped — once — only when something did change, so a
    /// sync that found nothing new leaves open paging cursors valid and the disk untouched.
    /// Preference columns are never overwritten by a sync.
    /// </summary>
    /// <returns>How many rows were added or changed.</returns>
    public int UpsertRange(IEnumerable<SessionMetadata> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var changed = 0;
        using var tx = _connection.BeginTransaction(deferred: false);
        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO sessions
                  (backend,account_context,organization_context,native_id,cli_session_id,
                   title,search_key,project_ref,project_key,native_open_ref,last_activity_utc,
                   source_revision,sidebar_group,title_is_placeholder,visibility,native_archive_state,
                   source_coverage,local_pinned,preference_revision,local_group,merged_into,transcript_bytes,
                   continued_in,resumed_from)
                VALUES
                  ($backend,$account,$org,$native,$cli,
                   $title,$search,$project,$projectKey,$open,$activity,
                   $rev,$group,$placeholder,$visibility,$nativeState,
                   $coverage,0,0,NULL,$merged,$bytes,
                   $continued,$resumed)
                ON CONFLICT(backend,account_context,organization_context,native_id)
                DO UPDATE SET
                  cli_session_id=excluded.cli_session_id,
                  title=excluded.title,
                  search_key=excluded.search_key,
                  project_ref=excluded.project_ref,
                  project_key=excluded.project_key,
                  native_open_ref=excluded.native_open_ref,
                  last_activity_utc=excluded.last_activity_utc,
                  source_revision=excluded.source_revision,
                  sidebar_group=excluded.sidebar_group,
                  title_is_placeholder=excluded.title_is_placeholder,
                  native_archive_state=excluded.native_archive_state,
                  source_coverage=excluded.source_coverage,
                  merged_into=excluded.merged_into,
                  transcript_bytes=excluded.transcript_bytes,
                  continued_in=excluded.continued_in,
                  resumed_from=excluded.resumed_from,
                  source_gone_utc=NULL
                WHERE sessions.source_gone_utc IS NOT NULL
                   OR sessions.cli_session_id IS NOT excluded.cli_session_id
                   OR sessions.title IS NOT excluded.title
                   OR sessions.search_key IS NOT excluded.search_key
                   OR sessions.project_ref IS NOT excluded.project_ref
                   OR sessions.project_key IS NOT excluded.project_key
                   OR sessions.native_open_ref IS NOT excluded.native_open_ref
                   OR sessions.last_activity_utc IS NOT excluded.last_activity_utc
                   OR sessions.source_revision IS NOT excluded.source_revision
                   OR sessions.sidebar_group IS NOT excluded.sidebar_group
                   OR sessions.title_is_placeholder IS NOT excluded.title_is_placeholder
                   OR sessions.native_archive_state IS NOT excluded.native_archive_state
                   OR sessions.source_coverage IS NOT excluded.source_coverage
                   OR sessions.merged_into IS NOT excluded.merged_into
                   OR sessions.transcript_bytes IS NOT excluded.transcript_bytes
                   OR sessions.continued_in IS NOT excluded.continued_in
                   OR sessions.resumed_from IS NOT excluded.resumed_from;
                """;
            // local_group is absent from the UPDATE clause on purpose: it is the user's own
            // grouping, and a sync re-reading Claude's files must never reset it.
            var deleted = DeletedKeys(tx);
            foreach (var row in rows)
            {
                if (deleted.Contains(row.Key)) continue;
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("$backend", row.Key.Backend);
                cmd.Parameters.AddWithValue("$account", row.Key.AccountContextId);
                cmd.Parameters.AddWithValue("$org", row.Key.OrganizationContextId);
                cmd.Parameters.AddWithValue("$native", row.Key.NativeSessionId);
                cmd.Parameters.AddWithValue("$cli", (object?)row.CliSessionId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$title", row.Title);
                cmd.Parameters.AddWithValue("$search", row.SearchKey);
                cmd.Parameters.AddWithValue("$project", (object?)row.ProjectRef ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$projectKey",
                    row.ProjectRef is null ? DBNull.Value : SearchKeys.Normalize(row.ProjectRef));
                cmd.Parameters.AddWithValue("$open", (object?)row.NativeOpenRef ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$activity",
                    row.LastActivityUtc is { } t
                        ? t.UtcDateTime.ToString(UtcFormat, CultureInfo.InvariantCulture)
                        : (object)DBNull.Value);
                cmd.Parameters.AddWithValue("$rev", row.SourceRevision);
                cmd.Parameters.AddWithValue("$group", (object?)row.SidebarGroup ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$placeholder", row.TitleIsPlaceholder ? 1 : 0);
                cmd.Parameters.AddWithValue("$visibility", row.Visibility.ToString());
                cmd.Parameters.AddWithValue("$nativeState", row.NativeArchiveState.ToString());
                cmd.Parameters.AddWithValue("$coverage", row.SourceCoverage.ToString());
                cmd.Parameters.AddWithValue("$merged", (object?)row.MergedInto ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$bytes", (object?)row.TranscriptBytes ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$continued", (object?)row.ContinuedIn ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$resumed", (object?)row.ResumedFrom ?? DBNull.Value);
                changed += cmd.ExecuteNonQuery();
            }
        }
        if (changed > 0) BumpRevision(tx);
        tx.Commit();
        return changed;
    }

    /// <summary>
    /// Marks the rows of one backend that a full, successful read of that source no longer
    /// contains — a record the app deleted, cleaned up or moved away. They stay listed: this
    /// tool keeps what it has seen until the user deletes it here (<see cref="Delete"/>).
    ///
    /// <para>Only ever called for a source that was read completely. A source that failed
    /// keeps its rows unmarked: "unreadable" must never be turned into "gone".</para>
    /// </summary>
    /// <param name="accounts">
    /// When given, only rows of these accounts are judged: the ones that were read in full. A
    /// source with several homes (DeepSeek Harness) says nothing about a home it no longer reads.
    /// </param>
    /// <returns>How many rows were newly found gone.</returns>
    public int MarkGone(string backend, IEnumerable<ConversationKey> present, IReadOnlyCollection<string>? accounts = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backend);
        ArgumentNullException.ThrowIfNull(present);

        var keep = present.Where(k => k.Backend == backend).ToHashSet();
        var judged = accounts is null ? null : new HashSet<string>(accounts, StringComparer.OrdinalIgnoreCase);
        using var tx = _connection.BeginTransaction(deferred: false);

        var gone = new List<ConversationKey>();
        using (var select = _connection.CreateCommand())
        {
            select.Transaction = tx;
            select.CommandText = """
                SELECT account_context, organization_context, native_id FROM sessions
                WHERE backend=$backend AND source_gone_utc IS NULL;
                """;
            select.Parameters.AddWithValue("$backend", backend);
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                var key = new ConversationKey(backend, reader.GetString(0), reader.GetString(1), reader.GetString(2));
                if (judged is not null && !judged.Contains(key.AccountContextId)) continue;
                if (!keep.Contains(key)) gone.Add(key);
            }
        }

        if (gone.Count == 0)
        {
            tx.Commit();
            return 0;
        }

        using (var mark = _connection.CreateCommand())
        {
            mark.Transaction = tx;
            mark.CommandText = """
                UPDATE sessions SET source_gone_utc=$now
                WHERE backend=$backend AND account_context=$account
                  AND organization_context=$org AND native_id=$native;
                """;
            var now = DateTimeOffset.UtcNow.UtcDateTime.ToString(UtcFormat, CultureInfo.InvariantCulture);
            foreach (var key in gone)
            {
                mark.Parameters.Clear();
                mark.Parameters.AddWithValue("$now", now);
                AddKey(mark, key);
                mark.ExecuteNonQuery();
            }
        }
        BumpRevision(tx);
        tx.Commit();
        return gone.Count;
    }

    /// <summary>
    /// Deletes conversations from this tool at the user's word — with the rows folded into
    /// them — and remembers them, so a sync never lists them again even while the app still
    /// has them. Nothing in the apps is touched.
    /// </summary>
    /// <returns>The rows deleted, the folded ones included.</returns>
    public List<ConversationKey> Delete(IEnumerable<ConversationKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var deleted = new List<ConversationKey>();
        using var tx = _connection.BeginTransaction(deferred: false);
        var now = DateTimeOffset.UtcNow.UtcDateTime.ToString(UtcFormat, CultureInfo.InvariantCulture);
        foreach (var key in keys.Distinct())
        {
            var targets = new List<ConversationKey> { key };
            using (var folded = _connection.CreateCommand())
            {
                folded.Transaction = tx;
                folded.CommandText = """
                    SELECT backend, account_context, organization_context, native_id FROM sessions
                    WHERE merged_into=$native AND account_context=$account AND organization_context=$org;
                    """;
                folded.Parameters.AddWithValue("$native", key.NativeSessionId);
                folded.Parameters.AddWithValue("$account", key.AccountContextId);
                folded.Parameters.AddWithValue("$org", key.OrganizationContextId);
                using var reader = folded.ExecuteReader();
                while (reader.Read())
                    targets.Add(new ConversationKey(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
            }
            foreach (var target in targets)
            {
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    DELETE FROM sessions
                    WHERE backend=$backend AND account_context=$account AND organization_context=$org AND native_id=$native;
                    INSERT OR REPLACE INTO deleted_sessions(backend, account_context, organization_context, native_id, deleted_utc)
                    VALUES($backend, $account, $org, $native, $now);
                    """;
                AddKey(cmd, target);
                cmd.Parameters.AddWithValue("$now", now);
                cmd.ExecuteNonQuery();
                deleted.Add(target);
            }
        }
        if (deleted.Count > 0) BumpRevision(tx);
        tx.Commit();
        return deleted;
    }

    /// <summary>
    /// Drops every row of a source this tool imported, when the import itself is removed. Not
    /// remembered as deleted: importing again lists them again.
    /// </summary>
    public int Forget(string backend)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backend);
        using var tx = _connection.BeginTransaction(deferred: false);
        int count;
        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM sessions WHERE backend=$backend;";
            cmd.Parameters.AddWithValue("$backend", backend);
            count = cmd.ExecuteNonQuery();
        }
        if (count > 0) BumpRevision(tx);
        tx.Commit();
        return count;
    }

    private HashSet<ConversationKey> DeletedKeys(SqliteTransaction tx)
    {
        var keys = new HashSet<ConversationKey>();
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT backend, account_context, organization_context, native_id FROM deleted_sessions;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            keys.Add(new ConversationKey(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        return keys;
    }

    // -------------------------------------------------------------- paging

    /// <summary>Columns of <see cref="RowColumns"/>: the sort keys are read from the ordinals after them.</summary>
    private const int RowColumnCount = 27;

    public CatalogPage Page(
        Visibility visibility,
        string? searchText = null,
        PageCursor? cursor = null,
        int limit = DefaultPageSize,
        GroupFilter group = default,
        BackendFilter backend = default,
        SortOrder sort = default,
        DateTimeOffset? since = null,
        bool mineOnly = false)
    {
        limit = Math.Clamp(limit, 1, MaxPageSize);
        var fingerprint = Fingerprint(visibility, searchText, group, backend, sort, since)
                          + "|c:" + _contentGeneration.ToString(CultureInfo.InvariantCulture) + (mineOnly ? "|m" : "");
        var said = PrepareContentSearch(searchText, mineOnly);
        var keys = sort.Field == SortField.Relevance
            ? RelevanceKeys(sort, SearchKeys.Parse(searchText), said, mineOnly)
            : KeysFor(sort);

        // Revision and rows are read in one transaction, so the page is a coherent snapshot.
        using var tx = _connection.BeginTransaction();
        var revision = CurrentRevision();

        if (cursor is not null &&
            (cursor.FilterFingerprint != fingerprint || cursor.CatalogRevision != revision
             || cursor.SortKeys.Count != keys.Count))
        {
            tx.Commit();
            return new CatalogPage(PageOutcome.CursorExpired, [], null, revision);
        }

        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;

        var sql = new StringBuilder("SELECT ").Append(RowColumns);
        for (var i = 0; i < keys.Count; i++) sql.Append(',').Append(keys[i].Sql);
        sql.Append(" FROM sessions");
        AppendFilter(sql, cmd, visibility, searchText, group, backend, since, said, mineOnly);

        // Keyset paging over the whole order: rows strictly after the cursor's key, compared
        // component by component. The last components are the table's primary key, so the
        // order is total and a page can neither repeat nor skip a row, however many rows
        // share a title, a group or a timestamp.
        if (cursor is not null)
        {
            sql.Append(" AND (");
            for (var i = 0; i < keys.Count; i++)
            {
                if (i > 0) sql.Append(" OR ");
                sql.Append('(');
                for (var j = 0; j < i; j++) sql.Append(keys[j].Sql).Append(" = $k").Append(j).Append(" AND ");
                sql.Append(keys[i].Sql).Append(keys[i].Descending ? " < $k" : " > $k").Append(i).Append(')');
            }
            sql.Append(')');
            for (var i = 0; i < keys.Count; i++) cmd.Parameters.AddWithValue("$k" + i, cursor.SortKeys[i]);
        }

        sql.Append(" ORDER BY ");
        for (var i = 0; i < keys.Count; i++)
        {
            if (i > 0) sql.Append(", ");
            sql.Append(keys[i].Sql).Append(keys[i].Descending ? " DESC" : " ASC");
        }
        sql.Append(" LIMIT $limit;");

        cmd.CommandText = sql.ToString();
        cmd.Parameters.AddWithValue("$limit", limit);

        var rows = new List<SessionMetadata>(limit);
        object[]? last = null;
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                rows.Add(Read(reader));
                last = new object[keys.Count];
                for (var i = 0; i < keys.Count; i++) last[i] = reader.GetValue(RowColumnCount + i);
            }
        }
        tx.Commit();

        var next = rows.Count == limit && last is not null
            ? new PageCursor(fingerprint, revision, last)
            : null;

        return new CatalogPage(PageOutcome.Ok, rows, next, revision);
    }

    /// <summary>One component of the list's order, as SQL, with its direction.</summary>
    private sealed record SortKey(string Sql, bool Descending);

    /// <summary>
    /// The full order for a sort: the chosen field, then newest first within it, then the
    /// primary key. The default order is exactly the paging index's, so it stays an index walk.
    /// </summary>
    private static IReadOnlyList<SortKey> KeysFor(SortOrder sort)
    {
        var descending = sort.Descending;
        // Pinned rows first in every order, both directions: a pin is about the row, not the value.
        var keys = new List<SortKey>(9) { new("local_pinned", Descending: true) };
        switch (sort.Field)
        {
            case SortField.Title:
                keys.Add(new("COALESCE(NULLIF(local_title,''),title) COLLATE " + UiCollation.Name, descending));
                break;
            case SortField.Group:
                keys.Add(new($"(({EffectiveGroupSql}) IS NULL)", false));         // ungrouped last
                keys.Add(new($"COALESCE({EffectiveGroupSql},'') COLLATE {UiCollation.Name}", descending));
                break;
            case SortField.Source:
                keys.Add(new(SourceRankSql, descending));
                break;
            case SortField.Project:
                // No directory last. A source can record it as '' as well as NULL; both are
                // the one "no directory" section, so both must sort together.
                keys.Add(new("(NULLIF(project_ref,'') IS NULL)", false));
                keys.Add(new($"COALESCE(project_ref,'') COLLATE {UiCollation.Name}", descending));
                break;
            case SortField.AppState:
                keys.Add(new(StandingSql, descending));
                break;
            case SortField.Size:
                // No file last in both directions, as with any missing value.
                keys.Add(new("(transcript_bytes IS NULL)", false));
                keys.Add(new("COALESCE(transcript_bytes,0)", descending));
                break;
            case SortField.LastActivity when !descending:
                // Oldest first still leaves rows with no timestamp at the end.
                keys.Add(new("(last_activity_utc IS NULL)", false));
                break;
        }

        keys.Add(new(SortTimeSql, sort.Field != SortField.LastActivity || descending));
        keys.Add(new("backend", false));
        keys.Add(new("account_context", false));
        keys.Add(new("organization_context", false));
        keys.Add(new("native_id", false));
        return keys;
    }

    /// <summary>
    /// Most relevant first: a conversation whose title, name, note or folder has every word
    /// before one where some are only in what was said; then by how many messages say them;
    /// then newest first. Reversed, the same order backwards.
    /// </summary>
    private static IReadOnlyList<SortKey> RelevanceKeys(
        SortOrder sort, IReadOnlyList<SearchTerm> query, IReadOnlyDictionary<string, string>? said, bool mineOnly)
    {
        var descending = sort.Descending;
        var keys = new List<SortKey>(9) { new("local_pinned", Descending: true) };
        if (RelevanceSectionSql(query) is { } section) keys.Add(new(section, !descending));
        if (HitsSql(query, said, mineOnly) is { } hits) keys.Add(new(hits, descending));
        keys.Add(new(SortTimeSql, descending));
        keys.Add(new("backend", false));
        keys.Add(new("account_context", false));
        keys.Add(new("organization_context", false));
        keys.Add(new("native_id", false));
        return keys;
    }

    /// <summary>'0' when every word to find is in what the catalog names, '1' otherwise; null without such words.</summary>
    private static string? RelevanceSectionSql(IReadOnlyList<SearchTerm> query)
    {
        var parts = query.Select((term, i) => (term, i)).Where(x => !x.term.Exclude)
            .Select(x => MetadataMatchSql(TermParameter(x.i), x.term.Text)).ToList();
        return parts.Count == 0 ? null : $"(CASE WHEN {string.Join(" AND ", parts)} THEN '0' ELSE '1' END)";
    }

    /// <summary>How many messages say the words to find, summed; null when nothing was looked up in the index.</summary>
    private static string? HitsSql(IReadOnlyList<SearchTerm> query, IReadOnlyDictionary<string, string>? said, bool mineOnly)
    {
        if (said is null) return null;
        var parts = query.Where(t => !t.Exclude)
            .Select(t => said.TryGetValue(SaidKey(t.Text, mineOnly), out var table)
                ? $"COALESCE((SELECT hits FROM temp.{table} WHERE conversation={ContentIndex.KeySql}),0)"
                : null)
            .OfType<string>()
            .ToList();
        return parts.Count == 0 ? null : "(" + string.Join("+", parts) + ")";
    }

    /// <summary>
    /// The section a row falls in under a sort, in the same terms <see cref="SectionCounts"/>
    /// counts by — so a section heading can say how many rows it has, loaded or not. Null for
    /// an order without sections (and for time, when no <paramref name="dates"/> are given).
    /// Pinned rows are their own section, first, whatever the order.
    /// </summary>
    /// <param name="terms">The words searched for, for the relevance sections.</param>
    public static string? SectionKey(SessionMetadata row, SortField field, DateBuckets? dates = null,
        IReadOnlyList<string>? terms = null)
    {
        if (field == SortField.LastActivity && dates is null) return null;
        if (field == SortField.Title) return null;
        if (field == SortField.Relevance && terms is not { Count: > 0 }) return null;
        if (row.LocalPinned) return PinnedSection;
        return field switch
        {
            SortField.Group => GroupRules.Effective(row.LocalGroup, row.SidebarGroup) ?? string.Empty,
            SortField.Source => row.Key.Backend,
            SortField.Project => row.ProjectRef ?? string.Empty,
            SortField.AppState => ((int)OrderingRules.Standing(row.Key.Backend, row.NativeArchiveState, row.ContinuedIn is not null))
                .ToString(CultureInfo.InvariantCulture),
            SortField.LastActivity => dates!.KeyOf(row.LastActivityUtc),
            SortField.Size => SizeSection(row.TranscriptBytes),
            SortField.Relevance => ContentOnlyTerm(row, terms!) is null ? RelevanceNamedKey : RelevanceSaidKey,
            _ => null,
        };
    }

    /// <summary>
    /// How many rows each category holds under the same filter, keyed like
    /// <see cref="SectionKey"/>. Empty for a sort that is not by category.
    /// </summary>
    public IReadOnlyDictionary<string, int> SectionCounts(
        Visibility visibility,
        string? searchText,
        GroupFilter group,
        BackendFilter backend,
        SortField field,
        DateBuckets? dates = null,
        DateTimeOffset? since = null,
        bool mineOnly = false)
    {
        var key = field switch
        {
            SortField.Group => $"COALESCE({EffectiveGroupSql},'')",
            SortField.Source => "backend",
            SortField.Project => "COALESCE(project_ref,'')",
            SortField.AppState => $"CAST(({StandingSql}) AS TEXT)",
            SortField.LastActivity when dates is not null => DateBuckets.Sql,
            SortField.Size => SizeSectionSql,
            SortField.Relevance => RelevanceSectionSql(SearchKeys.Parse(searchText)),
            _ => null,
        };
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        if (key is null) return counts;

        var said = PrepareContentSearch(searchText, mineOnly);
        using var cmd = _connection.CreateCommand();
        var sql = new StringBuilder("SELECT CASE WHEN local_pinned=1 THEN $pinned ELSE (")
            .Append(key).Append(") END AS k, COUNT(*) FROM sessions");
        cmd.Parameters.AddWithValue("$pinned", PinnedSection);
        dates?.Bind(cmd);
        AppendFilter(sql, cmd, visibility, searchText, group, backend, since, said, mineOnly);
        cmd.CommandText = sql.Append(" GROUP BY k;").ToString();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) counts[reader.GetString(0)] = reader.GetInt32(1);
        return counts;
    }

    /// <summary>
    /// How many rows the same filter reaches. Shares its WHERE clause with <see cref="Page"/>,
    /// so "显示 100 / 共 314 条" can never promise rows that paging cannot deliver.
    /// </summary>
    public int Count(
        Visibility visibility,
        string? searchText = null,
        GroupFilter group = default,
        BackendFilter backend = default,
        DateTimeOffset? since = null,
        bool mineOnly = false)
    {
        var said = PrepareContentSearch(searchText, mineOnly);
        using var cmd = _connection.CreateCommand();
        var sql = new StringBuilder("SELECT COUNT(*) FROM sessions");
        AppendFilter(sql, cmd, visibility, searchText, group, backend, since, said, mineOnly);
        cmd.CommandText = sql.Append(';').ToString();
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>Size of each view for one app, ignoring search and group, as a tab badge.</summary>
    public ViewCounts CountViews(BackendFilter backend = default)
    {
        using var cmd = _connection.CreateCommand();
        var restriction = backend.Predicate is { } p ? " AND " + p : string.Empty;
        cmd.CommandText =
            $"SELECT visibility, COUNT(*) FROM sessions WHERE merged_into IS NULL{restriction} GROUP BY visibility;";
        int active = 0, archived = 0;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.GetString(0) == nameof(Visibility.ArchivedView)) archived = reader.GetInt32(1);
            else active += reader.GetInt32(1);
        }
        return new ViewCounts(active, archived);
    }

    /// <summary>
    /// Appends the WHERE clause for one view, search, group and app, and binds its parameters.
    ///
    /// <para>Every search word must match (AND), and a word may match the title, the name or
    /// note the user gave it, the working directory, what was said or — when it looks like one —
    /// the session id (OR). So "wheat 病级" narrows as words are added, and a folder name or a
    /// pasted id finds its conversations as readily as a title does. A word typed -词 must match
    /// none of these. Wildcards typed are always literals.</para>
    ///
    /// <para>A terminal transcript folded into its desktop session is never listed on its own,
    /// and a time limit drops rows with no timestamp.</para>
    /// </summary>
    private static void AppendFilter(
        StringBuilder sql, SqliteCommand cmd,
        Visibility visibility, string? searchText, GroupFilter group, BackendFilter backend,
        DateTimeOffset? since = null, IReadOnlyDictionary<string, string>? said = null, bool mineOnly = false)
    {
        sql.Append(" WHERE visibility=$visibility AND merged_into IS NULL");
        cmd.Parameters.AddWithValue("$visibility", visibility.ToString());
        if (since is { } from)
        {
            sql.Append(" AND last_activity_utc >= $since");
            cmd.Parameters.AddWithValue("$since", Utc(from));
        }

        var query = SearchKeys.Parse(searchText);
        for (var i = 0; i < query.Count; i++)
        {
            var (text, exclude) = (query[i].Text, query[i].Exclude);
            var name = TermParameter(i);
            cmd.Parameters.AddWithValue(name, "%" + EscapeLike(text) + "%");
            sql.Append(exclude ? " AND NOT (" : " AND (").Append(MetadataMatchSql(name, text));
            // What was said: the conversations the index found the word in, looked up once for
            // the whole search (see PrepareContentSearch).
            if (said is not null && said.TryGetValue(SaidKey(text, mineOnly), out var table))
                sql.Append(" OR ").Append(ContentIndex.KeySql)
                   .Append(" IN (SELECT conversation FROM temp.").Append(table).Append(')');
            sql.Append(')');
        }

        if (backend.Predicate is { } backendPredicate)
            sql.Append(" AND ").Append(backendPredicate);

        // Filtering on the expression rather than on local_group keeps a Claude-grouped
        // conversation in its Claude group until the user says otherwise.
        if (group.Kind == GroupFilterKind.Ungrouped)
            sql.Append(" AND ").Append(EffectiveGroupSql).Append(" IS NULL");
        else if (group.Kind == GroupFilterKind.Named)
        {
            sql.Append(" AND ").Append(EffectiveGroupSql).Append("=$group");
            cmd.Parameters.AddWithValue("$group", group.Name ?? string.Empty);
        }
    }

    private static string TermParameter(int i) => "$term" + i.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Whether a word is in what the catalog itself knows of a conversation: its title, the
    /// name and note the user gave it, its working directory, and — for a word that looks like
    /// one — its session id. Never NULL, so a word to leave out can be negated safely.
    /// </summary>
    private static string MetadataMatchSql(string name, string term)
    {
        var sql = new StringBuilder("(search_key LIKE ").Append(name).Append(" ESCAPE '\\'")
            .Append(" OR COALESCE(local_search_key,'') LIKE ").Append(name).Append(" ESCAPE '\\'")
            .Append(" OR COALESCE(project_key,'') LIKE ").Append(name).Append(" ESCAPE '\\'");
        if (SearchKeys.LooksLikeIdFragment(term))
            sql.Append(" OR lower(native_id) LIKE ").Append(name).Append(" ESCAPE '\\'")
               .Append(" OR lower(COALESCE(cli_session_id,'')) LIKE ").Append(name).Append(" ESCAPE '\\'");
        return sql.Append(')').ToString();
    }

    // ------------------------------------------------- local view archive

    /// <summary>
    /// Applies this launcher's own archive/restore. The row's preference revision guards
    /// against a stale caller; the journal entry and the row change share one transaction.
    /// Claude's own state is never touched.
    /// </summary>
    public ViewArchiveResult SetVisibility(
        ConversationKey key, Visibility target, long expectedRevision, string operationId)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);

        using var tx = _connection.BeginTransaction(deferred: false);

        if (TryGetExistingOperation(tx, operationId, out var replay))
        {
            tx.Commit();
            return replay;
        }

        var current = Load(tx, key) ?? throw new KeyNotFoundException(key.ToLogForm());
        var newRevision = current.PreferenceRevision + 1;
        var result = target == Visibility.ArchivedView
            ? ViewArchiveRules.Archive(current, expectedRevision, newRevision)
            : ViewArchiveRules.Restore(current, expectedRevision, newRevision);

        if (result.Outcome != ViewArchiveOutcome.Applied)
        {
            tx.Commit();
            return result;
        }

        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE sessions SET visibility=$visibility, preference_revision=$rev
                WHERE backend=$backend AND account_context=$account
                  AND organization_context=$org AND native_id=$native
                  AND preference_revision=$expected;
                """;
            cmd.Parameters.AddWithValue("$visibility", target.ToString());
            cmd.Parameters.AddWithValue("$rev", newRevision);
            cmd.Parameters.AddWithValue("$backend", key.Backend);
            cmd.Parameters.AddWithValue("$account", key.AccountContextId);
            cmd.Parameters.AddWithValue("$org", key.OrganizationContextId);
            cmd.Parameters.AddWithValue("$native", key.NativeSessionId);
            cmd.Parameters.AddWithValue("$expected", expectedRevision);
            if (cmd.ExecuteNonQuery() != 1)
            {
                tx.Rollback();
                return new ViewArchiveResult(ViewArchiveOutcome.Conflict, current, current.PreferenceRevision);
            }
        }

        WriteJournal(tx, operationId, "view-archive", current, result.Row);
        BumpRevision(tx);
        tx.Commit();
        return result;
    }

    // ----------------------------------------------------- local grouping

    /// <summary>
    /// Every group in one view with its size, largest first. A null name is the ungrouped
    /// bucket, which is listed like any other so it can be selected rather than inferred.
    /// </summary>
    public IReadOnlyList<GroupTally> Groups(Visibility visibility, BackendFilter backend = default)
    {
        using var cmd = _connection.CreateCommand();
        var restriction = backend.Predicate is { } p ? " AND " + p : string.Empty;
        cmd.CommandText = $"""
            SELECT {EffectiveGroupSql} AS g, COUNT(*) AS n
            FROM sessions
            WHERE visibility=$visibility AND merged_into IS NULL{restriction}
            GROUP BY g
            ORDER BY n DESC, g ASC;
            """;
        cmd.Parameters.AddWithValue("$visibility", visibility.ToString());

        var tallies = new List<GroupTally>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            tallies.Add(new GroupTally(reader.IsDBNull(0) ? null : reader.GetString(0), reader.GetInt32(1)));
        return tallies;
    }

    /// <summary>
    /// Puts every given conversation into one group. The name is stored exactly as passed —
    /// callers normalise with <c>GroupRules.Normalize</c> first, so two names that look the
    /// same cannot become two groups.
    /// </summary>
    public GroupWriteResult AssignGroup(IEnumerable<SessionMetadata> rows, string name)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return WriteGroup(rows, name);
    }

    /// <summary>
    /// Takes the conversations out of any group. Stores the explicit "no group" marker rather
    /// than null, so a conversation Claude groups stays out of that group too.
    /// </summary>
    public GroupWriteResult ClearGroup(IEnumerable<SessionMetadata> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return WriteGroup(rows, GroupRules.ExplicitlyUngrouped);
    }

    private GroupWriteResult WriteGroup(IEnumerable<SessionMetadata> rows, string value)
    {
        var targets = new List<GroupTarget>();
        var unchanged = 0;
        foreach (var row in rows)
        {
            if (string.Equals(row.LocalGroup, value, StringComparison.Ordinal)) unchanged++;
            else targets.Add(new GroupTarget(row.Key, row.PreferenceRevision, value, row.LocalGroup));
        }

        using var tx = _connection.BeginTransaction(deferred: false);
        var result = Apply(tx, targets) with { Unchanged = unchanged };
        tx.Commit();
        return result;
    }

    /// <summary>
    /// Gives every conversation that has no group at all one named after its working
    /// directory. Rows the user already grouped — including ones they explicitly took out of
    /// a group — and rows Claude groups are left alone, so this can be run repeatedly.
    /// </summary>
    public GroupWriteResult GroupByProject(Visibility visibility)
    {
        using var tx = _connection.BeginTransaction(deferred: false);

        var targets = new List<GroupTarget>();
        var unnamed = 0;
        using (var select = _connection.CreateCommand())
        {
            select.Transaction = tx;
            select.CommandText = """
                SELECT backend,account_context,organization_context,native_id,
                       project_ref,preference_revision
                FROM sessions
                WHERE visibility=$visibility
                  AND merged_into IS NULL
                  AND local_group IS NULL
                  AND (sidebar_group IS NULL OR sidebar_group='');
                """;
            select.Parameters.AddWithValue("$visibility", visibility.ToString());
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                var name = reader.IsDBNull(4) ? null : GroupRules.FromProjectPath(reader.GetString(4));
                if (name is null)
                {
                    unnamed++;          // no working directory to name a group after
                    continue;
                }
                // Selected only where local_group IS NULL, so that is what an undo restores.
                targets.Add(new GroupTarget(
                    new ConversationKey(reader.GetString(0), reader.GetString(1),
                                        reader.GetString(2), reader.GetString(3)),
                    reader.GetInt64(5), name, Before: null));
            }
        }

        var result = Apply(tx, targets) with { Unchanged = unnamed };
        tx.Commit();
        return result;
    }

    /// <summary>One row a group write means to change, and what it held when the caller read it.</summary>
    private sealed record GroupTarget(ConversationKey Key, long Expected, string Value, string? Before);

    /// <summary>
    /// Writes the group of each target, guarded by the preference revision the caller read.
    /// No journal entry: unlike the native operations the journal exists for, this touches
    /// only this tool's own column, and every change is reported back so it can be undone.
    /// </summary>
    private GroupWriteResult Apply(SqliteTransaction tx, IReadOnlyList<GroupTarget> targets)
    {
        int applied = 0, conflict = 0;
        var changes = new List<GroupChange>();
        if (targets.Count > 0)
        {
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE sessions
                SET local_group=$value, preference_revision=preference_revision+1
                WHERE backend=$backend AND account_context=$account
                  AND organization_context=$org AND native_id=$native
                  AND preference_revision=$expected;
                """;
            foreach (var target in targets)
            {
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("$value", target.Value);
                AddKey(cmd, target.Key);
                cmd.Parameters.AddWithValue("$expected", target.Expected);
                if (cmd.ExecuteNonQuery() == 1)
                {
                    applied++;
                    changes.Add(new GroupChange(target.Key, target.Before, target.Value, target.Expected + 1));
                }
                else conflict++;
            }
        }

        // Only a real change expires outstanding cursors.
        if (applied > 0) BumpRevision(tx);
        return new GroupWriteResult(applied, 0, conflict) { Changes = changes };
    }

    /// <summary>
    /// Puts groups back the way a write found them: the undo of <see cref="AssignGroup"/>,
    /// <see cref="ClearGroup"/> and <see cref="GroupByProject"/>.
    ///
    /// <para>A row is reverted only while it still carries the revision that write produced.
    /// One the user has changed again since is left alone and counted as a conflict, so an
    /// undo can never quietly throw away a newer choice.</para>
    /// </summary>
    public GroupWriteResult RevertGroups(IEnumerable<GroupChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);

        using var tx = _connection.BeginTransaction(deferred: false);
        int applied = 0, conflict = 0;
        var reverted = new List<GroupChange>();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE sessions
                SET local_group=$before, preference_revision=preference_revision+1
                WHERE backend=$backend AND account_context=$account
                  AND organization_context=$org AND native_id=$native
                  AND preference_revision=$revision;
                """;
            foreach (var change in changes)
            {
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("$before", (object?)change.Before ?? DBNull.Value);
                AddKey(cmd, change.Key);
                cmd.Parameters.AddWithValue("$revision", change.Revision);
                if (cmd.ExecuteNonQuery() == 1)
                {
                    applied++;
                    reverted.Add(change with { Revision = change.Revision + 1 });
                }
                else conflict++;
            }
        }

        if (applied > 0) BumpRevision(tx);
        tx.Commit();
        return new GroupWriteResult(applied, 0, conflict) { Changes = reverted };
    }

    // ------------------------------------------------- pin, name and note

    /// <summary>
    /// A terminal transcript folded into another row — its desktop session, or for a copy
    /// nothing was said in, the row that holds the original — hands over what the user set on
    /// it: a name, a note, a pin, and a group where that row has none at all, so nothing the
    /// user did disappears with the row it was set on. Handed over once, then cleared on the
    /// folded row, so clearing it later on the other row sticks.
    /// </summary>
    /// <returns>How many rows took something over.</returns>
    public int AdoptMergedPreferences()
    {
        using var tx = _connection.BeginTransaction(deferred: false);
        var pairs = new List<(ConversationKey Twin, ConversationKey Desktop)>();
        using (var select = _connection.CreateCommand())
        {
            select.Transaction = tx;
            select.CommandText = """
                SELECT t.backend,t.account_context,t.organization_context,t.native_id,
                       d.backend,d.account_context,d.organization_context,d.native_id
                FROM sessions t
                CROSS JOIN sessions d
                WHERE t.merged_into IS NOT NULL
                  AND (t.local_title IS NOT NULL OR t.local_note IS NOT NULL
                       OR t.local_pinned=1 OR t.local_group IS NOT NULL)
                  AND d.native_id=t.merged_into AND d.backend IN ('desktop-code','cli-code')
                  AND d.merged_into IS NULL;
                """;
            using var reader = select.ExecuteReader();
            while (reader.Read())
                pairs.Add((new ConversationKey(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)),
                           new ConversationKey(reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7))));
        }

        var adopted = 0;
        foreach (var (twinKey, desktopKey) in pairs)
        {
            if (Load(tx, twinKey) is not { } twin || Load(tx, desktopKey) is not { } desktop) continue;
            var title = desktop.LocalTitle ?? twin.LocalTitle;
            var note = desktop.LocalNote ?? twin.LocalNote;
            var pinned = desktop.LocalPinned || twin.LocalPinned;
            var group = desktop.LocalGroup ?? (string.IsNullOrEmpty(desktop.SidebarGroup) ? twin.LocalGroup : null);
            var changes = title != desktop.LocalTitle || note != desktop.LocalNote
                          || pinned != desktop.LocalPinned || group != desktop.LocalGroup;

            using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE sessions
                SET local_title=$title, local_note=$note, local_pinned=$pinned, local_group=$group,
                    local_search_key=$searchKey, preference_revision=preference_revision+1
                WHERE backend=$backend AND account_context=$account
                  AND organization_context=$org AND native_id=$native;
                """;
            if (changes)
            {
                cmd.Parameters.AddWithValue("$title", (object?)title ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$note", (object?)note ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$pinned", pinned ? 1 : 0);
                cmd.Parameters.AddWithValue("$group", (object?)group ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$searchKey", (object?)LocalSearchKey(title, note) ?? DBNull.Value);
                AddKey(cmd, desktopKey);
                cmd.ExecuteNonQuery();
                adopted++;
            }

            // The terminal row keeps nothing to hand over a second time.
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("$title", DBNull.Value);
            cmd.Parameters.AddWithValue("$note", DBNull.Value);
            cmd.Parameters.AddWithValue("$pinned", 0);
            cmd.Parameters.AddWithValue("$group", DBNull.Value);
            cmd.Parameters.AddWithValue("$searchKey", DBNull.Value);
            AddKey(cmd, twinKey);
            cmd.ExecuteNonQuery();
        }

        if (pairs.Count > 0) BumpRevision(tx);
        tx.Commit();
        return adopted;
    }

    /// <summary>Pins or unpins conversations in this launcher: pinned rows lead every order.</summary>
    public PreferenceWriteResult SetPinned(IEnumerable<SessionMetadata> rows, bool pinned)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return WritePreferences(rows.Select(r => (r, PreferenceField.Pinned, (string?)(pinned ? "1" : "0"))).ToList());
    }

    /// <summary>Names a conversation in this launcher only; null or blank goes back to the source's title.</summary>
    public PreferenceWriteResult SetLocalTitle(SessionMetadata row, string? title)
    {
        ArgumentNullException.ThrowIfNull(row);
        return WritePreferences([(row, PreferenceField.Title, Clean(title))]);
    }

    /// <summary>Sets the user's own note on a conversation; null or blank removes it.</summary>
    public PreferenceWriteResult SetNote(SessionMetadata row, string? note)
    {
        ArgumentNullException.ThrowIfNull(row);
        return WritePreferences([(row, PreferenceField.Note, Clean(note))]);
    }

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static string? Current(SessionMetadata row, PreferenceField field) => field switch
    {
        PreferenceField.Pinned => row.LocalPinned ? "1" : "0",
        PreferenceField.Title => row.LocalTitle,
        _ => row.LocalNote,
    };

    /// <summary>The search key of a name and a note together; null when there is neither.</summary>
    private static string? LocalSearchKey(string? title, string? note)
    {
        var text = string.Join(' ', new[] { title, note }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return text.Length == 0 ? null : SearchKeys.Normalize(text);
    }

    /// <summary>
    /// Writes each row's setting, guarded by the preference revision the caller read, and
    /// reports every change with its before-value so it can be undone.
    /// </summary>
    private PreferenceWriteResult WritePreferences(IReadOnlyList<(SessionMetadata Row, PreferenceField Field, string? Value)> targets)
    {
        using var tx = _connection.BeginTransaction(deferred: false);
        int applied = 0, unchanged = 0, conflict = 0;
        var changes = new List<PreferenceChange>();
        foreach (var (row, field, value) in targets)
        {
            var before = Current(row, field);
            if (string.Equals(before, value, StringComparison.Ordinal))
            {
                unchanged++;
                continue;
            }
            var title = field == PreferenceField.Title ? value : row.LocalTitle;
            var note = field == PreferenceField.Note ? value : row.LocalNote;
            if (WritePreference(tx, row.Key, field, value, LocalSearchKey(title, note), row.PreferenceRevision))
            {
                applied++;
                changes.Add(new PreferenceChange(row.Key, field, before, value, row.PreferenceRevision + 1));
            }
            else conflict++;
        }
        if (applied > 0) BumpRevision(tx);
        tx.Commit();
        return new PreferenceWriteResult(applied, unchanged, conflict) { Changes = changes };
    }

    private bool WritePreference(
        SqliteTransaction tx, ConversationKey key, PreferenceField field, string? value, string? searchKey, long expected)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        // The column comes from the enum, never from input.
        var column = field switch
        {
            PreferenceField.Pinned => "local_pinned",
            PreferenceField.Title => "local_title",
            _ => "local_note",
        };
        cmd.CommandText = $"""
            UPDATE sessions
            SET {column}=$value, local_search_key=$searchKey, preference_revision=preference_revision+1
            WHERE backend=$backend AND account_context=$account
              AND organization_context=$org AND native_id=$native
              AND preference_revision=$expected;
            """;
        cmd.Parameters.AddWithValue("$value", field == PreferenceField.Pinned
            ? value == "1" ? 1 : 0
            : (object?)value ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$searchKey", (object?)searchKey ?? DBNull.Value);
        AddKey(cmd, key);
        cmd.Parameters.AddWithValue("$expected", expected);
        return cmd.ExecuteNonQuery() == 1;
    }

    /// <summary>
    /// Puts pins, names and notes back the way a write found them. A row is reverted only while
    /// it still carries the revision that write produced, so an undo never throws away a newer
    /// change; one that moved on is counted as a conflict and left alone.
    /// </summary>
    public PreferenceWriteResult RevertPreferences(IEnumerable<PreferenceChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        using var tx = _connection.BeginTransaction(deferred: false);
        int applied = 0, conflict = 0;
        var reverted = new List<PreferenceChange>();
        foreach (var change in changes)
        {
            var current = Load(tx, change.Key);
            if (current is null || current.PreferenceRevision != change.Revision)
            {
                conflict++;
                continue;
            }
            var title = change.Field == PreferenceField.Title ? change.Before : current.LocalTitle;
            var note = change.Field == PreferenceField.Note ? change.Before : current.LocalNote;
            if (WritePreference(tx, change.Key, change.Field, change.Before, LocalSearchKey(title, note), change.Revision))
            {
                applied++;
                reverted.Add(change with { Before = change.After, After = change.Before, Revision = change.Revision + 1 });
            }
            else conflict++;
        }
        if (applied > 0) BumpRevision(tx);
        tx.Commit();
        return new PreferenceWriteResult(applied, 0, conflict) { Changes = reverted };
    }

    private static void AddKey(SqliteCommand cmd, ConversationKey key)
    {
        cmd.Parameters.AddWithValue("$backend", key.Backend);
        cmd.Parameters.AddWithValue("$account", key.AccountContextId);
        cmd.Parameters.AddWithValue("$org", key.OrganizationContextId);
        cmd.Parameters.AddWithValue("$native", key.NativeSessionId);
    }

    // ------------------------------------------------------------ helpers

    /// <summary>Escapes the escape character first, then the LIKE wildcards.</summary>
    public static string EscapeLike(string input) => input
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

    private static string Fingerprint(
        Visibility visibility, string? search, GroupFilter group, BackendFilter backend, SortOrder sort,
        DateTimeOffset? since) =>
        $"{visibility}|{SearchKeys.Normalize(search ?? string.Empty)}|{group.Fingerprint}|{backend.Fingerprint}"
        + $"|{sort.Fingerprint}|t:{since?.UtcTicks}";

    private long BumpRevision(SqliteTransaction tx)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "UPDATE catalog_version SET revision=revision+1 WHERE singleton_id=1; " +
            "SELECT revision FROM catalog_version WHERE singleton_id=1;";
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private bool TryGetExistingOperation(SqliteTransaction tx, string operationId, out ViewArchiveResult result)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT after_state_json FROM operations WHERE operation_id=$id;";
        cmd.Parameters.AddWithValue("$id", operationId);
        var existing = cmd.ExecuteScalar();
        if (existing is null or DBNull)
        {
            result = default!;
            return false;
        }
        // Replaying a completed operation id returns the original outcome rather than acting twice.
        result = new ViewArchiveResult(ViewArchiveOutcome.AlreadyApplied, null!, CurrentRevision());
        return true;
    }

    private void WriteJournal(
        SqliteTransaction tx, string operationId, string kind, SessionMetadata before, SessionMetadata after)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO operations(operation_id,kind,state,created_utc,before_state_json,after_state_json,verified_utc)
            VALUES($id,$kind,'Verified',$now,$before,$after,$now);
            """;
        cmd.Parameters.AddWithValue("$id", operationId);
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$now",
            DateTimeOffset.UtcNow.UtcDateTime.ToString(UtcFormat, CultureInfo.InvariantCulture));
        // Journal holds visibility transitions only — never titles or bodies.
        cmd.Parameters.AddWithValue("$before", $"{{\"visibility\":\"{before.Visibility}\"}}");
        cmd.Parameters.AddWithValue("$after", $"{{\"visibility\":\"{after.Visibility}\"}}");
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// A conversation Claude carried on across transcripts, in order: the terminal transcripts
    /// it was continued from, oldest first, then the desktop session that carries it now —
    /// for the desktop session and for each of its earlier parts alike. Empty for any other
    /// row, and for a desktop session that was never continued.
    /// </summary>
    public IReadOnlyList<SessionMetadata> ContinuationChain(SessionMetadata row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var desktop = row.Key.Backend == ConversationKey.DesktopCode ? row.Key.NativeSessionId : row.ContinuedIn;
        if (desktop is null) return [];

        using var cmd = _connection.CreateCommand();
        // In the order the conversation happened: each earlier transcript stopped being
        // written when the next one began, so their last activity is their order.
        cmd.CommandText = $"""
            SELECT {RowColumns}
            FROM sessions
            WHERE merged_into IS NULL
              AND (continued_in=$desktop OR (backend='desktop-code' AND native_id=$desktop))
            ORDER BY backend='desktop-code', COALESCE(last_activity_utc,''), native_id;
            """;
        cmd.Parameters.AddWithValue("$desktop", desktop);
        var chain = new List<SessionMetadata>();
        using (var reader = cmd.ExecuteReader())
            while (reader.Read()) chain.Add(Read(reader));
        return chain.Count > 1 ? chain : [];
    }

    /// <param name="From">For a copy: the row that shows the session it was resumed from.</param>
    /// <param name="Into">Copies of this row's transcript that something was said in, oldest first.</param>
    /// <param name="Folded">Copies nothing was said in, folded into this row.</param>
    public sealed record ResumeRelatives(SessionMetadata? From, IReadOnlyList<SessionMetadata> Into, int Folded)
    {
        public static ResumeRelatives None { get; } = new(null, [], 0);

        public bool Any => From is not null || Into.Count > 0 || Folded > 0;
    }

    /// <summary>
    /// A terminal conversation's resumes: the session a copy was resumed from, shown as the row
    /// that lists it (a desktop session, when that transcript is one's); and for any row, the
    /// copies resumed from its transcript — listed ones, and a count of those folded into it.
    /// </summary>
    public ResumeRelatives Resumes(SessionMetadata row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var from = row.ResumedFrom is { } origin ? ListedHolderOf(origin, row.Key.Backend) : null;

        var transcript = row.Key.Backend is ConversationKey.CliCode or ConversationKey.Codex ? row.Key.NativeSessionId
            : row.Key.Backend == ConversationKey.DesktopCode ? row.CliSessionId : null;
        var into = new List<SessionMetadata>();
        if (transcript is not null)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $"""
                SELECT {RowColumns} FROM sessions
                WHERE resumed_from=$t AND merged_into IS NULL
                ORDER BY COALESCE(last_activity_utc,''), native_id;
                """;
            cmd.Parameters.AddWithValue("$t", transcript);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) into.Add(Read(reader));
        }

        int folded;
        using (var count = _connection.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) FROM sessions WHERE resumed_from IS NOT NULL AND merged_into=$n;";
            count.Parameters.AddWithValue("$n", row.Key.NativeSessionId);
            folded = Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        return from is null && into.Count == 0 && folded == 0 ? ResumeRelatives.None : new(from, into, folded);
    }

    /// <summary>
    /// The row that shows a transcript: a terminal one's own or the one it is folded into, a
    /// Codex task's own.
    /// </summary>
    private SessionMetadata? ListedHolderOf(string id, string backend)
    {
        using var cmd = _connection.CreateCommand();
        var source = backend == ConversationKey.Codex ? ConversationKey.Codex : ConversationKey.CliCode;
        cmd.CommandText = $"""
            SELECT {RowColumns} FROM sessions
            WHERE native_id=(SELECT COALESCE(merged_into, native_id) FROM sessions
                             WHERE backend=$backend AND native_id=$id LIMIT 1)
              AND merged_into IS NULL
            ORDER BY backend=$backend LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$backend", source);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    private SessionMetadata? Load(SqliteTransaction tx, ConversationKey key)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            SELECT {RowColumns}
            FROM sessions
            WHERE backend=$backend AND account_context=$account
              AND organization_context=$org AND native_id=$native;
            """;
        cmd.Parameters.AddWithValue("$backend", key.Backend);
        cmd.Parameters.AddWithValue("$account", key.AccountContextId);
        cmd.Parameters.AddWithValue("$org", key.OrganizationContextId);
        cmd.Parameters.AddWithValue("$native", key.NativeSessionId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    private static SessionMetadata Read(SqliteDataReader r) => new()
    {
        Key = new ConversationKey(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)),
        CliSessionId = r.IsDBNull(4) ? null : r.GetString(4),
        Title = r.GetString(5),
        SearchKey = r.GetString(6),
        ProjectRef = r.IsDBNull(7) ? null : r.GetString(7),
        NativeOpenRef = r.IsDBNull(8) ? null : r.GetString(8),
        LastActivityUtc = r.IsDBNull(9)
            ? null
            : DateTimeOffset.ParseExact(r.GetString(9), UtcFormat,
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
        SourceRevision = r.GetString(10),
        SidebarGroup = r.IsDBNull(11) ? null : r.GetString(11),
        TitleIsPlaceholder = r.GetInt32(12) == 1,
        Visibility = Enum.Parse<Visibility>(r.GetString(13)),
        NativeArchiveState = Enum.Parse<NativeArchiveState>(r.GetString(14)),
        SourceCoverage = Enum.Parse<SourceCoverage>(r.GetString(15)),
        LocalPinned = r.GetInt32(16) == 1,
        PreferenceRevision = r.GetInt64(17),
        LocalGroup = r.IsDBNull(18) ? null : r.GetString(18),
        LocalTitle = r.IsDBNull(19) ? null : r.GetString(19),
        LocalNote = r.IsDBNull(20) ? null : r.GetString(20),
        MergedInto = r.IsDBNull(21) ? null : r.GetString(21),
        TranscriptBytes = r.IsDBNull(22) ? null : r.GetInt64(22),
        ContinuedIn = r.IsDBNull(23) ? null : r.GetString(23),
        ResumedFrom = r.IsDBNull(24) ? null : r.GetString(24),
        SourceGoneUtc = r.IsDBNull(25)
            ? null
            : DateTimeOffset.ParseExact(r.GetString(25), UtcFormat,
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
        EarlierParts = r.GetInt32(26),
    };

    private void Execute(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();
}
