using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClaudeLauncher.Infrastructure.Semantic;

/// <summary>How a sentence vector is made from the model's per-token output.</summary>
public enum EmbeddingPooling
{
    /// <summary>The first token's vector ([CLS]): BGE models.</summary>
    Cls,

    /// <summary>The average over the real tokens: E5 and most sentence-transformers models.</summary>
    Mean,
}

/// <summary>
/// How to use the embedding model in a folder, read from its optional <c>embedding.json</c>. The
/// defaults fit BAAI/bge-small-zh-v1.5's ONNX export; an E5-style model needs mean pooling and the
/// "query: " / "passage: " prefixes.
/// </summary>
public sealed record EmbeddingConfig
{
    public const string ModelFile = "model.onnx";
    public const string VocabularyFile = "vocab.txt";
    public const string ConfigFile = "embedding.json";

    public EmbeddingPooling Pooling { get; init; } = EmbeddingPooling.Cls;

    /// <summary>Scale every vector to length 1, so a dot product is the cosine.</summary>
    public bool Normalize { get; init; } = true;

    /// <summary>Longest input in tokens, [CLS] and [SEP] included; the rest of a longer text is cut.</summary>
    public int MaxTokens { get; init; } = 512;

    /// <summary>Put before a search.</summary>
    public string QueryPrefix { get; init; } = "";

    /// <summary>Put before a passage to be found.</summary>
    public string PassagePrefix { get; init; } = "";

    /// <summary>Lower-case and strip accents before looking words up: an uncased vocabulary.</summary>
    public bool Lowercase { get; init; } = true;

    /// <summary>The folder's settings: the defaults without an embedding.json.</summary>
    /// <exception cref="SemanticUnavailableException">The file is there but cannot be read or says something unusable.</exception>
    public static EmbeddingConfig Load(string folder)
    {
        var path = Path.Combine(folder, ConfigFile);
        if (!File.Exists(path)) return new EmbeddingConfig();
        string json;
        try
        {
            json = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SemanticUnavailableException($"{ConfigFile} 读不了：{ex.Message}", ex);
        }
        return Parse(json);
    }

    /// <exception cref="SemanticUnavailableException">Not JSON, or a value of the wrong kind.</exception>
    public static EmbeddingConfig Parse(string json)
    {
        var config = new EmbeddingConfig();
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new SemanticUnavailableException($"{ConfigFile} 应该是一个 JSON 对象");
            foreach (var property in document.RootElement.EnumerateObject())
            {
                var value = property.Value;
                switch (property.Name.ToLowerInvariant())
                {
                    case "pooling":
                        config = config with
                        {
                            Pooling = (value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim().ToLowerInvariant() : null) switch
                            {
                                "cls" => EmbeddingPooling.Cls,
                                "mean" => EmbeddingPooling.Mean,
                                _ => throw new SemanticUnavailableException($"{ConfigFile} 里 pooling 只能是 \"cls\" 或 \"mean\""),
                            },
                        };
                        break;
                    case "normalize":
                        config = config with { Normalize = Flag(value, "normalize") };
                        break;
                    case "lowercase":
                        config = config with { Lowercase = Flag(value, "lowercase") };
                        break;
                    case "maxtokens":
                        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var max) || max is < 8 or > 8192)
                            throw new SemanticUnavailableException($"{ConfigFile} 里 maxTokens 应是 8 到 8192 之间的整数");
                        config = config with { MaxTokens = max };
                        break;
                    case "queryprefix":
                        config = config with { QueryPrefix = Text(value, "queryPrefix") };
                        break;
                    case "passageprefix":
                        config = config with { PassagePrefix = Text(value, "passagePrefix") };
                        break;
                }
            }
        }
        catch (JsonException ex)
        {
            throw new SemanticUnavailableException($"{ConfigFile} 不是有效的 JSON：{ex.Message}", ex);
        }
        return config;
    }

    private static bool Flag(JsonElement value, string name) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw new SemanticUnavailableException($"{ConfigFile} 里 {name} 应是 true 或 false"),
    };

    private static string Text(JsonElement value, string name) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Null => "",
        _ => throw new SemanticUnavailableException($"{ConfigFile} 里 {name} 应是一段文字"),
    };

    /// <summary>The settings in a stable form, for <see cref="ModelIdOf"/>.</summary>
    public string Describe() => string.Join(';',
        Pooling.ToString(), Normalize ? "n" : "-", MaxTokens.ToString(CultureInfo.InvariantCulture),
        Lowercase ? "l" : "-", QueryPrefix, PassagePrefix);

    /// <summary>
    /// The id of the model in a folder with these settings: its files' sizes and write times, and the
    /// settings. A model replaced, or used another way, gets another id and its index is built again.
    /// Read from the file system only — the model is not opened.
    /// </summary>
    public static string ModelIdOf(string folder, EmbeddingConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var text = new StringBuilder(config.Describe());
        foreach (var name in new[] { ModelFile, VocabularyFile })
        {
            var file = new FileInfo(Path.Combine(folder, name));
            text.Append('|').Append(name).Append(':');
            if (file.Exists)
                text.Append(file.Length.ToString(CultureInfo.InvariantCulture)).Append(':')
                    .Append(file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));
        }
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..16].ToLowerInvariant();
        var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
        return (string.IsNullOrEmpty(folderName) ? "model" : folderName) + ":" + hash;
    }
}
