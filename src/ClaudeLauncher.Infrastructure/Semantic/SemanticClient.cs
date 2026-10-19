using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ClaudeLauncher.Infrastructure.Semantic;

/// <summary>A running worker as its client sees it: where requests go, where answers come from, how to end it.</summary>
/// <param name="Exited">Completes when the worker has ended.</param>
/// <param name="Kill">Ends it at once, for one that does not end by itself.</param>
public sealed record SemanticConnection(TextWriter Input, TextReader Output, Task Exited, Action Kill);

/// <summary>A finished build, as the worker reports it.</summary>
/// <param name="Source">The full-text index's fingerprint the build read: equal to the current one, nothing new to embed.</param>
/// <param name="Model">The id of the model the worker embedded with — the one it loaded when it started.</param>
public sealed record SemanticBuilt(long Chunks, int Conversations, int Updated, long Embedded, int Removed, string? Source,
    string? Model);

/// <summary>
/// The worker process from the window's side (or any other process's): started on the first
/// request, and again on the next one after it ended by itself when idle; requests matched to
/// their answers; the build's events raised on the thread that made the client (the window's),
/// like <see cref="Progress{T}"/>. Never waits for the worker on that thread.
/// </summary>
public sealed class SemanticClient : IDisposable
{
    private readonly Func<SemanticConnection> _connect;
    private readonly SynchronizationContext? _context;
    private readonly SemaphoreSlim _starting = new(1, 1);
    private readonly Lock _lock = new();
    private Worker? _worker;
    private long _nextId;
    private bool _disposed;

    /// <summary>A build's conversations done, of those to do.</summary>
    public event Action<int, int>? Progress;

    public event Action<SemanticBuilt>? Built;

    /// <summary>A build was stopped, with how far it got.</summary>
    public event Action<int, int>? Stopped;

    /// <summary>A build could not go on; the words are for the user.</summary>
    public event Action<string>? Failed;

    /// <summary>
    /// The worker ended: by itself when idle (null), or after reporting the model cannot be loaded
    /// (the reason). Not raised for a worker the client ended (<see cref="EndWorkerAsync"/>,
    /// <see cref="CloseAsync"/>, <see cref="Dispose"/>).
    /// </summary>
    public event Action<string?>? Exited;

    /// <param name="connect">Starts a worker; called off the calling thread, on the first request and after one ended.</param>
    public SemanticClient(Func<SemanticConnection> connect)
    {
        ArgumentNullException.ThrowIfNull(connect);
        _connect = connect;
        _context = SynchronizationContext.Current;
    }

    /// <summary>
    /// The client of a worker process: <paramref name="executable"/> (this program) started with
    /// <c>--semantic-worker</c>, the data root and model folder given explicitly — a child never
    /// works out a root of its own — below normal priority, without a window.
    /// </summary>
    public static SemanticClient ForWorker(string executable, string dataRoot, string modelFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelFolder);
        return new SemanticClient(() => StartProcess(executable,
            [SemanticWorker.Argument, SemanticWorker.DataRootArgument, dataRoot, SemanticWorker.ModelArgument, modelFolder]));
    }

    /// <summary>Starts a worker process with its stdio piped, in UTF-8.</summary>
    public static SemanticConnection StartProcess(string executable, IEnumerable<string> arguments)
    {
        var utf8 = new UTF8Encoding(false);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = utf8,
            StandardOutputEncoding = utf8,
            StandardErrorEncoding = utf8,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        var process = Process.Start(start) ?? throw new InvalidOperationException("语义搜索进程没有启动");
        try
        {
            // Below the window, always; the build lowers itself further.
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // Already ended (it says why on stdout), or not ours to change: it runs as it is.
        }
        process.StandardInput.AutoFlush = false;
        process.StandardInput.NewLine = "\n";
        // Read whatever it says on stderr, or a full pipe would stall it.
        _ = process.StandardError.ReadToEndAsync();
        var exited = process.WaitForExitAsync().ContinueWith(_ => process.Dispose(), TaskScheduler.Default);
        return new SemanticConnection(process.StandardInput, process.StandardOutput, exited, () =>
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                // Gone already.
            }
        });
    }

    /// <summary>Whether a worker is running now, and not being ended.</summary>
    public bool IsRunning
    {
        get
        {
            lock (_lock) return _worker is { Ended: false, Ending: false };
        }
    }

    /// <summary>Closed: no worker is started any more, nor started again for a request one left waiting.</summary>
    private bool Closed
    {
        get
        {
            lock (_lock) return _disposed;
        }
    }

    // --------------------------------------------------------------- requests

    /// <summary>Asks for a build; its progress and end come as events.</summary>
    /// <exception cref="SemanticUnavailableException">The worker could not start or failed.</exception>
    public async Task<BuildStart> BuildAsync(CancellationToken cancellation = default)
    {
        var reply = await SendAsync("build", null, cancellation).ConfigureAwait(false);
        if (reply.TryGetProperty("refused", out var refused) && refused.ValueKind == JsonValueKind.True) return BuildStart.Refused;
        Ensure(reply);
        return reply.TryGetProperty("started", out var started) && started.ValueKind == JsonValueKind.True
            ? BuildStart.Started : BuildStart.AlreadyBuilding;
    }

    /// <summary>Stops a running build; false when none was running or no worker is.</summary>
    public async Task<bool> StopAsync(CancellationToken cancellation = default)
    {
        if (!IsRunning) return false;
        var reply = await SendAsync("stop", null, cancellation).ConfigureAwait(false);
        Ensure(reply);
        return reply.TryGetProperty("stopping", out var stopping) && stopping.ValueKind == JsonValueKind.True;
    }

    /// <summary>The conversations closest in meaning to <paramref name="text"/>, closest first.</summary>
    /// <exception cref="SemanticUnavailableException">The worker could not start, or could not search; the message says why.</exception>
    public async Task<IReadOnlyList<SemanticHit>> QueryAsync(string text, int limit = 20, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        var reply = await SendAsync("query", w =>
        {
            w.WriteString("text", text);
            w.WriteNumber("limit", limit);
        }, cancellation).ConfigureAwait(false);
        Ensure(reply);
        var hits = new List<SemanticHit>();
        if (!reply.TryGetProperty("hits", out var found) || found.ValueKind != JsonValueKind.Array) return hits;
        foreach (var hit in found.EnumerateArray())
        {
            var chunks = new List<SemanticChunkHit>();
            if (hit.TryGetProperty("chunks", out var pieces) && pieces.ValueKind == JsonValueKind.Array)
                foreach (var piece in pieces.EnumerateArray())
                    chunks.Add(new SemanticChunkHit(
                        (float)Number(piece, "score"), Text(piece, "role") ?? "user", Text(piece, "stamp"),
                        (int)Number(piece, "ordinal"), (int)Number(piece, "start"), Text(piece, "snippet") ?? ""));
            if (Text(hit, "conversation") is { } conversation)
                hits.Add(new SemanticHit(conversation, (float)Number(hit, "score"), chunks));
        }
        return hits;
    }

    /// <summary>What the index holds and whether a build runs — starting the worker if need be.</summary>
    public async Task<SemanticStatus> StatusAsync(CancellationToken cancellation = default)
    {
        var reply = await SendAsync("status", null, cancellation).ConfigureAwait(false);
        Ensure(reply);
        return new SemanticStatus(
            (int)Number(reply, "conversations"), (long)Number(reply, "chunks"),
            Text(reply, "updated") is { } updated
            && DateTimeOffset.TryParse(updated, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null,
            Flag(reply, "building"), (int)Number(reply, "done"), (int)Number(reply, "total"),
            Text(reply, "model") ?? "", Flag(reply, "sameModel"), Flag(reply, "builder"));
    }

    private static void Ensure(JsonElement reply)
    {
        if (!Flag(reply, "ok"))
            throw new SemanticUnavailableException(Text(reply, "error") ?? "语义搜索进程没有完成这个请求");
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0;

    private static bool Flag(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>
    /// Sends one request and waits for its answer. A worker that ended by itself as the request
    /// went out — its idle time ran out just then — is started again and asked once more.
    /// </summary>
    private async Task<JsonElement> SendAsync(string op, Action<Utf8JsonWriter>? fields, CancellationToken cancellation,
        bool again = true)
    {
        var worker = await RunningAsync().ConfigureAwait(false);
        var id = Interlocked.Increment(ref _nextId);
        var answer = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            if (!worker.Ended) worker.Pending[id] = answer;
            else answer.TrySetException(new WorkerEndedException(worker.Fatal));
        }

        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("id", id);
            w.WriteString("op", op);
            fields?.Invoke(w);
            w.WriteEndObject();
        }
        var line = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        if (!answer.Task.IsCompleted)
        {
            try
            {
                await worker.WriteAsync(line).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // Gone, or going: its output ends next, and every request still waiting is told
                // why it ended — a model that could not be loaded is said there, not lost here.
            }
        }

        try
        {
            using (cancellation.Register(() => answer.TrySetCanceled(cancellation)))
                return await answer.Task.ConfigureAwait(false);
        }
        catch (WorkerEndedException ended) when (ended.Fatal is null && again && !Closed)
        {
            return await SendAsync(op, fields, cancellation, again: false).ConfigureAwait(false);
        }
        catch (WorkerEndedException ended)
        {
            throw new SemanticUnavailableException(ended.Fatal ?? "语义搜索进程已退出");
        }
        finally
        {
            lock (_lock) worker.Pending.Remove(id);
        }
    }

    /// <summary>The worker, started when none runs.</summary>
    private async Task<Worker> RunningAsync()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_worker is { Ended: false, Ending: false } running) return running;
        }
        await _starting.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_worker is { Ended: false, Ending: false } running) return running;
            }
            SemanticConnection connection;
            try
            {
                // Starting a process takes a moment: never on the caller's (the window's) thread.
                connection = await Task.Run(_connect).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
            {
                throw new SemanticUnavailableException("语义搜索进程启动不了：" + ex.Message, ex);
            }
            var worker = new Worker(this, connection);
            lock (_lock)
            {
                if (_disposed)
                {
                    worker.Close();
                    throw new ObjectDisposedException(nameof(SemanticClient));
                }
                _worker = worker;
            }
            worker.StartReading();
            return worker;
        }
        finally
        {
            _starting.Release();
        }
    }

    // ---------------------------------------------------------------- ending

    /// <summary>
    /// Ends the worker for good and waits for it, up to <paramref name="wait"/>, then kills it: for
    /// when its files are to be deleted next. From the moment it is called no worker is started —
    /// not even again for a request the ending one leaves waiting. Not for the window's thread to
    /// wait on synchronously.
    /// </summary>
    public async Task CloseAsync(TimeSpan wait)
    {
        lock (_lock) _disposed = true;
        await EndAsync(wait).ConfigureAwait(false);
    }

    /// <summary>
    /// Ends the running worker, if any, and waits for it as <see cref="CloseAsync"/> does, but the
    /// client stays open: the next request starts a new worker, and a request the ending one leaves
    /// waiting is asked again of that. For a worker that still holds a model whose files changed
    /// after it loaded them.
    /// </summary>
    public Task EndWorkerAsync(TimeSpan wait) => EndAsync(wait);

    private async Task EndAsync(TimeSpan wait)
    {
        Worker? worker;
        lock (_lock)
        {
            worker = _worker;
            if (worker is null) return;
            worker.Ending = true;
        }
        worker.Close();
        if (await Task.WhenAny(worker.Connection.Exited, Task.Delay(wait)).ConfigureAwait(false) != worker.Connection.Exited)
        {
            worker.Connection.Kill();
            await Task.WhenAny(worker.Connection.Exited, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
        }
        // Its output ends with it: once that is read, nothing more is waited for.
        await Task.WhenAny(worker.Reading, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
    }

    /// <summary>
    /// Closes the worker's input — it stops a build and ends by itself — and kills it if it is
    /// still there a few seconds later. Returns at once.
    /// </summary>
    public void Dispose()
    {
        Worker? worker;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            worker = _worker;
            if (worker is null) return;
            worker.Ending = true;
        }
        worker.Close();
        _ = Task.WhenAny(worker.Connection.Exited, Task.Delay(TimeSpan.FromSeconds(10))).ContinueWith(done =>
        {
            if (done.Result != worker.Connection.Exited) worker.Connection.Kill();
        }, TaskScheduler.Default);
    }

    private void Raise(Action raise)
    {
        if (_context is null) raise();
        else _context.Post(_ => raise(), null);
    }

    private sealed class WorkerEndedException(string? fatal) : Exception("语义搜索进程已退出")
    {
        public string? Fatal { get; } = fatal;
    }

    /// <summary>One worker process: its connection, the requests waiting for an answer, the reading of its output.</summary>
    private sealed class Worker(SemanticClient client, SemanticConnection connection)
    {
        private readonly SemaphoreSlim _write = new(1, 1);

        public SemanticConnection Connection { get; } = connection;
        public Dictionary<long, TaskCompletionSource<JsonElement>> Pending { get; } = [];
        public bool Ended { get; private set; }
        public string? Fatal { get; private set; }

        /// <summary>Being ended by the client: given no new requests, and its end is not reported. Under the client's lock.</summary>
        public bool Ending { get; set; }

        public async Task WriteAsync(string line)
        {
            await _write.WaitAsync().ConfigureAwait(false);
            try
            {
                await Connection.Input.WriteLineAsync(line).ConfigureAwait(false);
                await Connection.Input.FlushAsync().ConfigureAwait(false);
            }
            finally
            {
                _write.Release();
            }
        }

        /// <summary>Reading its output; completes when the output ends.</summary>
        public Task Reading { get; private set; } = Task.CompletedTask;

        public void StartReading() => Reading = Task.Run(ReadAsync);

        private async Task ReadAsync()
        {
            try
            {
                while (await Connection.Output.ReadLineAsync().ConfigureAwait(false) is { } line)
                    Dispatch(line);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // The pipe broke: the worker is gone.
            }
            End(Fatal);
        }

        private void Dispatch(string line)
        {
            JsonElement message;
            try
            {
                using var document = JsonDocument.Parse(line);
                message = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                return;     // not a message: nothing is said that way
            }
            if (message.ValueKind != JsonValueKind.Object) return;

            if (message.TryGetProperty("id", out var idValue) && idValue.TryGetInt64(out var id))
            {
                TaskCompletionSource<JsonElement>? answer;
                lock (client._lock) Pending.TryGetValue(id, out answer);
                answer?.TrySetResult(message);
                return;
            }
            switch (Text(message, "event"))
            {
                case "progress":
                    var (done, total) = ((int)Number(message, "done"), (int)Number(message, "total"));
                    client.Raise(() => client.Progress?.Invoke(done, total));
                    break;
                case "built":
                    var built = new SemanticBuilt((long)Number(message, "chunks"), (int)Number(message, "conversations"),
                        (int)Number(message, "updated"), (long)Number(message, "embedded"), (int)Number(message, "removed"),
                        Text(message, "source"), Text(message, "model"));
                    client.Raise(() => client.Built?.Invoke(built));
                    break;
                case "stopped":
                    var (at, of) = ((int)Number(message, "done"), (int)Number(message, "total"));
                    client.Raise(() => client.Stopped?.Invoke(at, of));
                    break;
                case "failed":
                    var error = Text(message, "error") ?? "建立语义索引时出错";
                    client.Raise(() => client.Failed?.Invoke(error));
                    break;
                case "fatal":
                    Fatal = Text(message, "error") ?? "语义搜索进程启动不了";
                    break;
            }
        }

        /// <summary>No more answers come: every request still waiting is told, and the client hears it once — unless it ended this worker itself.</summary>
        public void End(string? fatal)
        {
            List<TaskCompletionSource<JsonElement>> waiting;
            bool ending;
            lock (client._lock)
            {
                if (Ended) return;
                Ended = true;
                Fatal ??= fatal;
                waiting = [.. Pending.Values];
                ending = Ending;
            }
            foreach (var answer in waiting) answer.TrySetException(new WorkerEndedException(Fatal));
            if (ending) return;
            var reason = Fatal;
            client.Raise(() => client.Exited?.Invoke(reason));
        }

        /// <summary>Closes its input: it stops what it is doing and ends.</summary>
        public void Close()
        {
            try
            {
                Connection.Input.Close();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // Its end of the pipe is gone already.
            }
        }
    }
}
