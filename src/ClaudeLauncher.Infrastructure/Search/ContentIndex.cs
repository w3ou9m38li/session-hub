using System.Globalization;
using ClaudeLauncher.Core.Contracts;
using ClaudeLauncher.Infrastructure.Backends;
using ClaudeLauncher.Infrastructure.Catalog;
using Microsoft.Data.Sqlite;

namespace ClaudeLauncher.Infrastructure.Search;

/// <summary>
/// A full-text index of what the user and the assistant said, so a search finds a
/// conversation by its content and not only by its title. It is a database of its own beside
/// the catalog — the catalog holds no transcript text — and can be deleted at any time: it is
/// rebuilt from the transcripts. Tool output and app context are never indexed.
///
/// <para>Indexing is incremental. A transcript is read again only when its length or write
/// time changed, and one that merely grew is read from where the previous pass stopped.</para>
///
/// <para>Safe to use from two threads — an update running in the background while the
/// preview asks where a term was said: each database step holds the lock briefly, and
/// reading the transcripts, the slow part, holds nothing.</para>
/// </summary>
public sealed class ContentIndex : IDisposable
{
    /// <summary>Longest text kept per message: enough to find it, bounded so one pasted log cannot bloat the index.</summary>
    public const int MaxMessageChars = 20_000;

    /// <summary>Shortest term the index answers; one character would match nearly everything.</summary>
    public const int MinTermLength = 2;

    /// <summary>The layout below. An index written by any other layout is dropped and rebuilt.</summary>
    /// <remarks>3: what a copy holds of its own only (<see cref="Source.Part"/>), and the user's words
    /// after an app's reminder in the same message.</remarks>
    private const int Layout = 3;

    private readonly SqliteConnection _connection;
    private readonly Lock _gate = new();

    public string DatabasePath { get; }

    public ContentIndex(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = databasePath;
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());
        _connection.Open();
        try
        {
            Prepare();
        }
        catch
        {
            // A file that is not a usable database: let go of it, so it can be deleted and rebuilt.
            _connection.Dispose();
            throw;
        }
    }

    private void Prepare()
    {
        Execute("PRAGMA journal_mode=WAL;");
        Execute("PRAGMA busy_timeout=5000;");
        // Derived data: a transaction lost to a power cut is simply indexed again.
        Execute("PRAGMA synchronous=NORMAL;");
        if (Scalar("PRAGMA user_version;") != Layout)
        {
            Execute("DROP TABLE IF EXISTS messages; DROP TABLE IF EXISTS sources;");
            Execute($"PRAGMA user_version={Layout};");
        }
        // A conversation's messages have rowids (id << 32) + n: one rowid range, so replacing
        // or dropping them never scans the index. The conversation column repeats the name
        // for the catalog's search, which asks which conversations hold a term.
        Execute("""
            CREATE TABLE IF NOT EXISTS sources (
              id INTEGER PRIMARY KEY,
              conversation TEXT NOT NULL UNIQUE,
              path TEXT NOT NULL,
              length INTEGER NOT NULL,
              written INTEGER NOT NULL,
              next_offset INTEGER NOT NULL,
              next_message INTEGER NOT NULL,
              only_session TEXT
            );
            CREATE VIRTUAL TABLE IF NOT EXISTS messages USING fts5(
              text, conversation UNINDEXED, role UNINDEXED, stamp UNINDEXED, tokenize='trigram');
            """);
    }

    /// <summary>
    /// Opens the index, starting it over when the file cannot be used — it holds nothing that
    /// is not in the transcripts. Null when even a new one cannot be made; search then covers
    /// titles and folders only.
    /// </summary>
    public static ContentIndex? OpenOrRebuild(string databasePath)
    {
        try
        {
            return new ContentIndex(databasePath);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            try
            {
                SqliteConnection.ClearAllPools();
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                    if (File.Exists(databasePath + suffix)) File.Delete(databasePath + suffix);
                return new ContentIndex(databasePath);
            }
            catch (Exception again) when (again is SqliteException or IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    /// <summary>How a catalog row is named here: its conversation key's four parts.</summary>
    public static string KeyOf(ConversationKey key) =>
        $"{key.Backend}|{key.AccountContextId}|{key.OrganizationContextId}|{key.NativeSessionId}";

    /// <summary>The same name, as a SQL expression over the catalog's sessions table.</summary>
    public const string KeySql = "(backend||'|'||account_context||'|'||organization_context||'|'||native_id)";

    /// <summary>A conversation and the transcript that holds it.</summary>
    /// <param name="Part">
    /// For a copy that began with another conversation's history — a resumed Claude session, a
    /// forked Codex task — only what was said after the copy is indexed here; the copied part is
    /// found in the original.
    /// </param>
    public sealed record Source(string Conversation, string TranscriptPath, OwnPart Part = default);

    /// <param name="Indexed">Transcripts read this time, whole or from where the last pass stopped.</param>
    /// <param name="Removed">Conversations no longer listed, dropped from the index.</param>
    public sealed record UpdateRun(int Sources, int Indexed, int Removed, int Messages)
    {
        public bool ChangedAnything => Indexed + Removed > 0;
    }

    private sealed record Known(long Id, string Path, long Length, long Written, long NextOffset, long NextMessage,
        string? OnlySession);

    /// <summary>
    /// Brings the index up to date with these transcripts. <paramref name="progress"/> counts
    /// the transcripts that have to be read — none, most of the time.
    /// </summary>
    /// <param name="listed">
    /// Every conversation still listed, whether or not its transcript could be found this time.
    /// What was said in one whose file is gone stays in the index; only a conversation no
    /// longer listed at all leaves it. Null: the sources are the whole list.
    /// </param>
    public UpdateRun Update(
        IReadOnlyList<Source> sources, IProgress<(int Done, int Total)>? progress = null,
        CancellationToken cancellation = default, IReadOnlySet<string>? listed = null)
    {
        ArgumentNullException.ThrowIfNull(sources);
        Dictionary<string, Known> known;
        lock (_gate) known = LoadKnown();

        // What has to be read, decided before reading anything.
        var work = new List<(Source Source, (long Length, long Written) Info, Known? Before)>();
        foreach (var source in sources)
        {
            cancellation.ThrowIfCancellationRequested();
            (long Length, long Written) info;
            try
            {
                // A packed copy says what it holds unpacked: offsets are the plain transcript's.
                if (TranscriptFiles.Stat(source.TranscriptPath) is not { } stat) continue;
                info = stat;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                continue;
            }
            known.TryGetValue(source.Conversation, out var before);
            if (before is not null && before.Path == source.TranscriptPath && before.OnlySession == source.Part.Stored
                && before.Length == info.Length && before.Written == info.Written)
                continue;
            if (before is not null && before.OnlySession == source.Part.Stored && before.Length == info.Length
                && PackedCopy.IsPackingOf(source.TranscriptPath, before.Path))
            {
                // The same transcript, packed since it was read: only its name changed.
                lock (_gate)
                {
                    using var tx = _connection.BeginTransaction();
                    SaveKnown(tx, before.Id, source.TranscriptPath, info.Length, info.Written, before.NextOffset, before.NextMessage,
                        before.OnlySession);
                    tx.Commit();
                }
                continue;
            }
            work.Add((source, info, before));
        }

        int indexed = 0, messages = 0;
        // One line buffer for the whole pass: grown once by the longest line, let go at the end.
        using var line = new PooledBytes();
        for (var i = 0; i < work.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            progress?.Report((i, work.Count));
            var (source, info, before) = work[i];
            var written = info.Written;

            // Grown in place: read on from where the last pass stopped. Anything else starts over.
            // A Gemini or dsh session is read whole: there is nothing to read on from.
            var append = !WholeTranscript.Is(source.TranscriptPath)
                         && before is not null && before.Path == source.TranscriptPath && before.OnlySession == source.Part.Stored
                         && info.Length >= before.Length && before.NextOffset <= info.Length;
            IReadOnlyList<TranscriptEntry> entries;
            long next;
            try
            {
                (entries, next) = TranscriptReader.ReadDialogueFrom(
                    source.TranscriptPath, append ? before!.NextOffset : 0, line, cancellation, source.Part);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;       // held or unreadable this time: the next pass tries again
            }

            lock (_gate)
            {
                using var tx = _connection.BeginTransaction();
                var id = before?.Id ?? NewId(tx, source.Conversation);
                if (before is not null && !append) DeleteMessages(tx, id);
                var first = append ? before!.NextMessage : 0;
                Insert(tx, id, first, source.Conversation, entries, cancellation);
                SaveKnown(tx, id, source.TranscriptPath, info.Length, written, next, first + entries.Count, source.Part.Stored);
                tx.Commit();
            }
            indexed++;
            messages += entries.Count;
        }

        // Conversations no longer listed leave the index as well; one whose file is gone stays.
        var present = sources.Select(s => s.Conversation).ToHashSet(StringComparer.Ordinal);
        if (listed is not null) present.UnionWith(listed);
        var removed = 0;
        foreach (var gone in known.Where(k => !present.Contains(k.Key)).Select(k => k.Value.Id).ToList())
        {
            lock (_gate)
            {
                using var tx = _connection.BeginTransaction();
                DeleteMessages(tx, gone);
                using (var cmd = _connection.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "DELETE FROM sources WHERE id=$id;";
                    cmd.Parameters.AddWithValue("$id", gone);
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }
            removed++;
        }

        progress?.Report((work.Count, work.Count));
        return new UpdateRun(sources.Count, indexed, removed, messages);
    }

    /// <summary>How many conversations are in the index.</summary>
    public int Count()
    {
        lock (_gate) return (int)Scalar("SELECT COUNT(*) FROM sources;");
    }

    /// <summary>
    /// The SQL condition, over the index's messages table, for one search term — a phrase match
    /// through the trigram index from three characters on, a scan below that — or null for a
    /// term too short to look for. <paramref name="parameter"/> is bound to <see cref="TermValue"/>.
    /// </summary>
    public static string? TermCondition(string term, string parameter) =>
        term.Length >= 3 ? $"messages MATCH {parameter}"
        : term.Length >= MinTermLength ? $"text LIKE {parameter} ESCAPE '\\'"
        : null;

    public static string TermValue(string term) =>
        term.Length >= 3 ? "\"" + term.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
        : "%" + SqliteCatalog.EscapeLike(term) + "%";

    /// <summary>
    /// Where a term was said in one conversation: up to <paramref name="max"/> messages, each cut
    /// down to the words around the first occurrence, oldest first.
    /// </summary>
    /// <param name="userOnly">Only what the user said.</param>
    public IReadOnlyList<ContentHit> Hits(string conversation, string term, int max = 2, bool userOnly = false)
    {
        var hits = new List<ContentHit>();
        if (TermCondition(term, "$q") is not { } condition) return hits;
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $"""
                SELECT text, role, stamp FROM messages
                WHERE rowid >= (SELECT id FROM sources WHERE conversation=$c) << 32
                  AND rowid < ((SELECT id FROM sources WHERE conversation=$c) + 1) << 32
                  AND {condition}{(userOnly ? " AND role='user'" : "")}
                ORDER BY rowid LIMIT $max;
                """;
            cmd.Parameters.AddWithValue("$c", conversation);
            cmd.Parameters.AddWithValue("$q", TermValue(term));
            cmd.Parameters.AddWithValue("$max", max);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                hits.Add(new ContentHit(reader.GetString(1) == "user", reader.IsDBNull(2) ? null : reader.GetString(2),
                    Around(reader.GetString(0), term)));
        }
        return hits;
    }

    /// <summary>The words around a term's first occurrence, on one line.</summary>
    public static string Around(string text, string term, int before = 40, int after = 90)
    {
        var at = CultureInfo.InvariantCulture.CompareInfo.IndexOf(text, term,
            CompareOptions.IgnoreCase | CompareOptions.IgnoreWidth);
        if (at < 0) at = 0;
        var start = Math.Max(0, at - before);
        var end = Math.Min(text.Length, at + term.Length + after);
        var slice = string.Join(' ', text[start..end].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return (start > 0 ? "…" : "") + slice + (end < text.Length ? "…" : "");
    }

    private Dictionary<string, Known> LoadKnown()
    {
        var known = new Dictionary<string, Known>(StringComparer.Ordinal);
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT conversation, id, path, length, written, next_offset, next_message, only_session FROM sources;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            known[reader.GetString(0)] = new Known(reader.GetInt64(1), reader.GetString(2), reader.GetInt64(3),
                reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.IsDBNull(7) ? null : reader.GetString(7));
        return known;
    }

    /// <summary>A conversation seen for the first time gets its number, and with it its rowid range.</summary>
    private long NewId(SqliteTransaction tx, string conversation)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO sources(conversation, path, length, written, next_offset, next_message)
            VALUES($c, '', -1, 0, 0, 0) RETURNING id;
            """;
        cmd.Parameters.AddWithValue("$c", conversation);
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private void DeleteMessages(SqliteTransaction tx, long id)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM messages WHERE rowid >= $from AND rowid < $to;";
        cmd.Parameters.AddWithValue("$from", id << 32);
        cmd.Parameters.AddWithValue("$to", (id + 1) << 32);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Stops between messages when cancelled — the transaction then rolls back — so closing never waits for a long transcript.</summary>
    private void Insert(SqliteTransaction tx, long id, long first, string conversation, IReadOnlyList<TranscriptEntry> entries,
        CancellationToken cancellation)
    {
        if (entries.Count == 0) return;
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO messages(rowid, text, conversation, role, stamp) VALUES($id, $t, $c, $r, $s);";
        var rowid = cmd.Parameters.Add("$id", SqliteType.Integer);
        var text = cmd.Parameters.Add("$t", SqliteType.Text);
        cmd.Parameters.AddWithValue("$c", conversation);
        var role = cmd.Parameters.Add("$r", SqliteType.Text);
        var stamp = cmd.Parameters.Add("$s", SqliteType.Text);
        var n = first;
        foreach (var entry in entries)
        {
            if (((n - first) & 255) == 255) cancellation.ThrowIfCancellationRequested();
            rowid.Value = (id << 32) + n++;
            text.Value = entry.Text.Length <= MaxMessageChars ? entry.Text : entry.Text[..MaxMessageChars];
            role.Value = entry.Role == MessageRole.User ? "user" : "assistant";
            stamp.Value = (object?)entry.Timestamp ?? DBNull.Value;
            cmd.ExecuteNonQuery();
        }
    }

    private void SaveKnown(SqliteTransaction tx, long id, string path, long length, long written, long next, long nextMessage,
        string? onlySession)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "UPDATE sources SET path=$p, length=$l, written=$w, next_offset=$n, next_message=$m, only_session=$o WHERE id=$id;";
        cmd.Parameters.AddWithValue("$o", (object?)onlySession ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$p", path);
        cmd.Parameters.AddWithValue("$l", length);
        cmd.Parameters.AddWithValue("$w", written);
        cmd.Parameters.AddWithValue("$n", next);
        cmd.Parameters.AddWithValue("$m", nextMessage);
        cmd.ExecuteNonQuery();
    }

    private void Execute(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private long Scalar(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public void Dispose()
    {
        lock (_gate) _connection.Dispose();
    }
}

/// <summary>One place a search term was said: by whom, when, and the words around it.</summary>
public sealed record ContentHit(bool ByUser, string? Stamp, string Snippet);
