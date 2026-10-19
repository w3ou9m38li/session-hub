using System.Globalization;
using System.Text.Json;
using ClaudeLauncher.Core.Contracts;
using ClaudeLauncher.Infrastructure.Catalog;
using Microsoft.Data.Sqlite;

namespace ClaudeLauncher.Infrastructure.Backends;

/// <summary>Where Codex (the ChatGPT desktop app's coding agent) keeps its threads.</summary>
public sealed record CodexPaths(string Home, string StateDatabase, string SessionsRoot, string ArchivedSessionsRoot)
{
    /// <summary>
    /// <c>CODEX_HOME</c> from the process, then from the user environment (a desktop launcher
    /// may not inherit it), then the default <c>~\.codex</c>.
    /// </summary>
    public static string DefaultHome()
    {
        var configured = Environment.GetEnvironmentVariable("CODEX_HOME")
            ?? Environment.GetEnvironmentVariable("CODEX_HOME", EnvironmentVariableTarget.User);
        if (string.IsNullOrWhiteSpace(configured))
            configured = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        return Path.GetFullPath(configured);
    }

    public static CodexPaths Discover() => FromHome(DefaultHome());

    public static CodexPaths FromHome(string home)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(home);
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(home));
        return new CodexPaths(full,
            Path.Combine(full, "state_5.sqlite"),
            Path.Combine(full, "sessions"),
            Path.Combine(full, "archived_sessions"));
    }

    public bool DatabaseExists => File.Exists(StateDatabase);
}

/// <summary>What one read of the Codex store found, with the numbers the coverage report needs.</summary>
public sealed record CodexScan(
    IReadOnlyList<SessionMetadata> Rows,
    int ThreadsInDatabase,
    int RootThreads,
    int RootArchived,
    int Subagents,
    int RolloutFiles,
    int RolloutsNotInDatabase,
    int RowsWithoutFile);

/// <summary>
/// Reads Codex thread metadata from <c>CODEX_HOME</c>, strictly read-only.
///
/// <para>The authoritative list is the <c>threads</c> table of <c>state_5.sqlite</c>, opened
/// read-only: it carries the title, working directory, archive flag and the path of the
/// rollout file (the transcript) for every thread. The <c>sessions</c> and
/// <c>archived_sessions</c> trees are then enumerated by file name only, so a rollout the
/// database does not know about is still listed — its header is the only file content read,
/// and only within a fixed byte budget.</para>
///
/// <para>Sub-agent threads (spawned by another thread) are counted and left out of the list,
/// the same way Claude's sub-agent transcripts are: they are not conversations the user had.</para>
///
/// <para>Nothing here writes. The database is opened read-only; rollouts are opened for
/// shared read; and when SQLite refuses a read-only open of a live WAL database, a private
/// snapshot copy is read instead of ever touching the original with write access.</para>
/// </summary>
public sealed class CodexMetadataReader
{
    public const int HeaderByteLimit = 64 * 1024;
    public const int HeaderLineLimit = 32;
    private const int MaxTitleLength = 120;

    private readonly CodexPaths _paths;
    private readonly string _snapshotDirectory;

    public CodexMetadataReader(CodexPaths paths, string? snapshotDirectory = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _snapshotDirectory = snapshotDirectory ?? Path.Combine(LauncherPaths.DiagnosticsDirectory, "codex-snapshot");
    }

    public bool DatabaseExists => _paths.DatabaseExists;

    public CodexScan Read(CancellationToken cancellation = default)
    {
        var threads = ReadThreads(cancellation);
        var sections = threads.Sections;
        var files = IndexRollouts(cancellation);

        var rows = new List<SessionMetadata>(threads.Rows.Count);
        var knownIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int roots = 0, rootArchived = 0, subagents = 0, withoutFile = 0;

        foreach (var t in threads.Rows)
        {
            cancellation.ThrowIfCancellationRequested();
            knownIds.Add(t.Id);
            if (t.IsSubagent) { subagents++; continue; }
            roots++;
            if (t.Archived) rootArchived++;

            // The database's own path first — or that file compressed where it was (a maintenance
            // tool's rollout-….jsonl.zst); a thread whose file moved is looked up by id.
            var path = t.RolloutPath is { Length: > 0 } recorded && File.Exists(recorded) ? recorded
                : t.RolloutPath is { Length: > 0 } packed && File.Exists(packed + TranscriptFiles.ZstdExtension)
                    ? packed + TranscriptFiles.ZstdExtension
                : files.TryGetValue(t.Id, out var indexed) ? indexed.Path : null;
            if (path is null) withoutFile++;

            // A name the user gave stays as it is; the rest is text typed in, tidied for a list.
            var title = FirstNonBlank(t.Name, Tidy(t.Title), Tidy(t.Preview), Tidy(t.FirstUserMessage));
            rows.Add(new SessionMetadata
            {
                Key = new ConversationKey(ConversationKey.Codex, _paths.Home, "default", t.Id),
                CliSessionId = t.Id,
                Title = title ?? t.Id,
                SearchKey = SearchKeys.Normalize(title ?? t.Id),
                ProjectRef = string.IsNullOrWhiteSpace(t.Cwd) ? null : t.Cwd,
                NativeOpenRef = path,
                LastActivityUtc = t.UpdatedAt,
                SourceRevision = $"{t.UpdatedAt.ToUnixTimeMilliseconds()}:{(t.Archived ? 1 : 0)}",
                TranscriptBytes = SizeOf(path),
                SidebarGroup = t.SectionId is not null && sections.TryGetValue(t.SectionId, out var section) ? section : null,
                TitleIsPlaceholder = title is null,
                // Forked from another task: its history begins with a copy of that one's.
                ResumedFrom = path is null ? null : OwnPart.ForkedFrom(path),
                // Observed from Codex's own catalog; this tool never writes it back.
                NativeArchiveState = t.Archived ? NativeArchiveState.Archived : NativeArchiveState.Active,
                SourceCoverage = path is null ? SourceCoverage.Partial : SourceCoverage.Resolved,
            });
        }

        // Rollouts the database does not list: still conversations, still shown.
        var orphans = 0;
        foreach (var (id, file) in files)
        {
            if (knownIds.Contains(id)) continue;
            cancellation.ThrowIfCancellationRequested();
            var header = ReadHeader(file.Path);
            if (header.IsSubagent) { subagents++; continue; }
            orphans++;
            roots++;
            if (file.Archived) rootArchived++;

            var stamp = header.Timestamp ?? new DateTimeOffset(File.GetLastWriteTimeUtc(file.Path), TimeSpan.Zero);
            rows.Add(new SessionMetadata
            {
                Key = new ConversationKey(ConversationKey.Codex, _paths.Home, "default", id),
                CliSessionId = id,
                Title = id,
                SearchKey = SearchKeys.Normalize(id),
                ProjectRef = string.IsNullOrWhiteSpace(header.Cwd) ? null : header.Cwd,
                NativeOpenRef = file.Path,
                LastActivityUtc = stamp,
                SourceRevision = $"{stamp.ToUnixTimeMilliseconds()}:{(file.Archived ? 1 : 0)}:file",
                TranscriptBytes = SizeOf(file.Path),
                TitleIsPlaceholder = true,
                ResumedFrom = OwnPart.ForkedFrom(file.Path),
                NativeArchiveState = file.Archived ? NativeArchiveState.Archived : NativeArchiveState.Active,
                SourceCoverage = SourceCoverage.Resolved,
            });
        }

        return new CodexScan(rows, threads.Rows.Count, roots, rootArchived, subagents,
            files.Count, orphans, withoutFile);
    }

    /// <summary>A rollout's size, or null when it is not there to measure.</summary>
    private static long? SizeOf(string? path)
    {
        if (path is null) return null;
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------- database

    private sealed record ThreadRow(
        string Id, string? RolloutPath, DateTimeOffset UpdatedAt, string? Cwd,
        string? Name, string? Title, string? Preview, string? FirstUserMessage,
        bool Archived, bool IsSubagent, string? SectionId);

    private sealed record ThreadTable(IReadOnlyList<ThreadRow> Rows, IReadOnlyDictionary<string, string> Sections);

    private ThreadTable ReadThreads(CancellationToken cancellation)
    {
        if (!_paths.DatabaseExists)
            throw new FileNotFoundException("Codex 目录数据库不存在", _paths.StateDatabase);

        try
        {
            return ReadThreads(_paths.StateDatabase, cancellation);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 14 or 26 or 8)
        {
            // CANTOPEN / NOTADB / READONLY against a live WAL database: read a private copy
            // instead. The original is only ever read, never opened with write access.
            return ReadThreads(Snapshot(), cancellation);
        }
    }

    private static ThreadTable ReadThreads(string databasePath, CancellationToken cancellation)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();

        var sections = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT id, name FROM thread_sections;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                if (!reader.IsDBNull(0) && !reader.IsDBNull(1)) sections[reader.GetString(0)] = reader.GetString(1);
        }
        catch (SqliteException)
        {
            // An older store without sections simply has no groups to show.
        }

        var rows = new List<ThreadRow>();
        using (var cmd = connection.CreateCommand())
        {
            // SELECT * with lookups by name: the table has gained columns across versions and
            // the reader must keep working when a name is absent.
            cmd.CommandText = "SELECT * FROM threads;";
            using var reader = cmd.ExecuteReader();
            var ordinals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++) ordinals[reader.GetName(i)] = i;

            while (reader.Read())
            {
                cancellation.ThrowIfCancellationRequested();
                var id = Text(reader, ordinals, "id", "thread_id");
                if (string.IsNullOrWhiteSpace(id)) continue;

                var source = Text(reader, ordinals, "source");
                var threadSource = Text(reader, ordinals, "thread_source");
                var isSubagent = string.Equals(threadSource, "subagent", StringComparison.OrdinalIgnoreCase)
                    || SourceIsSubagent(source);

                rows.Add(new ThreadRow(
                    id,
                    NormalizePath(Text(reader, ordinals, "rollout_path")),
                    ReadUpdated(reader, ordinals),
                    NormalizePath(Text(reader, ordinals, "cwd")),
                    Clean(Text(reader, ordinals, "name")),
                    Clean(Text(reader, ordinals, "title")),
                    Clean(Text(reader, ordinals, "preview")),
                    Clean(Text(reader, ordinals, "first_user_message")),
                    Flag(reader, ordinals, "archived", "is_archived"),
                    isSubagent,
                    Text(reader, ordinals, "thread_section_id")));
            }
        }
        return new ThreadTable(rows, sections);
    }

    /// <summary>Copies the database and its write-ahead log into this tool's own folder.</summary>
    private string Snapshot()
    {
        Directory.CreateDirectory(_snapshotDirectory);
        var target = Path.Combine(_snapshotDirectory, "state_5.sqlite");
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var source = _paths.StateDatabase + suffix;
            var copy = target + suffix;
            if (File.Exists(source)) File.Copy(source, copy, overwrite: true);
            else if (File.Exists(copy)) File.Delete(copy);
        }
        return target;
    }

    private static bool SourceIsSubagent(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return false;
        if (source[0] != '{') return source.Contains("subagent", StringComparison.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(source);
            return SourceElementIsSubagent(doc.RootElement);
        }
        catch (JsonException)
        {
            return source.Contains("subagent", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Stored rollouts write <c>subagent</c>; the public protocol writes <c>subAgent</c>. Both are real.</summary>
    private static bool SourceElementIsSubagent(JsonElement source) => source.ValueKind switch
    {
        JsonValueKind.String => (source.GetString() ?? "").Contains("subagent", StringComparison.OrdinalIgnoreCase),
        JsonValueKind.Object => source.TryGetProperty("subagent", out _) || source.TryGetProperty("subAgent", out _),
        _ => false,
    };

    private static DateTimeOffset ReadUpdated(SqliteDataReader reader, Dictionary<string, int> ordinals)
    {
        if (Long(reader, ordinals, "updated_at_ms") is { } ms && ms > 0)
            return DateTimeOffset.FromUnixTimeMilliseconds(ms);
        if (Long(reader, ordinals, "updated_at") is { } seconds && seconds > 0)
            return seconds > 10_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(seconds)
                : DateTimeOffset.FromUnixTimeSeconds(seconds);
        return DateTimeOffset.UnixEpoch;
    }

    private static string? Text(SqliteDataReader reader, Dictionary<string, int> ordinals, params string[] names)
    {
        foreach (var name in names)
            if (ordinals.TryGetValue(name, out var i) && !reader.IsDBNull(i))
                return Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);
        return null;
    }

    private static long? Long(SqliteDataReader reader, Dictionary<string, int> ordinals, string name)
    {
        if (!ordinals.TryGetValue(name, out var i) || reader.IsDBNull(i)) return null;
        var value = reader.GetValue(i);
        return value switch
        {
            long l => l,
            int n => n,
            double d => (long)d,
            string s when long.TryParse(s, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    private static bool Flag(SqliteDataReader reader, Dictionary<string, int> ordinals, params string[] names)
    {
        var value = Text(reader, ordinals, names);
        return value is "1" or "true" or "True";
    }

    // ------------------------------------------------------------- rollouts

    private sealed record RolloutFile(string Path, bool Archived);

    /// <summary>Every rollout by thread id, from the file name alone: no file is opened here.</summary>
    private Dictionary<string, RolloutFile> IndexRollouts(CancellationToken cancellation)
    {
        var index = new Dictionary<string, RolloutFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var (root, archived) in new[] { (_paths.SessionsRoot, false), (_paths.ArchivedSessionsRoot, true) })
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories)
                    .Concat(Directory.EnumerateFiles(root, "*.jsonl" + TranscriptFiles.ZstdExtension, SearchOption.AllDirectories))
                    .ToList();
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            foreach (var file in files)
            {
                cancellation.ThrowIfCancellationRequested();
                var id = ThreadIdFromFileName(TranscriptFiles.WithoutCompression(file));
                if (id is null) continue;
                // A plain rollout wins over a compressed one of the same thread.
                if (TranscriptFiles.IsCompressed(file) && index.TryGetValue(id, out var plain) && !TranscriptFiles.IsCompressed(plain.Path)) continue;
                // A thread present in both trees keeps its active copy; archive wins nothing.
                if (!index.ContainsKey(id) || !archived) index[id] = new RolloutFile(file, archived);
            }
        }
        return index;
    }

    /// <summary>rollout-2026-09-22T23-41-55-&lt;uuid&gt;.jsonl → the uuid. Null for anything else.</summary>
    public static string? ThreadIdFromFileName(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        if (!stem.StartsWith("rollout-", StringComparison.OrdinalIgnoreCase) || stem.Length < 36) return null;
        var id = stem[^36..];
        return id.Count(c => c == '-') == 4 && id.All(c => c == '-' || Uri.IsHexDigit(c)) ? id : null;
    }

    private sealed record Header(string? Cwd, bool IsSubagent, DateTimeOffset? Timestamp);

    /// <summary>The <c>session_meta</c> line, read within a fixed budget; nothing past it.</summary>
    private static Header ReadHeader(string path)
    {
        try
        {
            foreach (var line in ReadPrefixLines(path, HeaderByteLimit, HeaderLineLimit))
            {
                if (line.Length == 0 || line[0] != '{') continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (Str(root, "type") != "session_meta" || !root.TryGetProperty("payload", out var payload)) continue;

                    var subagent = string.Equals(Str(payload, "thread_source"), "subagent", StringComparison.OrdinalIgnoreCase)
                        || (payload.TryGetProperty("source", out var source) && SourceElementIsSubagent(source));
                    DateTimeOffset? when = DateTimeOffset.TryParse(Str(root, "timestamp") ?? Str(payload, "timestamp"),
                        CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
                        ? parsed : null;
                    return new Header(NormalizePath(Str(payload, "cwd")), subagent, when);
                }
                catch (JsonException) { /* isolate one bad line */ }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (ZstdSharp.ZstdException) { /* a compressed rollout damaged or cut short */ }
        // No recognisable header: the file name is still a thread id and the folder decides
        // its state. Treated as a root conversation rather than silently dropped.
        return new Header(null, path.Contains("subagent", StringComparison.OrdinalIgnoreCase), null);
    }

    private static IEnumerable<string> ReadPrefixLines(string path, int maxBytes, int maxLines)
    {
        var buffer = new byte[maxBytes];
        var read = 0;
        // Through its compression when a maintenance tool compressed it (rollout-….jsonl.zst).
        using (var stream = TranscriptFiles.OpenRead(path))
        {
            while (read < buffer.Length)
            {
                var n = stream.Read(buffer, read, buffer.Length - read);
                if (n == 0) break;
                read += n;
            }
        }
        var text = System.Text.Encoding.UTF8.GetString(buffer, 0, read);
        var lines = new List<string>();
        var start = 0;
        while (start < text.Length && lines.Count < maxLines)
        {
            var newline = text.IndexOf('\n', start);
            var end = newline < 0 ? text.Length : newline;
            if (end > start && text[end - 1] == '\r') end--;
            lines.Add(text[start..end]);
            if (newline < 0) break;
            start = newline + 1;
        }
        return lines;
    }

    // -------------------------------------------------------------- helpers

    /// <summary>Codex stores some paths in the extended <c>\\?\</c> form; shown and compared without it.</summary>
    public static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + path[8..];
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path[4..];
        return path;
    }

    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var flat = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length > MaxTitleLength ? flat[..MaxTitleLength] : flat;
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static string? Tidy(string? text) => string.IsNullOrWhiteSpace(text) ? null : TitleText.Tidy(text);

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
