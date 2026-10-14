using ClaudeLauncher.Core.Contracts;
using ClaudeLauncher.Infrastructure.Catalog;

namespace ClaudeLauncher.Infrastructure.Backends;

/// <summary>What one read of DeepSeek Harness's sessions found, with the numbers the coverage report needs.</summary>
/// <param name="Subagents">Sessions a subagent ran for another session: counted, not listed.</param>
public sealed record DshScan(List<SessionMetadata> Rows, int Files, int Unreadable, int Homes, int Subagents);

/// <summary>Session headers kept across syncs: a log that has not changed is not unpacked again.</summary>
public sealed class DshProbeCache
{
    private readonly Dictionary<string, (long Length, long Written, DshSessionHeader? Header)> _known =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    public DshSessionHeader? Header(FileInfo file, CancellationToken cancellation = default)
    {
        lock (_gate)
            if (_known.TryGetValue(file.FullName, out var known)
                && known.Length == file.Length && known.Written == file.LastWriteTimeUtc.Ticks)
                return known.Header;
        var header = DshSession.ReadHeader(file.FullName, cancellation);
        lock (_gate) _known[file.FullName] = (file.Length, file.LastWriteTimeUtc.Ticks, header);
        return header;
    }
}

/// <summary>
/// Lists DeepSeek Harness's sessions as catalog rows, read-only: one row per session the user
/// started, named by the title dsh gave it (else by what the user first said), in the folder it
/// ran in, active or archived as dsh's own list has it. A home is the account: the same session
/// id in two homes is two conversations.
/// </summary>
public sealed class DshMetadataReader(DshPaths paths, DshProbeCache? cache = null)
{
    private readonly DshProbeCache _cache = cache ?? new DshProbeCache();

    public DshScan Read(CancellationToken cancellation = default)
    {
        var bySession = new Dictionary<(string Home, string Id), SessionMetadata>();
        var archived = new Dictionary<string, HashSet<string>?>(StringComparer.OrdinalIgnoreCase);
        int files = 0, unreadable = 0, subagents = 0;
        foreach (var (file, home) in paths.SessionFiles())
        {
            cancellation.ThrowIfCancellationRequested();
            files++;
            FileInfo info;
            DshSessionHeader? header;
            try
            {
                info = new FileInfo(file);
                header = _cache.Header(info, cancellation);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ZstdSharp.ZstdException)
            {
                unreadable++;          // held, or damaged: the next sync tries again
                continue;
            }
            if (header is null) continue;
            if (header.DelegationDepth > 0)
            {
                subagents++;
                continue;
            }

            var title = header.Title is { } named ? Tidy(named) : null;
            title ??= header.FirstUserWords is { } words ? Tidy(words) : null;
            if (!archived.TryGetValue(home, out var shelved)) archived[home] = shelved = DshPaths.ArchivedIn(home);
            var row = new SessionMetadata
            {
                Key = new ConversationKey(ConversationKey.DeepSeek, home, "default", header.SessionId),
                Title = title ?? header.SessionId,
                SearchKey = SearchKeys.Normalize(title ?? header.SessionId),
                ProjectRef = header.Cwd,
                NativeOpenRef = info.FullName,
                LastActivityUtc = header.LastActivity,
                SourceRevision = $"{header.LastActivity.ToUnixTimeMilliseconds()}:{info.Length}",
                TranscriptBytes = info.Length,
                TitleIsPlaceholder = title is null,
                // dsh's own list archives a session by id in its workspace registry.
                NativeArchiveState = shelved is null ? NativeArchiveState.Unknown
                    : shelved.Contains(header.SessionId) ? NativeArchiveState.Archived : NativeArchiveState.Active,
                SourceCoverage = SourceCoverage.Resolved,
            };
            var key = (home.ToUpperInvariant(), header.SessionId);
            if (!bySession.TryGetValue(key, out var seen) || seen.LastActivityUtc < row.LastActivityUtc) bySession[key] = row;
        }
        return new DshScan([.. bySession.Values], files, unreadable, paths.Homes.Count, subagents);
    }

    /// <summary>What a list can show of a title or what was said: one line, at most 200 characters.</summary>
    private static string? Tidy(string text)
    {
        var flat = string.Join(' ', TranscriptText.Clean(text).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var tidy = TitleText.Tidy(flat);
        if (tidy.Length == 0) return null;
        return tidy.Length > 200 ? tidy[..200] + "…" : tidy;
    }
}
