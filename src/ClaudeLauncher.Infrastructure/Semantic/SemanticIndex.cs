using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ClaudeLauncher.Infrastructure.Search;
using Microsoft.Data.Sqlite;

namespace ClaudeLauncher.Infrastructure.Semantic;

/// <summary>One piece of a conversation a search found: how close it came, who said it and when, and its first words.</summary>
/// <param name="Ordinal">The message's place in the conversation, as the full-text index numbers it.</param>
/// <param name="Start">Where the piece starts in that message.</param>
public sealed record SemanticChunkHit(float Score, string Role, string? Stamp, int Ordinal, int Start, string Snippet);

/// <summary>A conversation a search found: its best score, and its closest pieces, closest first.</summary>
/// <param name="Conversation">Its name in the indexes (<see cref="ContentIndex.KeyOf"/>).</param>
public sealed record SemanticHit(string Conversation, float Score, IReadOnlyList<SemanticChunkHit> Chunks);

/// <summary>What the index holds.</summary>
/// <param name="BuiltFrom">The full-text index's fingerprint when the last complete build began; null before one.</param>
public sealed record SemanticIndexStats(
    int Conversations, long Chunks, string? ModelId, int Dimensions, DateTimeOffset? Updated, string? BuiltFrom);

/// <summary>What a build did.</summary>
/// <param name="Updated">Conversations embedded this time, whole or from where they had grown.</param>
/// <param name="Embedded">Pieces embedded this time.</param>
/// <param name="Removed">Conversations no longer listed, dropped.</param>
/// <param name="Source">The full-text index's fingerprint when the build began.</param>
/// <param name="Model">The id of the model the vectors were made with (<see cref="IEmbedder.ModelId"/>).</param>
public sealed record SemanticBuildRun(int Conversations, long Chunks, int Updated, long Embedded, int Removed, string Source,
    string Model);

/// <summary>
/// The vectors of what was said, so a search finds a conversation by what it meant and not only by
/// its words. A database of its own beside the full-text index it is made from, derived and safe to
/// delete. Each piece's vector is kept in 8 bits per dimension with one scale, a quarter of the
/// space of floats, and searched by streaming them past the query — never all in memory at once.
/// The vectors of the pieces committed together are kept together in one block, apart from the
/// pieces' words: a search reads a few thousand blocks rather than tens of thousands of rows, and
/// never the words of pieces it does not show.
///
/// <para>Building is incremental and can stop anywhere: a conversation whose messages are as they
/// were is skipped, one that grew has only its new messages embedded, and what was embedded is
/// committed every few dozen pieces, so a stop (or a crash) loses at most those.</para>
///
/// <para>Safe to use from two threads — a build and a search — each database step holding the
/// lock briefly; embedding, the slow part, holds nothing. Only one process writes it (the worker
/// holding the data root's build mutex); others open it read-only.</para>
/// </summary>
public sealed class SemanticIndex : IDisposable
{
    /// <summary>The layout below, kept in user_version. Any other is dropped and built again, and left alone read-only.</summary>
    public const int Layout = 1;

    /// <summary>Pieces embedded between two commits: a stop loses no more than this.</summary>
    private const int CommitEvery = 64;

    /// <summary>Blocks of vectors read per statement while searching: each read is short, and a stop is noticed between them.</summary>
    private const int SearchBatch = 256;

    /// <summary>What each piece takes in a block: its scale (a float), then its vector (a byte per dimension).</summary>
    private static int Stride(int dimensions) => sizeof(float) + dimensions;

    private readonly SqliteConnection _connection;
    private readonly Lock _gate = new();
    private readonly bool _readOnly;

    public string DatabasePath { get; }

    private SemanticIndex(SqliteConnection connection, string path, bool readOnly)
    {
        _connection = connection;
        DatabasePath = path;
        _readOnly = readOnly;
    }

    /// <summary>Opens the index for building, making it when missing and starting it over when written by another layout.</summary>
    /// <exception cref="SqliteException">The file is not a usable database.</exception>
    public static SemanticIndex Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());
        try
        {
            connection.Open();
            Execute(connection, "PRAGMA busy_timeout=5000;");
            Execute(connection, "PRAGMA journal_mode=WAL;");
            // Derived data: a transaction lost to a power cut is simply embedded again.
            Execute(connection, "PRAGMA synchronous=NORMAL;");
            if (Scalar(connection, "PRAGMA user_version;") != Layout)
            {
                Execute(connection, "DROP TABLE IF EXISTS vectors; DROP TABLE IF EXISTS chunks; DROP TABLE IF EXISTS sources; DROP TABLE IF EXISTS meta;");
                Execute(connection, $"PRAGMA user_version={Layout};");
            }
            // A conversation's pieces have ids (source id << 32) + n: one id range, so a search
            // knows a piece's conversation without reading any text, and dropping one never scans.
            // A block of vectors has the id of its first piece and holds the pieces after it in order:
            // per piece a float scale (little-endian) and a signed byte per dimension.
            Execute(connection, """
                CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT) WITHOUT ROWID;
                CREATE TABLE IF NOT EXISTS sources (
                  id INTEGER PRIMARY KEY,
                  conversation TEXT NOT NULL UNIQUE,
                  fingerprint TEXT NOT NULL,
                  chunks INTEGER NOT NULL,
                  messages INTEGER NOT NULL,
                  digest TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS chunks (
                  id INTEGER PRIMARY KEY,
                  ordinal INTEGER NOT NULL,
                  role TEXT NOT NULL,
                  stamp TEXT,
                  start INTEGER NOT NULL,
                  snippet TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS vectors (
                  id INTEGER PRIMARY KEY,
                  block BLOB NOT NULL
                );
                """);
            return new SemanticIndex(connection, path, readOnly: false);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>Opens the index for building, deleting a file that cannot be used; null when not even a new one can be made.</summary>
    public static SemanticIndex? OpenOrRebuild(string path)
    {
        try
        {
            return Open(path);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            if (!DeleteFiles(path)) return null;
            try
            {
                return Open(path);
            }
            catch (Exception again) when (again is SqliteException or IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// The index for searching only, from a process that does not build it: never created, changed
    /// or dropped. Null when there is none, it cannot be opened, or it is of another layout.
    /// </summary>
    public static SemanticIndex? OpenReadOnly(string path)
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
            Execute(connection, "PRAGMA busy_timeout=5000;");
            // A search reads every block: mapped, the file's pages are read in place rather than
            // copied in one system call each — less than half the time. The mapping goes with the
            // connection, at the end of each search.
            Execute(connection, "PRAGMA mmap_size=268435456;");
            if (Scalar(connection, "PRAGMA user_version;") == Layout
                && Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('meta','sources','chunks','vectors');") == 4)
                return new SemanticIndex(connection, path, readOnly: true);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            // Not a database, or held in a way that cannot be read: no index, as far as searching goes.
        }
        connection.Dispose();
        return null;
    }

    /// <summary>What the index at <paramref name="path"/> holds, read without opening it for writing; null when there is none to read.</summary>
    public static SemanticIndexStats? ReadStats(string path)
    {
        using var index = OpenReadOnly(path);
        try
        {
            return index?.Stats();
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    /// <summary>Deletes the index's files; false when one is held open and stays for now.</summary>
    public static bool DeleteFiles(string path)
    {
        var deleted = true;
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try
            {
                if (File.Exists(path + suffix)) File.Delete(path + suffix);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                deleted = false;
            }
        }
        return deleted;
    }

    // ------------------------------------------------------------------ stats

    public SemanticIndexStats Stats()
    {
        lock (_gate)
        {
            var meta = Meta();
            using var cmd = _connection.CreateCommand();
            // A conversation part way through is searched already, but not yet counted as indexed.
            cmd.CommandText = "SELECT COUNT(*) FILTER (WHERE fingerprint <> ''), TOTAL(chunks) FROM sources;";
            using var reader = cmd.ExecuteReader();
            reader.Read();
            return new SemanticIndexStats(
                (int)reader.GetInt64(0),
                (long)reader.GetDouble(1),
                meta.GetValueOrDefault("model"),
                int.TryParse(meta.GetValueOrDefault("dims"), CultureInfo.InvariantCulture, out var dims) ? dims : 0,
                DateTimeOffset.TryParse(meta.GetValueOrDefault("updated"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var updated) ? updated : null,
                meta.GetValueOrDefault("source"));
        }
    }

    private Dictionary<string, string> Meta()
    {
        var meta = new Dictionary<string, string>(StringComparer.Ordinal);
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT key, value FROM meta;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            if (!reader.IsDBNull(1)) meta[reader.GetString(0)] = reader.GetString(1);
        return meta;
    }

    // ------------------------------------------------------------------ build

    private sealed record Known(long Id, string Fingerprint, long Chunks, int Messages, string Digest);

    /// <summary>What identifies a conversation's state in the full-text index: its messages, and its transcript's length and write time.</summary>
    public static string FingerprintOf(IndexedSource source) => string.Join(':',
        source.Messages.ToString(CultureInfo.InvariantCulture),
        source.Length.ToString(CultureInfo.InvariantCulture),
        source.Written.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// Brings the index up to date with <paramref name="dialogue"/>: conversations no longer listed
    /// are dropped, those that changed embedded again (only their new messages when the old ones
    /// stand), in the order listed. An index made by another model is started over first.
    /// <paramref name="progress"/> counts the conversations to embed.
    /// </summary>
    /// <param name="listed">Every conversation still listed, the ones to embed first first (the latest).</param>
    /// <exception cref="OperationCanceledException">Stopped; everything committed so far stays, and the next build goes on from there.</exception>
    public SemanticBuildRun Update(IDialogueSource dialogue, IEmbedder embedder, IReadOnlyList<string> listed,
        IProgress<(int Done, int Total)>? progress = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(dialogue);
        ArgumentNullException.ThrowIfNull(embedder);
        ArgumentNullException.ThrowIfNull(listed);
        if (_readOnly) throw new InvalidOperationException("只读打开的语义索引不能更新");

        UseModel(embedder);
        // Read before anything else: whatever the window writes meanwhile makes the next build look again.
        var source = dialogue.Fingerprint();
        var indexed = new Dictionary<string, IndexedSource>(StringComparer.Ordinal);
        foreach (var s in dialogue.Sources()) indexed[s.Conversation] = s;
        Dictionary<string, Known> known;
        lock (_gate) known = LoadKnown();

        // Conversations no longer listed go; one listed but not (yet) in the full-text index stays.
        var stillListed = listed.ToHashSet(StringComparer.Ordinal);
        var removed = 0;
        foreach (var (conversation, gone) in known)
        {
            if (stillListed.Contains(conversation)) continue;
            cancellation.ThrowIfCancellationRequested();
            lock (_gate)
            {
                using var tx = _connection.BeginTransaction();
                DeleteChunks(tx, gone.Id);
                Run(tx, "DELETE FROM sources WHERE id=$id;", ("$id", gone.Id));
                tx.Commit();
            }
            removed++;
        }

        var work = new List<(string Conversation, IndexedSource Source, Known? Before)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var conversation in listed)
        {
            if (!seen.Add(conversation) || !indexed.TryGetValue(conversation, out var now)) continue;
            known.TryGetValue(conversation, out var before);
            if (before is not null && before.Fingerprint == FingerprintOf(now)) continue;
            work.Add((conversation, now, before));
        }

        var updated = 0;
        long embedded = 0;
        for (var i = 0; i < work.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            progress?.Report((i, work.Count));
            var (conversation, now, before) = work[i];
            embedded += Embed(conversation, now, before, dialogue, embedder, cancellation);
            updated++;
        }
        progress?.Report((work.Count, work.Count));

        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            SetMeta(tx, "source", source);
            SetMeta(tx, "updated", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            tx.Commit();
        }
        var stats = Stats();
        return new SemanticBuildRun(stats.Conversations, stats.Chunks, updated, embedded, removed, source, embedder.ModelId);
    }

    /// <summary>Starts over when the vectors were made by another model, or from another layout of the full-text index.</summary>
    private void UseModel(IEmbedder embedder)
    {
        var model = embedder.ModelId;
        var dims = embedder.Dimensions.ToString(CultureInfo.InvariantCulture);
        var layout = ContentIndex.Layout.ToString(CultureInfo.InvariantCulture);
        lock (_gate)
        {
            var meta = Meta();
            if (meta.GetValueOrDefault("model") == model && meta.GetValueOrDefault("dims") == dims
                && meta.GetValueOrDefault("source-layout") == layout)
                return;
            using var tx = _connection.BeginTransaction();
            Run(tx, "DELETE FROM vectors; DELETE FROM chunks; DELETE FROM sources; DELETE FROM meta;");
            SetMeta(tx, "model", model);
            SetMeta(tx, "dims", dims);
            SetMeta(tx, "source-layout", layout);
            tx.Commit();
        }
    }

    /// <summary>Embeds one conversation's new or changed messages; how many pieces that came to.</summary>
    private long Embed(string conversation, IndexedSource now, Known? before, IDialogueSource dialogue, IEmbedder embedder,
        CancellationToken cancellation)
    {
        var messages = dialogue.Messages(conversation);
        // Written by the window between the two reads: what was read is embedded, and the next build looks again.
        var fingerprint = messages.Count == now.Messages ? FingerprintOf(now) : "?" + FingerprintOf(now);

        var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            // The messages embedded before are kept when they are still the first ones, word for word.
            var from = 0;
            long next = 0;
            if (before is not null && before.Messages <= messages.Count)
            {
                for (var i = 0; i < before.Messages; i++) Append(digest, messages[i]);
                if (Convert.ToHexString(digest.GetCurrentHash()) == before.Digest)
                {
                    from = before.Messages;
                    next = before.Chunks;
                }
                else
                {
                    digest.Dispose();
                    digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                }
            }
            var id = before?.Id;
            var startOver = before is not null && from == 0;

            long embedded = 0;
            var pending = new List<(IndexedMessage Message, TextChunk Chunk)>();
            for (var i = from; i < messages.Count; i++)
            {
                foreach (var chunk in TextChunker.Chunk(messages[i].Text)) pending.Add((messages[i], chunk));
                Append(digest, messages[i]);
                var last = i == messages.Count - 1;
                if (pending.Count < CommitEvery && !last) continue;
                embedded += pending.Count;
                (id, next) = Commit(conversation, id, startOver, pending, next, i + 1, digest, last ? fingerprint : "",
                    embedder, cancellation);
                startOver = false;
                pending.Clear();
            }
            // Nothing new to embed — every new message too short, or none at all: the conversation is up to date all the same.
            if (from == messages.Count)
                Commit(conversation, id, startOver, pending, next, from, digest, fingerprint, embedder, cancellation);
            return embedded;
        }
        finally
        {
            digest.Dispose();
        }
    }

    /// <summary>Embeds the pending pieces, then stores them, their block of vectors and how far the conversation is embedded, in one short transaction.</summary>
    private (long Id, long Next) Commit(string conversation, long? id, bool startOver,
        List<(IndexedMessage Message, TextChunk Chunk)> pending, long next, int messages, IncrementalHash digest,
        string fingerprint, IEmbedder embedder, CancellationToken cancellation)
    {
        var vectors = pending.Count == 0
            ? []
            : embedder.Embed(pending.Select(p => p.Chunk.Text).ToList(), query: false, cancellation);
        if (vectors.Length != pending.Count)
            throw new InvalidOperationException("模型返回的向量个数不对");
        cancellation.ThrowIfCancellationRequested();

        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            var sourceId = id ?? NewSource(tx, conversation);
            if (startOver) DeleteChunks(tx, sourceId);
            if (pending.Count > 0)
            {
                var first = (sourceId << 32) + next;
                var dimensions = vectors[0].Length;
                var block = new byte[pending.Count * Stride(dimensions)];
                using (var cmd = _connection.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = """
                        INSERT INTO chunks(id, ordinal, role, stamp, start, snippet)
                        VALUES($id, $o, $r, $s, $start, $snippet);
                        """;
                    var rowid = cmd.Parameters.Add("$id", SqliteType.Integer);
                    var ordinal = cmd.Parameters.Add("$o", SqliteType.Integer);
                    var role = cmd.Parameters.Add("$r", SqliteType.Text);
                    var stamp = cmd.Parameters.Add("$s", SqliteType.Text);
                    var start = cmd.Parameters.Add("$start", SqliteType.Integer);
                    var snippet = cmd.Parameters.Add("$snippet", SqliteType.Text);
                    for (var i = 0; i < pending.Count; i++)
                    {
                        if (vectors[i].Length != dimensions) throw new InvalidOperationException("模型返回的向量长短不一");
                        var (message, chunk) = pending[i];
                        var (scale, bytes) = Quantize(vectors[i]);
                        var at = i * Stride(dimensions);
                        BinaryPrimitives.WriteSingleLittleEndian(block.AsSpan(at), scale);
                        bytes.CopyTo(block, at + sizeof(float));
                        rowid.Value = (sourceId << 32) + next++;
                        ordinal.Value = message.Ordinal;
                        role.Value = message.Role;
                        stamp.Value = (object?)message.Stamp ?? DBNull.Value;
                        start.Value = chunk.Start;
                        snippet.Value = TextChunker.Snippet(chunk.Text);
                        cmd.ExecuteNonQuery();
                    }
                }
                Run(tx, "INSERT INTO vectors(id, block) VALUES($id, $block);", ("$id", first), ("$block", block));
            }
            Run(tx, "UPDATE sources SET fingerprint=$f, chunks=$c, messages=$m, digest=$d WHERE id=$id;",
                ("$f", fingerprint), ("$c", next), ("$m", messages), ("$d", Convert.ToHexString(digest.GetCurrentHash())),
                ("$id", sourceId));
            tx.Commit();
            return (sourceId, next);
        }
    }

    /// <summary>What a message contributes to its conversation's digest: who, when and the words.</summary>
    private static void Append(IncrementalHash digest, IndexedMessage message)
    {
        digest.AppendData(Encoding.UTF8.GetBytes(message.Role + '\u001f' + message.Stamp + '\u001f' + message.Text + '\u001e'));
    }

    /// <summary>A vector in 8 bits per dimension: each value as a multiple of one scale, the largest as ±127.</summary>
    public static (float Scale, byte[] Bytes) Quantize(float[] vector)
    {
        var max = 0f;
        foreach (var v in vector)
            if (float.IsFinite(v)) max = Math.Max(max, Math.Abs(v));
        var bytes = new byte[vector.Length];
        if (max == 0) return (0, bytes);
        var scale = max / 127f;
        for (var i = 0; i < vector.Length; i++)
        {
            var v = float.IsFinite(vector[i]) ? vector[i] : 0;
            bytes[i] = unchecked((byte)(sbyte)Math.Clamp((int)MathF.Round(v / scale), -127, 127));
        }
        return (scale, bytes);
    }

    private Dictionary<string, Known> LoadKnown()
    {
        var known = new Dictionary<string, Known>(StringComparer.Ordinal);
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT conversation, id, fingerprint, chunks, messages, digest FROM sources;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            known[reader.GetString(0)] = new Known(reader.GetInt64(1), reader.GetString(2), reader.GetInt64(3),
                (int)reader.GetInt64(4), reader.GetString(5));
        return known;
    }

    private long NewSource(SqliteTransaction tx, string conversation)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO sources(conversation, fingerprint, chunks, messages, digest)
            VALUES($c, '', 0, 0, '') RETURNING id;
            """;
        cmd.Parameters.AddWithValue("$c", conversation);
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private void DeleteChunks(SqliteTransaction tx, long id) =>
        Run(tx, "DELETE FROM chunks WHERE id >= $from AND id < $to; DELETE FROM vectors WHERE id >= $from AND id < $to;",
            ("$from", id << 32), ("$to", (id + 1) << 32));

    private void SetMeta(SqliteTransaction tx, string key, string value) =>
        Run(tx, "INSERT INTO meta(key, value) VALUES($k, $v) ON CONFLICT(key) DO UPDATE SET value=excluded.value;",
            ("$k", key), ("$v", value));

    // ----------------------------------------------------------------- search

    /// <summary>
    /// The conversations closest in meaning to <paramref name="query"/> (a vector made by
    /// <paramref name="modelId"/>), the closest first, each with its
    /// <paramref name="perConversation"/> closest pieces. Empty while nothing is indexed.
    /// </summary>
    /// <exception cref="SemanticUnavailableException">The index was made by another model.</exception>
    public IReadOnlyList<SemanticHit> Search(float[] query, string modelId, int limit = 20, int perConversation = 3,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        limit = Math.Clamp(limit, 1, 200);
        perConversation = Math.Clamp(perConversation, 1, 10);

        Dictionary<string, string> meta;
        lock (_gate) meta = Meta();
        if (meta.GetValueOrDefault("model") is not { } model) return [];
        if (model != modelId || meta.GetValueOrDefault("dims") != query.Length.ToString(CultureInfo.InvariantCulture))
            throw new SemanticUnavailableException("语义索引是用另一个模型建的，要等它按现在的模型重新建好才能搜");

        // Every block of vectors streamed past the query, a batch of blocks at a time; per
        // conversation only its best few pieces are kept.
        var best = new Dictionary<long, TopChunks>();
        var stride = Stride(query.Length);
        var after = long.MinValue;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            var rows = 0;
            lock (_gate)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = "SELECT id, block FROM vectors WHERE id > $after ORDER BY id LIMIT $n;";
                cmd.Parameters.AddWithValue("$after", after);
                cmd.Parameters.AddWithValue("$n", SearchBatch);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    rows++;
                    after = reader.GetInt64(0);
                    var block = reader.GetFieldValue<byte[]>(1);
                    if (block.Length % stride != 0) continue;
                    var source = after >> 32;
                    if (!best.TryGetValue(source, out var top)) best[source] = top = new TopChunks(perConversation);
                    for (int i = 0, at = 0; at < block.Length; i++, at += stride)
                    {
                        var scale = BinaryPrimitives.ReadSingleLittleEndian(block.AsSpan(at));
                        top.Offer(scale * Dot(query, block.AsSpan(at + sizeof(float), query.Length)), after + i);
                    }
                }
            }
            if (rows < SearchBatch) break;
        }

        var chosen = best.OrderByDescending(b => b.Value.Best).Take(limit).ToList();
        if (chosen.Count == 0) return [];
        Dictionary<long, string> names;
        Dictionary<long, (int Ordinal, string Role, string? Stamp, int Start, string Snippet)> pieces;
        lock (_gate)
        {
            names = Names(chosen.Select(c => c.Key));
            pieces = Pieces(chosen.SelectMany(c => c.Value.IdsInUse));
        }

        var hits = new List<SemanticHit>(chosen.Count);
        foreach (var (source, top) in chosen)
        {
            // Dropped meanwhile by the build: not a result any more.
            if (!names.TryGetValue(source, out var conversation)) continue;
            var chunks = new List<SemanticChunkHit>();
            for (var i = 0; i < top.Count; i++)
            {
                if (!pieces.TryGetValue(top.Ids[i], out var piece)) continue;
                chunks.Add(new SemanticChunkHit(top.Scores[i], piece.Role, piece.Stamp, piece.Ordinal, piece.Start, piece.Snippet));
            }
            if (chunks.Count > 0) hits.Add(new SemanticHit(conversation, chunks[0].Score, chunks));
        }
        return hits;
    }

    private Dictionary<long, string> Names(IEnumerable<long> sources)
    {
        var names = new Dictionary<long, string>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"SELECT id, conversation FROM sources WHERE id IN ({InList(cmd, sources)});";
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) names[reader.GetInt64(0)] = reader.GetString(1);
        return names;
    }

    private Dictionary<long, (int, string, string?, int, string)> Pieces(IEnumerable<long> ids)
    {
        var pieces = new Dictionary<long, (int, string, string?, int, string)>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"SELECT id, ordinal, role, stamp, start, snippet FROM chunks WHERE id IN ({InList(cmd, ids)});";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            pieces[reader.GetInt64(0)] = ((int)reader.GetInt64(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3),
                (int)reader.GetInt64(4), reader.GetString(5));
        return pieces;
    }

    private static string InList(SqliteCommand cmd, IEnumerable<long> values)
    {
        var names = new List<string>();
        foreach (var value in values)
        {
            var name = "$p" + names.Count.ToString(CultureInfo.InvariantCulture);
            cmd.Parameters.AddWithValue(name, value);
            names.Add(name);
        }
        return string.Join(',', names);
    }

    /// <summary>A float query against an 8-bit vector, in as wide steps as the processor takes.</summary>
    public static float Dot(ReadOnlySpan<float> query, ReadOnlySpan<byte> vector)
    {
        var values = MemoryMarshal.Cast<byte, sbyte>(vector);
        var n = Math.Min(query.Length, values.Length);
        var i = 0;
        var sum = 0f;
        if (Vector.IsHardwareAccelerated && n >= Vector<sbyte>.Count)
        {
            var acc = Vector<float>.Zero;
            var lanes = Vector<int>.Count;
            for (; i <= n - Vector<sbyte>.Count; i += Vector<sbyte>.Count)
            {
                Vector.Widen(new Vector<sbyte>(values[i..]), out Vector<short> low, out Vector<short> high);
                Vector.Widen(low, out Vector<int> a, out Vector<int> b);
                Vector.Widen(high, out Vector<int> c, out Vector<int> d);
                acc += Vector.ConvertToSingle(a) * new Vector<float>(query[i..]);
                acc += Vector.ConvertToSingle(b) * new Vector<float>(query[(i + lanes)..]);
                acc += Vector.ConvertToSingle(c) * new Vector<float>(query[(i + 2 * lanes)..]);
                acc += Vector.ConvertToSingle(d) * new Vector<float>(query[(i + 3 * lanes)..]);
            }
            sum = Vector.Sum(acc);
        }
        for (; i < n; i++) sum += query[i] * values[i];
        return sum;
    }

    /// <summary>One conversation's best few pieces so far, best first.</summary>
    private sealed class TopChunks(int size)
    {
        public readonly float[] Scores = new float[size];
        public readonly long[] Ids = new long[size];
        public int Count;

        public float Best => Scores[0];

        public IEnumerable<long> IdsInUse => Ids.Take(Count);

        public void Offer(float score, long id)
        {
            if (Count == Scores.Length && score <= Scores[Count - 1]) return;
            var at = Count < Scores.Length ? Count++ : Count - 1;
            while (at > 0 && Scores[at - 1] < score)
            {
                Scores[at] = Scores[at - 1];
                Ids[at] = Ids[at - 1];
                at--;
            }
            Scores[at] = score;
            Ids[at] = id;
        }
    }

    // ---------------------------------------------------------------- helpers

    private void Run(SqliteTransaction tx, string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public void Dispose()
    {
        lock (_gate) _connection.Dispose();
    }
}
