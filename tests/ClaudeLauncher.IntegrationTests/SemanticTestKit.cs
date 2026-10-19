using System.Text;
using System.Threading.Channels;
using ClaudeLauncher.Core.Contracts;
using ClaudeLauncher.Infrastructure.Catalog;
using ClaudeLauncher.Infrastructure.Search;
using ClaudeLauncher.Infrastructure.Semantic;

namespace ClaudeLauncher.IntegrationTests;

/// <summary>
/// An embedder for tests: each text a bag of its characters and character pairs, hashed into a few
/// dimensions and normalised — texts sharing words land close together, deterministically, with no
/// model. It counts what it embeds, can hold passages at a gate until the test lets them through,
/// and can run a step of the test's before each call.
/// </summary>
internal sealed class FakeEmbedder(string modelId = "fake:1", int dimensions = 64) : IEmbedder
{
    private int _calls, _texts, _queries;

    public string ModelId { get; } = modelId;
    public int Dimensions { get; } = dimensions;

    /// <summary>Calls that embedded passages (a build), and how many passages in all.</summary>
    public int Calls => Volatile.Read(ref _calls);
    public int Texts => Volatile.Read(ref _texts);
    public int Queries => Volatile.Read(ref _queries);

    /// <summary>When set, a build's call waits here (after <see cref="Entered"/> is set) until released.</summary>
    public SemaphoreSlim? Gate { get; set; }

    /// <summary>Set when a build's call reaches the gate.</summary>
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Set when the build held at the gate is told to stop.</summary>
    public TaskCompletionSource StopSeen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Runs before each build call with the call's number (1, 2, …).</summary>
    public Action<int>? BeforeCall { get; set; }

    /// <summary>Overrides the vector of a passage, by its text.</summary>
    public Func<string, float[]?>? Fixed { get; set; }

    public bool Disposed { get; private set; }

    public float[][] Embed(IReadOnlyList<string> texts, bool query, CancellationToken cancellation = default)
    {
        if (query)
        {
            Interlocked.Increment(ref _queries);
        }
        else
        {
            var call = Interlocked.Increment(ref _calls);
            BeforeCall?.Invoke(call);
            if (Gate is { } gate)
            {
                Entered.TrySetResult();
                using var stop = cancellation.Register(() => StopSeen.TrySetResult());
                gate.Wait(CancellationToken.None);
            }
            cancellation.ThrowIfCancellationRequested();
            Interlocked.Add(ref _texts, texts.Count);
        }
        return texts.Select(t => Fixed?.Invoke(t) ?? Vector(t, Dimensions)).ToArray();
    }

    public static float[] Vector(string text, int dimensions)
    {
        var v = new float[dimensions];
        var s = text.ToLowerInvariant();
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsWhiteSpace(s[i]) || char.IsPunctuation(s[i])) continue;
            v[Bucket(s[i].ToString(), dimensions)] += 1;
            if (i + 1 < s.Length && !char.IsWhiteSpace(s[i + 1])) v[Bucket(s.Substring(i, 2), dimensions)] += 2;
        }
        var norm = Math.Sqrt(v.Sum(x => (double)x * x));
        if (norm > 0)
            for (var i = 0; i < v.Length; i++) v[i] = (float)(v[i] / norm);
        return v;
    }

    private static int Bucket(string piece, int dimensions)
    {
        var hash = 17;
        foreach (var c in piece) hash = unchecked(hash * 31 + c);
        return (int)((uint)hash % (uint)dimensions);
    }

    public void Dispose() => Disposed = true;
}

/// <summary>
/// One direction of a pipe of lines, in memory: what is written to <see cref="Writer"/> is read
/// from <see cref="Reader"/>, which blocks until a line comes and ends when the writer is closed.
/// </summary>
internal sealed class LinePipe
{
    private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();

    public LinePipe()
    {
        Writer = new LineWriter(_lines.Writer);
        Reader = new LineReader(_lines.Reader);
    }

    public TextWriter Writer { get; }
    public TextReader Reader { get; }

    private sealed class LineWriter(ChannelWriter<string> lines) : TextWriter
    {
        private readonly StringBuilder _line = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            lock (_line)
            {
                if (value == '\n')
                {
                    lines.TryWrite(_line.ToString().TrimEnd('\r'));
                    _line.Clear();
                }
                else
                {
                    _line.Append(value);
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) lines.TryComplete();
            base.Dispose(disposing);
        }
    }

    private sealed class LineReader(ChannelReader<string> lines) : TextReader
    {
        public override string? ReadLine() => ReadLineAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await lines.ReadAsync(cancellationToken);
            }
            catch (ChannelClosedException)
            {
                return null;
            }
        }

        public override Task<string?> ReadLineAsync() => ReadLineAsync(CancellationToken.None).AsTask();
    }
}

/// <summary>
/// Workers serving in memory for a <see cref="SemanticClient"/>, each on a service the test makes,
/// counting how many were started.
/// </summary>
internal sealed class InMemoryWorkers(Func<SemanticService> service, TimeSpan idle)
{
    private int _started, _kills;

    public InMemoryWorkers(SemanticRoot root, TimeSpan idle) : this(() => root.Service(new FakeEmbedder()), idle)
    {
    }

    public int Started => Volatile.Read(ref _started);

    /// <summary>Workers ended by force rather than by their input closing.</summary>
    public int Kills => Volatile.Read(ref _kills);
    public List<Task<int>> Exits { get; } = [];

    public SemanticConnection Connect()
    {
        Interlocked.Increment(ref _started);
        var toWorker = new LinePipe();
        var fromWorker = new LinePipe();
        var exit = Task.Run(() =>
        {
            try
            {
                return SemanticWorker.Serve(toWorker.Reader, fromWorker.Writer, service(), idle);
            }
            finally
            {
                fromWorker.Writer.Dispose();       // a process's stdout closes as it ends
            }
        });
        lock (Exits) Exits.Add(exit);
        return new SemanticConnection(toWorker.Writer, fromWorker.Reader, exit, () =>
        {
            Interlocked.Increment(ref _kills);
            toWorker.Writer.Dispose();
        });
    }
}

/// <summary>
/// A thread of its own that runs what is posted to it one at a time, in order — the window's
/// thread, as far as code that captures the synchronization context can tell.
/// </summary>
internal sealed class WindowThread : IDisposable
{
    private readonly System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
    private readonly Thread _thread;

    public WindowThread()
    {
        var context = new Context(this);
        _thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(context);
            foreach (var (callback, state) in _queue.GetConsumingEnumerable()) callback(state);
        }) { IsBackground = true, Name = "test window" };
        _thread.Start();
    }

    /// <summary>Runs <paramref name="work"/> on this thread; its awaits come back here.</summary>
    public Task<T> Run<T>(Func<Task<T>> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add((async _ =>
        {
            try
            {
                done.SetResult(await work());
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        }, null));
        return done.Task;
    }

    public Task<T> Run<T>(Func<T> work) => Run(() => Task.FromResult(work()));

    public void Dispose() => _queue.CompleteAdding();

    private sealed class Context(WindowThread thread) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
            try
            {
                thread._queue.Add((d, state));
            }
            catch (InvalidOperationException)
            {
                // The test is over.
            }
        }

        public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();
    }
}

/// <summary>
/// A data root of its own in a temp folder: a catalog listing conversations, transcripts of them,
/// the full-text index read from those, and the path of the semantic index beside them.
/// </summary>
internal sealed class SemanticRoot : IDisposable
{
    public string Folder { get; }
    public string CatalogPath => Path.Combine(Folder, "catalog.sqlite");
    public string ContentPath => Path.Combine(Folder, "search-index.sqlite");
    public string IndexPath => Path.Combine(Folder, "semantic-index.sqlite");

    private readonly Dictionary<string, string> _transcripts = new(StringComparer.Ordinal);

    public SemanticRoot()
    {
        Folder = Path.Combine(Path.GetTempPath(), "launcher-semantic", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(Folder);
    }

    public static string KeyOf(string id) => ContentIndex.KeyOf(new ConversationKey(ConversationKey.CliCode, "acc", "org", id));

    public static string User(string text) =>
        "{\"type\":\"user\",\"timestamp\":\"2026-10-01T08:00:00Z\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"" + text + "\"}]}}";

    public static string Assistant(string text) =>
        "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"" + text + "\"}]}}";

    /// <summary>Writes (or rewrites) a conversation's transcript: its messages alternating user and assistant.</summary>
    public void Write(string id, params string[] messages)
    {
        var path = Path.Combine(Folder, id + ".jsonl");
        var lines = messages.Select((m, i) => i % 2 == 0 ? User(m) : Assistant(m));
        File.WriteAllText(path, string.Join('\n', lines) + '\n', new UTF8Encoding(false));
        _transcripts[id] = path;
    }

    /// <summary>Adds messages to the end of a conversation's transcript, continuing the alternation.</summary>
    public void Append(string id, int already, params string[] messages)
    {
        var lines = messages.Select((m, i) => (already + i) % 2 == 0 ? User(m) : Assistant(m));
        File.AppendAllText(_transcripts[id], string.Join('\n', lines) + '\n', new UTF8Encoding(false));
    }

    /// <summary>Adds a tool call to the end of a conversation's transcript: it grows, and nothing is said.</summary>
    public void AppendToolCall(string id) => File.AppendAllText(_transcripts[id],
        "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"id\":\"toolu_1\",\"name\":\"Bash\",\"input\":{\"command\":\"ls\"}}]}}\n",
        new UTF8Encoding(false));

    /// <summary>The full-text index's fingerprint, as the window's check reads it.</summary>
    public string ContentFingerprint()
    {
        using var reader = ContentIndexReader.Open(ContentPath) ?? throw new InvalidOperationException("no full-text index");
        return reader.Fingerprint();
    }

    /// <summary>Leaves the full-text index there but empty, as content search has it just after being switched on.</summary>
    public void EmptyContent()
    {
        DropContent();
        using var _ = new ContentIndex(ContentPath);
    }

    /// <summary>
    /// Brings the full-text index up to date with the transcripts written so far — all of them, or
    /// only these, as a pass part way through has — never dropping the others.
    /// </summary>
    public void IndexContent(params string[] only)
    {
        using var index = new ContentIndex(ContentPath);
        var ids = only.Length > 0 ? only : [.. _transcripts.Keys];
        index.Update(ids.Select(id => new ContentIndex.Source(KeyOf(id), _transcripts[id])).ToList(),
            listed: _transcripts.Keys.Select(KeyOf).ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>Deletes the full-text index, as switching content search off does.</summary>
    public void DropContent()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            if (File.Exists(ContentPath + suffix)) File.Delete(ContentPath + suffix);
    }

    /// <summary>Lists conversations in the catalog, the first the latest.</summary>
    public void List(params string[] ids)
    {
        using var catalog = SqliteCatalog.OpenOrRebuild(CatalogPath,
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "CatalogSchema.sql")), out _);
        catalog.UpsertRange(ids.Select((id, i) => new SessionMetadata
        {
            Key = new ConversationKey(ConversationKey.CliCode, "acc", "org", id),
            Title = "会话 " + id,
            SearchKey = SearchKeys.Normalize("会话 " + id),
            SourceRevision = "1:1",
            LastActivityUtc = DateTimeOffset.UtcNow.AddHours(-i),
            NativeArchiveState = NativeArchiveState.Unsupported,
            SourceCoverage = SourceCoverage.Resolved,
        }));
    }

    /// <summary>Deletes conversations from the catalog, as the user's delete does.</summary>
    public void Unlist(params string[] ids)
    {
        using var catalog = SqliteCatalog.OpenOrRebuild(CatalogPath,
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "CatalogSchema.sql")), out _);
        catalog.Delete(ids.Select(id => new ConversationKey(ConversationKey.CliCode, "acc", "org", id)));
    }

    public IDialogueSource Dialogue => new ContentIndexDialogue(ContentPath);

    public SemanticService Service(IEmbedder embedder) =>
        SemanticService.ForFiles(CatalogPath, ContentPath, IndexPath, () => embedder);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Folder, recursive: true); } catch { /* temp dir */ }
    }
}
