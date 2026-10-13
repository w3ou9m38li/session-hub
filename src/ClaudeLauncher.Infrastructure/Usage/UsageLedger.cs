using System.Globalization;
using System.Text.Json;
using ClaudeLauncher.Core.Contracts;
using ClaudeLauncher.Infrastructure.Backends;
using ClaudeLauncher.Infrastructure.Search;
using Microsoft.Data.Sqlite;

namespace ClaudeLauncher.Infrastructure.Usage;

/// <summary>Tokens of one or more calls, added up.</summary>
public readonly record struct TokenCounts(long Calls, long Input, long CacheWrite, long CacheRead, long Output)
{
    /// <summary>Everything the model read and wrote: fresh input, cache writes, cache reads and output.</summary>
    public long Total => Input + CacheWrite + CacheRead + Output;

    public static TokenCounts operator +(TokenCounts a, TokenCounts b) => new(
        a.Calls + b.Calls, a.Input + b.Input, a.CacheWrite + b.CacheWrite, a.CacheRead + b.CacheRead, a.Output + b.Output);
}

/// <summary>One conversation's use of one model on one day (local time).</summary>
public sealed record UsageRow(string Conversation, UsageApp App, DateOnly Day, string Model, TokenCounts Tokens);

/// <summary>
/// How many tokens the conversations on this computer used, read from their transcripts and
/// kept in a database of its own beside the catalog. Like the content index it holds nothing
/// that is not in the transcripts, and can be deleted at any time.
///
/// <para>Reading is incremental: a transcript is read again only when its length or write time
/// changed, and one that merely grew is read from where the previous pass stopped. Every call
/// counted is remembered by its key, so a call recorded twice — or copied into a forked or
/// resumed conversation — is counted once, for the oldest file that has it.</para>
/// </summary>
public sealed class UsageLedger : IDisposable
{
    /// <summary>The layout below. A ledger written by any other layout is dropped and read again.</summary>
    private const int Layout = 2;

    private readonly SqliteConnection _connection;
    private readonly Lock _gate = new();

    public string DatabasePath { get; }

    public UsageLedger(string databasePath)
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
            _connection.Dispose();
            throw;
        }
    }

    private void Prepare()
    {
        Execute("PRAGMA journal_mode=WAL;");
        Execute("PRAGMA busy_timeout=5000;");
        Execute("PRAGMA synchronous=NORMAL;");
        if (Scalar("PRAGMA user_version;") != Layout)
        {
            Execute("DROP TABLE IF EXISTS usage; DROP TABLE IF EXISTS seen; DROP TABLE IF EXISTS files; DROP TABLE IF EXISTS threads;");
            Execute($"PRAGMA user_version={Layout};");
        }
        Execute("""
            CREATE TABLE IF NOT EXISTS files (
              id INTEGER PRIMARY KEY,
              path TEXT NOT NULL UNIQUE,
              conversation TEXT NOT NULL,
              app INTEGER NOT NULL,
              length INTEGER NOT NULL,
              written INTEGER NOT NULL,
              next_offset INTEGER NOT NULL,
              state TEXT,
              origin TEXT
            );
            CREATE TABLE IF NOT EXISTS seen (
              h INTEGER PRIMARY KEY,
              file INTEGER NOT NULL,
              day TEXT NOT NULL,
              model TEXT NOT NULL,
              input INTEGER NOT NULL,
              cache_write INTEGER NOT NULL,
              cache_read INTEGER NOT NULL,
              output INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS seen_file ON seen(file);
            CREATE TABLE IF NOT EXISTS threads (thread TEXT PRIMARY KEY, root TEXT NOT NULL) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS usage (
              file INTEGER NOT NULL,
              day TEXT NOT NULL,
              model TEXT NOT NULL,
              calls INTEGER NOT NULL,
              input INTEGER NOT NULL,
              cache_write INTEGER NOT NULL,
              cache_read INTEGER NOT NULL,
              output INTEGER NOT NULL,
              PRIMARY KEY (file, day, model)
            ) WITHOUT ROWID;
            """);
    }

    /// <summary>Opens the ledger, starting it over when the file cannot be used; null when not even that works.</summary>
    public static UsageLedger? OpenOrRebuild(string databasePath)
    {
        try
        {
            return new UsageLedger(databasePath);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            try
            {
                SqliteConnection.ClearAllPools();
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                    if (File.Exists(databasePath + suffix)) File.Delete(databasePath + suffix);
                return new UsageLedger(databasePath);
            }
            catch (Exception again) when (again is SqliteException or IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    /// <summary>A file that records calls, and the conversation they are counted for.</summary>
    /// <param name="Origin">For a rollout the list does not have: its thread and the one it came from, as read from it.</param>
    public sealed record Source(string Conversation, string Path, UsageApp App, string? Origin = null);

    /// <param name="Read">Files read this time, whole or from where the last pass stopped.</param>
    /// <param name="Calls">Calls counted for the first time.</param>
    public sealed record UpdateRun(int Sources, int Read, long Calls);

    private sealed record Known(long Id, string Conversation, long Length, long Written, long NextOffset, string? State,
        string? Origin);

    /// <summary>Brings the ledger up to date with these files. <paramref name="progress"/> counts the files that have to be read.</summary>
    public UpdateRun Update(
        IReadOnlyList<Source> sources, IProgress<(int Done, int Total)>? progress = null,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        Dictionary<string, Known> known;
        Dictionary<string, string> roots;
        lock (_gate)
        {
            known = LoadKnown();
            roots = LoadRoots();
        }

        var work = new List<(Source Source, FileInfo Info, Known? Before)>();
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!listed.Add(source.Path)) continue;          // one file, one conversation: the first to name it
            FileInfo info;
            try
            {
                info = new FileInfo(source.Path);
                if (!info.Exists) continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                continue;
            }
            known.TryGetValue(source.Path, out var before);
            if (before is not null && before.Length == info.Length && before.Written == info.LastWriteTimeUtc.Ticks)
            {
                if (before.Conversation != source.Conversation) lock (_gate) Reassign(before.Id, source.Conversation);
                continue;
            }
            work.Add((source, info, before));
        }

        // Oldest file first: a forked or resumed conversation repeats calls its original has, and
        // they stay counted where they were made.
        work.Sort((a, b) => CreatedTicks(a.Info).CompareTo(CreatedTicks(b.Info)));

        long calls = 0;
        var read = 0;
        for (var i = 0; i < work.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            progress?.Report((i, work.Count));
            var (source, info, before) = work[i];

            // Grown in place: read on from where the last pass stopped. Anything else starts over.
            // A Gemini session is written again whole: it is always counted afresh.
            var append = source.App != UsageApp.Gemini
                         && before is not null && info.Length >= before.Length && before.NextOffset <= info.Length;
            UsageScan scan;
            try
            {
                scan = UsageScanner.Scan(source.Path, source.App, append ? before!.NextOffset : 0,
                    append ? before!.State : null, thread => roots.GetValueOrDefault(thread), cancellation);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;       // held or unreadable this time: the next pass tries again
            }

            lock (_gate)
            {
                using var tx = _connection.BeginTransaction();
                var id = before?.Id ?? NewFile(tx, source);
                if (before is not null && !append) Forget(tx, id);
                calls += Record(tx, id, scan.Calls);
                SaveFile(tx, id, source, info.Length, info.LastWriteTimeUtc.Ticks, scan.NextOffset, scan.State);
                if (scan is { Thread: { } thread, Root: { } root })
                {
                    roots[thread] = root;
                    SaveRoot(tx, thread, root);
                }
                tx.Commit();
            }
            read++;
        }
        progress?.Report((work.Count, work.Count));
        return new UpdateRun(sources.Count, read, calls);
    }

    private static long CreatedTicks(FileInfo info)
    {
        try { return info.CreationTimeUtc.Ticks; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return long.MaxValue; }
    }

    /// <summary>Every conversation's use of every model, day by day.</summary>
    public IReadOnlyList<UsageRow> Rows()
    {
        var rows = new List<UsageRow>();
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT f.conversation, f.app, u.day, u.model,
                       SUM(u.calls), SUM(u.input), SUM(u.cache_write), SUM(u.cache_read), SUM(u.output)
                FROM usage u JOIN files f ON f.id = u.file
                GROUP BY f.conversation, f.app, u.day, u.model;
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (!DateOnly.TryParseExact(reader.GetString(2), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var day))
                    continue;
                rows.Add(new UsageRow(reader.GetString(0), (UsageApp)reader.GetInt32(1), day, reader.GetString(3),
                    new TokenCounts(reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7),
                        reader.GetInt64(8))));
            }
        }
        return rows;
    }

    /// <summary>One conversation's tokens, every day and model together; null when none were counted.</summary>
    public TokenCounts? Totals(string conversation)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT SUM(u.calls), SUM(u.input), SUM(u.cache_write), SUM(u.cache_read), SUM(u.output)
                FROM usage u JOIN files f ON f.id = u.file WHERE f.conversation = $c;
                """;
            cmd.Parameters.AddWithValue("$c", conversation);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read() || reader.IsDBNull(0)) return null;
            return new TokenCounts(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
                reader.GetInt64(4));
        }
    }

    /// <summary>How many files the ledger has read.</summary>
    public int FileCount()
    {
        lock (_gate) return (int)Scalar("SELECT COUNT(*) FROM files;");
    }

    // --------------------------------------------------------------- sources

    /// <summary>
    /// The files holding the listed conversations' calls: each transcript; a Claude
    /// conversation's subagent transcripts beside it; and Codex rollouts the list does not have
    /// on their own — a subagent's, say — counted for the thread they were spawned from when
    /// that is listed, under their own name otherwise.
    /// </summary>
    public List<Source> SourcesFor(
        IReadOnlyList<SessionMetadata> rows, string claudeConfigDir, CodexPaths codex,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        Dictionary<string, Known> known;
        lock (_gate) known = LoadKnown();
        var sources = new List<Source>();
        var threads = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (row, path) in TranscriptLocator.FindAll(rows, claudeConfigDir, codex, cancellation))
        {
            // Takeout records no token counts for Gemini on the web.
            if (row.Key.Backend == ConversationKey.GeminiWeb) continue;
            var conversation = ContentIndex.KeyOf(row.Key);
            if (row.Key.Backend == ConversationKey.Gemini)
            {
                sources.Add(new Source(conversation, path, UsageApp.Gemini));
                continue;
            }
            if (row.Key.Backend == ConversationKey.Codex)
            {
                sources.Add(new Source(conversation, path, UsageApp.Codex));
                threads.TryAdd(row.Key.NativeSessionId, conversation);
                continue;
            }
            sources.Add(new Source(conversation, path, UsageApp.Claude));
            var subagents = Path.Combine(Path.GetDirectoryName(path) ?? "", Path.GetFileNameWithoutExtension(path), "subagents");
            foreach (var file in Files(subagents, "*.jsonl", SearchOption.TopDirectoryOnly))
                sources.Add(new Source(conversation, file, UsageApp.Claude));
        }

        var named = sources.Select(s => s.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var root in new[] { codex.SessionsRoot, codex.ArchivedSessionsRoot })
        {
            foreach (var file in Files(root, "rollout-*.jsonl", SearchOption.AllDirectories))
            {
                cancellation.ThrowIfCancellationRequested();
                if (named.Contains(file)) continue;
                // Where it came from is read from its first line once, and remembered.
                var recorded = known.TryGetValue(file, out var before) ? before.Origin : null;
                var (thread, origin) = recorded is not null ? SplitOrigin(recorded) : CodexOrigin(file);
                var owner = origin is not null && threads.TryGetValue(origin, out var parent) ? parent
                    : thread is not null && threads.TryGetValue(thread, out var own) ? own
                    : $"{ConversationKey.Codex}|||{thread ?? Path.GetFileNameWithoutExtension(file)}";
                sources.Add(new Source(owner, file, UsageApp.Codex, $"{thread}{OriginSeparator}{origin}"));
            }
        }
        return sources;
    }

    private static List<string> Files(string directory, string pattern, SearchOption option)
    {
        try
        {
            // A folder that cannot be listed is passed over; the rest of the tree still counts.
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = option == SearchOption.AllDirectories,
                IgnoreInaccessible = true,
            };
            return Directory.Exists(directory) ? Directory.EnumerateFiles(directory, pattern, options).ToList() : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private const char OriginSeparator = '\u001F';

    private static (string? Thread, string? Origin) SplitOrigin(string recorded)
    {
        var parts = recorded.Split(OriginSeparator);
        return (parts[0].Length > 0 ? parts[0] : null, parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null);
    }

    /// <summary>A rollout's thread id and the thread it was spawned or forked from, read from its first line.</summary>
    private static (string? Thread, string? Origin) CodexOrigin(string path)
    {
        try
        {
            // The first line holds the thread's instructions as well: usually tens of kilobytes.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[64 * 1024];
            var length = 0;
            int end;
            while ((end = Array.IndexOf(buffer, (byte)'\n', 0, length)) < 0 && length < 4 * 1024 * 1024)
            {
                if (length == buffer.Length) Array.Resize(ref buffer, buffer.Length * 2);
                var got = stream.Read(buffer, length, buffer.Length - length);
                if (got == 0) break;
                length += got;
            }
            using var doc = JsonDocument.Parse(buffer.AsMemory(0, end < 0 ? length : end));
            if (!doc.RootElement.TryGetProperty("payload", out var meta) || meta.ValueKind != JsonValueKind.Object)
                return (null, null);
            var thread = Text(meta, "id");
            var origin = Text(meta, "parent_thread_id") ?? Text(meta, "forked_from_id");
            if (origin is null && Text(meta, "session_id") is { } session && session != thread) origin = session;
            return (thread, origin);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return (null, null);
        }

        static string? Text(JsonElement parent, string name) =>
            parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    // ------------------------------------------------------------------ store

    /// <summary>Each Codex thread read so far, and the thread its calls are counted under.</summary>
    private Dictionary<string, string> LoadRoots()
    {
        var roots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT thread, root FROM threads;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) roots[reader.GetString(0)] = reader.GetString(1);
        return roots;
    }

    private void SaveRoot(SqliteTransaction tx, string thread, string root)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO threads(thread, root) VALUES($t, $r) ON CONFLICT(thread) DO UPDATE SET root=excluded.root;";
        cmd.Parameters.AddWithValue("$t", thread);
        cmd.Parameters.AddWithValue("$r", root);
        cmd.ExecuteNonQuery();
    }

    private Dictionary<string, Known> LoadKnown()
    {
        var known = new Dictionary<string, Known>(StringComparer.OrdinalIgnoreCase);
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT path, id, conversation, length, written, next_offset, state, origin FROM files;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            known[reader.GetString(0)] = new Known(reader.GetInt64(1), reader.GetString(2), reader.GetInt64(3),
                reader.GetInt64(4), reader.GetInt64(5), reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7));
        return known;
    }

    private long NewFile(SqliteTransaction tx, Source source)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO files(path, conversation, app, length, written, next_offset, state)
            VALUES($p, $c, $a, -1, 0, 0, NULL) RETURNING id;
            """;
        cmd.Parameters.AddWithValue("$p", source.Path);
        cmd.Parameters.AddWithValue("$c", source.Conversation);
        cmd.Parameters.AddWithValue("$a", (int)source.App);
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>A file rewritten rather than grown: what it counted is counted again from its new content.</summary>
    private void Forget(SqliteTransaction tx, long file)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM usage WHERE file=$f; DELETE FROM seen WHERE file=$f;";
        cmd.Parameters.AddWithValue("$f", file);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Counts the calls not counted before, added up per day and model — and, for a call counted
    /// before, whatever its figures grew by: a response's later lines can carry more output than
    /// its first. Returns how many calls were new.
    /// </summary>
    private long Record(SqliteTransaction tx, long file, IReadOnlyList<UsageCall> calls)
    {
        if (calls.Count == 0) return 0;

        // One call per key within this read, with the largest figures any of its lines gave.
        var merged = new Dictionary<long, UsageCall>();
        var order = new List<long>();
        foreach (var call in calls)
        {
            if (merged.TryGetValue(call.Key, out var known))
                merged[call.Key] = known with
                {
                    Input = Math.Max(known.Input, call.Input),
                    CacheWrite = Math.Max(known.CacheWrite, call.CacheWrite),
                    CacheRead = Math.Max(known.CacheRead, call.CacheRead),
                    Output = Math.Max(known.Output, call.Output),
                };
            else
            {
                merged[call.Key] = call;
                order.Add(call.Key);
            }
        }

        var sums = new Dictionary<(long File, string Day, string Model), TokenCounts>();
        long counted = 0;
        using (var insert = _connection.CreateCommand())
        using (var lookup = _connection.CreateCommand())
        using (var grow = _connection.CreateCommand())
        {
            insert.Transaction = lookup.Transaction = grow.Transaction = tx;
            insert.CommandText = """
                INSERT INTO seen(h, file, day, model, input, cache_write, cache_read, output)
                VALUES($h, $f, $d, $m, $i, $w, $r, $o) ON CONFLICT(h) DO NOTHING;
                """;
            var h = insert.Parameters.Add("$h", SqliteType.Integer);
            insert.Parameters.AddWithValue("$f", file);
            var d = insert.Parameters.Add("$d", SqliteType.Text);
            var m = insert.Parameters.Add("$m", SqliteType.Text);
            var i = insert.Parameters.Add("$i", SqliteType.Integer);
            var w = insert.Parameters.Add("$w", SqliteType.Integer);
            var r = insert.Parameters.Add("$r", SqliteType.Integer);
            var o = insert.Parameters.Add("$o", SqliteType.Integer);
            lookup.CommandText = "SELECT file, day, model, input, cache_write, cache_read, output FROM seen WHERE h=$h;";
            var lh = lookup.Parameters.Add("$h", SqliteType.Integer);
            grow.CommandText = "UPDATE seen SET input=$i, cache_write=$w, cache_read=$r, output=$o WHERE h=$h;";
            var gh = grow.Parameters.Add("$h", SqliteType.Integer);
            var gi = grow.Parameters.Add("$i", SqliteType.Integer);
            var gw = grow.Parameters.Add("$w", SqliteType.Integer);
            var gr = grow.Parameters.Add("$r", SqliteType.Integer);
            var go = grow.Parameters.Add("$o", SqliteType.Integer);

            foreach (var key in order)
            {
                var call = merged[key];
                var callDay = DayOf(call.At);
                (h.Value, d.Value, m.Value, i.Value, w.Value, r.Value, o.Value) =
                    (key, callDay, call.Model, call.Input, call.CacheWrite, call.CacheRead, call.Output);
                if (insert.ExecuteNonQuery() == 1)
                {
                    var slot = (file, callDay, call.Model);
                    sums[slot] = sums.GetValueOrDefault(slot)
                                 + new TokenCounts(1, call.Input, call.CacheWrite, call.CacheRead, call.Output);
                    counted++;
                    continue;
                }

                // Counted before — a repeat, a later line of the same response, or a copy in a
                // forked or resumed conversation: only what its figures grew by is added, to the
                // file, day and model it was first counted for.
                lh.Value = key;
                long firstFile;
                string firstDay, firstModel;
                TokenCounts before;
                using (var row = lookup.ExecuteReader())
                {
                    if (!row.Read()) continue;
                    (firstFile, firstDay, firstModel) = (row.GetInt64(0), row.GetString(1), row.GetString(2));
                    before = new TokenCounts(0, row.GetInt64(3), row.GetInt64(4), row.GetInt64(5), row.GetInt64(6));
                }
                var grown = new TokenCounts(0, Math.Max(before.Input, call.Input), Math.Max(before.CacheWrite, call.CacheWrite),
                    Math.Max(before.CacheRead, call.CacheRead), Math.Max(before.Output, call.Output));
                if (grown == before) continue;
                (gh.Value, gi.Value, gw.Value, gr.Value, go.Value) =
                    (key, grown.Input, grown.CacheWrite, grown.CacheRead, grown.Output);
                grow.ExecuteNonQuery();
                var first = (firstFile, firstDay, firstModel);
                sums[first] = sums.GetValueOrDefault(first) + new TokenCounts(0, grown.Input - before.Input,
                    grown.CacheWrite - before.CacheWrite, grown.CacheRead - before.CacheRead, grown.Output - before.Output);
            }
        }

        using var add = _connection.CreateCommand();
        add.Transaction = tx;
        add.CommandText = """
            INSERT INTO usage(file, day, model, calls, input, cache_write, cache_read, output)
            VALUES($f, $d, $m, $n, $i, $w, $r, $o)
            ON CONFLICT(file, day, model) DO UPDATE SET
              calls = calls + excluded.calls, input = input + excluded.input,
              cache_write = cache_write + excluded.cache_write, cache_read = cache_read + excluded.cache_read,
              output = output + excluded.output;
            """;
        var target = add.Parameters.Add("$f", SqliteType.Integer);
        var day = add.Parameters.Add("$d", SqliteType.Text);
        var model = add.Parameters.Add("$m", SqliteType.Text);
        var n = add.Parameters.Add("$n", SqliteType.Integer);
        var input = add.Parameters.Add("$i", SqliteType.Integer);
        var write = add.Parameters.Add("$w", SqliteType.Integer);
        var read = add.Parameters.Add("$r", SqliteType.Integer);
        var output = add.Parameters.Add("$o", SqliteType.Integer);
        foreach (var ((f, d, m), t) in sums)
        {
            target.Value = f;
            day.Value = d;
            model.Value = m;
            n.Value = t.Calls;
            input.Value = t.Input;
            write.Value = t.CacheWrite;
            read.Value = t.CacheRead;
            output.Value = t.Output;
            add.ExecuteNonQuery();
        }
        return counted;
    }

    /// <summary>The local day a call was made on, as the ledger keeps it.</summary>
    public static string DayOf(DateTimeOffset at) =>
        at.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private void SaveFile(SqliteTransaction tx, long id, Source source, long length, long written, long next, string? state)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE files SET conversation=$c, app=$a, length=$l, written=$w, next_offset=$n, state=$s, origin=$o WHERE id=$id;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$c", source.Conversation);
        cmd.Parameters.AddWithValue("$a", (int)source.App);
        cmd.Parameters.AddWithValue("$l", length);
        cmd.Parameters.AddWithValue("$w", written);
        cmd.Parameters.AddWithValue("$n", next);
        cmd.Parameters.AddWithValue("$s", (object?)state ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$o", (object?)source.Origin ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private void Reassign(long id, string conversation)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE files SET conversation=$c WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$c", conversation);
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
