using System.Text.Json;
using ClaudeLauncher.Infrastructure.Semantic;

namespace ClaudeLauncher.IntegrationTests;

/// <summary>
/// The worker process's protocol, run in memory with a fake model: builds with progress, searches
/// meanwhile, stops, ends on its own when idle or when its input closes, and leaves building to
/// one process per data root. And the window's client of it.
/// </summary>
public sealed class SemanticWorkerTests : IDisposable
{
    /// <summary>Only a limit for a test that hangs: builds run in background mode, which waits behind any other work on a busy machine.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromMinutes(2);

    private readonly SemanticRoot _root = new();

    private const string GwasA = "小麦条锈病的全基因组关联分析，用 GEMMA 跑混合线性模型";
    private const string GwasB = "显著位点附近的候选基因要用表达数据再筛一遍";
    private const string Cook = "红烧肉要先焯水，再用冰糖炒出糖色，小火慢炖一个小时";
    private const string CookB = "炖的时候加一点黄酒和八角，出锅前大火收汁";

    public SemanticWorkerTests()
    {
        _root.Write("a", GwasA, GwasB);
        _root.Write("b", Cook, CookB);
        _root.List("a", "b");
        _root.IndexContent();
    }

    /// <summary>A worker serving in memory: lines to it, lines from it, and its exit.</summary>
    private sealed class Running
    {
        private readonly LinePipe _in = new(), _out = new();
        private readonly List<JsonElement> _seen = [];

        public Running(SemanticService service, TimeSpan? idle = null)
        {
            Exit = Task.Run(() => SemanticWorker.Serve(_in.Reader, _out.Writer, service, idle ?? TimeSpan.FromMinutes(5)));
        }

        public Task<int> Exit { get; }

        public IReadOnlyList<JsonElement> Seen => _seen;

        public void Send(string json) => _in.Writer.WriteLine(json);

        public void CloseInput() => _in.Writer.Dispose();

        /// <summary>Reads on until a message matches; every message read is kept in <see cref="Seen"/>.</summary>
        public async Task<JsonElement> NextAsync(Func<JsonElement, bool> match)
        {
            while (true)
            {
                var line = await _out.Reader.ReadLineAsync().WaitAsync(Patience)
                           ?? throw new InvalidOperationException("the worker ended first");
                var message = JsonDocument.Parse(line).RootElement.Clone();
                _seen.Add(message);
                if (match(message)) return message;
            }
        }

        public Task<JsonElement> AnswerAsync(long id) =>
            NextAsync(m => m.TryGetProperty("id", out var n) && n.GetInt64() == id);

        public Task<JsonElement> EventAsync(string name) =>
            NextAsync(m => m.TryGetProperty("event", out var e) && e.GetString() == name);
    }

    [Fact]
    public async Task ABuildReportsProgressThenItsEndAndSearchesFindByMeaning()
    {
        var embedder = new FakeEmbedder();
        var worker = new Running(_root.Service(embedder));

        worker.Send("""{"id":1,"op":"build"}""");
        var started = await worker.AnswerAsync(1);
        Assert.True(started.GetProperty("ok").GetBoolean());
        Assert.True(started.GetProperty("started").GetBoolean());

        var built = await worker.EventAsync("built");
        Assert.Equal(2, built.GetProperty("conversations").GetInt32());
        Assert.Equal(4, built.GetProperty("chunks").GetInt64());
        Assert.Equal(_root.Dialogue.Fingerprint(), built.GetProperty("source").GetString());
        var progress = worker.Seen.Where(m => m.TryGetProperty("event", out var e) && e.GetString() == "progress").ToList();
        Assert.NotEmpty(progress);
        Assert.Equal(0, progress[0].GetProperty("done").GetInt32());
        Assert.All(progress, p => Assert.Equal(2, p.GetProperty("total").GetInt32()));

        worker.Send("""{"id":2,"op":"status"}""");
        var status = await worker.AnswerAsync(2);
        Assert.Equal(2, status.GetProperty("conversations").GetInt32());
        Assert.Equal(4, status.GetProperty("chunks").GetInt64());
        Assert.False(status.GetProperty("building").GetBoolean());
        Assert.True(status.GetProperty("builder").GetBoolean());
        Assert.True(status.GetProperty("sameModel").GetBoolean());
        Assert.Equal("fake:1", status.GetProperty("model").GetString());
        Assert.Equal(JsonValueKind.String, status.GetProperty("updated").ValueKind);

        worker.Send("""{"id":3,"op":"query","text":"用 GEMMA 做关联分析","limit":5}""");
        var answer = await worker.AnswerAsync(3);
        var hits = answer.GetProperty("hits").EnumerateArray().ToList();
        Assert.Equal(SemanticRoot.KeyOf("a"), hits[0].GetProperty("conversation").GetString());
        var chunk = hits[0].GetProperty("chunks")[0];
        Assert.Equal("user", chunk.GetProperty("role").GetString());
        Assert.Equal(GwasA, chunk.GetProperty("snippet").GetString());
        Assert.Equal(0, chunk.GetProperty("ordinal").GetInt32());

        worker.CloseInput();
        Assert.Equal(0, await worker.Exit.WaitAsync(Patience));
        Assert.True(embedder.Disposed);
    }

    [Fact]
    public async Task ASearchIsAnsweredWhileABuildRuns()
    {
        var gate = new SemaphoreSlim(0);
        var embedder = new FakeEmbedder { Gate = gate };
        var worker = new Running(_root.Service(embedder));

        worker.Send("""{"id":1,"op":"build"}""");
        await worker.AnswerAsync(1);
        await embedder.Entered.Task.WaitAsync(Patience);        // the build is held in the middle of embedding

        worker.Send("""{"id":2,"op":"query","text":"红烧肉"}""");
        var answer = await worker.AnswerAsync(2);
        Assert.True(answer.GetProperty("ok").GetBoolean());
        worker.Send("""{"id":3,"op":"status"}""");
        Assert.True((await worker.AnswerAsync(3)).GetProperty("building").GetBoolean());
        Assert.DoesNotContain(worker.Seen, m => m.TryGetProperty("event", out var e) && e.GetString() == "built");

        embedder.Gate = null;
        gate.Release(100);
        await worker.EventAsync("built");
        worker.CloseInput();
        await worker.Exit.WaitAsync(Patience);
    }

    [Fact]
    public async Task AStoppedBuildEndsAndTheNextOneFinishesWithoutDuplicates()
    {
        var gate = new SemaphoreSlim(0);
        var embedder = new FakeEmbedder { Gate = gate };
        var worker = new Running(_root.Service(embedder));

        worker.Send("""{"id":1,"op":"build"}""");
        await worker.AnswerAsync(1);
        await embedder.Entered.Task.WaitAsync(Patience);
        worker.Send("""{"id":2,"op":"stop"}""");
        Assert.True((await worker.AnswerAsync(2)).GetProperty("stopping").GetBoolean());
        embedder.Gate = null;
        gate.Release(100);
        await worker.EventAsync("stopped");

        worker.Send("""{"id":3,"op":"build"}""");
        Assert.True((await worker.AnswerAsync(3)).GetProperty("started").GetBoolean());
        var built = await worker.EventAsync("built");
        Assert.Equal(4, built.GetProperty("chunks").GetInt64());
        Assert.Equal(2, built.GetProperty("conversations").GetInt32());

        worker.Send("""{"id":4,"op":"stop"}""");
        Assert.False((await worker.AnswerAsync(4)).GetProperty("stopping").GetBoolean());   // nothing to stop
        worker.CloseInput();
        await worker.Exit.WaitAsync(Patience);
    }

    [Fact]
    public async Task ABuildAskedForWhileAStoppedOneWindsDownRunsRightAfterIt()
    {
        var gate = new SemaphoreSlim(0);
        var embedder = new FakeEmbedder { Gate = gate };
        var worker = new Running(_root.Service(embedder));

        worker.Send("""{"id":1,"op":"build"}""");
        await worker.AnswerAsync(1);
        await embedder.Entered.Task.WaitAsync(Patience);
        worker.Send("""{"id":2,"op":"stop"}""");
        await worker.AnswerAsync(2);
        // Still held in the middle of its batch: the next one waits for it rather than being lost.
        worker.Send("""{"id":3,"op":"build"}""");
        Assert.True((await worker.AnswerAsync(3)).GetProperty("started").GetBoolean());
        worker.Send("""{"id":4,"op":"build"}""");
        Assert.False((await worker.AnswerAsync(4)).GetProperty("started").GetBoolean());   // that one covers it

        embedder.Gate = null;
        gate.Release(100);
        await worker.EventAsync("stopped");
        var built = await worker.EventAsync("built");
        Assert.Equal(4, built.GetProperty("chunks").GetInt64());

        worker.CloseInput();
        await worker.Exit.WaitAsync(Patience);
    }

    [Fact]
    public async Task AStopDropsABuildThatHadNotBegun()
    {
        var gate = new SemaphoreSlim(0);
        var embedder = new FakeEmbedder { Gate = gate };
        var worker = new Running(_root.Service(embedder));

        worker.Send("""{"id":1,"op":"build"}""");
        await worker.AnswerAsync(1);
        await embedder.Entered.Task.WaitAsync(Patience);
        worker.Send("""{"id":2,"op":"stop"}""");
        await worker.AnswerAsync(2);
        worker.Send("""{"id":3,"op":"build"}""");
        Assert.True((await worker.AnswerAsync(3)).GetProperty("started").GetBoolean());
        worker.Send("""{"id":4,"op":"stop"}""");
        Assert.True((await worker.AnswerAsync(4)).GetProperty("stopping").GetBoolean());

        embedder.Gate = null;
        gate.Release(100);
        await worker.EventAsync("stopped");
        worker.Send("""{"id":5,"op":"status"}""");
        var status = await worker.AnswerAsync(5);
        Assert.False(status.GetProperty("building").GetBoolean());          // nothing waits to run
        Assert.Equal(0, status.GetProperty("chunks").GetInt64());
        Assert.DoesNotContain(worker.Seen, m => m.TryGetProperty("event", out var e) && e.GetString() == "built");
        worker.CloseInput();
        await worker.Exit.WaitAsync(Patience);
    }

    [Fact]
    public async Task AnIdleWorkerEndsByItselfButNotWhileBuilding()
    {
        var idle = new Running(_root.Service(new FakeEmbedder()), TimeSpan.FromMilliseconds(50));
        Assert.Equal(0, await idle.Exit.WaitAsync(Patience));

        var gate = new SemaphoreSlim(0);
        var embedder = new FakeEmbedder { Gate = gate };
        // Long enough that the request reaches it before it ends idle, however busy the machine.
        var building = new Running(_root.Service(embedder), TimeSpan.FromMilliseconds(400));
        building.Send("""{"id":1,"op":"build"}""");
        await building.AnswerAsync(1);
        await embedder.Entered.Task.WaitAsync(Patience);
        // Several idle times pass while the build is held: the worker stays.
        Assert.NotSame(building.Exit, await Task.WhenAny(building.Exit, Task.Delay(1500)));
        embedder.Gate = null;
        gate.Release(100);
        await building.EventAsync("built");
        Assert.Equal(0, await building.Exit.WaitAsync(Patience));
    }

    [Fact]
    public async Task ClosingTheInputStopsABuildAndLetsAnotherProcessBuild()
    {
        var gate = new SemaphoreSlim(0);
        var embedder = new FakeEmbedder { Gate = gate };
        var worker = new Running(_root.Service(embedder));
        worker.Send("""{"id":1,"op":"build"}""");
        await worker.AnswerAsync(1);
        await embedder.Entered.Task.WaitAsync(Patience);

        worker.CloseInput();
        await embedder.StopSeen.Task.WaitAsync(Patience);                      // told to stop, in the middle of its batch
        gate.Release(100);
        Assert.Equal(0, await worker.Exit.WaitAsync(Patience));
        Assert.Equal(0, SemanticIndex.ReadStats(_root.IndexPath)!.Chunks);      // so nothing of that batch was kept

        // The build mutex went with it.
        using var next = _root.Service(new FakeEmbedder());
        Assert.Equal(BuildStart.Started, await next.StartBuildAsync());
    }

    [Fact]
    public async Task OnlyOneProcessBuildsADataRootsIndex()
    {
        var builder = _root.Service(new FakeEmbedder());
        var built = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        builder.Built += _ => built.TrySetResult();
        Assert.Equal(BuildStart.Started, await builder.StartBuildAsync());
        await built.Task.WaitAsync(Patience);

        // Another worker on the same root: it searches what the first built, and is refused building.
        var other = new Running(_root.Service(new FakeEmbedder()));
        other.Send("""{"id":1,"op":"build"}""");
        var refused = await other.AnswerAsync(1);
        Assert.False(refused.GetProperty("ok").GetBoolean());
        Assert.True(refused.GetProperty("refused").GetBoolean());
        Assert.Contains("另一个", refused.GetProperty("error").GetString());
        other.Send("""{"id":2,"op":"query","text":"红烧肉 冰糖"}""");
        var hits = (await other.AnswerAsync(2)).GetProperty("hits");
        Assert.Equal(SemanticRoot.KeyOf("b"), hits[0].GetProperty("conversation").GetString());
        other.Send("""{"id":3,"op":"status"}""");
        Assert.False((await other.AnswerAsync(3)).GetProperty("builder").GetBoolean());

        // Once the first is gone, the other may build.
        builder.Dispose();
        other.Send("""{"id":4,"op":"build"}""");
        Assert.True((await other.AnswerAsync(4)).GetProperty("ok").GetBoolean());
        await other.EventAsync("built");
        other.CloseInput();
        await other.Exit.WaitAsync(Patience);
    }

    [Fact]
    public async Task AConversationDeletedSinceTheBuildIsNotFound()
    {
        var worker = new Running(_root.Service(new FakeEmbedder()));
        worker.Send("""{"id":1,"op":"build"}""");
        await worker.EventAsync("built");

        _root.Unlist("b");
        worker.Send("""{"id":2,"op":"query","text":"红烧肉 冰糖"}""");
        var hits = (await worker.AnswerAsync(2)).GetProperty("hits").EnumerateArray().ToList();
        Assert.NotEmpty(hits);
        Assert.DoesNotContain(hits, h => h.GetProperty("conversation").GetString() == SemanticRoot.KeyOf("b"));
        worker.CloseInput();
        await worker.Exit.WaitAsync(Patience);
    }

    [Fact]
    public async Task UnreadableLinesAreSkippedAndUnknownRequestsAnswered()
    {
        var worker = new Running(_root.Service(new FakeEmbedder()));
        worker.Send("this is not json");
        worker.Send("""{"op":"status"}""");
        worker.Send("""{"id":7,"op":"dance"}""");
        var unknown = await worker.AnswerAsync(7);
        Assert.False(unknown.GetProperty("ok").GetBoolean());
        Assert.Contains("不认识", unknown.GetProperty("error").GetString());
        worker.Send("""{"id":8,"op":"status"}""");
        Assert.True((await worker.AnswerAsync(8)).GetProperty("ok").GetBoolean());
        Assert.Equal(2, worker.Seen.Count);                     // nothing was said about the two broken lines
        worker.CloseInput();
        await worker.Exit.WaitAsync(Patience);
    }

    // ------------------------------------------------------------------ client

    [Fact]
    public async Task TheClientMatchesAnswersAndPassesOnTheBuildsEvents()
    {
        var workers = new InMemoryWorkers(_root, TimeSpan.FromMinutes(5));
        using var client = new SemanticClient(workers.Connect);
        var progress = new List<(int, int)>();
        var built = new TaskCompletionSource<SemanticBuilt>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Progress += (done, total) => { lock (progress) progress.Add((done, total)); };
        client.Built += b => built.TrySetResult(b);

        Assert.False(client.IsRunning);
        Assert.Equal(BuildStart.Started, await client.BuildAsync());
        var run = await built.Task.WaitAsync(Patience);
        Assert.Equal(4, run.Chunks);
        Assert.Equal(2, run.Conversations);
        lock (progress) Assert.Contains((0, 2), progress);

        var queries = await Task.WhenAll(client.QueryAsync("红烧肉 冰糖"), client.QueryAsync("GEMMA 关联分析"));
        Assert.Equal(SemanticRoot.KeyOf("b"), queries[0][0].Conversation);
        Assert.Equal(SemanticRoot.KeyOf("a"), queries[1][0].Conversation);
        Assert.Equal(Cook, queries[0][0].Chunks[0].Snippet);

        var status = await client.StatusAsync();
        Assert.Equal(4, status.Chunks);
        Assert.False(status.Building);
        Assert.Equal(1, workers.Started);
        Assert.True(client.IsRunning);

        await client.CloseAsync(Patience);
        Assert.False(client.IsRunning);
        Assert.Equal(0, await workers.Exits[0].WaitAsync(Patience));
    }

    [Fact]
    public async Task AWorkerThatEndedWhenIdleIsStartedAgainOnTheNextRequest()
    {
        // Long enough that a request reaches a worker before it ends idle, however busy the machine:
        // the client asks a second worker once, not a third.
        var workers = new InMemoryWorkers(_root, TimeSpan.FromMilliseconds(400));
        using var client = new SemanticClient(workers.Connect);
        var exited = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Exited += reason => exited.TrySetResult(reason);

        var status = await client.StatusAsync();
        Assert.Equal(0, status.Chunks);
        Assert.Null(await exited.Task.WaitAsync(Patience));          // ended by itself, no reason given
        Assert.False(client.IsRunning);

        Assert.Equal(0, (await client.StatusAsync()).Chunks);
        Assert.True(workers.Started >= 2);
    }

    [Fact]
    public async Task AWorkerThatCannotLoadItsModelSaysWhy()
    {
        const string reason = "没有找到向量模型文件夹：D:\\nowhere";
        var started = 0;
        using var client = new SemanticClient(() =>
        {
            Interlocked.Increment(ref started);
            var output = new LinePipe();
            output.Writer.WriteLine($$"""{"event":"fatal","error":{{JsonSerializer.Serialize(reason)}}}""");
            output.Writer.Dispose();
            return new SemanticConnection(new LinePipe().Writer, output.Reader, Task.CompletedTask, () => { });
        });
        var exited = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Exited += r => exited.TrySetResult(r);

        var error = await Assert.ThrowsAsync<SemanticUnavailableException>(() => client.QueryAsync("引物"));
        Assert.Equal(reason, error.Message);
        Assert.Equal(reason, await exited.Task.WaitAsync(Patience));
        Assert.Equal(1, started);                                    // not started again and again for one request
    }

    /// <summary>A worker that reads requests and answers none, ending when its input closes; <paramref name="received"/> is set on the first request.</summary>
    private static SemanticConnection Silent(TaskCompletionSource received)
    {
        var input = new LinePipe();
        var output = new LinePipe();
        var exited = Task.Run(async () =>
        {
            while (await input.Reader.ReadLineAsync() is not null) received.TrySetResult();
            output.Writer.Dispose();
        });
        return new SemanticConnection(input.Writer, output.Reader, exited, () => input.Writer.Dispose());
    }

    [Fact]
    public async Task AClosingClientStartsNoWorkerForWhatTheLastOneLeftWaiting()
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var another = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        using var client = new SemanticClient(() =>
        {
            if (Interlocked.Increment(ref started) > 1) another.TrySetResult();
            return Silent(received);
        });

        // A build asked of a worker still loading its model when the setting goes off.
        var build = client.BuildAsync();
        await received.Task.WaitAsync(Patience);
        await client.CloseAsync(Patience);
        Assert.Same(build, await Task.WhenAny(build, another.Task).WaitAsync(Patience));
        await Assert.ThrowsAsync<SemanticUnavailableException>(() => build);
        Assert.Equal(1, Volatile.Read(ref started));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.StatusAsync());
    }

    [Fact]
    public async Task EndingTheWorkerAsksWhatItLeftWaitingOfANewOneWithoutReportingAnExit()
    {
        var workers = new InMemoryWorkers(_root, TimeSpan.FromMinutes(5));
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = 1;
        // Made off any synchronization context, its events are raised as they happen: one not
        // raised by the time the worker's output has been read to its end is not raised at all.
        using var client = await Task.Run(() => new SemanticClient(() =>
            Interlocked.Exchange(ref first, 0) == 1 ? Silent(received) : workers.Connect()));
        var exits = 0;
        client.Exited += _ => Interlocked.Increment(ref exits);

        var status = client.StatusAsync();
        await received.Task.WaitAsync(Patience);
        Assert.True(client.IsRunning);
        await client.EndWorkerAsync(Patience);
        Assert.Equal(0, (await status.WaitAsync(Patience)).Chunks);     // answered by a new worker
        Assert.Equal(1, workers.Started);
        Assert.Equal(0, Volatile.Read(ref exits));
        Assert.True(client.IsRunning);
        await client.CloseAsync(Patience);
    }

    [Fact]
    public async Task DisposingTheClientEndsItsWorker()
    {
        var workers = new InMemoryWorkers(_root, TimeSpan.FromMinutes(5));
        var client = new SemanticClient(workers.Connect);
        await client.StatusAsync();
        client.Dispose();
        Assert.Equal(0, await workers.Exits[0].WaitAsync(Patience));
        Assert.Equal(0, workers.Kills);                 // ended because its input closed, not killed later
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.StatusAsync());
    }

    public void Dispose() => _root.Dispose();
}
