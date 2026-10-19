using System.Globalization;
using Microsoft.Data.Sqlite;

namespace ClaudeLauncher.Infrastructure.Search;

/// <summary>One message as the index holds it: its place in the conversation, who said it, when, and the words (cut at <see cref="ContentIndex.MaxMessageChars"/>).</summary>
/// <param name="Role">"user" or "assistant".</param>
public sealed record IndexedMessage(int Ordinal, string Role, string? Stamp, string Text);

/// <summary>One conversation in the index: its transcript's length and write time when it was last read, and how many messages it holds.</summary>
public sealed record IndexedSource(string Conversation, long Id, int Messages, long Length, long Written);

/// <summary>
/// The full-text index read from a process beside the window, which keeps writing it. Never
/// creates, rebuilds, drops or deletes anything: an index that is not there, or was written by
/// another layout, is not opened at all. Each call is one statement, so a read never holds the
/// index for longer than it takes and the window's writes and checkpoints go on around it.
/// </summary>
public sealed class ContentIndexReader : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly Lock _gate = new();

    private ContentIndexReader(SqliteConnection connection) => _connection = connection;

    /// <summary>The index at <paramref name="path"/>, for reading; null when there is none, it cannot be opened, or it is of another layout.</summary>
    public static ContentIndexReader? Open(string path)
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
            if (Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) == ContentIndex.Layout)
                return new ContentIndexReader(connection);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            // Not a database, or held in a way that cannot be read: no index, as far as this reader goes.
        }
        connection.Dispose();
        return null;
    }

    /// <summary>How many conversations are in the index.</summary>
    public int Count()
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sources;";
            return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Changes whenever the window indexes or drops something — cheap, from the small sources
    /// table only. Equal values mean nothing a search finds has changed in between.
    /// </summary>
    public string Fingerprint()
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*), MAX(id), TOTAL(next_message), TOTAL(length) FROM sources;";
            using var reader = cmd.ExecuteReader();
            reader.Read();
            return string.Join(':',
                reader.GetInt64(0).ToString(CultureInfo.InvariantCulture),
                reader.IsDBNull(1) ? "-" : reader.GetInt64(1).ToString(CultureInfo.InvariantCulture),
                reader.GetDouble(2).ToString("R", CultureInfo.InvariantCulture),
                reader.GetDouble(3).ToString("R", CultureInfo.InvariantCulture));
        }
    }

    /// <summary>Where a term was said in one conversation, exactly as <see cref="ContentIndex.Hits(string, string, int, bool)"/> finds it.</summary>
    /// <param name="userOnly">Only what the user said.</param>
    public IReadOnlyList<ContentHit> Hits(string conversation, string term, int max = 2, bool userOnly = false)
    {
        if (ContentIndex.TermCondition(term, "$q") is null) return new List<ContentHit>();
        lock (_gate) return ContentIndex.Hits(_connection, conversation, term, max, userOnly);
    }

    /// <summary>What was said in one conversation, in order — dialogue only, each message cut as the index keeps it; empty when it is not in the index.</summary>
    public IReadOnlyList<IndexedMessage> Messages(string conversation, int maxMessages = int.MaxValue)
    {
        var messages = new List<IndexedMessage>();
        if (maxMessages <= 0) return messages;
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            // A conversation's messages are one rowid range: (id << 32) + their ordinal.
            cmd.CommandText = """
                SELECT rowid & 4294967295, role, stamp, text FROM messages
                WHERE rowid >= (SELECT id FROM sources WHERE conversation=$c) << 32
                  AND rowid < ((SELECT id FROM sources WHERE conversation=$c) + 1) << 32
                ORDER BY rowid LIMIT $max;
                """;
            cmd.Parameters.AddWithValue("$c", conversation);
            cmd.Parameters.AddWithValue("$max", maxMessages);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                messages.Add(new IndexedMessage((int)reader.GetInt64(0), reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3)));
        }
        return messages;
    }

    /// <summary>Every conversation in the index, by its number there.</summary>
    public IReadOnlyList<IndexedSource> Sources()
    {
        var sources = new List<IndexedSource>();
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT conversation, id, next_message, length, written FROM sources ORDER BY id;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                sources.Add(new IndexedSource(reader.GetString(0), reader.GetInt64(1), (int)reader.GetInt64(2),
                    reader.GetInt64(3), reader.GetInt64(4)));
        }
        return sources;
    }

    public void Dispose()
    {
        lock (_gate) _connection.Dispose();
    }
}
