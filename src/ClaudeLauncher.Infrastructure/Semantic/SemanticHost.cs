using ClaudeLauncher.Infrastructure.Diagnostics;
using ClaudeLauncher.Infrastructure.Search;
using Microsoft.Data.Sqlite;

namespace ClaudeLauncher.Infrastructure.Semantic;

/// <summary>Where the semantic index's build stands, as the window shows it.</summary>
public enum SemanticBuildState
{
    Idle,

    /// <summary>Asked for, or running in the worker.</summary>
    Building,

    /// <summary>Stopped by the user; not built again until resumed (or the next start).</summary>
    Paused,
}

/// <summary>What a look for a due build came to.</summary>
public enum SemanticBuildCheck
{
    /// <summary>A build was asked for.</summary>
    Started,

    /// <summary>The index holds what was said in the full-text index, embedded by the model in the folder.</summary>
    UpToDate,

    /// <summary>There is no full-text index to read: content search is off, or its index not made yet.</summary>
    NoContent,

    /// <summary>
    /// Not looked at now: a build runs or is paused, a look is already in hand, or a build finished
    /// within <see cref="SemanticHost.BuildInterval"/>. Looked at again after a later sync.
    /// </summary>
    NotNow,

    /// <summary>
    /// None started: it could not be, or failed before on the same content or too often, or the
    /// worker cannot run. <see cref="SemanticHost.Unavailable"/> or <see cref="SemanticHost.LastError"/> says why.
    /// </summary>
    Failed,
}

/// <summary>
/// Semantic search from the window's side: one worker (<see cref="SemanticClient"/>), started only
/// when there is something to do; a build asked for only when what was said in the full-text index
/// changed since the last complete build — or the model did — and, after one finished, not again
/// for <see cref="BuildInterval"/>, so the worker (and the model's memory) is gone most of the time.
/// A build that failed is not tried again until there is something new to read, and not at all
/// after <see cref="MaxFailures"/> in a row. Used from one thread, the window's: the client raises
/// its events there, and so does this.
/// </summary>
public sealed class SemanticHost : IDisposable
{
    /// <summary>
    /// The least time between two builds started on their own, after one that had anything to read;
    /// one asked for (<c>now</c>) does not wait.
    /// </summary>
    public static readonly TimeSpan BuildInterval = TimeSpan.FromMinutes(10);

    /// <summary>How long a worker still holding a replaced model is given to end before it is killed.</summary>
    private static readonly TimeSpan WorkerEndWait = TimeSpan.FromSeconds(10);

    /// <summary>Builds that may fail one after another before none is started on its own for the rest of the run.</summary>
    public const int MaxFailures = 3;

    private readonly SemanticClient _client;
    private readonly string _contentIndexPath;
    private readonly string _semanticIndexPath;
    private readonly Func<string?> _modelId;
    private readonly Func<DateTimeOffset> _now;

    // What the last complete build read: what was said in the full-text index (Said) and the model
    // the worker embedded with; read from the index file on the first check of a run.
    private bool _known;
    private string? _builtFrom;
    private string? _builtModel;

    // The build in hand: what it was asked for on.
    private string? _attempt;
    private string? _attemptModel;

    private string? _failedAt;
    private int _failures;
    private DateTimeOffset? _lastBuilt;
    private bool _paused;
    private bool _checking;
    private bool _disposed;

    /// <summary>Something below changed — raised on the window's thread.</summary>
    public event Action? Changed;

    /// <param name="modelId">The id the model in use will report (<see cref="EmbeddingConfig.ModelIdOf"/>), read from its files only; null when it cannot be told.</param>
    /// <param name="now">The clock, for <see cref="BuildInterval"/>.</param>
    public SemanticHost(SemanticClient client, string contentIndexPath, string semanticIndexPath, Func<string?> modelId,
        Func<DateTimeOffset>? now = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentIndexPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(semanticIndexPath);
        ArgumentNullException.ThrowIfNull(modelId);
        _client = client;
        _contentIndexPath = contentIndexPath;
        _semanticIndexPath = semanticIndexPath;
        _modelId = modelId;
        _now = now ?? (() => DateTimeOffset.UtcNow);

        client.Progress += OnProgress;
        client.Built += OnBuilt;
        client.Stopped += OnStopped;
        client.Failed += OnFailed;
        client.Exited += OnExited;
    }

    /// <summary>
    /// The host for this data root's worker: this program started with <c>--semantic-worker</c>,
    /// the model in <paramref name="modelFolder"/>.
    /// </summary>
    public static SemanticHost ForWorker(string executable, string dataRoot, string modelFolder, string contentIndexPath,
        string semanticIndexPath) =>
        new(SemanticClient.ForWorker(executable, dataRoot, modelFolder), contentIndexPath, semanticIndexPath,
            () => ModelIdOf(modelFolder));

    /// <summary>The id the worker will give the model in <paramref name="folder"/>; null when its settings cannot be read.</summary>
    public static string? ModelIdOf(string folder)
    {
        try
        {
            return EmbeddingConfig.ModelIdOf(folder, EmbeddingConfig.Load(folder));
        }
        catch (Exception ex) when (ex is SemanticUnavailableException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public SemanticBuildState State { get; private set; }

    /// <summary>The running build's conversations done, of those to do; zeros before its first report.</summary>
    public int Done { get; private set; }

    public int Total { get; private set; }

    /// <summary>Why the worker cannot run — the model cannot be loaded, say — in words for the user; null while it may be tried.</summary>
    public string? Unavailable { get; private set; }

    /// <summary>Why the last build did not finish, in words for the user; null after one that did.</summary>
    public string? LastError { get; private set; }

    /// <summary>Whether the worker is running now.</summary>
    public bool WorkerRunning => _client.IsRunning;

    // ------------------------------------------------------------------ build

    /// <summary>
    /// Asks the worker for a build when what was said in the full-text index, or the model, changed
    /// since the last complete one. Does nothing while a build runs, while paused, after the worker
    /// reported it cannot run, after a failure on the same content, or — unless
    /// <paramref name="now"/> — within <see cref="BuildInterval"/> of the last build. The check reads
    /// two small files off this thread, at background priority.
    /// </summary>
    /// <returns>Whether a build was started.</returns>
    public async Task<bool> BuildIfDueAsync(bool now = false) =>
        await TryBuildAsync(now).ConfigureAwait(true) == SemanticBuildCheck.Started;

    /// <summary>As <see cref="BuildIfDueAsync"/>, saying why no build was started.</summary>
    public async Task<SemanticBuildCheck> TryBuildAsync(bool now = false)
    {
        if (_disposed || _checking || State != SemanticBuildState.Idle) return SemanticBuildCheck.NotNow;
        if (Unavailable is not null || _failures >= MaxFailures) return SemanticBuildCheck.Failed;
        if (!now && _lastBuilt is { } last && _now() - last < BuildInterval) return SemanticBuildCheck.NotNow;

        _checking = true;
        Look look;
        try
        {
            var known = _known;
            look = await LowPriority.Run(() => LookAt(known), "semantic check").ConfigureAwait(true);
        }
        finally
        {
            _checking = false;
        }
        if (_disposed || State != SemanticBuildState.Idle) return SemanticBuildCheck.NotNow;
        if (Unavailable is not null) return SemanticBuildCheck.Failed;
        if (look.Stats is { } stats)
        {
            _known = true;
            _builtFrom = Said(stats.BuiltFrom);
            _builtModel = stats.ModelId;
        }
        if (look.Content is not { } content) return SemanticBuildCheck.NoContent;      // nothing to read
        var model = look.Model;
        if (content == _builtFrom && (model is null || model == _builtModel)) return SemanticBuildCheck.UpToDate;
        if (content == _failedAt) return SemanticBuildCheck.Failed;

        _attempt = content;
        _attemptModel = model;
        State = SemanticBuildState.Building;
        Done = Total = 0;
        Raise();
        try
        {
            // A worker started before the model's files changed still has the old model loaded and
            // would build with that: a new one loads the files as they are now.
            if (model is not null && _client.IsRunning)
            {
                if ((await _client.StatusAsync().ConfigureAwait(true)).ModelId != model)
                    await _client.EndWorkerAsync(WorkerEndWait).ConfigureAwait(true);
                if (_disposed || State != SemanticBuildState.Building) return SemanticBuildCheck.NotNow;     // paused meanwhile
            }
            var start = await _client.BuildAsync().ConfigureAwait(true);
            if (start != BuildStart.Refused) return SemanticBuildCheck.Started;
            Fail("另一个会话管家进程正在建立这个数据目录的语义索引");
            return SemanticBuildCheck.Failed;
        }
        catch (Exception ex) when (ex is SemanticUnavailableException or ObjectDisposedException)
        {
            // The worker could not start, or said why it cannot run (OnExited has kept that).
            if (!_disposed) Fail(ex.Message);
            return SemanticBuildCheck.Failed;
        }
    }

    /// <summary>
    /// The part of the full-text index's fingerprint (<see cref="ContentIndexReader.Fingerprint"/>:
    /// conversations, the newest of them, messages, then the transcripts' total length) that
    /// changes with what was said. The length is left out: it grows with every tool call and line
    /// of reasoning too, and builds asked for on that alone would find nothing to embed while
    /// keeping the worker, and the model's memory, from ever ending when idle. A transcript written
    /// again with as many messages as before is looked at with the next thing said anywhere.
    /// </summary>
    private static string? Said(string? fingerprint)
    {
        if (fingerprint is null) return null;
        var cut = fingerprint.LastIndexOf(':');
        return cut > 0 ? fingerprint[..cut] : fingerprint;
    }

    /// <summary>What a check reads: what was said in the full-text index (<see cref="Said"/>), the model's id, and the first time what the index was built from.</summary>
    private sealed record Look(string? Content, string? Model, SemanticIndexStats? Stats);

    private Look LookAt(bool known)
    {
        string? content;
        try
        {
            using var reader = ContentIndexReader.Open(_contentIndexPath);
            content = Said(reader?.Fingerprint());
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            content = null;     // being rebuilt or moved: looked at again at the next pass
        }
        var stats = known ? null : SemanticIndex.ReadStats(_semanticIndexPath) ?? new SemanticIndexStats(0, 0, null, 0, null, null);
        return new Look(content, _modelId(), stats);
    }

    /// <summary>The build in hand did not finish: not tried again on the same content. Once per build, however many ways it is heard.</summary>
    private void Fail(string error)
    {
        if (State != SemanticBuildState.Building) return;
        _failedAt = _attempt;
        _failures++;
        LastError = error;
        State = SemanticBuildState.Idle;
        Raise();
    }

    /// <summary>Stops the running build after the batch in hand; nothing is built again until <see cref="ResumeAsync"/>.</summary>
    /// <returns>Whether a build was running.</returns>
    public async Task<bool> PauseAsync()
    {
        if (State != SemanticBuildState.Building) return false;
        _paused = true;
        State = SemanticBuildState.Paused;
        Raise();
        try
        {
            await _client.StopAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is SemanticUnavailableException or ObjectDisposedException)
        {
            // Gone already: nothing is building.
        }
        return true;
    }

    /// <summary>Lets builds run again after a pause, without asking for one: the next sync looks.</summary>
    public void Resume()
    {
        _paused = false;
        if (State != SemanticBuildState.Paused) return;
        State = SemanticBuildState.Idle;
        Raise();
    }

    /// <summary>Builds again after a pause, at once when there is anything new to read.</summary>
    public Task<SemanticBuildCheck> ResumeAsync()
    {
        Resume();
        return RefreshAsync();
    }

    /// <summary>
    /// The user is looking (the search window opened, say): whatever stopped builds before — a
    /// failure, a worker that could not run — is tried again, and a due build starts now, without
    /// waiting out <see cref="BuildInterval"/>. A pause stays.
    /// </summary>
    public Task<SemanticBuildCheck> RefreshAsync()
    {
        _failedAt = null;
        _failures = 0;
        Unavailable = null;
        Raise();
        return TryBuildAsync(now: true);
    }

    // ------------------------------------------------------------------ query

    /// <summary>The conversations closest in meaning to <paramref name="text"/>, starting the worker when need be.</summary>
    /// <exception cref="SemanticUnavailableException">The worker cannot run or could not search; the message says why.</exception>
    public Task<IReadOnlyList<SemanticHit>> QueryAsync(string text, int limit = 20, CancellationToken cancellation = default)
    {
        // Asked by the user: whatever stopped the worker before is tried again.
        Unavailable = null;
        return _client.QueryAsync(text, limit, cancellation);
    }

    /// <summary>What the index holds, read from its file without the worker; null when there is none.</summary>
    public Task<SemanticIndexStats?> StatsAsync() => Task.Run(() => SemanticIndex.ReadStats(_semanticIndexPath));

    // ----------------------------------------------------------------- events

    private void OnProgress(int done, int total)
    {
        if (_disposed) return;
        Done = done;
        Total = total;
        if (State == SemanticBuildState.Idle) State = SemanticBuildState.Building;
        Raise();
    }

    private void OnBuilt(SemanticBuilt built)
    {
        if (_disposed) return;
        _known = true;
        _builtFrom = Said(built.Source) ?? _attempt;
        // The model the worker loaded when it started, whatever the files said at the check.
        _builtModel = built.Model ?? _attemptModel ?? _builtModel;
        // Every build holds the next one back, whether or not it had anything new to embed — but one
        // that found nothing at all to read (content search just switched on, its index still
        // empty) does not hold back the one that will.
        if (built.Conversations > 0 || built.Removed > 0) _lastBuilt = _now();
        _failedAt = null;
        _failures = 0;
        LastError = null;
        Done = Total = built.Conversations;
        State = _paused ? SemanticBuildState.Paused : SemanticBuildState.Idle;
        Raise();
    }

    private void OnStopped(int done, int total)
    {
        if (_disposed) return;
        Done = done;
        Total = total;
        State = _paused ? SemanticBuildState.Paused : SemanticBuildState.Idle;
        Raise();
    }

    private void OnFailed(string error)
    {
        if (!_disposed) Fail(error);
    }

    private void OnExited(string? reason)
    {
        if (_disposed) return;
        if (reason is not null) Unavailable = reason;
        // Ended in the middle of a build: the next one goes on from what it committed.
        if (State == SemanticBuildState.Building) Fail(reason ?? "语义搜索进程意外退出");
        else Raise();
    }

    private void Raise() => Changed?.Invoke();

    // ----------------------------------------------------------------- ending

    /// <summary>Ends the worker and waits for it (up to <paramref name="wait"/>, then kills it): its files can be deleted next.</summary>
    public async Task CloseAsync(TimeSpan wait)
    {
        _disposed = true;
        await _client.CloseAsync(wait).ConfigureAwait(false);
        _client.Dispose();
    }

    /// <summary>Closes the worker's input and returns at once; it stops a build and ends by itself.</summary>
    public void Dispose()
    {
        _disposed = true;
        _client.Progress -= OnProgress;
        _client.Built -= OnBuilt;
        _client.Stopped -= OnStopped;
        _client.Failed -= OnFailed;
        _client.Exited -= OnExited;
        _client.Dispose();
    }
}
