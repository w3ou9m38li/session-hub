using System.Text.Json;
using ClaudeLauncher.Core.Contracts;
using ClaudeLauncher.Infrastructure.Catalog;

namespace ClaudeLauncher.Infrastructure.Backends;

/// <summary>
/// Reads Desktop Code session records from the MSIX profile, strictly read-only.
///
/// The store is laid out as <c>claude-code-sessions\{account}\{organization}\local_*.json</c>.
/// Only <c>local_*.json</c> are conversations — sibling files such as scheduled-tasks.json are
/// not, which is why the count here is lower than the file count.
///
/// <c>isArchived</c> is read as an observation of Claude's own state and is never written.
/// </summary>
public sealed class DesktopCodeMetadataReader
{
    private readonly string _storeRoot;
    private readonly SidebarGroups _groups;

    public DesktopCodeMetadataReader(string profileRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileRoot);
        _storeRoot = Path.Combine(profileRoot, "claude-code-sessions");
        // Read-only label source. Empty when Claude changed its preference shape.
        _groups = SidebarGroupReader.Read(profileRoot);
    }

    /// <summary>How many sessions carry a sidebar group. 0 means the label source gave nothing.</summary>
    public int GroupedSessionCount => _groups.GroupIdBySessionId.Count;

    public bool RootExists => Directory.Exists(_storeRoot);

    /// <summary>All files in the store, including non-conversation ones. For the coverage report.</summary>
    public int CountStoreFiles() =>
        RootExists ? Directory.EnumerateFiles(_storeRoot, "*.json", SearchOption.AllDirectories).Count() : 0;

    /// <summary>
    /// Records the last <see cref="Read"/> could not read this time — caught mid-rewrite, held,
    /// cut short. Not the files that are no conversation, nor ones deleted since they were listed.
    /// </summary>
    public int Unreadable { get; private set; }

    /// <summary>A record caught mid-rewrite is whole again a moment later.</summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(20);

    public IReadOnlyList<SessionMetadata> Read(CancellationToken cancellation = default)
    {
        var rows = new List<SessionMetadata>();
        Unreadable = 0;
        if (!RootExists) return rows;

        var unreadable = 0;
        foreach (var accountDir in Directory.EnumerateDirectories(_storeRoot))
        {
            var account = Path.GetFileName(accountDir);
            foreach (var orgDir in Directory.EnumerateDirectories(accountDir))
            {
                cancellation.ThrowIfCancellationRequested();
                var organization = Path.GetFileName(orgDir);

                foreach (var file in Directory.EnumerateFiles(orgDir, "local_*.json", SearchOption.TopDirectoryOnly))
                {
                    cancellation.ThrowIfCancellationRequested();
                    var row = TryRead(file, account, organization, _groups, ref unreadable);
                    if (row is not null) rows.Add(row);
                }
            }
        }
        Unreadable = unreadable;
        return rows;
    }

    /// <summary>One record, asked twice before it counts as unreadable; null when it is none.</summary>
    private static SessionMetadata? TryRead(
        string file, string account, string organization, SidebarGroups groups, ref int unreadable)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return Parse(file, account, organization, groups);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return null;        // deleted since it was listed: gone, not unreadable
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // Isolated rather than failing the whole sync — and counted, so the read is not
                // taken as complete and the session is never marked gone for it.
                if (attempt < 2)
                {
                    Thread.Sleep(RetryDelay);
                    continue;
                }
                unreadable++;
                return null;
            }
        }
    }

    private static SessionMetadata? Parse(
        string file, string account, string organization, SidebarGroups groups)
    {
        var info = new FileInfo(file);
        using var stream = OtherAppFile.Open(file);
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;

        var sessionId = Text(root, "sessionId");
        if (string.IsNullOrWhiteSpace(sessionId)) return null;

        var title = Text(root, "title") ?? sessionId;
        var archived = root.TryGetProperty("isArchived", out var a)
            && a.ValueKind is JsonValueKind.True;

        DateTimeOffset? activity = null;
        if (root.TryGetProperty("lastActivityAt", out var ts) && ts.ValueKind == JsonValueKind.Number
            && ts.TryGetInt64(out var ms))
            activity = DateTimeOffset.FromUnixTimeMilliseconds(ms);

        return new SessionMetadata
        {
            Key = new ConversationKey(ConversationKey.DesktopCode, account, organization, sessionId),
            CliSessionId = Text(root, "cliSessionId"),
            PriorCliSessionIds = Texts(root, "priorCliSessionIds"),
            Title = title,
            SearchKey = SearchKeys.Normalize(title),
            ProjectRef = Text(root, "cwd"),
            NativeOpenRef = null,
            LastActivityUtc = activity,
            SourceRevision = $"{info.Length}:{info.LastWriteTimeUtc.Ticks}",
            // Claude's own sidebar group; null = ungrouped or unknowable.
            SidebarGroup = groups.NameFor(sessionId),
            // Observed, not intended. The launcher never writes this back.
            NativeArchiveState = archived ? NativeArchiveState.Archived : NativeArchiveState.Active,
            SourceCoverage = SourceCoverage.Resolved,
        };
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    /// <summary>The strings of an array; none when the field is missing or of another shape.</summary>
    private static IReadOnlyList<string> Texts(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Array) return [];
        var values = new List<string>();
        foreach (var item in el.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } value) values.Add(value);
        return values.Count == 0 ? [] : values;
    }
}
