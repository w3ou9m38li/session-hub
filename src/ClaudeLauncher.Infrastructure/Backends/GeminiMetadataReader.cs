using ClaudeLauncher.Core.Contracts;
using ClaudeLauncher.Infrastructure.Catalog;

namespace ClaudeLauncher.Infrastructure.Backends;

/// <summary>What one read of Gemini CLI's sessions found, with the numbers the coverage report needs.</summary>
public sealed record GeminiScan(List<SessionMetadata> Rows, int Files, int Unreadable, int Projects);

/// <summary>
/// Session headers kept across syncs: a session file that has not changed is not parsed again.
/// One of them can carry a file sent inline — megabytes of it — and a sync runs every minute.
/// </summary>
public sealed class GeminiProbeCache
{
    private readonly Dictionary<string, (long Length, long Written, GeminiSessionHeader? Header)> _known =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    public GeminiSessionHeader? Header(FileInfo file)
    {
        lock (_gate)
            if (_known.TryGetValue(file.FullName, out var known)
                && known.Length == file.Length && known.Written == file.LastWriteTimeUtc.Ticks)
                return known.Header;
        var header = GeminiSession.ReadHeader(file.FullName);
        lock (_gate) _known[file.FullName] = (file.Length, file.LastWriteTimeUtc.Ticks, header);
        return header;
    }
}

/// <summary>
/// Lists Gemini CLI's sessions as catalog rows, read-only: one row per session, named by the
/// summary Gemini wrote for it when there is one, otherwise by what the user first said; its
/// folder is the working directory the project's <c>.project_root</c> names. The conversations
/// imported from Gemini on the web are kept in the same form and listed by the same reader
/// (<see cref="ForTakeout"/>), as a source of their own with no folder and no CLI to resume in.
/// </summary>
public sealed class GeminiMetadataReader
{
    private readonly GeminiProbeCache _cache;
    private readonly string _backend;
    private readonly string _account;
    private readonly Func<IEnumerable<(string File, string? ProjectFolder)>> _files;
    private readonly bool _resumable;

    public GeminiMetadataReader(GeminiPaths paths, GeminiProbeCache? cache = null)
        : this(ConversationKey.Gemini, paths.Home, () => paths.SessionFiles().Select(f => (f.File, (string?)f.ProjectFolder)),
            resumable: true, cache)
    {
    }

    private GeminiMetadataReader(string backend, string account,
        Func<IEnumerable<(string File, string? ProjectFolder)>> files, bool resumable, GeminiProbeCache? cache)
    {
        _backend = backend;
        _account = account;
        _files = files;
        _resumable = resumable;
        _cache = cache ?? new GeminiProbeCache();
    }

    /// <summary>The conversations imported from Google Takeout into this tool's own store.</summary>
    public static GeminiMetadataReader ForTakeout(GeminiTakeoutStore store, GeminiProbeCache? cache = null) =>
        new(ConversationKey.GeminiWeb, store.Root, () => store.SessionFiles().Select(f => (f, (string?)null)),
            resumable: false, cache);

    /// <summary>The conversations imported from ChatGPT's or Claude's data export, kept in the same form.</summary>
    public static GeminiMetadataReader ForImport(WebExportStore store, GeminiProbeCache? cache = null) =>
        new(store.Backend, store.Root, () => store.SessionFiles().Select(f => (f, (string?)null)), resumable: false, cache);

    public GeminiScan Read(CancellationToken cancellation = default)
    {
        var bySession = new Dictionary<string, SessionMetadata>(StringComparer.OrdinalIgnoreCase);
        var roots = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        int files = 0, unreadable = 0;
        foreach (var (file, project) in _files())
        {
            cancellation.ThrowIfCancellationRequested();
            files++;
            FileInfo info;
            GeminiSessionHeader? header;
            try
            {
                info = new FileInfo(file);
                header = _cache.Header(info);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                unreadable++;          // being written right now, or damaged: the next sync tries again
                continue;
            }
            if (header is null) continue;

            string? root = null;
            if (project is not null && !roots.TryGetValue(project, out root)) roots[project] = root = GeminiPaths.ProjectRootOf(project);
            var title = header.Summary is { } summary ? Tidy(summary) : header.FirstUserWords is { } words ? Tidy(words) : null;
            var updated = header.LastUpdated ?? new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
            var row = new SessionMetadata
            {
                Key = new ConversationKey(_backend, _account, "default", header.SessionId),
                CliSessionId = _resumable ? header.SessionId : null,
                Title = title ?? header.SessionId,
                SearchKey = SearchKeys.Normalize(title ?? header.SessionId),
                ProjectRef = root,
                NativeOpenRef = info.FullName,
                LastActivityUtc = updated,
                SourceRevision = $"{updated.ToUnixTimeMilliseconds()}:{info.Length}",
                TranscriptBytes = info.Length,
                TitleIsPlaceholder = title is null,
                // A terminal tool, or an imported copy: nothing in it is archived or listed anywhere else.
                NativeArchiveState = NativeArchiveState.Unsupported,
                SourceCoverage = SourceCoverage.Resolved,
            };
            // The same session in two files (a copy left behind): the one written last is the conversation.
            if (!bySession.TryGetValue(header.SessionId, out var seen) || seen.LastActivityUtc < row.LastActivityUtc)
                bySession[header.SessionId] = row;
        }
        return new GeminiScan([.. bySession.Values], files, unreadable, roots.Count);
    }

    /// <summary>What a list can show of what was said: one line, the file an @-mention names, at most 200 characters.</summary>
    private static string? Tidy(string text)
    {
        var flat = string.Join(' ', TranscriptText.Clean(text).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var tidy = TitleText.Tidy(flat);
        if (tidy.Length == 0) return null;
        return tidy.Length > 200 ? tidy[..200] + "…" : tidy;
    }
}
