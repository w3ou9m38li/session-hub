using ClaudeLauncher.Infrastructure.Search;

namespace ClaudeLauncher.Infrastructure.Semantic;

/// <summary>
/// Where the semantic index reads what was said: dialogue only, already taken out of the
/// transcripts by the full-text index, so nothing is parsed twice.
/// </summary>
public interface IDialogueSource
{
    /// <summary>Changes whenever anything in the source changes; equal values mean nothing new to read.</summary>
    string Fingerprint();

    /// <summary>Every conversation the source holds, with what identifies its current state.</summary>
    IReadOnlyList<IndexedSource> Sources();

    /// <summary>One conversation's messages, in order; empty when it is not there (any more).</summary>
    IReadOnlyList<IndexedMessage> Messages(string conversation);
}

/// <summary>
/// The window's full-text index as the source, read from beside the window: opened for each read
/// and let go of at once, so the window can still delete it (content search switched off) or write
/// it while a long build embeds what was read.
/// </summary>
public sealed class ContentIndexDialogue(string indexPath) : IDialogueSource
{
    /// <summary>What the user is told when there is nothing to read.</summary>
    public const string Missing = "对话内容索引还没有建立：语义搜索读的是它，需要先打开「⋯」菜单里的「搜索对话内容」，等它建好";

    public string IndexPath { get; } = indexPath;

    public string Fingerprint()
    {
        using var reader = Open();
        return reader.Fingerprint();
    }

    public IReadOnlyList<IndexedSource> Sources()
    {
        using var reader = Open();
        return reader.Sources();
    }

    public IReadOnlyList<IndexedMessage> Messages(string conversation)
    {
        using var reader = Open();
        return reader.Messages(conversation);
    }

    /// <exception cref="SemanticUnavailableException">No index there, or one of another layout.</exception>
    private ContentIndexReader Open() =>
        ContentIndexReader.Open(IndexPath) ?? throw new SemanticUnavailableException(Missing);
}
