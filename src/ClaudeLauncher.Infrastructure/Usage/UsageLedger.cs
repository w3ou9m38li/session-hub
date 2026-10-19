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

    /// <summary>What the calls read in: the conversation so far, each call reading all of it again.</summary>
    public long Context => Input + CacheWrite + CacheRead;

    /// <summary>The context a call read on average: what makes a long conversation dear and slow; 0 without calls.</summary>
    public double ContextPerCall => Calls > 0 ? (double)Context / Calls : 0;

    /// <summary>The share of the context read from the cache, at a fraction of the price of fresh input; 0 without context.</summary>
    public double CacheHit => Context > 0 ? (double)CacheRead / Context : 0;

    public static TokenCounts operator +(TokenCounts a, TokenCounts b) => new(
        a.Calls + b.Calls, a.Input + b.Input, a.CacheWrite + b.CacheWrite, a.CacheRead + b.CacheRead, a.Output + b.Output);
}

/// <summary>One conversation's use of one model on one day (local time).</summary>
public sealed record UsageRow(string Conversation, AppFamily App, DateOnly Day, string Model, TokenCounts Tokens);

/// <summary>
/// How a conversation used its context, as the ledger keeps it: the largest context a call read,
/// the compactions, the times the cache had expired and the whole context was written (or read
/// at full price) again, and what tools' results put into it. Subagents and spawned tasks count
/// with their conversation; what a copy repeats counts where it first was.
/// </summary>
/// <param name="ToolBytes">The size of the tools' results as written: about 3.5 bytes a token.</param>
public sealed record SessionMetrics(long MaxContext, int Compactions, int Rewrites, long RewrittenTokens, int ToolResults,
    long ToolBytes);

/// <summary>One conversation's cache rewrites after a pause on one day (local time): how many, and the tokens written again.</summary>
public sealed record RewriteRow(string Conversation, AppFamily App, DateOnly Day, int Count, long Tokens);

/// <summary>
/// How many tokens the conversations on this computer used, read from their transcripts and
/// kept in a database of its own beside the catalog. Like the content index it holds nothing
/// that is not in the transcripts, and can be deleted at any time.
///
/// <para>Reading is incremental: a transcript is read again only when its length or write time
/// changed, and one that merely grew is read from where the previous pass stopped. Every call
/// counted is remembered by its key, so a call recorded twice — or copied into a forked or
/// resumed conversation — is counted once, for the oldest file that has it.</para>
///
/// <para>The same read keeps a summary of how each conversation used its context
/// (<see cref="Metrics"/>): its largest context, the compactions, the cache rewrites after a
/// pause and the tools' results, judged as the analysis window judges them. A file's last call is
/// carried to its next read, so a file read as it grows comes to what one read would.</para>
/// </summary>
public sealed class UsageLedger : IDisposable
{
    /// <summary>The layout below. A ledger written by any other layout is dropped and read again.</summary>
    private const int Layout = 3;

    private readonly SqliteConnection _connection;
    private readonly Lock _gate = new();

    /// <summary>Opened beside the window by <see cref="OpenReadOnly"/>: read, never updated.</summary>
    private readonly bool _readOnly;

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
            Execute("DROP TABLE IF EXISTS usage; DROP TABLE IF EXISTS seen; DROP TABLE IF EXISTS files; DROP TABLE IF EXISTS threads;"
                    + " DROP TABLE IF EXISTS facts; DROP TABLE IF EXISTS tools;");
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
              origin TEXT,
              tail TEXT,
              max_context INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS files_conversation ON files(conversation);
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
            CREATE TABLE IF NOT EXISTS facts (
              h INTEGER PRIMARY KEY,
              file INTEGER NOT NULL,
              kind INTEGER NOT NULL,
              at INTEGER NOT NULL,
              model TEXT NOT NULL,
              amount INTEGER NOT NULL,
              extra INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS facts_file ON facts(file);
            CREATE TABLE IF NOT EXISTS tools (
              h INTEGER PRIMARY KEY,
              file INTEGER NOT NULL,
              name TEXT,
              bytes INTEGER NOT NULL,
              result INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS tools_file ON tools(file);
            """);
    }

    // A fact's kind, as the facts table keeps it.
    private const int CompactionFact = 1;
    private const int RewriteFact = 2;

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

    private UsageLedger(SqliteConnection connection, string databasePath)
    {
        _connection = connection;
        DatabasePath = databasePath;
        _readOnly = true;
    }

    /// <summary>
    /// Opens the ledger for reading only, from a process beside the window: its rows and totals
    /// are read, and nothing is ever written, created or dropped. Null when there is no ledger —
    /// the user never opened the usage window — or one of another layout, or one that cannot be opened.
    /// </summary>
    public static UsageLedger? OpenReadOnly(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path)) return null;
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());
        try
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA busy_timeout=5000;";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "PRAGMA user_version;";
            if (Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) == Layout)
                return new UsageLedger(connection, path);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            // Not a database, or held in a way that cannot be read: no ledger, as far as this goes.
        }
        connection.Dispose();
        return null;
    }

    /// <summary>A file that records calls, and the conversation they are counted for.</summary>
    /// <param name="Origin">For a rollout the list does not have: its thread and the one it came from, as read from it.</param>
    public sealed record Source(string Conversation, string Path, AppFamily App, string? Origin = null);

    /// <param name="Read">Files read this time, whole or from where the last pass stopped.</param>
    /// <param name="Calls">Calls counted for the first time.</param>
    public sealed record UpdateRun(int Sources, int Read, long Calls);

    private sealed record Known(long Id, string Conversation, long Length, long Written, long NextOffset, string? State,
        string? Origin, string? Tail);

    /// <summary>Brings the ledger up to date with these files. <paramref name="progress"/> counts the files that have to be read.</summary>
    public UpdateRun Update(
        IReadOnlyList<Source> sources, IProgress<(int Done, int Total)>? progress = null,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (_readOnly) throw new InvalidOperationException("用量账本以只读方式打开：" + DatabasePath);
        Dictionary<string, Known> known;
        Dictionary<string, string> roots;
        lock (_gate)
        {
            known = LoadKnown();
            roots = LoadRoots();
        }

        var work = new List<(Source Source, FileInfo File, (long Length, long Written) Info, Known? Before)>();
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, (string Path, Known File)>? plainByName = null;
        foreach (var source in sources)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!listed.Add(source.Path)) continue;          // one file, one conversation: the first to name it
            FileInfo file;
            (long Length, long Written) info;
            try
            {
                // A packed copy says what it holds unpacked: offsets are the plain transcript's.
                if (TranscriptFiles.Stat(source.Path) is not { } stat) continue;
                info = stat;
                file = new FileInfo(source.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                continue;
            }
            known.TryGetValue(source.Path, out var before);
            if (before is not null && before.Length == info.Length && before.Written == info.Written)
            {
                if (before.Conversation != source.Conversation) lock (_gate) Reassign(before.Id, source.Conversation);
                continue;
            }
            if (before is null && PackedCopy.HasSuffix(source.Path)
                && (plainByName ??= PlainByName(known)).TryGetValue(Path.GetFileName(source.Path)[..^PackedCopy.Suffix.Length], out var plain)
                && plain.File.Conversation == source.Conversation && plain.File.Length == info.Length
                && PackedCopy.IsPackingOf(source.Path, plain.Path))
            {
                // The same transcript, packed since it was counted: only its name changed.
                lock (_gate) Rename(plain.File.Id, source.Path, info.Written);
                continue;
            }
            work.Add((source, file, info, before));
        }

        // Oldest file first: a forked or resumed conversation repeats calls its original has, and
        // they stay counted where they were made.
        work.Sort((a, b) => CreatedTicks(a.File).CompareTo(CreatedTicks(b.File)));

        long calls = 0;
        var read = 0;
        for (var i = 0; i < work.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            progress?.Report((i, work.Count));
            var (source, _, info, before) = work[i];

            // Grown in place: read on from where the last pass stopped. Anything else starts over.
            // A Gemini or dsh session is read whole: it is always counted afresh.
            var append = !WholeTranscript.Is(source.Path)
                         && before is not null && info.Length >= before.Length && before.NextOffset <= info.Length;
            UsageScan scan;
            try
            {
                scan = UsageScanner.Scan(source.Path, source.App, append ? before!.NextOffset : 0,
                    append ? before!.State : null, facts: true, part: default, thread => roots.GetValueOrDefault(thread),
                    cancellation);
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
                var owners = new Dictionary<long, (UsageCall Call, Owner Owner)>();
                calls += Record(tx, id, scan.Calls, owners);
                var tail = RecordFacts(tx, id, source.App, scan, owners, append ? before!.Tail : null, out var largest);
                SaveFile(tx, id, source, info.Length, info.Written, scan.NextOffset, scan.State, tail, largest);
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

    /// <summary>Files counted under a plain name, by that name: where one was packed since, it is found under its new name.</summary>
    private static Dictionary<string, (string Path, Known File)> PlainByName(Dictionary<string, Known> known) =>
        known.Where(k => !PackedCopy.HasSuffix(k.Key))
            .GroupBy(k => Path.GetFileName(k.Key), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => (g.First().Key, g.First().Value), StringComparer.OrdinalIgnoreCase);

    private void Rename(long id, string path, long written)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE files SET path=$p, written=$w WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$p", path);
        cmd.Parameters.AddWithValue("$w", written);
        cmd.ExecuteNonQuery();
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
                rows.Add(new UsageRow(reader.GetString(0), (AppFamily)reader.GetInt32(1), day, reader.GetString(3),
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

    /// <summary>One conversation's context, compactions, cache rewrites and tool results; null when none of its files was read.</summary>
    public SessionMetrics? Metrics(string conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        lock (_gate) return Gather(conversation).GetValueOrDefault(conversation);
    }

    /// <summary>Every conversation's context, compactions, cache rewrites and tool results, by conversation.</summary>
    public IReadOnlyDictionary<string, SessionMetrics> AllMetrics()
    {
        lock (_gate) return Gather(null);
    }

    /// <summary>
    /// The cache rewrites by conversation and day, as <see cref="Rows"/> gives the tokens: so a
    /// range of days can be ranked by them. A rewrite counts for the day of the call that made it.
    /// </summary>
    public IReadOnlyList<RewriteRow> RewriteRows()
    {
        var days = new Dictionary<(string Conversation, AppFamily App, DateOnly Day), (int Count, long Tokens)>();
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT f.conversation, f.app, x.at, x.amount FROM facts x JOIN files f ON f.id = x.file WHERE x.kind = $k;";
            cmd.Parameters.AddWithValue("$k", RewriteFact);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var ticks = reader.GetInt64(2);
                if (ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks) continue;
                var at = new DateTimeOffset(ticks, TimeSpan.Zero);
                var slot = (reader.GetString(0), (AppFamily)reader.GetInt32(1), DateOnly.FromDateTime(at.ToLocalTime().DateTime));
                var (count, tokens) = days.GetValueOrDefault(slot);
                days[slot] = (count + 1, tokens + reader.GetInt64(3));
            }
        }
        return days.Select(d => new RewriteRow(d.Key.Conversation, d.Key.App, d.Key.Day, d.Value.Count, d.Value.Tokens)).ToList();
    }

    private sealed class Tally
    {
        public long MaxContext;
        public int Compactions, Rewrites, ToolResults;
        public long RewrittenTokens, ToolBytes;
    }

    /// <summary>The metrics of one conversation, or of all when <paramref name="conversation"/> is null.</summary>
    private Dictionary<string, SessionMetrics> Gather(string? conversation)
    {
        var where = conversation is null ? "" : "WHERE f.conversation = $c";
        var tallies = new Dictionary<string, Tally>(StringComparer.Ordinal);
        Tally Of(string key) => tallies.TryGetValue(key, out var t) ? t : tallies[key] = new Tally();

        using var cmd = _connection.CreateCommand();
        if (conversation is not null) cmd.Parameters.AddWithValue("$c", conversation);
        cmd.CommandText = $"SELECT f.conversation, MAX(f.max_context) FROM files f {where} GROUP BY f.conversation;";
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
                Of(reader.GetString(0)).MaxContext = reader.GetInt64(1);

        cmd.CommandText = $"""
            SELECT f.conversation, x.kind, COUNT(*), SUM(x.amount) FROM facts x JOIN files f ON f.id = x.file {where}
            GROUP BY f.conversation, x.kind;
            """;
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
            {
                var tally = Of(reader.GetString(0));
                var count = reader.GetInt32(2);
                if (reader.GetInt32(1) == CompactionFact) tally.Compactions = count;
                else if (reader.GetInt32(1) == RewriteFact)
                    (tally.Rewrites, tally.RewrittenTokens) = (count, reader.GetInt64(3));
            }

        cmd.CommandText = $"""
            SELECT f.conversation, COUNT(*), SUM(t.bytes) FROM tools t JOIN files f ON f.id = t.file
            {(where.Length == 0 ? "WHERE" : where + " AND")} t.result = 1
            GROUP BY f.conversation;
            """;
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
                (Of(reader.GetString(0)).ToolResults, Of(reader.GetString(0)).ToolBytes) = (reader.GetInt32(1), reader.GetInt64(2));

        return tallies.ToDictionary(t => t.Key,
            t => new SessionMetrics(t.Value.MaxContext, t.Value.Compactions, t.Value.Rewrites, t.Value.RewrittenTokens,
                t.Value.ToolResults, t.Value.ToolBytes), StringComparer.Ordinal);
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
            // A source whose records carry no token counts (Gemini on the web, from Takeout) adds nothing.
            if (Sources.Of(row.Key.Backend) is not { CountsTokens: true } source) continue;
            var conversation = ContentIndex.KeyOf(row.Key);
            sources.Add(new Source(conversation, path, source.Family));
            if (source.Family == AppFamily.Codex)
                threads.TryAdd(row.Key.NativeSessionId, conversation);
            else if (source.Family == AppFamily.Claude)
            {
                var subagents = Path.Combine(Path.GetDirectoryName(path) ?? "", Path.GetFileNameWithoutExtension(path), "subagents");
                foreach (var file in Files(subagents, "*.jsonl", SearchOption.TopDirectoryOnly))
                    sources.Add(new Source(conversation, file, AppFamily.Claude));
            }
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
                sources.Add(new Source(owner, file, AppFamily.Codex, $"{thread}{OriginSeparator}{origin}"));
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
        cmd.CommandText = "SELECT path, id, conversation, length, written, next_offset, state, origin, tail FROM files;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            known[reader.GetString(0)] = new Known(reader.GetInt64(1), reader.GetString(2), reader.GetInt64(3),
                reader.GetInt64(4), reader.GetInt64(5), reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8));
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
        cmd.CommandText = """
            DELETE FROM usage WHERE file=$f; DELETE FROM seen WHERE file=$f;
            DELETE FROM facts WHERE file=$f; DELETE FROM tools WHERE file=$f;
            UPDATE files SET tail=NULL, max_context=0 WHERE id=$f;
            """;
        cmd.Parameters.AddWithValue("$f", file);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Whose a call read this time is: this file's, counted now; this file's, counted by an earlier read; a copy's.</summary>
    private enum Owner { Own, Earlier, Copy }

    /// <summary>
    /// Counts the calls not counted before, added up per day and model — and, for a call counted
    /// before, whatever its figures grew by: a response's later lines can carry more output than
    /// its first. Returns how many calls were new; <paramref name="owners"/> gets each call read,
    /// with its largest figures, and whose it is.
    /// </summary>
    private long Record(SqliteTransaction tx, long file, IReadOnlyList<UsageCall> calls,
        Dictionary<long, (UsageCall Call, Owner Owner)> owners)
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
                    owners[key] = (call, Owner.Own);
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
                owners[key] = (call, firstFile == file ? Owner.Earlier : Owner.Copy);
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

    /// <summary>
    /// Follows the file's calls on from where its last read stopped, and keeps what happened to
    /// the context: a compaction, a cache that had expired over a pause, each tool's result. A
    /// compaction or a rewrite is kept for the call it came with, and only when that call is the
    /// file's own — a resumed or forked copy, a copy kept after the app deleted the original, a
    /// project folder moved: none counts again what the original has — though a copied call
    /// still stands as the one before, for the pause and the context. A tool is kept by its
    /// call's id, once, for the first file that has it. Returns where the file's line of calls
    /// stands, for the next read; <paramref name="largest"/> is the largest context of a call new to it.
    /// </summary>
    private string? RecordFacts(SqliteTransaction tx, long file, AppFamily app, UsageScan scan,
        Dictionary<long, (UsageCall Call, Owner Owner)> owners, string? tail, out long largest)
    {
        largest = 0;
        foreach (var (call, owner) in owners.Values)
            if (owner == Owner.Own) largest = Math.Max(largest, call.Input + call.CacheWrite + call.CacheRead);
        if (scan.Facts is not { } facts) return tail;

        using var fact = _connection.CreateCommand();
        using var named = _connection.CreateCommand();
        using var result = _connection.CreateCommand();
        fact.Transaction = named.Transaction = result.Transaction = tx;
        fact.CommandText = """
            INSERT INTO facts(h, file, kind, at, model, amount, extra) VALUES($h, $f, $k, $t, $m, $a, $x)
            ON CONFLICT(h) DO NOTHING;
            """;
        var fh = fact.Parameters.Add("$h", SqliteType.Integer);
        fact.Parameters.AddWithValue("$f", file);
        var fk = fact.Parameters.Add("$k", SqliteType.Integer);
        var ft = fact.Parameters.Add("$t", SqliteType.Integer);
        var fm = fact.Parameters.Add("$m", SqliteType.Text);
        var fa = fact.Parameters.Add("$a", SqliteType.Integer);
        var fx = fact.Parameters.Add("$x", SqliteType.Integer);
        named.CommandText = """
            INSERT INTO tools(h, file, name, bytes, result) VALUES($h, $f, $n, 0, 0)
            ON CONFLICT(h) DO UPDATE SET name = COALESCE(name, excluded.name);
            """;
        result.CommandText = """
            INSERT INTO tools(h, file, name, bytes, result) VALUES($h, $f, $n, $b, 1)
            ON CONFLICT(h) DO UPDATE SET bytes = MAX(bytes, excluded.bytes), result = 1, name = COALESCE(name, excluded.name);
            """;
        foreach (var cmd in new[] { named, result })
        {
            cmd.Parameters.Add("$h", SqliteType.Integer);
            cmd.Parameters.AddWithValue("$f", file);
            cmd.Parameters.Add("$n", SqliteType.Text);
        }
        result.Parameters.Add("$b", SqliteType.Integer);

        void Keep(int kind, long key, UsageCall call, long amount, long extra)
        {
            (fh.Value, fk.Value, ft.Value, fm.Value, fa.Value, fx.Value) =
                (UsageScanner.Hash(string.Create(CultureInfo.InvariantCulture, $"{kind}|{key}")), kind, call.At.UtcTicks,
                    call.Model, amount, extra);
            fact.ExecuteNonQuery();
        }

        var sequence = new ContextSequence(app, tail);
        var taken = new HashSet<long>();
        var events = facts.Events;
        var e = 0;
        for (var i = 0; ; i++)
        {
            for (; e < events.Count && events[e].Before <= i; e++)
            {
                var noted = events[e];
                if (noted.Kind == UsageFactKind.Compaction) sequence.Compacted();
                else if (noted.Id is { } id)
                {
                    var cmd = noted.Kind == UsageFactKind.ToolCall ? named : result;
                    cmd.Parameters["$h"].Value = UsageScanner.Hash("tool|" + id);
                    cmd.Parameters["$n"].Value = (object?)noted.Name ?? DBNull.Value;
                    if (cmd == result) cmd.Parameters["$b"].Value = noted.Bytes;
                    cmd.ExecuteNonQuery();
                }
            }
            if (i >= scan.Calls.Count) break;
            var key = scan.Calls[i].Key;
            // A response's later line, or a call an earlier read of the file already followed.
            if (!taken.Add(key) || !owners.TryGetValue(key, out var known) || known.Owner == Owner.Earlier) continue;
            var traits = facts.Traits[i];
            if (!sequence.Follows(traits)) continue;
            var step = sequence.Next(known.Call, traits);
            if (known.Owner != Owner.Own) continue;
            if (step.Compaction) Keep(CompactionFact, key, known.Call, step.ContextBefore, step.ExplicitCompaction ? 1 : 0);
            if (step.Rewritten > 0) Keep(RewriteFact, key, known.Call, step.Rewritten, (long)(step.Gap ?? TimeSpan.Zero).TotalSeconds);
        }
        return sequence.Tail;
    }

    /// <summary>The local day a call was made on, as the ledger keeps it.</summary>
    public static string DayOf(DateTimeOffset at) =>
        at.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private void SaveFile(SqliteTransaction tx, long id, Source source, long length, long written, long next, string? state,
        string? tail, long largest)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE files SET conversation=$c, app=$a, length=$l, written=$w, next_offset=$n, state=$s, origin=$o, tail=$t,
                             max_context=MAX(max_context, $m)
            WHERE id=$id;
            """;
        cmd.Parameters.AddWithValue("$t", (object?)tail ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$m", largest);
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
