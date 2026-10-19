using System.Text.Json;
using ClaudeLauncher.Infrastructure.Semantic;

namespace ClaudeLauncher.IntegrationTests;

/// <summary>
/// Semantic search from the window's side: a build is asked for only when there is something new
/// to read, not more often than every few minutes, never while paused, and not again and again
/// after it failed or the worker could not run — so the worker, and the model's memory, is gone
/// most of the time.
/// </summary>
public sealed class SemanticHostTests : IDisposable
{
    /// <summary>Only a limit for a test that hangs: builds run in background mode, which waits behind any other work on a busy machine.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromMinutes(2);

    private readonly SemanticRoot _root = new();
    private readonly WindowThread _window = new();
    private DateTimeOffset _now = new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);

    public SemanticHostTests()
    {
        _root.Write("a", "小麦条锈病的全基因组关联分析，用 GEMMA 跑混合线性模型", "显著位点附近的候选基因要用表达数据再筛一遍");
        _root.Write("b", "红烧肉要先焯水，再用冰糖炒出糖色，小火慢炖一个小时", "炖的时候加一点黄酒和八角，出锅前大火收汁");
        _root.List("a", "b");
        _root.IndexContent();
    }

    /// <summary>A host on the window thread, its worker in memory; <paramref name="model"/> is the id the model's files give.</summary>
    private Task<SemanticHost> HostAsync(InMemoryWorkers workers, Func<string?>? model = null, string? indexPath = null) =>
        _window.Run(() => new SemanticHost(new SemanticClient(workers.Connect), _root.ContentPath, indexPath ?? _root.IndexPath,
            model ?? (() => "fake:1"), () => _now));

    private Task<bool> On(SemanticHost host, Func<SemanticHost, Task<bool>> call) => _window.Run(() => call(host));

    private Task<SemanticBuildCheck> Check(SemanticHost host, Func<SemanticHost, Task<SemanticBuildCheck>> call) =>
        _window.Run(() => call(host));

    /// <summary>Waits, on the window thread, until the host says <paramref name="condition"/>.</summary>
    private Task Until(SemanticHost host, Func<SemanticHost, bool> condition) => _window.Run(() =>
    {
        if (condition(host)) return Task.FromResult(true);
        var met = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action? check = null;
        check = () =>
        {
            if (!condition(host)) return;
            host.Changed -= check;
            met.TrySetResult(true);
        };
        host.Changed += check;
        return met.Task;
    }).WaitAsync(Patience);

    private static bool Built(SemanticHost host) =>
        host.State == SemanticBuildState.Idle && host.LastError is null && host.Total > 0 && host.Done == host.Total;

    private void Grow() => _root.Append("a", 2, "关联分析的显著性阈值用 Bonferroni 校正");

    [Fact]
    public async Task ABuildIsAskedForOnlyWhenThereIsSomethingNewAndNotTooOften()
    {
        var workers = new InMemoryWorkers(_root, TimeSpan.FromMinutes(5));
        var host = await HostAsync(workers);

        Assert.True(await On(host, h => h.BuildIfDueAsync()));
        await Until(host, Built);
        Assert.Equal(4, SemanticIndex.ReadStats(_root.IndexPath)!.Chunks);

        // Nothing new: not asked again, even when asked to hurry.
        Assert.False(await On(host, h => h.BuildIfDueAsync()));
        Assert.False(await On(host, h => h.BuildIfDueAsync(now: true)));

        // Something new, but a build just finished: it waits for the interval — unless asked for now.
        Grow();
        _root.IndexContent("a");
        Assert.False(await On(host, h => h.BuildIfDueAsync()));
        _now += SemanticHost.BuildInterval + TimeSpan.FromSeconds(1);
        Assert.True(await On(host, h => h.BuildIfDueAsync()));
        await Until(host, h => Built(h) && SemanticIndex.ReadStats(_root.IndexPath)!.Chunks == 5);
        Assert.Equal(1, workers.Started);

        Grow();
        _root.IndexContent("a");
        Assert.True(await On(host, h => h.BuildIfDueAsync(now: true)));
        await Until(host, h => Built(h) && SemanticIndex.ReadStats(_root.IndexPath)!.Chunks == 6);
        await _window.Run(() => host.CloseAsync(Patience).ContinueWith(_ => true));
    }

    [Fact]
    public async Task ABuildThatFoundNothingToReadDoesNotHoldTheNextOneBack()
    {
        // Content search just switched on: its index is there, and still empty.
        _root.EmptyContent();
        var host = await HostAsync(new InMemoryWorkers(_root, TimeSpan.FromMinutes(5)));
        Assert.True(await On(host, h => h.BuildIfDueAsync()));
        await Until(host, h => h.State == SemanticBuildState.Idle);
        Assert.Null(await _window.Run(() => host.LastError));
        Assert.Equal(0, SemanticIndex.ReadStats(_root.IndexPath)!.Conversations);

        // A minute later the content index has read the transcripts: built at once, not ten minutes on.
        _root.IndexContent();
        _now += TimeSpan.FromMinutes(1);
        Assert.True(await On(host, h => h.BuildIfDueAsync()));
        await Until(host, h => Built(h) && SemanticIndex.ReadStats(_root.IndexPath)!.Chunks == 4);
        await _window.Run(() => host.CloseAsync(Patience).ContinueWith(_ => true));
    }

    [Fact]
    public async Task ABuildThatEmbeddedNothingNewStillHoldsTheNextOneBack()
    {
        var host = await HostAsync(new InMemoryWorkers(_root, TimeSpan.FromMinutes(5)));
        Assert.True(await On(host, h => h.BuildIfDueAsync()));
        await Until(host, Built);

        // Ten minutes on only a word was said, too short to embed: built all the same, embedding nothing.
        _now += SemanticHost.BuildInterval + TimeSpan.FromSeconds(1);
        _root.Append("a", 2, "好的");
        _root.IndexContent("a");
        Assert.True(await On(host, h => h.BuildIfDueAsync()));
        await Until(host, Built);
        Assert.Equal(4, SemanticIndex.ReadStats(_root.IndexPath)!.Chunks);

        // A minute later something is said again: it waits out the interval like after any other build.
        _root.Append("a", 3, "关联分析的显著性阈值用 Bonferroni 校正");
        _root.IndexContent("a");
        _now += TimeSpan.FromMinutes(1);
        Assert.Equal(SemanticBuildCheck.NotNow, await Check(host, h => h.TryBuildAsync()));
        await _window.Run(() => host.CloseAsync(Patience).ContinueWith(_ => true));
    }

    [Fact]
    public async Task ATranscriptThatGrewWithoutAnythingSaidAsksForNoBuild()
    {
        var host = await HostAsync(new InMemoryWorkers(_root, TimeSpan.FromMinutes(5)));
        Assert.True(await On(host, h => h.BuildIfDueAsync()));
        await Until(host, Built);

        // A long run of tool calls: the transcript grows and is read again, but nothing new was said.
        var before = _root.ContentFingerprint();
        _root.AppendToolCall("a");
        _root.IndexContent("a");
        Assert.NotEqual(before, _root.ContentFingerprint());
        Assert.Equal(SemanticBuildCheck.UpToDate, await Check(host, h => h.TryBuildAsync(now: true)));

        // Something said: built.
        Grow();
        _root.IndexContent("a");
        Assert.Equal(SemanticBuildCheck.Started, await Check(host, h => h.TryBuildAsync(now: true)));
        await Until(host, h => Built(h) && SemanticIndex.ReadStats(_root.IndexPath)!.Chunks == 5);
        await _window.Run(() => host.CloseAsync(Patience).ContinueWith(_ => true));
    }

    [Fact]
    public async Task ACheckSaysWhyItStartedNoBuild()
    {
        var host = await HostAsync(new InMemoryWorkers(_root, TimeSpan.FromMinutes(5)));
        Assert.Equal(SemanticBuildCheck.Started, await Check(host, h => h.TryBuildAsync()));
        await Until(host, Built);
        Assert.Equal(SemanticBuildCheck.UpToDate, await Check(host, h => h.TryBuildAsync(now: true)));

        Grow();
        _root.IndexContent("a");
        Assert.Equal(SemanticBuildCheck.NotNow, await Check(host, h => h.TryBuildAsync()));             // within the interval

        _root.DropContent();
        Assert.Equal(SemanticBuildCheck.NoContent, await Check(host, h => h.TryBuildAsync(now: true)));
        await _window.Run(() => host.CloseAsync(Patience).ContinueWith(_ => true));
    }

    [Fact]
    public async Task ANewRunReadsWhatTheIndexWasBuiltFromAndNoticesAnotherModel()
    {
        var first = await HostAsync(new InMemoryWorkers(_root, TimeSpan.FromMinutes(5)));
        Assert.True(await On(first, h => h.BuildIfDueAsync()));
        await Until(first, Built);
        await _window.Run(() => first.CloseAsync(Patience).ContinueWith(_ => true));

        // The next start: up to date, so no worker at all.
        var model = "fake:1";
        var workers = new InMemoryWorkers(() => _root.Service(new FakeEmbedder(model)), TimeSpan.FromMinutes(5));
        var next = await HostAsync(workers, () => model);
        Assert.False(await On(next, h => h.BuildIfDueAsync()));
        Assert.Equal(0, workers.Started);

        // The model in the folder was replaced: everything is to be embedded again.
        model = "fake:2";
        Assert.True(await On(next, h => h.BuildIfDueAsync()));
        await Until(next, Built);
        Assert.Equal(1, workers.Started);
        Assert.Equal("fake:2", SemanticIndex.ReadStats(_root.IndexPath)!.ModelId);
        await _window.Run(() => next.CloseAsync(Patience).ContinueWith(_ => true));
    }

    [Fact]
    public async Task AWorkerStillHoldingAReplacedModelIsEndedBeforeTheNextBuild()
    {
        var model = "fake:1";
        var workers = new InMemoryWorkers(() => _root.Service(new FakeEmbedder(model)), TimeSpan.FromMinutes(5));
        var host = await HostAsync(workers, () => model);
        Assert.True(await On(host, h => h.BuildIfDueAsync()));
        await Until(host, Built);
        Assert.True(await _window.Run(() => host.WorkerRunning));

        // embedding.json edited while the worker still runs with the model it loaded: a new worker
        // loads the files as they are now, and everything is embedded again with them.
        model = "fake:2";
        Assert.Equal(SemanticBuildCheck.Started, await Check(host, h => h.RefreshAsync()));
        await Until(host, Built);
        var stats = SemanticIndex.ReadStats(_root.IndexPath)!;
        Assert.Equal("fake:2", stats.ModelId);
        Assert.Equal(4, stats.Chunks);
        Assert.Equal(2, workers.Started);
        Assert.Equal(0, await workers.Exits[0].WaitAsync(Patience));
        Assert.Equal(0, workers.Kills);
        Assert.Equal(SemanticBuildCheck.UpToDate, await Check(host, h => h.RefreshAsync()));
        await _window.Run(() => host.CloseAsync(Patience).ContinueWith(_ => true));
    }

    [Fact]
    public async Task WhatCountsIsTheModelTheWorkerEmbeddedWith()
    {
        // The model's files changed between the check and the worker loading them: it embedded with the new ones.
        var files = "fake:1";
        var workers = new InMemoryWorkers(() => _root.Service(new FakeEmbedder("fake:2")), TimeSpan.FromMinutes(5));
        var host = await HostAsync(workers, () => files);
        Assert.True(await On(host, h => h.BuildIfDueAsync()));
        await Until(host, Built);
        Assert.Equal("fake:2", SemanticIndex.ReadStats(_root.IndexPath)!.ModelId);

        // The next check sees the files as the worker did: nothing to build again.
        files = "fake:2";
        Assert.Equal(SemanticBuildCheck.UpToDate, await Check(host, h => h.RefreshAsync()));
        Assert.Equal(1, workers.Started);
        await _window.Run(() => host.CloseAsync(Patience).ContinueWith(_ => true));
    }

    [Fact]
    public async Task APausedBuildStopsAndWaitsForResume()
    {
        var gate = new SemaphoreSlim(0);
        var embedder = new FakeEmbedder { Gate = gate };
        var workers = new InMemoryWorkers(() => _root.Service(embedder), TimeSpan.FromMinutes(5));
        var ended = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = await _window.Run(() =>
        {
            var client = new SemanticClient(workers.Connect);
            client.Stopped += (_, _) => ended.TrySetResult("stopped");
            client.Built += _ => ended.TrySetResult("built");
            return new SemanticHost(client, _root.ContentPath, _root.IndexPath, () => "fake:1", () => _now);
        });

        Assert.True(await On(host, h => h.BuildIfDueAsync()));
        await embedder.Entered.Task.WaitAsync(Patience);
        Assert.True(await On(host, h => h.PauseAsync()));
        Assert.Equal(SemanticBuildState.Paused, await _window.Run(() => host.State));
        // The worker was told to stop while the batch was in hand: let the batch go, and the build ends there.
        Assert.True(embedder.StopSeen.Task.IsCompleted);
        embedder.Gate = null;
        gate.Release(100);
        Assert.Equal("stopped", await ended.Task.WaitAsync(Patience));
        Assert.True(SemanticIndex.ReadStats(_root.IndexPath)!.Chunks < 4);
        Assert.Equal(SemanticBuildState.Paused, await _window.Run(() => host.State));

        // Paused: not built on its own, however new the content or long the wait.
        Grow();
        _root.IndexContent("a");
        _now += TimeSpan.FromHours(1);
        Assert.False(await On(host, h => h.BuildIfDueAsync()));
        Assert.Equal(SemanticBuildCheck.NotNow, await Check(host, h => h.TryBuildAsync(now: true)));
        Assert.Equal(SemanticBuildCheck.NotNow, await Check(host, h => h.RefreshAsync()));

        // Going on clears the pause before anything else, whether or not a build can start.
        Assert.Equal(SemanticBuildState.Idle, await _window.Run(() =>
        {
            host.Resume();
            return host.State;
        }));
        Assert.Equal(SemanticBuildCheck.Started, await Check(host, h => h.ResumeAsync()));
        await Until(host, Built);
        Assert.Equal(5, SemanticIndex.ReadStats(_root.IndexPath)!.Chunks);
        await _window.Run(() => host.CloseAsync(Patience).ContinueWith(_ => true));
    }

    [Fact]
    public async Task AFailedBuildIsTriedAgainOnlyOnNewContentAndNotForever()
    {
        // An index path the worker cannot make a file at: every build fails.
        var unusable = Directory.CreateDirectory(Path.Combine(_root.Folder, "not-a-file")).FullName;
        var workers = new InMemoryWorkers(
            () => SemanticService.ForFiles(_root.CatalogPath, _root.ContentPath, unusable, () => new FakeEmbedder()),
            TimeSpan.FromMinutes(5));
        var host = await HostAsync(workers, indexPath: unusable);

        Assert.True(await On(host, h => h.BuildIfDueAsync()));
        await Until(host, h => h.LastError is not null);
        Assert.Contains("语义索引文件建不了", await _window.Run(() => host.LastError!));
        Assert.Equal(SemanticBuildState.Idle, await _window.Run(() => host.State));
        // The same content: not again.
        Assert.Equal(SemanticBuildCheck.Failed, await Check(host, h => h.TryBuildAsync(now: true)));

        for (var failure = 2; failure <= SemanticHost.MaxFailures; failure++)
        {
            Grow();
            _root.IndexContent("a");
            Assert.True(await On(host, h => h.BuildIfDueAsync(now: true)));
            await Until(host, h => h.State == SemanticBuildState.Idle);
        }
        Grow();
        _root.IndexContent("a");
        // Given up for this run.
        Assert.Equal(SemanticBuildCheck.Failed, await Check(host, h => h.TryBuildAsync(now: true)));

        // The user looks again: tried once more.
        Assert.Equal(SemanticBuildCheck.Started, await Check(host, h => h.RefreshAsync()));
        await _window.Run(() => host.CloseAsync(Patience).ContinueWith(_ => true));
    }

    [Fact]
    public async Task AWorkerThatCannotRunIsNotStartedAgainOnItsOwn()
    {
        const string reason = "没有找到向量模型文件夹：D:\\nowhere";
        var started = 0;
        var host = await _window.Run(() => new SemanticHost(new SemanticClient(() =>
        {
            Interlocked.Increment(ref started);
            var output = new LinePipe();
            output.Writer.WriteLine($$"""{"event":"fatal","error":{{JsonSerializer.Serialize(reason)}}}""");
            output.Writer.Dispose();
            return new SemanticConnection(new LinePipe().Writer, output.Reader, Task.CompletedTask, () => { });
        }), _root.ContentPath, _root.IndexPath, () => "fake:1", () => _now));

        Assert.False(await On(host, h => h.BuildIfDueAsync()));
        await Until(host, h => h.Unavailable is not null);
        Assert.Equal(reason, await _window.Run(() => host.Unavailable));
        Assert.Equal(reason, await _window.Run(() => host.LastError));
        Assert.Equal(SemanticBuildState.Idle, await _window.Run(() => host.State));

        _now += TimeSpan.FromHours(1);
        Grow();
        _root.IndexContent("a");
        Assert.False(await On(host, h => h.BuildIfDueAsync(now: true)));
        Assert.Equal(1, Volatile.Read(ref started));

        // A search the user asks for tries again — and says why it cannot.
        var error = await Assert.ThrowsAsync<SemanticUnavailableException>(() => _window.Run(() => host.QueryAsync("引物")));
        Assert.Equal(reason, error.Message);
        Assert.Equal(2, Volatile.Read(ref started));
        host.Dispose();
    }

    [Fact]
    public async Task WithoutTheFullTextIndexNothingIsStarted()
    {
        _root.DropContent();
        var workers = new InMemoryWorkers(_root, TimeSpan.FromMinutes(5));
        var host = await HostAsync(workers);
        Assert.False(await On(host, h => h.BuildIfDueAsync(now: true)));
        Assert.Equal(0, workers.Started);
        Assert.Null(await host.StatsAsync());
        Assert.False(File.Exists(_root.IndexPath));
        host.Dispose();
    }

    public void Dispose()
    {
        _window.Dispose();
        _root.Dispose();
    }
}
