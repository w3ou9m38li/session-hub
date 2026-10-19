using ClaudeLauncher.Infrastructure.Semantic;
using Microsoft.Data.Sqlite;

namespace ClaudeLauncher.IntegrationTests;

/// <summary>
/// The semantic index: built from the full-text index, kept up to date by embedding only what
/// changed, resumable after a stop, and searched by streaming 8-bit vectors that rank as the
/// floats they stand for.
/// </summary>
public sealed class SemanticIndexTests : IDisposable
{
    private readonly SemanticRoot _root = new();

    private const string GwasA = "小麦条锈病的全基因组关联分析，用 GEMMA 跑混合线性模型";
    private const string GwasB = "显著位点附近的候选基因要用表达数据再筛一遍";
    private const string Cook = "红烧肉要先焯水，再用冰糖炒出糖色，小火慢炖一个小时";
    private const string CookB = "炖的时候加一点黄酒和八角，出锅前大火收汁";

    private static readonly string[] Ids = ["a", "b", "c"];

    private void Fill()
    {
        _root.Write("a", GwasA, GwasB);
        _root.Write("b", Cook, CookB);
        _root.Write("c", "好的", "继续");               // nothing long enough to embed
        _root.IndexContent();
    }

    private static List<string> Listed(params string[] ids) => ids.Select(SemanticRoot.KeyOf).ToList();

    [Fact]
    public void ABuildEmbedsEverythingOnceAndThenNothingUnchanged()
    {
        Fill();
        var embedder = new FakeEmbedder();
        using var index = SemanticIndex.Open(_root.IndexPath);

        var first = index.Update(_root.Dialogue, embedder, Listed(Ids));
        Assert.Equal(3, first.Updated);
        Assert.Equal(4, first.Embedded);
        Assert.Equal(3, first.Conversations);                   // the short one is indexed too, with no pieces
        Assert.Equal(4, first.Chunks);
        Assert.Equal(_root.Dialogue.Fingerprint(), first.Source);

        var calls = embedder.Calls;
        var second = index.Update(_root.Dialogue, embedder, Listed(Ids));
        Assert.Equal(0, second.Updated);
        Assert.Equal(0, second.Embedded);
        Assert.Equal(calls, embedder.Calls);                    // the model was not even asked
        Assert.Equal(4, second.Chunks);

        var stats = index.Stats();
        Assert.Equal("fake:1", stats.ModelId);
        Assert.Equal(64, stats.Dimensions);
        Assert.NotNull(stats.Updated);
        Assert.Equal(first.Source, stats.BuiltFrom);
    }

    [Fact]
    public void AConversationThatGrewHasOnlyItsNewMessagesEmbedded()
    {
        Fill();
        var embedder = new FakeEmbedder();
        using var index = SemanticIndex.Open(_root.IndexPath);
        index.Update(_root.Dialogue, embedder, Listed(Ids));

        _root.Append("a", 2, "关联分析的显著性阈值用 Bonferroni 校正");
        _root.IndexContent("a");
        var texts = embedder.Texts;
        var run = index.Update(_root.Dialogue, embedder, Listed(Ids));

        Assert.Equal(1, run.Updated);
        Assert.Equal(1, run.Embedded);
        Assert.Equal(texts + 1, embedder.Texts);
        Assert.Equal(5, run.Chunks);
        AssertNoDuplicates();
        var hit = Search(index, embedder, "Bonferroni 校正阈值").First();
        Assert.Equal(SemanticRoot.KeyOf("a"), hit.Conversation);
        Assert.Equal(2, hit.Chunks[0].Ordinal);
    }

    [Fact]
    public void AConversationWrittenAgainDifferentlyIsEmbeddedAgainWhole()
    {
        Fill();
        var embedder = new FakeEmbedder();
        using var index = SemanticIndex.Open(_root.IndexPath);
        index.Update(_root.Dialogue, embedder, Listed(Ids));

        _root.Write("b", "清蒸鲈鱼只要蒸八分钟，淋上热油和蒸鱼豉油", CookB);
        _root.IndexContent("b");
        var run = index.Update(_root.Dialogue, embedder, Listed(Ids));

        Assert.Equal(1, run.Updated);
        Assert.Equal(2, run.Embedded);                           // both messages, the unchanged second one too
        Assert.Equal(4, run.Chunks);
        AssertNoDuplicates();
        Assert.DoesNotContain(Search(index, embedder, Cook).SelectMany(h => h.Chunks), c => c.Snippet == Cook);
    }

    [Fact]
    public void ConversationsNoLongerListedLeaveTheIndex()
    {
        Fill();
        var embedder = new FakeEmbedder();
        using var index = SemanticIndex.Open(_root.IndexPath);
        index.Update(_root.Dialogue, embedder, Listed(Ids));

        var run = index.Update(_root.Dialogue, embedder, Listed("a", "c"));
        Assert.Equal(1, run.Removed);
        Assert.Equal(2, run.Conversations);
        Assert.Equal(2, run.Chunks);
        AssertNoDuplicates();
        Assert.DoesNotContain(Search(index, embedder, Cook), h => h.Conversation == SemanticRoot.KeyOf("b"));

        // Listed but not (yet) in the full-text index — it is being rebuilt, say: kept as it is.
        _root.DropContent();
        _root.IndexContent("a");
        var kept = index.Update(_root.Dialogue, embedder, Listed("a", "c"));
        Assert.Equal(0, kept.Removed);
        Assert.Equal(2, kept.Conversations);
    }

    [Fact]
    public void AnotherModelStartsTheIndexOver()
    {
        Fill();
        using var index = SemanticIndex.Open(_root.IndexPath);
        index.Update(_root.Dialogue, new FakeEmbedder(), Listed(Ids));

        var other = new FakeEmbedder("fake:2", 32);
        var run = index.Update(_root.Dialogue, other, Listed(Ids));
        Assert.Equal(3, run.Updated);
        Assert.Equal(4, run.Embedded);
        Assert.Equal(4, run.Chunks);
        Assert.Equal("fake:2", index.Stats().ModelId);
        Assert.Equal(32, index.Stats().Dimensions);
        AssertNoDuplicates();

        // A search with the old model's vectors is refused rather than answered wrongly.
        Assert.Throws<SemanticUnavailableException>(() => index.Search(FakeEmbedder.Vector(GwasA, 64), "fake:1"));
    }

    [Fact]
    public void AStoppedBuildGoesOnFromWhereItStoppedWithoutDuplicates()
    {
        // One long conversation (more pieces than a commit holds) and a few short ones.
        var long1 = Enumerable.Range(0, 150).Select(i => $"第 {i} 条消息：关于群体结构和亲缘关系矩阵的讨论").ToArray();
        _root.Write("long", long1);
        _root.Write("a", GwasA, GwasB);
        _root.Write("b", Cook, CookB);
        _root.IndexContent();
        string[] all = ["long", "a", "b"];

        using var stop = new CancellationTokenSource();
        var embedder = new FakeEmbedder { BeforeCall = call => { if (call == 2) stop.Cancel(); } };
        using (var index = SemanticIndex.Open(_root.IndexPath))
        {
            Assert.ThrowsAny<OperationCanceledException>(() => index.Update(_root.Dialogue, embedder, Listed(all), cancellation: stop.Token));
            // The first commit's pieces were kept; the second batch was not.
            Assert.Equal(64, index.Stats().Chunks);
            Assert.Equal(0, index.Stats().Conversations);
        }

        var resumed = new FakeEmbedder();
        using (var index = SemanticIndex.Open(_root.IndexPath))
        {
            var run = index.Update(_root.Dialogue, resumed, Listed(all));
            Assert.Equal(150 - 64 + 4, run.Embedded);              // only what the stop left
            Assert.Equal(154, run.Chunks);
            Assert.Equal(3, run.Conversations);
        }
        AssertNoDuplicates();

        // The same as one build without a stop.
        var fresh = Path.Combine(_root.Folder, "fresh.sqlite");
        using var whole = SemanticIndex.Open(fresh);
        var once = whole.Update(_root.Dialogue, new FakeEmbedder(), Listed(all));
        Assert.Equal(154, once.Chunks);
        Assert.Equal(Pieces(_root.IndexPath), Pieces(fresh));
    }

    [Fact]
    public void QuantisedVectorsRankAsTheirFloatsDo()
    {
        var random = new Random(20261015);
        float[] RandomVector()
        {
            var v = Enumerable.Range(0, 64).Select(_ => (float)(random.NextDouble() * 2 - 1)).ToArray();
            var norm = MathF.Sqrt(v.Sum(x => x * x));
            return v.Select(x => x / norm).ToArray();
        }

        var vectors = new Dictionary<string, float[]>();
        var ids = Enumerable.Range(0, 300).Select(i => $"d{i:000}").ToArray();
        foreach (var id in ids)
        {
            var text = $"文档 {id} 的一段足够长的内容";
            _root.Write(id, text);
            vectors[text] = RandomVector();
        }
        _root.IndexContent();
        var embedder = new FakeEmbedder { Fixed = t => vectors.GetValueOrDefault(t) };
        using var index = SemanticIndex.Open(_root.IndexPath);
        index.Update(_root.Dialogue, embedder, Listed(ids));

        for (var round = 0; round < 5; round++)
        {
            var query = RandomVector();
            var exact = ids.ToDictionary(SemanticRoot.KeyOf,
                id => vectors[$"文档 {id} 的一段足够长的内容"].Zip(query, (a, b) => a * b).Sum());
            var ranked = exact.OrderByDescending(e => e.Value).ToList();

            var hits = index.Search(query, "fake:1", limit: 10, perConversation: 1);
            Assert.Equal(10, hits.Count);
            Assert.All(hits, h => Assert.InRange(h.Score - exact[h.Conversation], -0.02f, 0.02f));
            // The same ten, but for near ties at the edge.
            Assert.All(hits, h => Assert.True(exact[h.Conversation] >= ranked[9].Value - 0.03f));
            if (ranked[0].Value - ranked[1].Value > 0.04f) Assert.Equal(ranked[0].Key, hits[0].Conversation);
        }

        // One of the vectors itself is found first, as close as can be.
        var self = index.Search(vectors["文档 d123 的一段足够长的内容"], "fake:1", limit: 1);
        Assert.Equal(SemanticRoot.KeyOf("d123"), Assert.Single(self).Conversation);
        Assert.InRange(self[0].Score, 0.98f, 1.02f);
    }

    [Fact]
    public void ResultsAreGroupedByConversationBestFirst()
    {
        _root.Write("many", "引物设计要避开 SNP 位点", "好", "引物的 Tm 值要接近", "嗯", "引物二聚体要检查", "对", "引物长度二十个碱基左右", "行",
            "引物设计软件用 Primer3");
        _root.Write("one", "引物设计", "说得对，引物设计要注意 GC 含量");
        _root.Write("none", Cook, CookB);
        _root.IndexContent();
        var embedder = new FakeEmbedder();
        using var index = SemanticIndex.Open(_root.IndexPath);
        index.Update(_root.Dialogue, embedder, Listed("many", "one", "none"));

        var hits = Search(index, embedder, "引物设计", perConversation: 3);
        Assert.Equal(3, hits.Count);
        Assert.Equal(hits.Select(h => h.Score).OrderByDescending(s => s), hits.Select(h => h.Score));
        Assert.Equal(SemanticRoot.KeyOf("none"), hits[^1].Conversation);          // the unrelated one last
        var many = hits.Single(h => h.Conversation == SemanticRoot.KeyOf("many"));
        Assert.Equal(3, many.Chunks.Count);                                          // its best three of five
        Assert.Equal(many.Chunks.Select(c => c.Score).OrderByDescending(s => s), many.Chunks.Select(c => c.Score));
        Assert.Equal(many.Score, many.Chunks[0].Score);
        Assert.All(many.Chunks, c => Assert.Equal("user", c.Role));
        Assert.All(many.Chunks, c => Assert.Equal(0, c.Ordinal % 2));

        var best = Assert.Single(Search(index, embedder, "引物设计", limit: 1));
        Assert.Equal(hits[0].Conversation, best.Conversation);
    }

    [Fact]
    public void AnEmptyOrMissingIndexFindsNothingAndIsNotMadeByReading()
    {
        Assert.Null(SemanticIndex.OpenReadOnly(_root.IndexPath));
        Assert.Null(SemanticIndex.ReadStats(_root.IndexPath));
        Assert.False(File.Exists(_root.IndexPath));

        using (var index = SemanticIndex.Open(_root.IndexPath))
            Assert.Empty(index.Search(FakeEmbedder.Vector("引物", 64), "fake:1"));
        using var reader = SemanticIndex.OpenReadOnly(_root.IndexPath);
        Assert.NotNull(reader);
        Assert.Equal(0, reader.Stats().Chunks);
        Assert.Throws<InvalidOperationException>(() => reader.Update(_root.Dialogue, new FakeEmbedder(), []));
    }

    [Fact]
    public void TheDotProductIsTheScalarOneAtEveryLength()
    {
        var random = new Random(7);
        foreach (var length in new[] { 1, 3, 31, 32, 33, 64, 100, 384, 512, 1027 })
        {
            var query = Enumerable.Range(0, length).Select(_ => (float)random.NextDouble() - 0.5f).ToArray();
            var bytes = Enumerable.Range(0, length).Select(_ => (byte)random.Next(256)).ToArray();
            var expected = 0.0;
            for (var i = 0; i < length; i++) expected += query[i] * (sbyte)bytes[i];
            Assert.Equal(expected, SemanticIndex.Dot(query, bytes), 0.01);
        }

        var (scale, quantised) = SemanticIndex.Quantize([0.5f, -1f, 0.25f, 0f]);
        Assert.Equal(1f / 127, scale, 6);
        Assert.Equal([64, 129, 32, 0], quantised.Select(b => (int)b));      // -127 as a byte is 129
    }

    private static IReadOnlyList<SemanticHit> Search(SemanticIndex index, FakeEmbedder embedder, string text, int limit = 20,
        int perConversation = 3) =>
        index.Search(embedder.Embed([text], query: true)[0], embedder.ModelId, limit, perConversation);

    /// <summary>
    /// Every piece once: no message's piece stored twice under any id — and a vector for each piece,
    /// none for a piece that is gone: the blocks start at pieces and hold as many vectors as there are pieces.
    /// </summary>
    private void AssertNoDuplicates()
    {
        using var connection = new SqliteConnection($"Data Source={_root.IndexPath};Pooling=False;Mode=ReadOnly");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*), COUNT(DISTINCT (id >> 32) || ':' || ordinal || ':' || start),
                   COUNT(*) * (4 + (SELECT CAST(value AS INTEGER) FROM meta WHERE key = 'dims')),
                   (SELECT TOTAL(length(block)) FROM vectors),
                   (SELECT COUNT(*) FROM vectors v WHERE NOT EXISTS (SELECT 1 FROM chunks c WHERE c.id = v.id))
            FROM chunks;
            """;
        using var reader = cmd.ExecuteReader();
        reader.Read();
        Assert.Equal(reader.GetInt64(0), reader.GetInt64(1));
        Assert.Equal(reader.GetInt64(2), (long)reader.GetDouble(3));
        Assert.Equal(0, reader.GetInt64(4));
    }

    /// <summary>The pieces an index holds, by conversation, message and place: what two builds must agree on.</summary>
    private static List<string> Pieces(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False;Mode=ReadOnly");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT s.conversation || '#' || c.ordinal || '@' || c.start || ':' || c.snippet
            FROM chunks c JOIN sources s ON s.id = c.id >> 32 ORDER BY 1;
            """;
        using var reader = cmd.ExecuteReader();
        var pieces = new List<string>();
        while (reader.Read()) pieces.Add(reader.GetString(0));
        return pieces;
    }

    public void Dispose() => _root.Dispose();
}
