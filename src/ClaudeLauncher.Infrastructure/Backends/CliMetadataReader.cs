using System.Text;
using System.Text.Json;
using ClaudeLauncher.Core.Contracts;
using ClaudeLauncher.Infrastructure.Catalog;

namespace ClaudeLauncher.Infrastructure.Backends;

/// <summary>
/// Reads CLI Code transcript metadata from <c>{CLAUDE_CONFIG_DIR}\projects</c>.
///
/// Only direct children of a project directory are root conversations; nested JSONL are
/// sub-agent transcripts and are counted separately, never listed as conversations.
///
/// Parsing is strictly budgeted. A single transcript here can be hundreds of megabytes on
/// one line, so the reader streams bytes, stops at the budget, and still emits the row with
/// a placeholder title rather than dropping it.
/// </summary>
public sealed class CliMetadataReader
{
    /// <summary>Bytes read per transcript before giving up on finding a title.</summary>
    public const int DefaultByteBudget = 256 * 1024;

    private readonly string _projectsRoot;
    private readonly int _byteBudget;
    private readonly CliProbeCache? _cache;

    /// <param name="cache">
    /// What earlier reads found, to be reused for transcripts that have not changed since.
    /// Without one every transcript is opened on every read.
    /// </param>
    public CliMetadataReader(string claudeConfigDir, int byteBudget = DefaultByteBudget, CliProbeCache? cache = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claudeConfigDir);
        _projectsRoot = Path.Combine(claudeConfigDir, "projects");
        _byteBudget = byteBudget > 0 ? byteBudget : DefaultByteBudget;
        _cache = cache;
    }

    public bool RootExists => Directory.Exists(_projectsRoot);

    /// <summary>Nested (sub-agent) transcripts. Reported for the coverage report only.</summary>
    public int CountNonRootTranscripts()
    {
        if (!RootExists) return 0;
        var count = 0;
        foreach (var project in Directory.EnumerateDirectories(_projectsRoot))
        {
            foreach (var nested in Directory.EnumerateDirectories(project))
                count += Directory.EnumerateFiles(nested, "*.jsonl", SearchOption.AllDirectories).Count();
        }
        return count;
    }

    /// <param name="newest">
    /// Only the transcripts written last, this many: a first look at a long history, shown while
    /// the rest is read. What the cache knows of the others is kept.
    /// </param>
    public IEnumerable<SessionMetadata> Read(
        string accountContext, string organizationContext, CancellationToken cancellation = default, int? newest = null)
    {
        if (!RootExists) yield break;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var copies = NewestCopies(cancellation);
        if (newest is { } count) copies = [.. copies.OrderByDescending(c => c.Info.LastWriteTimeUtc).Take(count)];
        foreach (var (file, info, projectName) in copies)
        {
            cancellation.ThrowIfCancellationRequested();

            seen.Add(file);
            var stem = Path.GetFileNameWithoutExtension(file);
            var probe = CachedOrProbe(file, info, cancellation);

            yield return new SessionMetadata
            {
                Key = new ConversationKey(
                    ConversationKey.CliCode, accountContext, organizationContext, stem),
                CliSessionId = stem,
                Title = probe.Title ?? stem,
                SearchKey = SearchKeys.Normalize(probe.Title ?? stem),
                ProjectRef = probe.Cwd ?? projectName,
                NativeOpenRef = null,              // resume target is built at launch time
                LastActivityUtc = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                SourceRevision = $"{info.Length}:{info.LastWriteTimeUtc.Ticks}",
                TranscriptBytes = info.Length,
                TitleIsPlaceholder = probe.Title is null,
                // Begun as a copy of another session's history: Claude Code's resume does that.
                ResumedFrom = probe.HeadSession is { } head && head != stem ? head : null,
                ResumedSilently = probe.SaidAnything == false,
                // CLI transcripts have no native archive concept at all.
                NativeArchiveState = NativeArchiveState.Unsupported,
                SourceCoverage = probe.Truncated ? SourceCoverage.Partial : SourceCoverage.Resolved,
            };
        }
        if (newest is null) _cache?.Retain(seen);
    }

    /// <summary>
    /// The probe of an unchanged transcript is the one found last time; only a transcript
    /// whose length or write time moved — a conversation in progress — is opened again.
    /// </summary>
    private Probe CachedOrProbe(string file, FileInfo info, CancellationToken cancellation)
    {
        var length = info.Length;
        var written = info.LastWriteTimeUtc;
        if (_cache is not null && _cache.TryGet(file, length, written, out var known)) return known;

        var probe = ProbeWithinBudget(file, cancellation);
        // A copy of another session's history: was anything said in it after the copy?
        var stem = Path.GetFileNameWithoutExtension(file);
        if (probe.HeadSession is { } head && head != stem && !probe.Failed)
            probe = probe with { SaidAnything = SaidAnythingOfItsOwn(file, stem) };
        // A file that could not be opened (held mid-write) is asked again next time.
        if (!probe.Failed) _cache?.Set(file, length, written, probe);
        return probe;
    }

    /// <summary>
    /// One transcript per session id. Moving a project to another folder or drive leaves
    /// Claude Code's copy under the old folder's name as well; the copy written last is the
    /// conversation as it stands, and the one <c>claude --resume</c> continues from the new
    /// folder. <see cref="TranscriptLocator"/> picks by the same rule.
    /// </summary>
    private List<(string File, FileInfo Info, string ProjectName)> NewestCopies(CancellationToken cancellation)
    {
        var newest = new Dictionary<string, (string File, FileInfo Info, string ProjectName)>(StringComparer.OrdinalIgnoreCase);
        foreach (var projectDir in Directory.EnumerateDirectories(_projectsRoot))
        {
            cancellation.ThrowIfCancellationRequested();
            var projectName = Path.GetFileName(projectDir);

            foreach (var file in Directory.EnumerateFiles(projectDir, "*.jsonl", SearchOption.TopDirectoryOnly))
            {
                FileInfo info;
                try { info = new FileInfo(file); }
                catch { continue; }   // isolate one unreadable file; never abort the sync

                var stem = Path.GetFileNameWithoutExtension(file);
                if (!newest.TryGetValue(stem, out var seen) || info.LastWriteTimeUtc > seen.Info.LastWriteTimeUtc)
                    newest[stem] = (file, info, projectName);
            }
        }
        return newest.Values.ToList();
    }

    /// <param name="Title">Best available title, by the precedence in <see cref="Inspect"/>.</param>
    /// <param name="TitleSource">Which record supplied it, for diagnostics.</param>
    /// <param name="Failed">The file could not be read at all this time.</param>
    /// <param name="HeadSession">The session id the transcript's first records carry — another
    /// session's when Claude Code copied that session's history into it on resume.</param>
    /// <param name="SaidAnything">For such a copy: whether any message was written in it
    /// afterwards; null when that could not be told.</param>
    internal readonly record struct Probe(string? Title, string? TitleSource, string? Cwd, bool Truncated, bool Failed = false,
        string? HeadSession = null, bool? SaidAnything = null);

    /// <summary>
    /// Streams at most the byte budget looking for the working directory and the first real
    /// user message. Lines longer than the remaining budget are skipped without materialising
    /// them, which is what keeps a 400 MB single-line transcript from being loaded.
    /// </summary>
    private Probe ProbeWithinBudget(string path, CancellationToken cancellation)
    {
        // Title precedence: the user's own title, then the one Claude generated, then the
        // first real user message. The first two are explicit records written at the very
        // start of a transcript, so they are both cheaper and more accurate than inferring
        // a title from message text — which fails outright on /compact continuations, whose
        // opening messages are all boilerplate.
        string? custom = null, ai = null, firstMessage = null, cwd = null, head = null;
        var truncated = false;
        var failed = false;

        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, FileOptions.SequentialScan);

            var remaining = _byteBudget;
            var buffer = new byte[64 * 1024];
            var line = new MemoryStream(8 * 1024);
            var lineOverBudget = false;

            while (remaining > 0)
            {
                cancellation.ThrowIfCancellationRequested();
                var read = stream.Read(buffer, 0, Math.Min(buffer.Length, remaining));
                if (read == 0) break;
                remaining -= read;

                for (var i = 0; i < read; i++)
                {
                    if (buffer[i] != (byte)'\n')
                    {
                        // Cap a single line; beyond this we skip to the next newline instead
                        // of growing an unbounded buffer.
                        if (line.Length < 512 * 1024) line.WriteByte(buffer[i]);
                        else lineOverBudget = true;
                        continue;
                    }

                    if (!lineOverBudget && line.Length > 0)
                        Inspect(line, ref custom, ref ai, ref firstMessage, ref cwd, ref head);
                    else if (lineOverBudget)
                        truncated = true;

                    line.SetLength(0);
                    lineOverBudget = false;

                    // A user-set title is the best answer available; stop as soon as we
                    // have it and the working directory.
                    if (custom is not null && cwd is not null && head is not null)
                        return new Probe(custom, "custom-title", cwd, truncated, HeadSession: head);
                }
            }

            if (remaining <= 0 && custom is null && ai is null && firstMessage is null)
                truncated = true;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // Unreadable or locked file: keep the row, mark it partial.
            truncated = true;
            failed = true;
        }

        if (custom is not null) return new Probe(custom, "custom-title", cwd, truncated, failed, head);
        if (ai is not null) return new Probe(ai, "ai-title", cwd, truncated, failed, head);
        return new Probe(firstMessage, firstMessage is null ? null : "first-message", cwd, truncated, failed, head);
    }

    private static readonly string[] Boilerplate =
    [
        "this session is being continued", "caveat:", "<system-reminder",
        "<command-", "<local-command", "<task-notification",
    ];

    /// <summary>Built-in commands whose arguments configure the session rather than ask for anything.</summary>
    private static readonly HashSet<string> SettingsCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "/compact", "/model", "/effort", "/config", "/output-style", "/add-dir", "/permissions",
        "/resume", "/rename", "/export", "/fast", "/theme", "/auto-mode-setup",
    };

    private static bool IsTrue(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    private static void Inspect(
        MemoryStream line, ref string? custom, ref string? ai, ref string? firstMessage, ref string? cwd, ref string? head)
    {
        try
        {
            using var doc = JsonDocument.Parse(line.GetBuffer().AsMemory(0, (int)line.Length));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;

            if (head is null && root.TryGetProperty("sessionId", out var sessionEl)
                && sessionEl.ValueKind == JsonValueKind.String)
                head = JsonText.Of(sessionEl);

            if (cwd is null && root.TryGetProperty("cwd", out var cwdEl)
                && cwdEl.ValueKind == JsonValueKind.String)
                cwd = JsonText.Of(cwdEl);

            if (!root.TryGetProperty("type", out var type)) return;
            var kind = JsonText.Of(type);

            // Claude Code writes both of these near the top of a transcript.
            if (kind == "custom-title")
            {
                custom ??= Clean(root, "customTitle");
                return;
            }
            if (kind == "ai-title")
            {
                ai ??= Clean(root, "aiTitle");
                return;
            }

            if (firstMessage is not null) return;
            if (kind != "user") return;
            // Injected by Claude Code (a skill, a command's expansion, a compaction summary):
            // not the user's words, so never the conversation's title.
            if (IsTrue(root, "isMeta") || IsTrue(root, "isCompactSummary")) return;
            if (!root.TryGetProperty("message", out var msg)) return;
            if (!msg.TryGetProperty("content", out var content)) return;

            var text = ExtractText(content);
            if (string.IsNullOrWhiteSpace(text)) return;
            // A reminder the desktop app put in front of what the user typed is not the title;
            // what the user typed after it is.
            text = ClaudeCodeMarkup.UsersWords(text);

            // "/loop 规划一个平台": the request is the arguments. A settings command
            // ("/model x", "/compact 保留…") or one typed bare says nothing about the topic.
            if (ClaudeCodeMarkup.TryReadCommand(text, out var command))
            {
                if (SettingsCommands.Contains(command.Name) || command.Arguments.StartsWith('-')) return;
                text = command.Arguments;
                if (text.Length == 0) return;
            }

            var flat = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (!CarriesEnoughMeaning(flat)) return;
            foreach (var b in Boilerplate)
                if (flat.StartsWith(b, StringComparison.OrdinalIgnoreCase)) return;

            // The file an @-mention names, a link's words: what a list can show of it.
            var tidy = TitleText.Tidy(flat);
            if (tidy.Length > 0) firstMessage = tidy;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // One malformed line is isolated; the rest of the transcript still parses.
        }
    }

    /// <summary>
    /// Whether a transcript that began as a copy of another session's history holds anything
    /// said in it. What is written after a resume follows the copied history, so the last
    /// message near the end tells: under its own session id, something was said; under the
    /// copied one, nothing. Null when no message turns up near the end at all.
    /// </summary>
    internal static bool? SaidAnythingOfItsOwn(string path, string ownId)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = stream.Length;
            foreach (var window in new long[] { 256 * 1024, 4 * 1024 * 1024 })
            {
                var take = (int)Math.Min(length, window);
                var bytes = new byte[take];
                stream.Position = length - take;
                stream.ReadExactly(bytes);
                var end = take;
                while (end > 0)
                {
                    var newline = Array.LastIndexOf(bytes, (byte)'\n', end - 1);
                    // The window's first line is cut short unless the window starts the file.
                    if (newline < 0 && take < length) break;
                    var start = newline + 1;
                    var (type, session) = TypeAndSession(bytes.AsSpan(start, end - start));
                    if (type is "user" or "assistant" && session is not null) return session == ownId;
                    end = Math.Max(newline, 0);
                }
                if (take == length) break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* told nothing */ }
        return null;
    }

    /// <summary>A record's type and session id, read without taking the rest of it apart.</summary>
    private static (string? Type, string? Session) TypeAndSession(ReadOnlySpan<byte> line)
    {
        string? type = null, session = null;
        try
        {
            var reader = new Utf8JsonReader(line, new JsonReaderOptions { MaxDepth = 512 });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return default;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var isType = reader.ValueTextEquals("type"u8);
                var isSession = reader.ValueTextEquals("sessionId"u8);
                if (!reader.Read()) break;
                if (isType && reader.TokenType == JsonTokenType.String) type = reader.GetString();
                else if (isSession && reader.TokenType == JsonTokenType.String) session = reader.GetString();
                else reader.Skip();
                if (type is not null && session is not null) break;
            }
        }
        catch (JsonException) { /* a line cut short: nothing more to tell */ }
        return (type, session);
    }

    /// <summary>
    /// Whether a candidate title says anything. Counting characters would be an ASCII-centric
    /// rule: 「病级提取」 is four characters but a complete, meaningful title, while four Latin
    /// letters usually are not. CJK characters therefore count double.
    /// </summary>
    private static bool CarriesEnoughMeaning(string text)
    {
        const int RequiredWeight = 6;
        var weight = 0;
        foreach (var ch in text)
        {
            weight += IsCjk(ch) ? 2 : 1;
            if (weight >= RequiredWeight) return true;
        }
        return false;
    }

    private static bool IsCjk(char ch) =>
        ch is >= '一' and <= '鿿'      // CJK Unified Ideographs
           or >= '㐀' and <= '䶿'      // Extension A
           or >= '぀' and <= 'ヿ'      // Kana
           or >= '가' and <= '힯';     // Hangul

    private static string? Clean(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var el) || el.ValueKind != JsonValueKind.String)
            return null;
        var value = JsonText.Of(el);
        if (string.IsNullOrWhiteSpace(value)) return null;
        var flat = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length > 120 ? flat[..120] : flat;
    }

    private static string? ExtractText(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String) return JsonText.Of(content);
        if (content.ValueKind != JsonValueKind.Array) return null;

        var sb = new StringBuilder();
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object) continue;
            if (!block.TryGetProperty("type", out var t) || JsonText.Of(t) != "text") continue;
            if (block.TryGetProperty("text", out var txt) && txt.ValueKind == JsonValueKind.String)
                sb.Append(JsonText.Of(txt)).Append(' ');
        }
        return sb.Length == 0 ? null : sb.ToString();
    }
}

/// <summary>
/// What earlier reads found in each transcript, valid while the file's length and write
/// time are unchanged. Kept by whoever syncs repeatedly, so that a sync opens only the
/// transcripts that changed — usually the one or two conversations in progress — instead
/// of the head of every conversation on disk.
/// </summary>
public sealed class CliProbeCache
{
    /// <summary>
    /// Bumped whenever what a probe finds changes (a title rule, a new field): a saved cache from
    /// before is then read as empty, and every transcript is probed once more.
    /// </summary>
    public const int ProbeRules = 1;

    private readonly Dictionary<string, (long Length, DateTime WrittenUtc, CliMetadataReader.Probe Probe)> _entries =
        new(StringComparer.OrdinalIgnoreCase);

    public int Count => _entries.Count;

    /// <summary>Something was learned or forgotten since the cache was loaded or last saved.</summary>
    public bool Changed { get; private set; }

    private sealed record Saved(int Rules, List<SavedProbe> Probes);

    private sealed record SavedProbe(
        string Path, long Length, long Written, string? Title, string? TitleSource, string? Cwd, bool Truncated,
        string? HeadSession, bool? SaidAnything);

    private static readonly System.Text.Json.JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>
    /// What a run before this one found, kept beside the catalog: a launch reopens only the
    /// transcripts that changed since, not the head of every conversation on disk. Missing,
    /// damaged, or written under other probe rules, it reads as empty.
    /// </summary>
    public static CliProbeCache Load(string file)
    {
        var cache = new CliProbeCache();
        try
        {
            if (!File.Exists(file)) return cache;
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            var saved = System.Text.Json.JsonSerializer.Deserialize<Saved>(stream, Json);
            if (saved is null || saved.Rules != ProbeRules) return cache;
            foreach (var p in saved.Probes)
                cache._entries[p.Path] = (p.Length, new DateTime(p.Written, DateTimeKind.Utc),
                    new CliMetadataReader.Probe(p.Title, p.TitleSource, p.Cwd, p.Truncated, false, p.HeadSession, p.SaidAnything));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            cache._entries.Clear();
        }
        return cache;
    }

    /// <summary>Writes the cache for the next run, whole and then moved into place; false when it could not be.</summary>
    public bool Save(string file)
    {
        try
        {
            var saved = new Saved(ProbeRules, [.. _entries.Select(e => new SavedProbe(e.Key, e.Value.Length, e.Value.WrittenUtc.Ticks,
                e.Value.Probe.Title, e.Value.Probe.TitleSource, e.Value.Probe.Cwd, e.Value.Probe.Truncated,
                e.Value.Probe.HeadSession, e.Value.Probe.SaidAnything))]);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var temp = file + ".tmp";
            File.WriteAllBytes(temp, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(saved, Json));
            File.Move(temp, file, overwrite: true);
            Changed = false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal bool TryGet(string path, long length, DateTime writtenUtc, out CliMetadataReader.Probe probe)
    {
        if (_entries.TryGetValue(path, out var entry) && entry.Length == length && entry.WrittenUtc == writtenUtc)
        {
            probe = entry.Probe;
            return true;
        }
        probe = default;
        return false;
    }

    internal void Set(string path, long length, DateTime writtenUtc, CliMetadataReader.Probe probe)
    {
        _entries[path] = (length, writtenUtc, probe);
        Changed = true;
    }

    /// <summary>Forgets transcripts that are no longer there.</summary>
    internal void Retain(IReadOnlySet<string> paths)
    {
        foreach (var gone in _entries.Keys.Where(k => !paths.Contains(k)).ToList())
        {
            _entries.Remove(gone);
            Changed = true;
        }
    }
}
