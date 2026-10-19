using System.Globalization;
using System.Text;

namespace ClaudeLauncher.Infrastructure.Semantic;

/// <summary>
/// BERT's tokenizer, as the models in <see cref="EmbeddingConfig"/> were trained with: the basic
/// split (control characters dropped, every CJK ideograph a word of its own, lower case without
/// accents when the vocabulary is uncased, punctuation apart, whitespace), then WordPiece — the
/// longest piece of each word found in the vocabulary, the rest continued with "##" pieces, and
/// [UNK] for a word that cannot be pieced together or is longer than 100 characters. Written here
/// rather than taken from a package: it is small, and the window's process never needs it.
/// Immutable, so safe to share between threads.
/// </summary>
public sealed class WordPieceTokenizer
{
    public const string Cls = "[CLS]", Sep = "[SEP]", Unknown = "[UNK]", Pad = "[PAD]";

    /// <summary>Longer words are [UNK] outright, as in BERT.</summary>
    private const int MaxWordChars = 100;

    private readonly Dictionary<string, int> _vocabulary;
    private readonly Dictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> _lookup;
    private readonly bool _lowercase;

    public int ClsId { get; }
    public int SepId { get; }
    public int UnknownId { get; }
    public int PadId { get; }

    public int VocabularySize => _vocabulary.Count;

    /// <param name="vocabulary">The tokens in id order, one per line of vocab.txt.</param>
    /// <param name="lowercase">An uncased vocabulary: words are lower-cased and their accents stripped first.</param>
    /// <exception cref="SemanticUnavailableException">[CLS], [SEP] or [UNK] is not in the vocabulary.</exception>
    public WordPieceTokenizer(IEnumerable<string> vocabulary, bool lowercase = true)
    {
        ArgumentNullException.ThrowIfNull(vocabulary);
        _vocabulary = new Dictionary<string, int>(StringComparer.Ordinal);
        var id = 0;
        // As BERT reads it: a token listed twice keeps its later id.
        foreach (var token in vocabulary) _vocabulary[token.TrimEnd('\r', '\n')] = id++;
        _lookup = _vocabulary.GetAlternateLookup<ReadOnlySpan<char>>();
        _lowercase = lowercase;
        ClsId = Required(Cls);
        SepId = Required(Sep);
        UnknownId = Required(Unknown);
        PadId = _vocabulary.TryGetValue(Pad, out var pad) ? pad : 0;
    }

    private int Required(string token) => _vocabulary.TryGetValue(token, out var id)
        ? id
        : throw new SemanticUnavailableException($"{EmbeddingConfig.VocabularyFile} 里没有 {token}，不像 BERT 的词表");

    /// <exception cref="SemanticUnavailableException">The file is not a BERT vocabulary.</exception>
    public static WordPieceTokenizer FromFile(string path, bool lowercase = true) =>
        new(File.ReadLines(path, Encoding.UTF8), lowercase);

    /// <summary>
    /// The ids the model reads: [CLS], the pieces of <paramref name="text"/>, [SEP] — cut to
    /// <paramref name="maxTokens"/> in all.
    /// </summary>
    public long[] Encode(string text, int maxTokens)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTokens, 2);
        var ids = new List<long>(Math.Min(maxTokens, text.Length + 2)) { ClsId };
        foreach (var word in BasicWords(text))
        {
            if (!AddPieces(word, ids, maxTokens - 1)) break;
        }
        ids.Add(SepId);
        return ids.ToArray();
    }

    /// <summary>The WordPiece tokens of a text, without [CLS] and [SEP]: what <see cref="Encode"/> looks up.</summary>
    public IReadOnlyList<string> Tokenize(string text)
    {
        var reverse = new string[_vocabulary.Count == 0 ? 0 : _vocabulary.Values.Max() + 1];
        foreach (var (token, id) in _vocabulary) reverse[id] = token;
        var ids = new List<long>();
        foreach (var word in BasicWords(text)) AddPieces(word, ids, int.MaxValue);
        return ids.Select(id => reverse[id]).ToList();
    }

    /// <summary>Adds one word's pieces; false once <paramref name="limit"/> ids are reached and the rest is to be cut.</summary>
    private bool AddPieces(string word, List<long> ids, int limit)
    {
        if (ids.Count >= limit) return false;
        if (word.EnumerateRunes().Count() > MaxWordChars)
        {
            ids.Add(UnknownId);
            return ids.Count < limit;
        }

        // Greedy longest match first; a word with any part not in the vocabulary is [UNK] whole.
        var pieces = new List<int>();
        Span<char> buffer = word.Length + 2 <= 256 ? stackalloc char[word.Length + 2] : new char[word.Length + 2];
        var start = 0;
        while (start < word.Length)
        {
            var end = word.Length;
            var found = -1;
            while (start < end)
            {
                scoped ReadOnlySpan<char> candidate;
                if (start == 0)
                {
                    candidate = word.AsSpan(0, end);
                }
                else
                {
                    buffer[0] = '#';
                    buffer[1] = '#';
                    word.AsSpan(start, end - start).CopyTo(buffer[2..]);
                    candidate = buffer[..(end - start + 2)];
                }
                if (_lookup.TryGetValue(candidate, out var id))
                {
                    found = id;
                    break;
                }
                end--;
                // Never split a surrogate pair: the piece would be half a character.
                if (end > start && char.IsLowSurrogate(word[end]) && char.IsHighSurrogate(word[end - 1])) end--;
            }
            if (found < 0)
            {
                pieces.Clear();
                pieces.Add(UnknownId);
                break;
            }
            pieces.Add(found);
            start = end;
        }
        foreach (var piece in pieces)
        {
            if (ids.Count >= limit) return false;
            ids.Add(piece);
        }
        return ids.Count < limit;
    }

    /// <summary>BERT's basic tokenizer: the words WordPiece then pieces together.</summary>
    private IEnumerable<string> BasicWords(string text)
    {
        var word = new StringBuilder();
        foreach (var rune in text.EnumerateRunes())
        {
            var value = rune.Value;
            if (value == 0 || value == 0xFFFD || IsControl(rune)) continue;
            if (IsWhitespace(rune))
            {
                foreach (var w in Finish(word)) yield return w;
                continue;
            }
            if (IsCjk(value))
            {
                foreach (var w in Finish(word)) yield return w;
                foreach (var w in Normalized(rune.ToString())) yield return w;
                continue;
            }
            word.Append(rune.ToString());
        }
        foreach (var w in Finish(word)) yield return w;
    }

    private IEnumerable<string> Finish(StringBuilder word)
    {
        if (word.Length == 0) return [];
        var text = word.ToString();
        word.Clear();
        return Normalized(text);
    }

    /// <summary>Lower case and no accents when uncased, then punctuation split off as words of their own.</summary>
    private List<string> Normalized(string token)
    {
        if (_lowercase)
        {
            token = token.ToLowerInvariant().Normalize(NormalizationForm.FormD);
            var stripped = new StringBuilder(token.Length);
            foreach (var rune in token.EnumerateRunes())
                if (Rune.GetUnicodeCategory(rune) != UnicodeCategory.NonSpacingMark) stripped.Append(rune.ToString());
            token = stripped.ToString();
        }
        var words = new List<string>();
        var current = new StringBuilder();
        foreach (var rune in token.EnumerateRunes())
        {
            if (IsPunctuation(rune))
            {
                if (current.Length > 0) words.Add(current.ToString());
                current.Clear();
                words.Add(rune.ToString());
            }
            else
            {
                current.Append(rune.ToString());
            }
        }
        if (current.Length > 0) words.Add(current.ToString());
        return words;
    }

    private static bool IsWhitespace(Rune rune) =>
        rune.Value is ' ' or '\t' or '\n' or '\r' || Rune.GetUnicodeCategory(rune) == UnicodeCategory.SpaceSeparator;

    private static bool IsControl(Rune rune)
    {
        if (rune.Value is '\t' or '\n' or '\r') return false;
        return Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format
            or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned;
    }

    /// <summary>ASCII symbols count as punctuation too, as in BERT: "$", "^", "`" and the like.</summary>
    private static bool IsPunctuation(Rune rune)
    {
        var cp = rune.Value;
        if (cp is >= 33 and <= 47 or >= 58 and <= 64 or >= 91 and <= 96 or >= 123 and <= 126) return true;
        return Rune.GetUnicodeCategory(rune) is UnicodeCategory.ConnectorPunctuation or UnicodeCategory.DashPunctuation
            or UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation or UnicodeCategory.InitialQuotePunctuation
            or UnicodeCategory.FinalQuotePunctuation or UnicodeCategory.OtherPunctuation;
    }

    /// <summary>The CJK ideograph blocks BERT splits into single characters (not kana or hangul).</summary>
    private static bool IsCjk(int cp) =>
        cp is >= 0x4E00 and <= 0x9FFF or >= 0x3400 and <= 0x4DBF or >= 0x20000 and <= 0x2A6DF
            or >= 0x2A700 and <= 0x2B73F or >= 0x2B740 and <= 0x2B81F or >= 0x2B820 and <= 0x2CEAF
            or >= 0xF900 and <= 0xFAFF or >= 0x2F800 and <= 0x2FA1F;
}
