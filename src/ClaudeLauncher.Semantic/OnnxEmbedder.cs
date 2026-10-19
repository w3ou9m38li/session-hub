using ClaudeLauncher.Infrastructure.Semantic;
using Microsoft.ML.OnnxRuntime;

namespace ClaudeLauncher.Semantic;

/// <summary>
/// The entry point the host looks for (<c>EmbedderLoader</c>): a type of this name with this method.
/// </summary>
public static class OnnxEmbedderFactory
{
    /// <exception cref="SemanticUnavailableException">The model cannot be loaded; the message says why, for the user.</exception>
    public static IEmbedder Create(string modelFolder) => new OnnxEmbedder(modelFolder);
}

/// <summary>
/// A BERT-style embedding model run by ONNX Runtime on the CPU: on the calling thread only (one
/// intra-op thread, sequential), so a build at background priority stays at background priority,
/// and without ONNX Runtime's memory arena, so what a batch took goes back when it is done.
/// Texts are embedded 16 at a time, shortest with shortest, padded only to the longest of their batch.
/// </summary>
public sealed class OnnxEmbedder : IEmbedder
{
    private const int BatchSize = 16;

    private readonly EmbeddingConfig _config;
    private readonly WordPieceTokenizer _tokenizer;
    private readonly SessionOptions _options;
    private readonly InferenceSession _session;
    private readonly string _output;
    private readonly bool _pooled;
    private readonly (string Name, InputKind Kind, bool Int32)[] _inputs;
    private int _dimensions;

    private enum InputKind { Ids, Mask, Types }

    public string ModelId { get; }

    public OnnxEmbedder(string modelFolder)
    {
        _config = EmbeddingConfig.Load(modelFolder);
        try
        {
            _tokenizer = WordPieceTokenizer.FromFile(Path.Combine(modelFolder, EmbeddingConfig.VocabularyFile), _config.Lowercase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SemanticUnavailableException($"{EmbeddingConfig.VocabularyFile} 读不了：{ex.Message}", ex);
        }
        ModelId = EmbeddingConfig.ModelIdOf(modelFolder, _config);

        _options = new SessionOptions
        {
            IntraOpNumThreads = 1,
            InterOpNumThreads = 1,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            EnableCpuMemArena = false,
            // Warnings would go to stderr; the protocol is on stdout, but nobody reads them either.
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
        };
        try
        {
            _session = new InferenceSession(Path.Combine(modelFolder, EmbeddingConfig.ModelFile), _options);
        }
        catch (Exception ex) when (ex is OnnxRuntimeException or IOException or UnauthorizedAccessException)
        {
            _options.Dispose();
            throw new SemanticUnavailableException("向量模型加载不了：" + ex.Message, ex);
        }

        try
        {
            _inputs = _session.InputMetadata.Select(input => (input.Key, KindOf(input.Key), Int32Input(input.Key, input.Value))).ToArray();
            if (!_inputs.Any(i => i.Kind == InputKind.Ids))
                throw new SemanticUnavailableException("这个模型没有 input_ids 输入，不像 BERT 类的向量模型");
            (_output, _pooled, _dimensions) = ChooseOutput(_session.OutputMetadata);
        }
        catch
        {
            _session.Dispose();
            _options.Dispose();
            throw;
        }
    }

    private static InputKind KindOf(string name) =>
        name.Contains("input_ids", StringComparison.OrdinalIgnoreCase) ? InputKind.Ids
        : name.Contains("attention_mask", StringComparison.OrdinalIgnoreCase) ? InputKind.Mask
        : name.Contains("token_type_ids", StringComparison.OrdinalIgnoreCase) ? InputKind.Types
        : throw new SemanticUnavailableException($"模型要的输入 {name} 不认识：只认 input_ids、attention_mask、token_type_ids");

    private static bool Int32Input(string name, NodeMetadata metadata) =>
        metadata.ElementType == typeof(int)
        || (metadata.ElementType == typeof(long) ? false : throw new SemanticUnavailableException($"模型的输入 {name} 不是整数类型"));

    /// <summary>
    /// A sentence vector (two dimensions) is used as it is — but not BERT's pooler output, a
    /// classifier's head rather than an embedding; otherwise the per-token output (three dimensions)
    /// is pooled as the settings say.
    /// </summary>
    private static (string Name, bool Pooled, int Dimensions) ChooseOutput(IReadOnlyDictionary<string, NodeMetadata> outputs)
    {
        var floats = outputs.Where(o => o.Value.IsTensor && o.Value.ElementType == typeof(float)).ToList();
        var sentence = floats.FirstOrDefault(o => o.Key == "sentence_embedding" && o.Value.Dimensions.Length == 2);
        if (sentence.Key is null)
            sentence = floats.FirstOrDefault(o => o.Value.Dimensions.Length == 2 && o.Key != "pooler_output");
        if (sentence.Key is not null) return (sentence.Key, false, Math.Max(0, sentence.Value.Dimensions[1]));

        var tokens = floats.FirstOrDefault(o => o.Key is "last_hidden_state" or "token_embeddings" && o.Value.Dimensions.Length == 3);
        if (tokens.Key is null) tokens = floats.FirstOrDefault(o => o.Value.Dimensions.Length == 3);
        if (tokens.Key is not null) return (tokens.Key, true, Math.Max(0, tokens.Value.Dimensions[2]));

        throw new SemanticUnavailableException("这个模型的输出既不是句向量（二维 float）也不是逐词向量（三维 float），用不了");
    }

    public int Dimensions
    {
        get
        {
            // A model that leaves the size open says it with its first answer.
            if (_dimensions <= 0) _dimensions = Embed(["维度"], query: false)[0].Length;
            return _dimensions;
        }
    }

    public float[][] Embed(IReadOnlyList<string> texts, bool query, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(texts);
        var prefix = query ? _config.QueryPrefix : _config.PassagePrefix;
        var encoded = texts.Select(t => _tokenizer.Encode(prefix + t, _config.MaxTokens)).ToArray();
        // Like with like: a batch is padded to its longest text only.
        var order = Enumerable.Range(0, encoded.Length).OrderBy(i => encoded[i].Length).ToArray();
        var vectors = new float[encoded.Length][];
        for (var start = 0; start < order.Length; start += BatchSize)
        {
            cancellation.ThrowIfCancellationRequested();
            var batch = order[start..Math.Min(order.Length, start + BatchSize)];
            var embedded = Run(batch.Select(i => encoded[i]).ToArray());
            for (var k = 0; k < batch.Length; k++) vectors[batch[k]] = embedded[k];
        }
        return vectors;
    }

    private float[][] Run(long[][] batch)
    {
        var rows = batch.Length;
        var length = batch.Max(b => b.Length);
        var ids = new long[rows * length];
        var mask = new long[rows * length];
        for (var r = 0; r < rows; r++)
        {
            Array.Fill(ids, _tokenizer.PadId, r * length, length);
            batch[r].CopyTo(ids, r * length);
            Array.Fill(mask, 1L, r * length, batch[r].Length);
        }
        long[] shape = [rows, length];

        var values = new List<OrtValue>(_inputs.Length);
        try
        {
            foreach (var (_, kind, int32) in _inputs)
            {
                var data = kind switch
                {
                    InputKind.Ids => ids,
                    InputKind.Mask => mask,
                    _ => new long[rows * length],
                };
                values.Add(int32
                    ? OrtValue.CreateTensorValueFromMemory(Array.ConvertAll(data, v => (int)v), shape)
                    : OrtValue.CreateTensorValueFromMemory(data, shape));
            }
            using var options = new RunOptions();
            using var results = _session.Run(options, _inputs.Select(i => i.Name).ToArray(), values, [_output]);
            var output = results[0];
            var outputShape = output.GetTensorTypeAndShape().Shape;
            var flat = output.GetTensorDataAsSpan<float>();
            var vectors = new float[rows][];
            if (!_pooled)
            {
                var dims = (int)outputShape[1];
                for (var r = 0; r < rows; r++) vectors[r] = flat.Slice(r * dims, dims).ToArray();
            }
            else
            {
                var tokens = (int)outputShape[1];
                var dims = (int)outputShape[2];
                for (var r = 0; r < rows; r++)
                {
                    var rowData = flat.Slice(r * tokens * dims, tokens * dims);
                    vectors[r] = _config.Pooling == EmbeddingPooling.Cls
                        ? rowData[..dims].ToArray()
                        : Mean(rowData, dims, Math.Min(tokens, batch[r].Length));
                }
            }
            if (_config.Normalize)
                foreach (var v in vectors) Normalize(v);
            return vectors;
        }
        catch (OnnxRuntimeException ex)
        {
            throw new InvalidOperationException("向量模型运行出错：" + ex.Message, ex);
        }
        finally
        {
            foreach (var value in values) value.Dispose();
        }
    }

    /// <summary>The average of the real tokens' vectors; padding does not count.</summary>
    private static float[] Mean(ReadOnlySpan<float> tokens, int dims, int real)
    {
        var mean = new float[dims];
        for (var t = 0; t < real; t++)
        {
            var row = tokens.Slice(t * dims, dims);
            for (var d = 0; d < dims; d++) mean[d] += row[d];
        }
        if (real > 0)
            for (var d = 0; d < dims; d++) mean[d] /= real;
        return mean;
    }

    private static void Normalize(float[] vector)
    {
        var sum = 0.0;
        foreach (var v in vector) sum += (double)v * v;
        if (sum <= 0) return;
        var scale = (float)(1 / Math.Sqrt(sum));
        for (var i = 0; i < vector.Length; i++) vector[i] *= scale;
    }

    public void Dispose()
    {
        _session.Dispose();
        _options.Dispose();
    }
}
