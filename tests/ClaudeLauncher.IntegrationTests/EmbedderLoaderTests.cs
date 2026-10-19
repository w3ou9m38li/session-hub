using System.Runtime.Loader;
using System.Text;
using ClaudeLauncher.Infrastructure.Semantic;
using Xunit.Abstractions;

namespace ClaudeLauncher.IntegrationTests;

/// <summary>
/// The optional component and the user's model: every missing piece named in words, and a real
/// ONNX Runtime run through the component's own load context — on tiny models written here byte
/// by byte, so nothing is downloaded. A real model is tried too when SESSION_HUB_TEST_MODEL names one.
/// </summary>
public sealed class EmbedderLoaderTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "launcher-semantic-loader", Guid.NewGuid().ToString("n"))).FullName;

    internal static readonly string[] Vocabulary = ["[PAD]", "[UNK]", "[CLS]", "[SEP]", "引", "物", "设", "计", "红", "烧", "肉"];
    private const int Dims = 8;

    /// <summary>
    /// The component as the build put it: beside the solution's other outputs, in the same
    /// configuration — or, when SESSION_HUB_TEST_COMPONENT names one, a published component
    /// (scripts\Publish-Semantic.ps1), to check what is handed out.
    /// </summary>
    internal static string ComponentFolder()
    {
        var folder = Environment.GetEnvironmentVariable("SESSION_HUB_TEST_COMPONENT") is { Length: > 0 } published
            ? published
            : BuildOutput("ClaudeLauncher.Semantic");
        Assert.True(File.Exists(Path.Combine(folder, EmbedderLoader.ComponentAssembly)),
            $"语义搜索组件不在 {folder}：先构建整个解决方案（dotnet build ClaudeLauncher.slnx）");
        return folder;
    }

    /// <summary>Where the build put a project of the solution, in this test's configuration and framework.</summary>
    internal static string BuildOutput(string project)
    {
        // tests\ClaudeLauncher.IntegrationTests\bin\<configuration>\<framework>
        var framework = new DirectoryInfo(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
        var configuration = framework.Parent!;
        var repository = configuration.Parent!.Parent!.Parent!.Parent!;
        return Path.Combine(repository.FullName, "src", project, "bin", configuration.Name, framework.Name);
    }

    private string Folder(string name, bool model = true, bool vocabulary = true, string? config = null)
    {
        var folder = Directory.CreateDirectory(Path.Combine(_dir, name)).FullName;
        if (model) File.WriteAllBytes(Path.Combine(folder, "model.onnx"), TinyModel.Tokens());
        if (vocabulary) File.WriteAllLines(Path.Combine(folder, "vocab.txt"), Vocabulary);
        if (config is not null) File.WriteAllText(Path.Combine(folder, "embedding.json"), config);
        return folder;
    }

    [Fact]
    public void WhatIsMissingIsSaidInWords()
    {
        var nowhere = Path.Combine(_dir, "no-component");
        var model = Folder("model");

        var noComponent = Assert.Throws<SemanticUnavailableException>(() => EmbedderLoader.Load(model, nowhere));
        Assert.Contains("没有找到语义搜索组件", noComponent.Message);
        Assert.Contains(Path.Combine(nowhere, "ClaudeLauncher.Semantic.dll"), noComponent.Message);

        var component = ComponentFolder();
        var missingFolder = Path.Combine(_dir, "models", "embedding");
        var noFolder = Assert.Throws<SemanticUnavailableException>(() => EmbedderLoader.Load(missingFolder, component));
        Assert.Equal($"没有找到向量模型文件夹：{missingFolder}", noFolder.Message);

        var halfFolder = Folder("half", model: false);
        var half = Assert.Throws<SemanticUnavailableException>(() => EmbedderLoader.Load(halfFolder, component));
        Assert.Equal($"模型文件夹 {halfFolder} 里缺少：model.onnx", half.Message);
        var emptyFolder = Folder("empty", model: false, vocabulary: false);
        Assert.EndsWith("里缺少：model.onnx、vocab.txt", EmbedderLoader.Check(emptyFolder, component).Problems.Single());

        var badConfig = Folder("bad-config", config: """{"pooling":"max"}""");
        Assert.Contains("pooling", Assert.Throws<SemanticUnavailableException>(() => EmbedderLoader.Load(badConfig, component)).Message);

        // Checking reads the file system only: the same answers, and nothing loaded.
        var check = EmbedderLoader.Check(missingFolder, nowhere);
        Assert.False(check.Ready);
        Assert.Equal(2, check.Problems.Count);
        Assert.True(EmbedderLoader.Check(model, component).Ready);
        Assert.Empty(EmbedderLoader.Check(model, component).Problems);
    }

    [Fact]
    public void AComponentOrModelThatCannotBeLoadedIsSaidSo()
    {
        var broken = Directory.CreateDirectory(Path.Combine(_dir, "broken-component")).FullName;
        File.WriteAllText(Path.Combine(broken, "ClaudeLauncher.Semantic.dll"), "not an assembly");
        var model = Folder("model");
        Assert.Contains("语义搜索组件加载不了", Assert.Throws<SemanticUnavailableException>(() => EmbedderLoader.Load(model, broken)).Message);

        var garbage = Folder("garbage", model: false);
        File.WriteAllText(Path.Combine(garbage, "model.onnx"), "not a model");
        Assert.StartsWith("向量模型加载不了：", Assert.Throws<SemanticUnavailableException>(() => EmbedderLoader.Load(garbage, ComponentFolder())).Message);
    }

    [Fact]
    public void ARealModelRunsThroughTheComponentInALoadContextOfItsOwn()
    {
        var component = ComponentFolder();
        var table = TinyModel.Table(Vocabulary.Length, Dims);

        // Per-token output with a pooler beside it: the first token's vector, not the pooler's.
        using (var cls = EmbedderLoader.Load(Folder("cls"), component))
        {
            Assert.Equal(Dims, cls.Dimensions);
            Assert.StartsWith("cls:", cls.ModelId);
            var vectors = cls.Embed(["引物设计", "红烧肉"], query: false);
            Assert.Equal(Normalized(table[2]), vectors[0], Close);
            Assert.Equal(Normalized(table[2]), vectors[1], Close);

            var context = AssemblyLoadContext.GetLoadContext(cls.GetType().Assembly)!;
            Assert.NotSame(AssemblyLoadContext.Default, context);
            Assert.DoesNotContain(AssemblyLoadContext.Default.Assemblies, a => a.GetName().Name == "Microsoft.ML.OnnxRuntime");
            Assert.Contains(context.Assemblies, a => a.GetName().Name == "Microsoft.ML.OnnxRuntime");
        }

        // Mean pooling over the real tokens only, texts of different lengths in one padded batch, answers in order.
        using (var mean = EmbedderLoader.Load(Folder("mean", config: """{"pooling":"mean"}"""), component))
        {
            var texts = new[] { "引物设计", "红烧肉", "引" };
            var vectors = mean.Embed(texts, query: true);
            for (var i = 0; i < texts.Length; i++)
                Assert.Equal(MeanOf(table, Ids(texts[i])), vectors[i], Close);
        }

        // A sentence vector is used as it is, and 32-bit ids are fed as such.
        var sentenceFolder = Folder("sentence", model: false);
        File.WriteAllBytes(Path.Combine(sentenceFolder, "model.onnx"), TinyModel.Sentence());
        using (var sentence = EmbedderLoader.Load(sentenceFolder, component))
        {
            Assert.Equal(Dims, sentence.Dimensions);
            Assert.Equal(MeanOf(table, Ids("红烧肉")), sentence.Embed(["红烧肉"], query: false)[0], Close);
        }
    }

    [Fact]
    public void TheUsersOwnModelWhenOneIsNamed()
    {
        var folder = Environment.GetEnvironmentVariable("SESSION_HUB_TEST_MODEL");
        if (string.IsNullOrWhiteSpace(folder))
        {
            output.WriteLine("SESSION_HUB_TEST_MODEL is not set: no real model to try (none is downloaded).");
            return;
        }
        using var embedder = EmbedderLoader.Load(folder, ComponentFolder());
        var vectors = embedder.Embed(["怎么设计 KASP 引物", "KASP 引物的设计方法", "红烧肉怎么做才好吃"], query: false);
        Assert.All(vectors, v => Assert.Equal(embedder.Dimensions, v.Length));
        Assert.All(vectors, v => Assert.InRange(MathF.Sqrt(v.Sum(x => x * x)), 0.99f, 1.01f));
        float Cosine(float[] a, float[] b) => a.Zip(b, (x, y) => x * y).Sum();
        output.WriteLine($"{embedder.ModelId}, {embedder.Dimensions} dims: similar {Cosine(vectors[0], vectors[1]):0.000}, unrelated {Cosine(vectors[0], vectors[2]):0.000}");
        Assert.True(Cosine(vectors[0], vectors[1]) > Cosine(vectors[0], vectors[2]));
    }

    private static readonly IEqualityComparer<float> Close = EqualityComparer<float>.Create((a, b) => Math.Abs(a - b) < 1e-4f);

    private static int[] Ids(string text) =>
        [2, .. text.Select(c => Array.IndexOf(Vocabulary, c.ToString())), 3];

    private static float[] MeanOf(float[][] table, int[] ids) =>
        Normalized(Enumerable.Range(0, Dims).Select(d => ids.Average(id => table[id][d])).Select(x => (float)x).ToArray());

    private static float[] Normalized(float[] v)
    {
        var norm = MathF.Sqrt(v.Sum(x => x * x));
        return v.Select(x => x / norm).ToArray();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
    }

    /// <summary>
    /// Two ONNX models of one operator or two, written as protobuf by hand: an embedding table
    /// looked up by token id (Gather), giving per-token vectors — and in the second model their mean.
    /// </summary>
    internal static class TinyModel
    {
        public static float[][] Table(int vocabulary, int dims) =>
            Enumerable.Range(0, vocabulary)
                .Select(v => Enumerable.Range(0, dims).Select(d => MathF.Sin(v * 7 + d * 3 + 1)).ToArray())
                .ToArray();

        /// <summary>input_ids (and an unused attention_mask) → last_hidden_state, and pooler_output beside it.</summary>
        public static byte[] Tokens() => Model(Graph(
            [
                Node("Gather", ["table", "input_ids"], ["last_hidden_state"]),
                Node("ReduceMean", ["last_hidden_state"], ["pooler_output"], Ints("axes", 1), Int("keepdims", 0)),
            ],
            [Input("input_ids", Int64), Input("attention_mask", Int64)],
            [Output("last_hidden_state", 3), Output("pooler_output", 2)]));

        /// <summary>32-bit input_ids → sentence_embedding, the mean of the looked-up vectors.</summary>
        public static byte[] Sentence() => Model(Graph(
            [
                Node("Gather", ["table", "input_ids"], ["hidden"]),
                Node("ReduceMean", ["hidden"], ["sentence_embedding"], Ints("axes", 1), Int("keepdims", 0)),
            ],
            [Input("input_ids", Int32)],
            [Output("sentence_embedding", 2)]));

        private const int Float = 1, Int32 = 6, Int64 = 7;

        private static byte[] Model(byte[] graph)
        {
            var model = new Proto();
            model.Varint(1, 8);                                         // ir_version
            model.Text(2, "session-hub-test");                          // producer_name
            model.Message(7, graph);
            var opset = new Proto();
            opset.Text(1, "");
            opset.Varint(2, 13);
            model.Message(8, opset.Bytes());
            return model.Bytes();
        }

        private static byte[] Graph(byte[][] nodes, byte[][] inputs, byte[][] outputs)
        {
            var graph = new Proto();
            foreach (var node in nodes) graph.Message(1, node);
            graph.Text(2, "tiny");
            graph.Message(5, Initializer("table", Table(Vocabulary.Length, Dims)));
            foreach (var input in inputs) graph.Message(11, input);
            foreach (var output in outputs) graph.Message(12, output);
            return graph.Bytes();
        }

        private static byte[] Node(string op, string[] inputs, string[] outputs, params byte[][] attributes)
        {
            var node = new Proto();
            foreach (var input in inputs) node.Text(1, input);
            foreach (var output in outputs) node.Text(2, output);
            node.Text(3, op + "_" + outputs[0]);
            node.Text(4, op);
            foreach (var attribute in attributes) node.Message(5, attribute);
            return node.Bytes();
        }

        private static byte[] Int(string name, long value)
        {
            var attribute = new Proto();
            attribute.Text(1, name);
            attribute.Varint(3, value);
            attribute.Varint(20, 2);                                    // INT
            return attribute.Bytes();
        }

        private static byte[] Ints(string name, params long[] values)
        {
            var attribute = new Proto();
            attribute.Text(1, name);
            foreach (var value in values) attribute.Varint(8, value);
            attribute.Varint(20, 7);                                    // INTS
            return attribute.Bytes();
        }

        private static byte[] Initializer(string name, float[][] rows)
        {
            var tensor = new Proto();
            tensor.Varint(1, rows.Length);
            tensor.Varint(1, rows[0].Length);
            tensor.Varint(2, Float);
            tensor.Text(8, name);
            tensor.Message(9, rows.SelectMany(r => r).SelectMany(BitConverter.GetBytes).ToArray());   // raw_data, little-endian
            return tensor.Bytes();
        }

        private static byte[] Input(string name, int type) => ValueInfo(name, type, ["batch", "sequence"]);

        private static byte[] Output(string name, int rank) => ValueInfo(name, Float,
            rank == 3 ? ["batch", "sequence", Dims] : ["batch", Dims]);

        /// <summary>A tensor's name, element type and shape; a dimension is a size or a name.</summary>
        private static byte[] ValueInfo(string name, int type, object[] dims)
        {
            var shape = new Proto();
            foreach (var dim in dims)
            {
                var d = new Proto();
                if (dim is int size) d.Varint(1, size);
                else d.Text(2, (string)dim);
                shape.Message(1, d.Bytes());
            }
            var tensor = new Proto();
            tensor.Varint(1, type);
            tensor.Message(2, shape.Bytes());
            var typeProto = new Proto();
            typeProto.Message(1, tensor.Bytes());
            var info = new Proto();
            info.Text(1, name);
            info.Message(2, typeProto.Bytes());
            return info.Bytes();
        }
    }

    /// <summary>The protobuf wire format, as much as the models above need.</summary>
    private sealed class Proto
    {
        private readonly MemoryStream _bytes = new();

        public void Varint(int field, long value)
        {
            Key(field, 0);
            Raw((ulong)value);
        }

        public void Text(int field, string value) => Message(field, Encoding.UTF8.GetBytes(value));

        public void Message(int field, byte[] value)
        {
            Key(field, 2);
            Raw((ulong)value.Length);
            _bytes.Write(value);
        }

        private void Key(int field, int wireType) => Raw((ulong)((field << 3) | wireType));

        private void Raw(ulong value)
        {
            while (value >= 0x80)
            {
                _bytes.WriteByte((byte)(value | 0x80));
                value >>= 7;
            }
            _bytes.WriteByte((byte)value);
        }

        public byte[] Bytes() => _bytes.ToArray();
    }
}
