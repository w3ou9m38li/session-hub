using System.IO;
using ClaudeLauncher.Core.Contracts;
using ClaudeLauncher.Infrastructure;
using ClaudeLauncher.Infrastructure.Diagnostics;
using ClaudeLauncher.Infrastructure.Search;
using ClaudeLauncher.Infrastructure.Semantic;

namespace ClaudeLauncher.App.ViewModels;

/// <summary>A conversation a search by meaning found, as the catalog lists it now.</summary>
public sealed record SemanticResult(SessionMetadata Row, SemanticHit Hit);

/// <summary>What the search-by-meaning window says about the index before and around a search.</summary>
/// <param name="Readiness">Whether the component and the model are in place, from the file system.</param>
/// <param name="Stats">What the index holds; null before anything was built.</param>
/// <param name="Problem">Why builds or searches do not run now, in words for the user; null when nothing is wrong.</param>
public sealed record SemanticOverview(
    SemanticReadiness Readiness, SemanticIndexStats? Stats, SemanticBuildState State, int Done, int Total,
    bool ContentSearch, string? Problem);

/// <summary>
/// Search by meaning: a worker process (this program with <c>--semantic-worker</c>) embeds what was
/// said with the user's model and answers searches; this side only asks. Nothing of it exists until
/// the setting is on — no process, no file, no thread — and the window never loads the model.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>The worker and its builds; made on first need while the setting is on.</summary>
    private SemanticHost? _semantic;

    /// <summary>The index's files could not all be deleted when the setting went off: tried again after the next sync.</summary>
    private bool _semanticLeftovers;

    /// <summary>Whether this run has looked for index files left by an earlier one while the setting is off.</summary>
    private bool _semanticSwept;

    /// <summary>Where the model is looked for: the folder the settings name, or <c>models\embedding</c> in the data root.</summary>
    public static string SemanticModelFolder => AppSettings.Current.SemanticModelFolder ?? LauncherPaths.DefaultEmbeddingModelFolder;

    public bool SemanticSearchOn => AppSettings.Current.SemanticSearch;

    private string? _semanticProgress;

    /// <summary>「正在建立语义索引 120/791」 while a long build runs, 「语义索引已暂停」 while paused; null otherwise. Shown beside <see cref="LastSyncText"/>.</summary>
    public string? SemanticProgress { get => _semanticProgress; private set => Set(ref _semanticProgress, value); }

    private SemanticBuildState _semanticState;

    /// <summary>Whether the index is being built, or paused; the menu offers to pause or go on accordingly.</summary>
    public SemanticBuildState SemanticState { get => _semanticState; private set => Set(ref _semanticState, value); }

    private SemanticHost Semantic
    {
        get
        {
            if (_semantic is { } host) return host;
            host = SemanticHost.ForWorker(Environment.ProcessPath!, LauncherPaths.DataRoot, SemanticModelFolder,
                LauncherPaths.ContentIndexDatabase, LauncherPaths.SemanticIndexDatabase);
            host.Changed += ShowSemanticBuild;
            return _semantic = host;
        }
    }

    private void ShowSemanticBuild()
    {
        if (_semantic is not { } host) return;
        SemanticState = host.State;
        SemanticProgress = host.State switch
        {
            // A build of a few conversations is over in seconds: not worth a word.
            SemanticBuildState.Building when host.Total >= 10 && host.Done < host.Total => $"正在建立语义索引 {host.Done}/{host.Total}",
            SemanticBuildState.Paused => "语义索引已暂停",
            _ => null,
        };
    }

    /// <summary>
    /// The last of the passes after a sync: when the setting and content search are on, asks the
    /// worker to embed what the content index learned since the last build — at most every few
    /// minutes, and only when something changed. Otherwise nothing at all.
    /// </summary>
    private async Task BuildSemanticIndexQuietlyAsync()
    {
        if (_disposed) return;
        if (!SemanticSearchOn)
        {
            // The index holds pieces of what was said: files a switch-off could not delete — in this
            // run, or in one that ended before the next sync — are looked for once a run, and after
            // every sync while they stay.
            if (_semanticSwept && !_semanticLeftovers) return;
            _semanticSwept = true;
            _semanticLeftovers = !await LowPriority.Run(() => SemanticIndex.DeleteFiles(LauncherPaths.SemanticIndexDatabase),
                "semantic leftovers");
            return;
        }
        if (_content is null) return;
        try
        {
            await Semantic.BuildIfDueAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or ObjectDisposedException or System.ComponentModel.Win32Exception)
        {
            // A convenience: the next sync tries again; the search window says what is wrong.
        }
    }

    /// <summary>
    /// The setting went on, or the user looks: the index is built now rather than after the next
    /// sync, and a model that could not be loaded before is tried again — it may have been put right.
    /// </summary>
    /// <returns>What came of it; on <see cref="SemanticBuildCheck.Failed"/>, <see cref="SemanticProblem"/> says why.</returns>
    public async Task<SemanticBuildCheck> StartSemanticSearchAsync()
    {
        if (!SemanticSearchOn || _disposed) return SemanticBuildCheck.NotNow;
        if (_content is null) return SemanticBuildCheck.NoContent;
        return await Semantic.RefreshAsync();
    }

    /// <summary>Stops the running build; it is not built again until <see cref="ResumeSemanticIndexAsync"/> or the next start.</summary>
    public Task<bool> PauseSemanticIndexAsync() => _semantic?.PauseAsync() ?? Task.FromResult(false);

    /// <summary>
    /// Ends a pause — whatever comes next, so the menu and the status bar stop offering to go on —
    /// and builds now when content search is on and anything is new.
    /// </summary>
    /// <returns>What came of it; on <see cref="SemanticBuildCheck.Failed"/>, <see cref="SemanticProblem"/> says why.</returns>
    public async Task<SemanticBuildCheck> ResumeSemanticIndexAsync()
    {
        if (_disposed) return SemanticBuildCheck.NotNow;
        _semantic?.Resume();
        if (!SemanticSearchOn) return SemanticBuildCheck.NotNow;
        if (_content is null) return SemanticBuildCheck.NoContent;
        return await Semantic.ResumeAsync();
    }

    /// <summary>Why builds or searches do not run now, in words for the user; null when nothing is known to be wrong.</summary>
    public string? SemanticProblem => _semantic is { } host ? host.Unavailable ?? host.LastError : null;

    /// <summary>
    /// The setting went off: the worker is ended (waited for off this thread, then killed if need
    /// be) and the index's files deleted. False when a file could not be deleted yet; it is tried
    /// again after the next sync.
    /// </summary>
    public async Task<bool> DisableSemanticSearchAsync()
    {
        if (_semantic is { } host)
        {
            host.Changed -= ShowSemanticBuild;
            _semantic = null;
            SemanticState = SemanticBuildState.Idle;
            SemanticProgress = null;
            await host.CloseAsync(TimeSpan.FromSeconds(10));
            host.Dispose();
        }
        var deleted = await Task.Run(() => SemanticIndex.DeleteFiles(LauncherPaths.SemanticIndexDatabase));
        _semanticLeftovers = !deleted;
        return deleted;
    }

    /// <summary>What the index takes on disk, for the question before it is deleted.</summary>
    public static long SemanticIndexBytes()
    {
        long total = 0;
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try
            {
                var file = new FileInfo(LauncherPaths.SemanticIndexDatabase + suffix);
                if (file.Exists) total += file.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* not counted */ }
        }
        return total;
    }

    /// <summary>Whether the component and the model are in place, read from the file system off this thread.</summary>
    public static Task<SemanticReadiness> CheckSemanticAsync(string? modelFolder = null)
    {
        var folder = modelFolder ?? SemanticModelFolder;
        return Task.Run(() => EmbedderLoader.Check(folder));
    }

    /// <summary>
    /// The search window opened: what stopped builds before is tried again and a due build starts
    /// now, so the latest conversations are in. Then what the window shows above the results.
    /// </summary>
    public async Task<SemanticOverview> OpenSemanticSearchAsync()
    {
        if (SemanticSearchOn && _content is not null && !_disposed)
        {
            try
            {
                await StartSemanticSearchAsync();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                           or System.ComponentModel.Win32Exception)
            {
                // Said below, from the host's state.
            }
        }
        return await DescribeSemanticAsync();
    }

    /// <summary>What the search window shows above the results: the index's size and state, and what is wrong.</summary>
    public async Task<SemanticOverview> DescribeSemanticAsync()
    {
        var readiness = await CheckSemanticAsync();
        var stats = await Task.Run(() => SemanticIndex.ReadStats(LauncherPaths.SemanticIndexDatabase));
        var host = _semantic;
        return new SemanticOverview(readiness, stats, host?.State ?? SemanticBuildState.Idle, host?.Done ?? 0, host?.Total ?? 0,
            _content is not null, SemanticProblem);
    }

    /// <summary>
    /// The conversations closest in meaning to <paramref name="text"/>, closest first, as the
    /// catalog lists them now; the worker starts (and loads the model) when it is not running.
    /// </summary>
    /// <exception cref="SemanticUnavailableException">The worker cannot run or could not search; the message says why.</exception>
    public async Task<IReadOnlyList<SemanticResult>> SearchSemanticAsync(string text, CancellationToken cancellation)
    {
        if (!SemanticSearchOn || _disposed) return [];
        var hits = await Semantic.QueryAsync(text, 30, cancellation);
        if (hits.Count == 0) return [];
        return await OnCatalog(() =>
        {
            var results = new List<SemanticResult>(hits.Count);
            foreach (var hit in hits)
            {
                // Folded into another row, or deleted since: not a result of its own.
                if (hit.Chunks.Count > 0 && ContentIndex.TryParseKey(hit.Conversation, out var key)
                    && _catalog.Get(key) is { MergedInto: null } row)
                    results.Add(new SemanticResult(row, hit));
            }
            return (IReadOnlyList<SemanticResult>)results;
        }, cancellation);
    }

    /// <summary>The running build's conversations done, of those to do.</summary>
    public (int Done, int Total) SemanticBuildProgress => _semantic is { } host ? (host.Done, host.Total) : (0, 0);

    /// <summary>Whether the worker is running now: a search then answers without loading the model first.</summary>
    public bool SemanticWorkerRunning => _semantic?.WorkerRunning == true;

    /// <summary>From Dispose: the worker's input is closed and it ends by itself; nothing is waited for.</summary>
    private void ShutdownSemantic()
    {
        _semantic?.Dispose();
        _semantic = null;
    }
}
