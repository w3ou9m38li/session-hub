namespace ClaudeLauncher.Infrastructure.Semantic;

/// <summary>
/// Turns text into vectors whose dot product says how close two texts are in meaning. The real
/// one runs an embedding model the user supplied (the optional component, loaded only by the
/// worker process); tests use a fake. Safe to call from two threads at once — the index being
/// built while a query is answered.
/// </summary>
public interface IEmbedder : IDisposable
{
    /// <summary>
    /// Names the model and its settings: vectors made under one id cannot be compared with those
    /// of another, so an index made by another id is started over.
    /// </summary>
    string ModelId { get; }

    /// <summary>The length of every vector.</summary>
    int Dimensions { get; }

    /// <summary>
    /// One vector per text, in order, normalised when the model's settings say so.
    /// </summary>
    /// <param name="query">
    /// A search, rather than a passage to be found: some models want each marked differently
    /// (the prefixes in <see cref="EmbeddingConfig"/>), and the embedder adds the mark itself.
    /// </param>
    /// <exception cref="OperationCanceledException">Stopped between batches.</exception>
    float[][] Embed(IReadOnlyList<string> texts, bool query, CancellationToken cancellation = default);
}

/// <summary>Semantic search cannot run: a component, a model file or a setting is missing or unusable. The message says which, for the user.</summary>
public sealed class SemanticUnavailableException : Exception
{
    public SemanticUnavailableException(string message) : base(message) { }

    public SemanticUnavailableException(string message, Exception inner) : base(message, inner) { }
}
