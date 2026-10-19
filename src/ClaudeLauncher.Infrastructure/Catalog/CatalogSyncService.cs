using ClaudeLauncher.Core.Catalog;
using ClaudeLauncher.Core.Contracts;
using ClaudeLauncher.Infrastructure.Backends;
using ClaudeLauncher.Infrastructure.Discovery;

namespace ClaudeLauncher.Infrastructure.Catalog;

/// <param name="RowsWritten">Rows read from the sources and offered to the catalog.</param>
/// <param name="RowsChanged">Of those, how many were new or different from what the catalog held.</param>
/// <param name="RowsRemoved">Rows a fully read source no longer has: kept in the list, marked as gone from the app.</param>
public sealed record SyncResult(
    CoverageReport Coverage, int RowsWritten, string? Warning = null, int RowsChanged = 0, int RowsRemoved = 0)
{
    /// <summary>Whether the list could look any different after this sync.</summary>
    public bool ChangedAnything => RowsChanged + RowsRemoved > 0;
}

/// <summary>
/// Pulls metadata from each backend into the catalog and produces the coverage report.
///
/// Each source is read and tallied independently: a source that fails keeps its cached rows
/// and is reported Unreachable, and counts from different backends are never summed into a
/// single "total conversations" number.
/// </summary>
public sealed class CatalogSyncService
{
    /// <summary>The account and organization every terminal transcript is keyed under: one Claude Code home, not an account's folder.</summary>
    public const string CliAccount = "local", CliOrganization = "default";

    /// <summary>The key of a terminal transcript, by its session id.</summary>
    public static ConversationKey CliKey(string cliSessionId) =>
        new(ConversationKey.CliCode, CliAccount, CliOrganization, cliSessionId);

    private readonly SqliteCatalog _catalog;
    private readonly string _claudeConfigDir;

    /// <summary>Kept across syncs: an unchanged transcript is not opened again.</summary>
    private readonly CliProbeCache _cliProbes;

    /// <summary>Where the probe cache is kept between runs; null keeps it for this run only.</summary>
    private readonly string? _probeCacheFile;

    /// <summary>
    /// The sub-agent count walks every nested folder and only feeds the coverage report, so
    /// a sync repeated within a few minutes reuses it instead of walking them all again.
    /// </summary>
    private (int Count, DateTime CountedUtc)? _nonRoot;
    private static readonly TimeSpan NonRootRecount = TimeSpan.FromMinutes(5);

    /// <summary>Gemini CLI's sessions, read only when given: a source of its own the caller opts into.</summary>
    private readonly GeminiPaths? _gemini;
    private readonly GeminiProbeCache _geminiProbes = new();

    /// <summary>The conversations imported from Gemini on the web, read only when given.</summary>
    private readonly GeminiTakeoutStore? _geminiWeb;

    /// <summary>The conversations imported from ChatGPT's and Claude's data exports, read only when given.</summary>
    private readonly IReadOnlyList<WebExportStore> _webExports;

    /// <summary>
    /// DeepSeek Harness's homes, asked for again at every sync — the user can add or remove one
    /// while the list is open. Read only when given.
    /// </summary>
    private readonly Func<DshPaths>? _dsh;
    private readonly DshProbeCache _dshProbes = new();

    /// <param name="probeCacheFile">
    /// Where what was read from each terminal transcript is kept between runs, so a launch does
    /// not reopen thousands of unchanged files; null keeps it for this run only.
    /// </param>
    public CatalogSyncService(SqliteCatalog catalog, string claudeConfigDir, GeminiPaths? gemini = null,
        GeminiTakeoutStore? geminiWeb = null, string? probeCacheFile = null, Func<DshPaths>? dsh = null,
        IReadOnlyList<WebExportStore>? webExports = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _claudeConfigDir = claudeConfigDir;
        _gemini = gemini;
        _geminiWeb = geminiWeb;
        _webExports = webExports ?? [];
        _dsh = dsh;
        _probeCacheFile = probeCacheFile;
        _cliProbes = probeCacheFile is null ? new CliProbeCache() : CliProbeCache.Load(probeCacheFile);
    }

    public SyncResult Sync(ClaudeInstallation installation, CodexPaths? codex = null, CancellationToken cancellation = default) =>
        Apply(Read(installation, codex, cancellation));

    /// <summary>What one read of a source found: its rows, how many of its files could not be read this time, the note under its line in the report.</summary>
    /// <param name="Accounts">For a source read from several places, the accounts read in full; null when it is one place.</param>
    private sealed record SourceScan(List<SessionMetadata> Rows, int Unreadable, string Note,
        IReadOnlyCollection<string>? Accounts = null);

    /// <summary>What one read of every source found, before any of it is written to the catalog.</summary>
    /// <param name="Complete">The backends read in full this time: only they can tell what is gone.</param>
    /// <param name="CompleteAccounts">For a backend read from several places, the accounts it was read in full for.</param>
    public sealed record SyncRead(
        List<SessionMetadata> Rows, List<SourceTally> Tallies, int Linked, List<string> Unresolved, int NonRoot,
        string? Warning, IReadOnlyList<string> Complete,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>>? CompleteAccounts = null);

    /// <summary>
    /// Reads every source. The catalog is not touched: this — nearly all of a sync's time —
    /// can run while the list goes on being searched and paged. One read at a time.
    /// </summary>
    /// <param name="newestTranscripts">
    /// Read only this many terminal transcripts, the ones written last: a first look at a long
    /// history on a first launch. Such a read is not complete, so it never marks anything gone.
    /// </param>
    public SyncRead Read(ClaudeInstallation installation, CodexPaths? codex = null, CancellationToken cancellation = default,
        int? newestTranscripts = null)
    {
        var tallies = new List<SourceTally>();
        var rows = new List<SessionMetadata>();
        var desktopCliIds = new List<string>();
        string? warning = null;

        // ---- Codex (ChatGPT desktop) ----------------------------------------
        // Read first: its store is the largest, and a failure here must not hide Claude's.
        var codexPaths = codex ?? CodexPaths.Discover();
        var codexReader = new CodexMetadataReader(codexPaths);
        var codexResolved = false;
        if (codexReader.DatabaseExists)
        {
            try
            {
                var scan = codexReader.Read(cancellation);
                rows.AddRange(scan.Rows);
                codexResolved = true;
                tallies.Add(new SourceTally(
                    ConversationKey.Codex, scan.Rows.Count, scan.RootArchived, SourceCoverage.Resolved,
                    $"state_5.sqlite 共 {scan.ThreadsInDatabase} 条线程：主任务 {scan.RootThreads} 条"
                    + $"（其中已归档 {scan.RootArchived}），子任务 {scan.Subagents} 条不计入；"
                    + $"rollout 文件 {scan.RolloutFiles} 个"
                    + (scan.RolloutsNotInDatabase > 0 ? $"，{scan.RolloutsNotInDatabase} 个不在数据库里（已按文件收录）" : "")
                    + (scan.RowsWithoutFile > 0 ? $"，{scan.RowsWithoutFile} 条找不到文件（只有元数据）" : "")
                    + $"；CODEX_HOME={codexPaths.Home}"));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                tallies.Add(new SourceTally(ConversationKey.Codex, 0, 0, SourceCoverage.Unreachable, ex.Message));
                warning = "Codex 来源读取失败，已保留缓存条目";
            }
        }
        else
        {
            tallies.Add(new SourceTally(ConversationKey.Codex, 0, 0, SourceCoverage.Unreachable,
                $"未找到 {codexPaths.StateDatabase}"));
        }

        // ---- sources read the same way ------------------------------------
        // Each is listed only where it is found: a computer without it hears nothing about it,
        // rather than a source "not found" in every report.
        var resolved = new Dictionary<string, bool>(StringComparer.Ordinal);
        var scopes = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal);
        void ReadOne(string backend, Func<SourceScan> scan)
        {
            try
            {
                var found = scan();
                rows.AddRange(found.Rows);
                // A file that could not be read this time may be a conversation still listed:
                // nothing is marked gone on the strength of an incomplete read.
                resolved[backend] = found.Unreadable == 0;
                if (found.Accounts is { } accounts) scopes[backend] = accounts;
                tallies.Add(new SourceTally(
                    backend, found.Rows.Count, found.Rows.Count(r => r.NativeArchiveState == NativeArchiveState.Archived),
                    found.Unreadable == 0 ? SourceCoverage.Resolved : SourceCoverage.Partial, found.Note));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                tallies.Add(new SourceTally(backend, 0, 0, SourceCoverage.Unreachable, ex.Message));
                warning ??= $"{Sources.Of(backend)?.ReportName ?? backend} 来源读取失败，已保留缓存条目";
            }
        }

        if (_gemini is { Exists: true } gemini)
            ReadOne(ConversationKey.Gemini, () =>
            {
                var scan = new GeminiMetadataReader(gemini, _geminiProbes).Read(cancellation);
                return new SourceScan(scan.Rows, scan.Unreadable,
                    $"{scan.Projects} 个项目里共 {scan.Files} 个会话文件"
                    + (scan.Unreadable > 0 ? $"，{scan.Unreadable} 个这次没能读取（正在写入或已损坏），下次同步再试" : "")
                    + $"；{gemini.ProjectsRoot}");
            });

        // Gemini on the web: this tool's own copy — nothing imported (or the import removed) means no rows of it.
        if (_geminiWeb is { } store)
        {
            if (!store.Exists) resolved[ConversationKey.GeminiWeb] = true;
            else
                ReadOne(ConversationKey.GeminiWeb, () =>
                {
                    var scan = GeminiMetadataReader.ForTakeout(store, _geminiProbes).Read(cancellation);
                    return new SourceScan(scan.Rows, scan.Unreadable,
                        $"从 Google Takeout 导入的 {scan.Files} 段对话"
                        + (scan.Unreadable > 0 ? $"，{scan.Unreadable} 段这次没能读取" : "")
                        + $"；{store.Root}");
                });
        }

        // ChatGPT and Claude on the web: this tool's own copies of their exports, likewise.
        foreach (var web in _webExports)
        {
            if (!web.Exists) resolved[web.Backend] = true;
            else
                ReadOne(web.Backend, () =>
                {
                    var scan = GeminiMetadataReader.ForImport(web, _geminiProbes).Read(cancellation);
                    return new SourceScan(scan.Rows, scan.Unreadable,
                        $"从数据导出导入的 {scan.Files} 段对话"
                        + (scan.Unreadable > 0 ? $"，{scan.Unreadable} 段这次没能读取" : "")
                        + $"；{web.Root}");
                });
        }

        // DeepSeek Harness: a read is whole for the homes read, and says nothing about one no longer read.
        if (_dsh?.Invoke() is { Exists: true } dsh)
            ReadOne(ConversationKey.DeepSeek, () =>
            {
                var scan = new DshMetadataReader(dsh, _dshProbes).Read(cancellation);
                return new SourceScan(scan.Rows, scan.Unreadable,
                    $"{scan.Homes} 个数据目录里共 {scan.Files} 个会话"
                    + (scan.Subagents > 0 ? $"，子代理会话 {scan.Subagents} 个不计入" : "")
                    + (scan.Unreadable > 0 ? $"，{scan.Unreadable} 个这次没能读取（正在写入或已损坏），下次同步再试" : "")
                    + "；" + string.Join("、", dsh.Homes), dsh.Homes);
            });

        // ---- Desktop Code -------------------------------------------------
        if (installation.ProfileRoot is { Length: > 0 } profile)
        {
            var reader = new DesktopCodeMetadataReader(profile);
            if (reader.RootExists)
            {
                try
                {
                    var desktop = reader.Read(cancellation);
                    rows.AddRange(desktop);
                    desktopCliIds.AddRange(desktop.Where(d => d.CliSessionId is { Length: > 0 })
                                                  .Select(d => d.CliSessionId!));
                    // A record that could not be read may be a session in use: the read is not
                    // complete, so nothing is marked gone on the strength of it.
                    tallies.Add(new SourceTally(
                        ConversationKey.DesktopCode, desktop.Count,
                        desktop.Count(d => d.NativeArchiveState == NativeArchiveState.Archived),
                        reader.Unreadable == 0 ? SourceCoverage.Resolved : SourceCoverage.Partial,
                        $"store 内共 {reader.CountStoreFiles()} 个 JSON 文件，其中会话记录 {desktop.Count} 条"
                        + (reader.Unreadable > 0 ? $"，{reader.Unreadable} 条这次没能读取（正在写入或已损坏），下次同步再试" : "")));
                }
                catch (Exception ex)
                {
                    tallies.Add(new SourceTally(ConversationKey.DesktopCode, 0, 0,
                        SourceCoverage.Unreachable, ex.Message));
                    warning = "Desktop Code 来源读取失败，已保留缓存条目";
                }
            }
            else
            {
                tallies.Add(new SourceTally(ConversationKey.DesktopCode, 0, 0,
                    SourceCoverage.Unreachable, "未找到 claude-code-sessions 目录"));
            }
        }
        else
        {
            tallies.Add(new SourceTally(ConversationKey.DesktopCode, 0, 0,
                SourceCoverage.Unreachable, "未解析到官方 profile 目录"));
        }

        // ---- CLI Code -----------------------------------------------------
        var cliReader = new CliMetadataReader(_claudeConfigDir, cache: _cliProbes);
        var nonRoot = 0;
        if (cliReader.RootExists)
        {
            try
            {
                var cli = cliReader.Read(CliAccount, CliOrganization, cancellation, newestTranscripts).ToList();
                rows.AddRange(cli);
                if (_probeCacheFile is not null && _cliProbes.Changed) _cliProbes.Save(_probeCacheFile);
                if (_nonRoot is not { } known || DateTime.UtcNow - known.CountedUtc > NonRootRecount)
                    _nonRoot = (cliReader.CountNonRootTranscripts(), DateTime.UtcNow);
                nonRoot = _nonRoot.Value.Count;
                tallies.Add(new SourceTally(
                    ConversationKey.CliCode, cli.Count, 0,
                    newestTranscripts is null ? SourceCoverage.Resolved : SourceCoverage.Partial,
                    newestTranscripts is null
                        ? $"顶层主会话 {cli.Count} 个；另有 {nonRoot} 个子代理 JSONL 未计入"
                        : $"先读了最近的 {cli.Count} 个，其余的正在读"));
            }
            catch (Exception ex)
            {
                tallies.Add(new SourceTally(ConversationKey.CliCode, 0, 0,
                    SourceCoverage.Unreachable, ex.Message));
                warning = "CLI 来源读取失败，已保留缓存条目";
            }
        }
        else
        {
            tallies.Add(new SourceTally(ConversationKey.CliCode, 0, 0,
                SourceCoverage.Unreachable, $"未找到 {_claudeConfigDir}\\projects"));
        }

        // ---- CLI history sitting outside the scanned root ------------------
        // Counted and surfaced, never merged: a second root may hold sessions that cannot
        // be resumed from the active configuration, and silently ignoring them would make
        // a partial list look complete.
        foreach (var alt in AlternateRootProbe.Probe(_claudeConfigDir))
        {
            if (alt.IsRedundantCopy)
            {
                // Everything there is already indexed. Reporting its size as a gap would
                // imply missing conversations that are in fact present, so it is recorded
                // as Resolved with a zero shortfall and does not suppress the global count.
                tallies.Add(new SourceTally(
                    ConversationKey.CliCode + " (冗余副本)", 0, 0, SourceCoverage.Resolved,
                    $"{alt.Path} —— {alt.Reason}；其 {alt.TopLevelTranscripts} 条全部已在活动根中，无缺失"));
            }
            else
            {
                tallies.Add(new SourceTally(
                    ConversationKey.CliCode + " (其他根)", alt.NotInActiveRoot, 0,
                    SourceCoverage.UnknownSource,
                    $"{alt.Path} —— {alt.Reason}；共 {alt.TopLevelTranscripts} 条，"
                    + $"其中 {alt.NotInActiveRoot} 条活动根没有，不在扫描范围"));
            }
        }

        // ---- Chat / Cowork: probed, never guessed --------------------------
        tallies.Add(new SourceTally(ConversationKey.Chat, 0, 0, SourceCoverage.UnknownSource,
            "本机没有可读取的云端 Chat 会话来源，未收录"));
        tallies.Add(new SourceTally(ConversationKey.Cowork, 0, 0, SourceCoverage.UnknownSource,
            "local-agent-mode-sessions 的会话格式未验证，不读取该目录"));

        // ---- link analysis -------------------------------------------------
        var cliStems = rows.Where(r => r.Key.Backend == ConversationKey.CliCode)
                           .Select(r => r.Key.NativeSessionId)
                           .ToHashSet(StringComparer.Ordinal);
        var linked = desktopCliIds.Count(id => cliStems.Contains(id));
        var unresolved = desktopCliIds.Where(id => !cliStems.Contains(id)).ToList();

        FoldTerminalTwins(rows);
        FoldResumedCopies(rows);

        // A source that was read in full is also authoritative about what is gone from it.
        var complete = new List<string>();
        foreach (var backend in new[] { ConversationKey.DesktopCode, ConversationKey.CliCode })
            if (tallies.Any(t => t.Backend == backend && t.Coverage == SourceCoverage.Resolved)) complete.Add(backend);
        if (codexResolved) complete.Add(ConversationKey.Codex);
        complete.AddRange(resolved.Where(r => r.Value).Select(r => r.Key));
        return new SyncRead(rows, tallies, linked, unresolved, nonRoot, warning, complete, scopes);
    }

    /// <summary>
    /// Writes what a read found — the only part of a sync that needs the catalog, and a matter
    /// of milliseconds. A conversation a complete read no longer found stays listed, marked as
    /// gone from its app: rows leave the list only when the user deletes them here.
    /// </summary>
    public SyncResult Apply(SyncRead read)
    {
        ArgumentNullException.ThrowIfNull(read);
        // An import removed while this read was under way: what was read of it must not be
        // written back once the removal has forgotten it — nothing would ever forget it again.
        // Asked here, where the catalog is held, because the store is deleted before it is forgotten.
        var removedImports = RemovedImports();
        var rows = removedImports.Count == 0 ? read.Rows
            : read.Rows.Where(r => !removedImports.Contains(r.Key.Backend)).ToList();
        var changed = rows.Count > 0 ? _catalog.UpsertRange(rows) : 0;
        changed += _catalog.AdoptMergedPreferences();
        var removed = 0;
        foreach (var backend in read.Complete)
            removed += _catalog.MarkGone(backend, rows.Select(r => r.Key),
                read.CompleteAccounts is { } scopes && scopes.TryGetValue(backend, out var accounts) ? accounts : null);

        var coverage = SourceCoverageRules.Build(read.Tallies, read.Linked, read.Unresolved, read.NonRoot);
        TryWriteEvidence(coverage);
        return new SyncResult(coverage, rows.Count, read.Warning, changed, removed);
    }

    /// <summary>The imported sources whose store is gone: removed, or never imported.</summary>
    private HashSet<string> RemovedImports()
    {
        var gone = new HashSet<string>(StringComparer.Ordinal);
        foreach (var web in _webExports)
            if (!web.Exists) gone.Add(web.Backend);
        if (_geminiWeb is { Exists: false }) gone.Add(ConversationKey.GeminiWeb);
        return gone;
    }

    /// <summary>
    /// A Claude desktop session runs on a terminal transcript, and both are read: without this
    /// the list shows every such conversation twice. The terminal row is marked as folded into
    /// the desktop row, which is listed for both — with the later of the two activity times,
    /// both titles searchable, and a revision that changes when either file does.
    ///
    /// <para>A transcript the session ran on before — Claude starts a new one when it continues
    /// a conversation — stays listed, since it holds the earlier part; it is marked as
    /// continued in that desktop session, which is what it is in Claude.</para>
    /// </summary>
    public static void FoldTerminalTwins(List<SessionMetadata> rows)
    {
        var desktopByCli = new Dictionary<string, SessionMetadata>(StringComparer.Ordinal);
        var desktopByPrior = new Dictionary<string, SessionMetadata>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row.Key.Backend != ConversationKey.DesktopCode) continue;
            if (row.CliSessionId is { Length: > 0 } cli) desktopByCli.TryAdd(cli, row);
            foreach (var prior in row.PriorCliSessionIds)
                if (prior != row.CliSessionId) desktopByPrior.TryAdd(prior, row);
        }
        if (desktopByCli.Count == 0 && desktopByPrior.Count == 0) return;

        var cliById = new Dictionary<string, SessionMetadata>(StringComparer.Ordinal);
        foreach (var row in rows)
            if (row.Key.Backend == ConversationKey.CliCode) cliById.TryAdd(row.Key.NativeSessionId, row);

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.Key.Backend == ConversationKey.CliCode
                && desktopByCli.TryGetValue(row.Key.NativeSessionId, out var desktop))
            {
                rows[i] = row with { MergedInto = desktop.Key.NativeSessionId };
            }
            else if (row.Key.Backend == ConversationKey.CliCode
                     && desktopByPrior.TryGetValue(row.Key.NativeSessionId, out var continued))
            {
                rows[i] = row with { ContinuedIn = continued.Key.NativeSessionId };
            }
            else if (row.Key.Backend == ConversationKey.DesktopCode
                     && row.CliSessionId is { Length: > 0 } cliId
                     && cliById.TryGetValue(cliId, out var twin))
            {
                rows[i] = row with
                {
                    LastActivityUtc = Later(row.LastActivityUtc, twin.LastActivityUtc),
                    SearchKey = SearchKeys.Normalize(row.Title + " " + twin.Title),
                    SourceRevision = row.SourceRevision + "|" + twin.SourceRevision,
                    // The desktop session's transcript is the terminal one.
                    TranscriptBytes = twin.TranscriptBytes,
                };
            }
        }
    }

    /// <summary>
    /// Claude Code's resume starts a new transcript with a copy of the session's history. A
    /// copy nothing was said in afterwards is a second copy of the same conversation: it is
    /// folded into the row that holds the original — the original's own, or the desktop
    /// session it belongs to — through any copies of copies. One the conversation went on in
    /// stays listed and says where it came from. A copy whose original is gone is all there is
    /// of that history, and stays a conversation of its own.
    /// </summary>
    public static void FoldResumedCopies(List<SessionMetadata> rows)
    {
        var cliById = new Dictionary<string, SessionMetadata>(StringComparer.Ordinal);
        var codexIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (row.Key.Backend == ConversationKey.CliCode) cliById.TryAdd(row.Key.NativeSessionId, row);
            // Only a task whose record is on disk still holds what a fork copied from it.
            else if (row.Key.Backend == ConversationKey.Codex && row.TranscriptBytes is not null)
                codexIds.Add(row.Key.NativeSessionId);
        }

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            // A forked Codex task stays a task of its own, as in ChatGPT; it only keeps the link,
            // and only while the task it came from is still there, record and all.
            if (row.Key.Backend == ConversationKey.Codex && row.ResumedFrom is { } parent)
            {
                if (!codexIds.Contains(parent) || string.Equals(parent, row.Key.NativeSessionId, StringComparison.OrdinalIgnoreCase))
                    rows[i] = row with { ResumedFrom = null };
                continue;
            }
            if (row.Key.Backend != ConversationKey.CliCode || row.ResumedFrom is not { } origin) continue;
            if (!cliById.ContainsKey(origin) || origin == row.Key.NativeSessionId)
            {
                rows[i] = row with { ResumedFrom = null, ResumedSilently = false };
                continue;
            }
            if (row.MergedInto is null && row.ResumedSilently && HolderOf(origin, cliById) is { } holder)
                rows[i] = row with { MergedInto = holder };
        }
    }

    /// <summary>
    /// The listed row that holds a transcript: the desktop session it is folded into, or its
    /// own — or, for a copy nothing was said in, the holder of what it copied. Null past a loop.
    /// </summary>
    private static string? HolderOf(string cliId, IReadOnlyDictionary<string, SessionMetadata> cliById)
    {
        for (var hops = 0; hops < 64 && cliById.TryGetValue(cliId, out var row); hops++)
        {
            if (row.MergedInto is { } folded) return folded;
            if (row.ResumedSilently && row.ResumedFrom is { } up && up != cliId && cliById.ContainsKey(up))
            {
                cliId = up;
                continue;
            }
            return cliId;
        }
        return null;
    }

    private static DateTimeOffset? Later(DateTimeOffset? a, DateTimeOffset? b) =>
        a is null ? b : b is null ? a : a > b ? a : b;

    /// <summary>
    /// Persists the coverage report so the scan scope is auditable outside the UI.
    /// Counts and paths only — no titles, bodies or account identifiers.
    /// </summary>
    private static void TryWriteEvidence(CoverageReport coverage)
    {
        try
        {
            LauncherPaths.EnsureCreated();
            var payload = new
            {
                generatedUtc = DateTimeOffset.UtcNow.ToString("O"),
                sources = coverage.Sources.Select(s => new
                {
                    backend = s.Backend,
                    records = s.Records,
                    archived = s.Archived,
                    coverage = s.Coverage.ToString(),
                    note = s.Note,
                }),
                verifiedDesktopCliLinks = coverage.VerifiedDesktopCliLinks,
                unknownSourceCount = coverage.UnknownSourceIds.Count,
                nonRootTranscripts = coverage.NonRootTranscripts,
                globalUniqueConversationCount = coverage.GlobalUniqueConversationCount,
                globalCountReason = coverage.GlobalCountReason,
            };
            var json = System.Text.Json.JsonSerializer.Serialize(payload,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(LauncherPaths.EvidenceDirectory, "source-coverage.json"), json);
        }
        catch
        {
            // Evidence is a diagnostic nicety; failing to write it must not fail a sync.
        }
    }
}
