using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ClaudeLauncher.Infrastructure.Semantic;

/// <summary>
/// The <c>--semantic-worker</c> process: the window's child that builds and searches the local
/// semantic index, on the data root its parent hands it, with the embedding model loaded from the
/// optional component. Started without WPF.
///
/// <para>It speaks JSON lines on stdin/stdout, UTF-8, one compact object per line and nothing else
/// on stdout. Requests: <c>{"id":n,"op":"build"}</c>, <c>{"id":n,"op":"stop"}</c>,
/// <c>{"id":n,"op":"query","text":"…","limit":20}</c>, <c>{"id":n,"op":"status"}</c>. Every request
/// is answered <c>{"id":n,"ok":true,…}</c> or <c>{"id":n,"ok":false,"error":"…"}</c>. Events:
/// <c>{"event":"progress","done":d,"total":t}</c>, <c>{"event":"built",…,"model":"…"}</c> (the model it embedded with),
/// <c>{"event":"stopped",…}</c>, <c>{"event":"failed","error":"…"}</c>, and
/// <c>{"event":"fatal","error":"…"}</c> before an exit for a model that cannot be loaded.</para>
///
/// <para>It ends when stdin closes (the window closed or died), after <see cref="IdleTimeout"/>
/// without a request while not building, or when the model cannot be loaded.</para>
/// </summary>
public static class SemanticWorker
{
    public const string Argument = "--semantic-worker";
    public const string DataRootArgument = "--data-root";
    public const string ModelArgument = "--model";

    /// <summary>How long the worker stays with nothing to do: the model's memory goes back when it ends.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);

    /// <summary>The exit code after reporting a model or data root that cannot be used.</summary>
    public const int FatalExitCode = 3;

    /// <returns>The process exit code.</returns>
    public static int Run(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        // Stdout belongs to the protocol: raw UTF-8, whatever the console's code page.
        var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = false, NewLine = "\n" };
        try
        {
            string? root = null, model = null;
            for (var i = 1; i < args.Length - 1; i++)
            {
                if (args[i] == DataRootArgument) root = args[++i];
                else if (args[i] == ModelArgument) model = args[++i];
            }

            IEmbedder embedder;
            try
            {
                // The window's own root — the old one while a move is unfinished — never one resolved here.
                if (root is not null) LauncherPaths.UseDataRoot(root);
                DataRootMigrator.PrepareDataRoot(mayMove: false);
                embedder = EmbedderLoader.Load(model ?? LauncherPaths.DefaultEmbeddingModelFolder);
            }
            catch (Exception ex) when (ex is SemanticUnavailableException or ArgumentException or InvalidOperationException)
            {
                new Session(output).Event("fatal", w => w.WriteString("error", ex.Message));
                return FatalExitCode;
            }

            var service = SemanticService.ForFiles(LauncherPaths.CatalogDatabase, LauncherPaths.ContentIndexDatabase,
                LauncherPaths.SemanticIndexDatabase, () => embedder);
            return Serve(input, output, service, IdleTimeout);
        }
        catch (Exception ex)
        {
            // Never on stdout: the window would read it as a broken message.
            Console.Error.WriteLine("semantic worker: " + ex);
            return 1;
        }
    }

    /// <summary>
    /// Answers requests from <paramref name="input"/> on <paramref name="output"/> until the input
    /// ends or nothing was asked for <paramref name="idleTimeout"/> while not building; then stops a
    /// running build, waits for searches in hand, and disposes <paramref name="service"/>.
    /// </summary>
    /// <returns>0: the exit code.</returns>
    public static int Serve(TextReader input, TextWriter output, SemanticService service, TimeSpan idleTimeout)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(service);
        var session = new Session(output);
        try
        {
            session.Attach(service);
            return Task.Run(() => session.RunAsync(input, service, idleTimeout)).GetAwaiter().GetResult();
        }
        finally
        {
            session.WaitForQueries();
            service.Dispose();
        }
    }

    /// <summary>One run of the protocol: the writer, the requests in hand, when the last thing happened.</summary>
    private sealed class Session(TextWriter output)
    {
        private readonly Lock _write = new();
        private readonly List<Task> _queries = [];
        private long _lastActivity = Stopwatch.GetTimestamp();
        private long _lastProgress;

        public void Attach(SemanticService service)
        {
            service.Progress += (done, total) =>
            {
                // A few a second at most; the first and the last always.
                var now = Stopwatch.GetTimestamp();
                if (done != 0 && done != total && Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastProgress), now) < TimeSpan.FromMilliseconds(250))
                    return;
                Interlocked.Exchange(ref _lastProgress, now);
                Event("progress", w =>
                {
                    w.WriteNumber("done", done);
                    w.WriteNumber("total", total);
                });
            };
            service.Built += run =>
            {
                Touch();
                Event("built", w =>
                {
                    w.WriteNumber("chunks", run.Chunks);
                    w.WriteNumber("conversations", run.Conversations);
                    w.WriteNumber("updated", run.Updated);
                    w.WriteNumber("embedded", run.Embedded);
                    w.WriteNumber("removed", run.Removed);
                    w.WriteString("source", run.Source);
                    w.WriteString("model", run.Model);
                });
            };
            service.Stopped += (done, total) =>
            {
                Touch();
                Event("stopped", w =>
                {
                    w.WriteNumber("done", done);
                    w.WriteNumber("total", total);
                });
            };
            service.Failed += error =>
            {
                Touch();
                Event("failed", w => w.WriteString("error", error));
            };
        }

        private void Touch() => Interlocked.Exchange(ref _lastActivity, Stopwatch.GetTimestamp());

        public async Task<int> RunAsync(TextReader input, SemanticService service, TimeSpan idleTimeout)
        {
            var pending = input.ReadLineAsync();
            while (true)
            {
                var idle = Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastActivity));
                var wait = idleTimeout - idle;
                if (wait <= TimeSpan.Zero)
                {
                    // Idle: unless a build or a search is still running, the model's memory goes back now.
                    if (!service.IsBuilding && QueriesInHand() == 0) return 0;
                    Touch();
                    continue;
                }
                if (await Task.WhenAny(pending, Task.Delay(wait)).ConfigureAwait(false) != pending) continue;
                var line = await pending.ConfigureAwait(false);
                if (line is null) return 0;     // the window closed its end
                Touch();
                pending = input.ReadLineAsync();
                if (!string.IsNullOrWhiteSpace(line)) await HandleAsync(line, service).ConfigureAwait(false);
            }
        }

        private async Task HandleAsync(string line, SemanticService service)
        {
            long? id = null;
            string? op = null, text = null;
            var limit = 20;
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("id", out var idValue) && idValue.TryGetInt64(out var n)) id = n;
                    if (root.TryGetProperty("op", out var opValue) && opValue.ValueKind == JsonValueKind.String) op = opValue.GetString();
                    if (root.TryGetProperty("text", out var textValue) && textValue.ValueKind == JsonValueKind.String) text = textValue.GetString();
                    if (root.TryGetProperty("limit", out var limitValue) && limitValue.TryGetInt32(out var l)) limit = l;
                }
            }
            catch (JsonException)
            {
                Console.Error.WriteLine("semantic worker: a line that is not JSON was ignored");
                return;
            }
            if (id is not { } requestId)
            {
                Console.Error.WriteLine("semantic worker: a request without an id was ignored");
                return;
            }

            switch (op)
            {
                case "build":
                    var start = await service.StartBuildAsync().ConfigureAwait(false);
                    if (start == BuildStart.Refused)
                        Fail(requestId, "另一个会话管家进程正在建立这个数据目录的语义索引，这里只能搜索", w => w.WriteBoolean("refused", true));
                    else
                        Answer(requestId, w =>
                        {
                            w.WriteBoolean("started", start == BuildStart.Started);
                            w.WriteBoolean("building", true);
                        });
                    break;
                case "stop":
                    var stopping = service.StopBuild();
                    Answer(requestId, w => w.WriteBoolean("stopping", stopping));
                    break;
                case "status":
                    try
                    {
                        var status = service.Status();
                        Answer(requestId, w => WriteStatus(w, status));
                    }
                    catch (Exception ex) when (ex is SemanticUnavailableException or IOException or InvalidOperationException)
                    {
                        Fail(requestId, SemanticService.Describe(ex));
                    }
                    break;
                case "query":
                    var query = text ?? "";
                    // On a thread of normal priority, beside a build: the person typed it and waits.
                    lock (_queries)
                    {
                        _queries.RemoveAll(t => t.IsCompleted);
                        _queries.Add(Task.Run(() => Query(requestId, query, limit, service)));
                    }
                    break;
                default:
                    Fail(requestId, $"不认识的请求：{op}");
                    break;
            }
        }

        private void Query(long id, string text, int limit, SemanticService service)
        {
            try
            {
                var hits = service.Query(text, limit);
                Answer(id, w => WriteHits(w, hits));
            }
            catch (Exception ex)
            {
                Fail(id, ex is SemanticUnavailableException ? ex.Message : "搜索时出错：" + ex.Message);
            }
        }

        private int QueriesInHand()
        {
            lock (_queries) return _queries.Count(t => !t.IsCompleted);
        }

        public void WaitForQueries()
        {
            Task[] inHand;
            lock (_queries) inHand = [.. _queries];
            try
            {
                Task.WaitAll(inHand, TimeSpan.FromSeconds(10));
            }
            catch (AggregateException)
            {
                // Each search answers its own failure.
            }
        }

        private static void WriteStatus(Utf8JsonWriter w, SemanticStatus status)
        {
            w.WriteNumber("conversations", status.Conversations);
            w.WriteNumber("chunks", status.Chunks);
            if (status.Updated is { } updated) w.WriteString("updated", updated.ToString("O", CultureInfo.InvariantCulture));
            else w.WriteNull("updated");
            w.WriteBoolean("building", status.Building);
            w.WriteNumber("done", status.Done);
            w.WriteNumber("total", status.Total);
            w.WriteString("model", status.ModelId);
            w.WriteBoolean("sameModel", status.SameModel);
            w.WriteBoolean("builder", status.Builder);
        }

        private static void WriteHits(Utf8JsonWriter w, IReadOnlyList<SemanticHit> hits)
        {
            w.WriteStartArray("hits");
            foreach (var hit in hits)
            {
                w.WriteStartObject();
                w.WriteString("conversation", hit.Conversation);
                w.WriteNumber("score", Math.Round(hit.Score, 4));
                w.WriteStartArray("chunks");
                foreach (var chunk in hit.Chunks)
                {
                    w.WriteStartObject();
                    w.WriteNumber("score", Math.Round(chunk.Score, 4));
                    w.WriteString("role", chunk.Role);
                    if (chunk.Stamp is { } stamp) w.WriteString("stamp", stamp);
                    w.WriteNumber("ordinal", chunk.Ordinal);
                    w.WriteNumber("start", chunk.Start);
                    w.WriteString("snippet", chunk.Snippet);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }

        private void Answer(long id, Action<Utf8JsonWriter> body) => Write(w =>
        {
            w.WriteNumber("id", id);
            w.WriteBoolean("ok", true);
            body(w);
        });

        private void Fail(long id, string error, Action<Utf8JsonWriter>? more = null) => Write(w =>
        {
            w.WriteNumber("id", id);
            w.WriteBoolean("ok", false);
            w.WriteString("error", error);
            more?.Invoke(w);
        });

        public void Event(string name, Action<Utf8JsonWriter> body) => Write(w =>
        {
            w.WriteString("event", name);
            body(w);
        });

        /// <summary>One compact object, one line, flushed at once.</summary>
        private void Write(Action<Utf8JsonWriter> body)
        {
            using var buffer = new MemoryStream();
            using (var w = new Utf8JsonWriter(buffer))
            {
                w.WriteStartObject();
                body(w);
                w.WriteEndObject();
            }
            var line = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
            lock (_write)
            {
                try
                {
                    output.WriteLine(line);
                    output.Flush();
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    // The window is gone; stdin ends next and the worker with it.
                }
            }
        }
    }
}
