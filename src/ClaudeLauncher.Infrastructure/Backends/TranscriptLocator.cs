using ClaudeLauncher.Core.Contracts;

namespace ClaudeLauncher.Infrastructure.Backends;

/// <summary>
/// Finds the transcript file behind a catalog row.
///
/// A CLI row is named after its transcript. A Desktop row is not — it carries a
/// <c>cliSessionId</c> pointing at one, which is why a Desktop session can be read at all.
/// A Codex row records the rollout path Codex's own catalog gave it; failing that, the
/// rollout is looked up by thread id. A row without any of these has no transcript to show,
/// and that is reported rather than guessed at.
/// </summary>
public static class TranscriptLocator
{
    /// <summary>
    /// This tool's own copies of the transcripts, when it keeps them: read in place of a
    /// transcript the app has deleted, and of one only there compressed.
    /// </summary>
    public static TranscriptArchive? Archive { get; set; }

    public static string? Find(SessionMetadata row, string claudeConfigDir, CodexPaths? codex = null)
    {
        ArgumentNullException.ThrowIfNull(row);
        return Readable(row, FindOriginal(row, claudeConfigDir, codex));
    }

    /// <summary>
    /// What to read for a row: its transcript as found — this tool's copy of it when the app keeps
    /// it compressed — or, when the app no longer has it, the copy kept of it.
    /// </summary>
    private static string? Readable(SessionMetadata row, string? found, CancellationToken cancellation = default)
    {
        if (Archive is not { } archive || TranscriptArchive.IdentityOf(row) is not { } id)
            return TranscriptFiles.IsCompressed(found) ? null : found;
        if (found is not null && !TranscriptFiles.IsCompressed(found)) return found;
        if (found is not null) return archive.Readable(found, id.Family, id.Conversation, cancellation);
        return archive.CopyOf(id.Family, id.Conversation);
    }

    private static string? FindOriginal(SessionMetadata row, string claudeConfigDir, CodexPaths? codex)
    {
        if (row.Key.Backend == ConversationKey.Codex)
            return FindCodex(row, codex ?? CodexPaths.Discover());

        if (row.Key.Backend == ConversationKey.Gemini) return FindGemini(row);
        if (row.Key.Backend == ConversationKey.DeepSeek) return FindDsh(row);
        // Any other source but Claude's: the file its sync recorded.
        if (!ConversationKey.IsClaude(row.Key.Backend)) return FindRecorded(row);

        if (string.IsNullOrWhiteSpace(claudeConfigDir)) return null;

        var stem = StemOf(row);
        if (stem is null) return null;

        var projects = Path.Combine(claudeConfigDir, "projects");
        if (!Directory.Exists(projects)) return null;

        // Top-level only: a nested match would be a sub-agent transcript, not the conversation.
        // A project that moved folders leaves a copy under the old name; the newest copy is
        // the conversation as it stands — the same one the catalog row was read from.
        string? newest = null;
        var newestWritten = DateTime.MinValue;
        foreach (var project in Directory.EnumerateDirectories(projects))
        {
            var candidate = Path.Combine(project, stem + ".jsonl");
            if (!File.Exists(candidate)) continue;
            var written = File.GetLastWriteTimeUtc(candidate);
            if (newest is null || written > newestWritten) (newest, newestWritten) = (candidate, written);
        }
        return newest;
    }

    /// <summary>The file name a Claude row's transcript has, without its extension; null when there is none.</summary>
    private static string? StemOf(SessionMetadata row)
    {
        var stem = row.Key.Backend switch
        {
            ConversationKey.CliCode => row.CliSessionId ?? row.Key.NativeSessionId,
            ConversationKey.DesktopCode => row.CliSessionId,
            _ => null,
        };
        return string.IsNullOrWhiteSpace(stem) ? null : stem;
    }

    /// <summary>
    /// The transcript behind each of many rows, listing each folder once instead of once per
    /// row — what the search index asks for after every sync. The answers are those of
    /// <see cref="Find"/>; rows without a transcript are left out.
    /// </summary>
    public static List<(SessionMetadata Row, string Path)> FindAll(
        IReadOnlyList<SessionMetadata> rows, string claudeConfigDir, CodexPaths codex,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var found = new List<(SessionMetadata, string)>(rows.Count);
        Dictionary<string, (string Path, DateTime Written)>? claude = null;
        Dictionary<string, string>? rollouts = null;
        foreach (var row in rows)
        {
            cancellation.ThrowIfCancellationRequested();
            string? path = null;
            if (row.Key.Backend == ConversationKey.Codex)
            {
                if (row.NativeOpenRef is { Length: > 0 } recorded && File.Exists(recorded)) path = recorded;
                else if (!string.IsNullOrWhiteSpace(row.Key.NativeSessionId))
                    (rollouts ??= Rollouts(codex)).TryGetValue(row.Key.NativeSessionId, out path);
            }
            else if (row.Key.Backend == ConversationKey.Gemini)
                path = FindGemini(row);
            else if (row.Key.Backend == ConversationKey.DeepSeek)
                path = FindDsh(row);
            else if (!ConversationKey.IsClaude(row.Key.Backend))
                path = FindRecorded(row);
            else if (StemOf(row) is { } stem && !string.IsNullOrWhiteSpace(claudeConfigDir))
            {
                if ((claude ??= ClaudeTranscripts(claudeConfigDir)).TryGetValue(stem, out var hit)) path = hit.Path;
            }
            path = Readable(row, path, cancellation);
            if (path is not null) found.Add((row, path));
        }
        return found;
    }

    /// <summary>Every top-level transcript under projects\, by file name; the newest copy wins, as in <see cref="Find"/>.</summary>
    private static Dictionary<string, (string Path, DateTime Written)> ClaudeTranscripts(string claudeConfigDir)
    {
        var map = new Dictionary<string, (string Path, DateTime Written)>(StringComparer.OrdinalIgnoreCase);
        var projects = Path.Combine(claudeConfigDir, "projects");
        if (!Directory.Exists(projects)) return map;
        try
        {
            foreach (var project in new DirectoryInfo(projects).EnumerateDirectories())
            {
                List<FileInfo> files;
                try { files = project.EnumerateFiles("*.jsonl", SearchOption.TopDirectoryOnly).ToList(); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                foreach (var file in files)
                {
                    var stem = Path.GetFileNameWithoutExtension(file.Name);
                    if (!map.TryGetValue(stem, out var known) || file.LastWriteTimeUtc > known.Written)
                        map[stem] = (file.FullName, file.LastWriteTimeUtc);
                }
            }
        }
        catch (IOException) { /* what was listed before the failure still counts */ }
        catch (UnauthorizedAccessException) { }
        return map;
    }

    /// <summary>Every Codex rollout by the thread id at the end of its name (rollout-&lt;time&gt;-&lt;id&gt;.jsonl).</summary>
    private static Dictionary<string, string> Rollouts(CodexPaths codex)
    {
        const int IdLength = 36;
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in new[] { codex.SessionsRoot, codex.ArchivedSessionsRoot })
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (var file in Directory.EnumerateFiles(root, "rollout-*.jsonl", SearchOption.AllDirectories))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (name.Length > "rollout-".Length + IdLength) map.TryAdd(name[^IdLength..], file);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return map;
    }

    private static string? FindCodex(SessionMetadata row, CodexPaths codex)
    {
        // The catalog's own answer first; it is what Codex itself uses to open the thread.
        if (row.NativeOpenRef is { Length: > 0 } recorded && File.Exists(recorded)) return recorded;
        // Compressed where it was by a maintenance tool: rollout-….jsonl.zst.
        if (row.NativeOpenRef is { Length: > 0 } plain && File.Exists(plain + TranscriptFiles.ZstdExtension))
            return plain + TranscriptFiles.ZstdExtension;

        var id = row.Key.NativeSessionId;
        if (string.IsNullOrWhiteSpace(id)) return null;
        foreach (var root in new[] { codex.SessionsRoot, codex.ArchivedSessionsRoot })
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                var hit = Directory.EnumerateFiles(root, $"rollout-*-{id}.jsonl", SearchOption.AllDirectories)
                                   .FirstOrDefault();
                if (hit is not null) return hit;
            }
            catch (IOException) { /* an unreadable folder is not evidence of anything */ }
            catch (UnauthorizedAccessException) { }
        }
        return null;
    }

    /// <summary>
    /// A Gemini session's file: the one the sync recorded, else the file in a project's chats
    /// folder named after the session's first eight characters (session-&lt;time&gt;-&lt;id8&gt;.json)
    /// whose own id is the row's.
    /// </summary>
    private static string? FindGemini(SessionMetadata row)
    {
        if (row.NativeOpenRef is { Length: > 0 } recorded && File.Exists(recorded)) return recorded;
        var id = row.Key.NativeSessionId;
        if (id.Length < 8) return null;
        foreach (var (file, _) in GeminiPaths.Discover().SessionFiles())
        {
            if (!Path.GetFileNameWithoutExtension(file).EndsWith("-" + id[..8], StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                if (GeminiSession.ReadHeader(file) is { } header
                    && string.Equals(header.SessionId, id, StringComparison.OrdinalIgnoreCase)) return file;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        }
        return null;
    }

    /// <summary>
    /// A DeepSeek Harness session's log: the one the sync recorded, else the newest log in the
    /// folder named after the session (its id, <c>session-&lt;uuid&gt;</c>) under any workspace of
    /// its home — dsh upgrades a session to a new log generation beside the old one.
    /// </summary>
    private static string? FindDsh(SessionMetadata row)
    {
        if (row.NativeOpenRef is { Length: > 0 } recorded && File.Exists(recorded)) return recorded;
        var id = row.Key.NativeSessionId;
        var sessions = Path.Combine(row.Key.AccountContextId, "sessions");
        if (string.IsNullOrWhiteSpace(id) || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !Directory.Exists(sessions))
            return null;
        try
        {
            foreach (var workspace in Directory.EnumerateDirectories(sessions))
            {
                var folder = Path.Combine(workspace, id);
                if (Directory.Exists(folder) && DshPaths.LogOf(folder) is { } log) return log;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }

    /// <summary>
    /// The file a source's sync recorded for the row — an imported conversation's, in this tool's
    /// own store, or that of a source that names its file in its rows and has no other way to look.
    /// </summary>
    private static string? FindRecorded(SessionMetadata row) =>
        row.NativeOpenRef is { Length: > 0 } recorded && File.Exists(recorded) ? recorded : null;

    /// <summary>Why a row cannot be displayed, for the UI to show verbatim.</summary>
    public static string ExplainMissing(SessionMetadata row) =>
        row.Key.Backend == ConversationKey.DesktopCode && string.IsNullOrWhiteSpace(row.CliSessionId)
            ? "这条桌面会话没有关联的 CLI transcript，磁盘上没有可显示的正文。"
            : Sources.Of(row.Key.Backend)?.MissingText ?? "找不到对应的 transcript 文件——可能已被清理，或它属于另一个配置目录。";
}
