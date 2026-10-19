using System.Diagnostics;
using ClaudeLauncher.Infrastructure.Search;
using ClaudeLauncher.Infrastructure.Semantic;
using Xunit.Abstractions;

namespace ClaudeLauncher.IntegrationTests;

/// <summary>
/// How fast a search is over a large index: 50,000 pieces of 512 dimensions (bge-small-zh's size),
/// the target being well under 150 ms. Slow to set up, so it runs only when SESSION_HUB_BENCH is set.
/// </summary>
public sealed class SemanticSearchBenchmark(ITestOutputHelper output) : IDisposable
{
    private const int Conversations = 2000, MessagesEach = 25, Dims = 512;

    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "launcher-semantic-bench", Guid.NewGuid().ToString("n"))).FullName;

    /// <summary>Conversations of numbered messages, each long enough to be one piece.</summary>
    private sealed class ManyConversations : IDialogueSource
    {
        public string Fingerprint() => "bench";

        public IReadOnlyList<IndexedSource> Sources() =>
            Enumerable.Range(0, Conversations).Select(i => new IndexedSource($"c{i}", i + 1, MessagesEach, 1000, 1)).ToList();

        public IReadOnlyList<IndexedMessage> Messages(string conversation) =>
            Enumerable.Range(0, MessagesEach)
                .Select(m => new IndexedMessage(m, m % 2 == 0 ? "user" : "assistant", null, $"{conversation} 的第 {m} 条消息，足够长"))
                .ToList();
    }

    /// <summary>A unit vector per text, the same each time.</summary>
    private sealed class RandomEmbedder : IEmbedder
    {
        public string ModelId => "bench";
        public int Dimensions => Dims;

        public float[][] Embed(IReadOnlyList<string> texts, bool query, CancellationToken cancellation = default) =>
            texts.Select(t =>
            {
                var random = new Random(t.GetHashCode());
                var v = Enumerable.Range(0, Dims).Select(_ => (float)(random.NextDouble() * 2 - 1)).ToArray();
                var norm = MathF.Sqrt(v.Sum(x => x * x));
                return v.Select(x => x / norm).ToArray();
            }).ToArray();

        public void Dispose() { }
    }

    [Fact]
    public void FiftyThousandPiecesAreSearchedQuickly()
    {
        if (Environment.GetEnvironmentVariable("SESSION_HUB_BENCH") is not "1")
        {
            output.WriteLine("SESSION_HUB_BENCH is not 1: the benchmark was skipped.");
            return;
        }
        var path = Path.Combine(_dir, "semantic-index.sqlite");
        var embedder = new RandomEmbedder();
        var built = Stopwatch.StartNew();
        using (var index = SemanticIndex.Open(path))
        {
            var run = index.Update(new ManyConversations(), embedder,
                Enumerable.Range(0, Conversations).Select(i => $"c{i}").ToList());
            Assert.Equal(Conversations * MessagesEach, run.Chunks);
        }
        output.WriteLine($"built {Conversations * MessagesEach} pieces in {built.ElapsedMilliseconds} ms, file {new FileInfo(path).Length / 1024 / 1024} MB");

        using var reader = SemanticIndex.OpenReadOnly(path)!;
        var query = embedder.Embed(["c42 的第 7 条消息，足够长"], query: true)[0];
        var first = reader.Search(query, "bench");
        Assert.Equal("c42", first[0].Conversation);
        var times = new List<long>();
        for (var i = 0; i < 7; i++)
        {
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew();
            reader.Search(query, "bench");
            times.Add(watch.ElapsedMilliseconds);
            output.WriteLine($"search {watch.ElapsedMilliseconds} ms, {(GC.GetAllocatedBytesForCurrentThread() - allocated) / 1024} KB allocated");
        }
        times.Sort();
        output.WriteLine($"median {times[times.Count / 2]} ms");
        Assert.True(times[times.Count / 2] < 150, $"median {times[times.Count / 2]} ms");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
    }
}
