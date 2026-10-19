using System.Security.Cryptography;
using System.Text;
using ClaudeLauncher.Infrastructure.Catalog;
using ClaudeLauncher.Infrastructure.Diagnostics;
using ClaudeLauncher.Infrastructure.Search;
using Microsoft.Data.Sqlite;

namespace ClaudeLauncher.Infrastructure.Semantic;

/// <summary>What a request to build came to.</summary>
public enum BuildStart
{
    Started,

    /// <summary>A build is already running or about to: it covers this request too.</summary>
    AlreadyBuilding,

    /// <summary>Another process builds this data root's index; this one only answers searches.</summary>
    Refused,
}

/// <summary>Where the service reads and writes, and how it gets its parts.</summary>
/// <param name="IndexPath">The semantic index's database.</param>
/// <param name="MutexName">Held by whichever process builds this index; see <see cref="SemanticService.BuilderMutexName"/>.</param>
/// <param name="Embedder">Made once, on first use.</param>
/// <param name="Dialogue">Where what was said is read, for each build.</param>
/// <param name="Listed">The conversations still listed, the ones to embed first first; read for each build.</param>
/// <param name="StillListed">Which of the conversations a search found are still listed; null keeps them all.</param>
public sealed record SemanticServiceOptions(
    string IndexPath,
    string MutexName,
    Func<IEmbedder> Embedder,
    Func<IDialogueSource> Dialogue,
    Func<IReadOnlyList<string>> Listed,
    Func<IReadOnlyList<string>, IReadOnlySet<string>>? StillListed = null);

/// <summary>Where the index stands, for the window's status line.</summary>
/// <param name="Builder">Whether this process builds the index; false while another one does.</param>
public sealed record SemanticStatus(
    int Conversations, long Chunks, DateTimeOffset? Updated, bool Building, int Done, int Total, string ModelId,
    bool SameModel, bool Builder);

/// <summary>
/// The semantic index and the model that feeds it, as the worker process serves them: builds on a
/// thread of their own at background priority, one at a time and stoppable between any two
/// batches; searches on the caller's thread, meanwhile if need be. Only one process builds a data
/// root's index — the first to ask holds a named mutex until it ends — and any other answers
/// searches only.
/// </summary>
public sealed class SemanticService : IDisposable
{
    private readonly SemanticServiceOptions _options;
    private readonly Lazy<IEmbedder> _embedder;
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _wake = new(0);

    private Task? _loop;
    private TaskCompletionSource<bool>? _acquired;
    private CancellationTokenSource? _stop;
    private bool _pending, _building, _builder, _disposed;
    private (int Done, int Total) _progress;

    /// <summary>A build's conversations done, of those to do — raised on the build's thread.</summary>
    public event Action<int, int>? Progress;

    /// <summary>A build finished — raised on the build's thread.</summary>
    public event Action<SemanticBuildRun>? Built;

    /// <summary>A build was stopped part way, with its progress then — raised on the build's thread.</summary>
    public event Action<int, int>? Stopped;

    /// <summary>A build could not go on, in words for the user — raised on the build's thread.</summary>
    public event Action<string>? Failed;

    public SemanticService(SemanticServiceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _embedder = new Lazy<IEmbedder>(options.Embedder, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>
    /// The service over a data root's files: the dialogue from its full-text index, the listing from
    /// its catalog — both opened read-only, and only for as long as a read takes.
    /// </summary>
    public static SemanticService ForFiles(string catalogPath, string contentIndexPath, string semanticIndexPath,
        Func<IEmbedder> embedder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogPath);
        var root = Path.GetDirectoryName(Path.GetFullPath(semanticIndexPath)) ?? semanticIndexPath;
        return new SemanticService(new SemanticServiceOptions(
            semanticIndexPath,
            BuilderMutexName(root),
            embedder,
            () => new ContentIndexDialogue(contentIndexPath),
            () => ListedLatestFirst(catalogPath),
            found => StillListed(catalogPath, found)));
    }

    /// <summary>The mutex a data root's builder holds: named after the root, so copies on other roots build their own.</summary>
    public static string BuilderMutexName(string dataRoot)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot)).ToUpperInvariant();
        return @"Local\ClaudeSessionLauncher.SemanticIndex." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..12];
    }

    /// <summary>Every listed conversation by its index name, the latest first: those are the ones looked for most.</summary>
    private static IReadOnlyList<string> ListedLatestFirst(string catalogPath)
    {
        try
        {
            using var catalog = SqliteCatalog.OpenReadOnly(catalogPath);
            return catalog.ListedRows()
                .OrderByDescending(r => r.LastActivityUtc ?? DateTimeOffset.MinValue)
                .Select(r => ContentIndex.KeyOf(r.Key))
                .ToList();
        }
        catch (SqliteException ex)
        {
            throw new SemanticUnavailableException("会话目录暂时打不开：" + ex.Message, ex);
        }
    }

    /// <summary>The found conversations the catalog still lists on their own; all of them when it cannot be read just now.</summary>
    private static IReadOnlySet<string> StillListed(string catalogPath, IReadOnlyList<string> found)
    {
        try
        {
            using var catalog = SqliteCatalog.OpenReadOnly(catalogPath);
            return found.Where(c => ContentIndex.TryParseKey(c, out var key) && catalog.Get(key) is { MergedInto: null })
                .ToHashSet(StringComparer.Ordinal);
        }
        catch (SqliteException)
        {
            return found.ToHashSet(StringComparer.Ordinal);
        }
    }

    /// <summary>The model, loaded on first use.</summary>
    public IEmbedder Embedder => _embedder.Value;

    public bool IsBuilding
    {
        get
        {
            lock (_lock) return _building || _pending;
        }
    }

    // ------------------------------------------------------------------ build

    /// <summary>
    /// Starts a build on the build thread, unless one is running already or another process is
    /// the builder. The first build makes this process the builder for as long as it runs. Asked
    /// while a stopped build winds down, it runs as soon as that one has.
    /// </summary>
    public async Task<BuildStart> StartBuildAsync()
    {
        Task<bool> acquired;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_pending || (_building && _stop is not { IsCancellationRequested: true })) return BuildStart.AlreadyBuilding;
            if (_loop is null || _loop.IsCompleted)
            {
                var acquiring = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _acquired = acquiring;
                _loop = LowPriority.Run(() => BuildLoop(acquiring), "semantic index");
            }
            acquired = _acquired!.Task;
            _pending = true;
        }
        if (!await acquired.ConfigureAwait(false))
        {
            lock (_lock) _pending = false;
            return BuildStart.Refused;
        }
        _wake.Release();
        return BuildStart.Started;
    }

    /// <summary>Stops a running build after the batch in hand, and drops one waiting to run; false when there was neither.</summary>
    public bool StopBuild()
    {
        lock (_lock)
        {
            // One asked for and not begun: the build thread finds nothing to do when it wakes.
            var stopping = _pending;
            _pending = false;
            if (_stop is { IsCancellationRequested: false } running)
            {
                running.Cancel();
                stopping = true;
            }
            return stopping;
        }
    }

    /// <summary>
    /// The build thread: takes the data root's mutex (a mutex belongs to the thread that took it,
    /// so the same thread runs every build and lets it go), then builds each time it is woken.
    /// Ends at once when another process holds the mutex.
    /// </summary>
    private void BuildLoop(TaskCompletionSource<bool> acquired)
    {
        Mutex mutex;
        bool owned;
        try
        {
            mutex = new Mutex(false, _options.MutexName);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            acquired.SetResult(false);      // a name held in a way this process may not use: not the builder
            return;
        }
        using var _ = mutex;
        try
        {
            owned = mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            owned = true;       // the last builder died: whatever it was writing was rolled back
        }
        lock (_lock) _builder = owned;
        acquired.SetResult(owned);
        if (!owned) return;
        try
        {
            while (true)
            {
                _wake.Wait();
                CancellationTokenSource stop;
                lock (_lock)
                {
                    if (_disposed) return;
                    if (!_pending) continue;        // dropped by a stop before it began
                    _pending = false;
                    _building = true;
                    _progress = (0, 0);
                    stop = _stop = new CancellationTokenSource();
                }
                Action report;
                try
                {
                    report = Build(stop.Token);
                }
                finally
                {
                    lock (_lock)
                    {
                        _building = false;
                        _stop = null;
                    }
                    stop.Dispose();
                }
                // Told once the build is over: a status asked for on hearing it no longer says building.
                report();
            }
        }
        finally
        {
            lock (_lock) _builder = false;
            mutex.ReleaseMutex();
        }
    }

    /// <summary>One build; what to tell when it is over.</summary>
    private Action Build(CancellationToken stop)
    {
        try
        {
            var listed = _options.Listed();
            var dialogue = _options.Dialogue();
            var embedder = Embedder;
            using var index = SemanticIndex.OpenOrRebuild(_options.IndexPath)
                ?? throw new SemanticUnavailableException("语义索引文件建不了：数据目录无法写入");
            var run = index.Update(dialogue, embedder, listed, new InlineProgress(this), stop);
            return () => Built?.Invoke(run);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            var (done, total) = BuildProgress;
            return () => Stopped?.Invoke(done, total);
        }
        catch (Exception ex)
        {
            // Whatever went wrong, the worker goes on answering searches, and the next build tries again.
            var error = Describe(ex);
            return () => Failed?.Invoke(error);
        }
    }

    /// <summary>The words for the user: a reason already in words as it is, anything else with its message.</summary>
    public static string Describe(Exception ex) => ex switch
    {
        SemanticUnavailableException => ex.Message,
        SqliteException or IOException or UnauthorizedAccessException => "读写索引时出错：" + ex.Message,
        _ => "建立语义索引时出错：" + ex.Message,
    };

    /// <summary>The running build's progress: conversations done, of those to do.</summary>
    public (int Done, int Total) BuildProgress
    {
        get
        {
            lock (_lock) return _progress;
        }
    }

    private sealed class InlineProgress(SemanticService service) : IProgress<(int Done, int Total)>
    {
        public void Report((int Done, int Total) value)
        {
            lock (service._lock) service._progress = value;
            service.Progress?.Invoke(value.Done, value.Total);
        }
    }

    // ------------------------------------------------------------------ query

    /// <summary>
    /// The conversations closest in meaning to <paramref name="text"/>, closest first: embedded
    /// here, on the caller's thread, and searched in a read-only connection of its own, so a
    /// build may run meanwhile. Empty before anything is indexed.
    /// </summary>
    /// <exception cref="SemanticUnavailableException">The index was built by another model.</exception>
    public IReadOnlyList<SemanticHit> Query(string text, int limit = 20, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        limit = Math.Clamp(limit, 1, 100);
        if (string.IsNullOrWhiteSpace(text)) return [];
        var embedder = Embedder;
        var vector = embedder.Embed([text.Trim()], query: true, cancellation)[0];
        using var index = SemanticIndex.OpenReadOnly(_options.IndexPath);
        if (index is null) return [];
        // A few more than asked: a conversation deleted since the last build is still in the index.
        var hits = index.Search(vector, embedder.ModelId, _options.StillListed is null ? limit : limit + 10,
            cancellation: cancellation);
        if (_options.StillListed is { } still && hits.Count > 0)
        {
            var keep = still(hits.Select(h => h.Conversation).ToList());
            hits = hits.Where(h => keep.Contains(h.Conversation)).ToList();
        }
        return hits.Take(limit).ToList();
    }

    /// <summary>What the index holds and whether a build runs.</summary>
    public SemanticStatus Status()
    {
        var stats = SemanticIndex.ReadStats(_options.IndexPath);
        var model = Embedder.ModelId;
        lock (_lock)
        {
            return new SemanticStatus(stats?.Conversations ?? 0, stats?.Chunks ?? 0, stats?.Updated,
                _building || _pending, _progress.Done, _progress.Total, model,
                stats?.ModelId is null || stats.ModelId == model, _builder);
        }
    }

    /// <summary>Stops a running build and waits for the build thread to let go of the index and the mutex.</summary>
    public void Dispose()
    {
        Task? loop;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _stop?.Cancel();
            loop = _loop;
        }
        _wake.Release();
        try
        {
            // A batch in hand finishes first: well under a second with the small models this is for.
            loop?.Wait(TimeSpan.FromSeconds(30));
        }
        catch (AggregateException)
        {
            // The loop reports its own failures; nothing is left to wait for.
        }
        // Never from under a batch still running: the process is ending then anyway.
        if (_embedder.IsValueCreated && loop is not { IsCompleted: false }) _embedder.Value.Dispose();
    }
}
