using System.Globalization;
using System.Text;

namespace ClaudeLauncher.Infrastructure.Catalog;

/// <summary>A word of a search, and whether it is one to leave out (typed 「-词」).</summary>
public sealed record SearchTerm(string Text, bool Exclude);

/// <summary>
/// Builds the key used for searching. The displayed title is always kept exactly as the
/// source wrote it; only this derived key is normalised.
/// </summary>
public static class SearchKeys
{
    /// <summary>More words than this is a sentence pasted by accident, not a search.</summary>
    public const int MaxTerms = 8;

    /// <summary>
    /// The words a conversation must contain, as <see cref="Parse"/> reads them — each matched
    /// on its own: "wheat 病级" finds a conversation that has both, in any order. Words to leave
    /// out are not among them.
    /// </summary>
    public static IReadOnlyList<string> Terms(string? text) =>
        Parse(text).Where(t => !t.Exclude).Select(t => t.Text).ToList();

    /// <summary>
    /// The words of a search as typed. Spaces separate words and every word must match; a word
    /// in quotes — "…" or “…” — may hold spaces and is matched whole; a word written -词 leaves
    /// out whatever contains it. A single word, a Chinese phrase included (it has no spaces to
    /// split on), behaves exactly as a substring search.
    /// </summary>
    public static IReadOnlyList<SearchTerm> Parse(string? text)
    {
        var s = Normalize(text);
        var terms = new List<SearchTerm>();
        var i = 0;
        while (i < s.Length && terms.Count < MaxTerms)
        {
            if (s[i] == ' ')
            {
                i++;
                continue;
            }
            var exclude = s[i] == '-' && i + 1 < s.Length && s[i + 1] != ' ';
            if (exclude) i++;
            string word;
            if (IsQuote(s[i]))
            {
                var end = s.IndexOfAny(Quotes, i + 1);
                word = (end < 0 ? s[(i + 1)..] : s[(i + 1)..end]).Trim();
                i = end < 0 ? s.Length : end + 1;
            }
            else
            {
                var end = s.IndexOf(' ', i);
                word = end < 0 ? s[i..] : s[i..end];
                i = end < 0 ? s.Length : end;
            }
            var term = new SearchTerm(word, exclude);
            if (word.Length > 0 && !terms.Contains(term)) terms.Add(term);
        }
        return terms;
    }

    private static readonly char[] Quotes = ['"', '\u201C', '\u201D'];

    private static bool IsQuote(char c) => c is '"' or '\u201C' or '\u201D';

    /// <summary>
    /// Whether a search word could be part of a session id. Ids are hex with dashes (or
    /// Claude's <c>local_…</c> form); matching them only for words that look like one keeps
    /// "de" from finding every conversation whose id happens to contain those two letters.
    /// </summary>
    public static bool LooksLikeIdFragment(string term)
    {
        if (string.IsNullOrEmpty(term)) return false;
        if (term.StartsWith("local_", StringComparison.Ordinal)) return true;
        return term.Length >= 6 && term.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or '-');
    }

    /// <summary>
    /// NFKC folds full-width forms onto their ASCII equivalents and composes combining
    /// marks, so 「ＧＷＡＳ」 matches "gwas" and Chinese substrings match regardless of
    /// how they were encoded. Case folding is invariant, never ASCII-only NOCASE, which
    /// would silently change the meaning of non-Latin text.
    /// </summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var normalized = value.Normalize(NormalizationForm.FormKC);
        var folded = normalized.ToLower(CultureInfo.InvariantCulture);
        return CollapseWhitespace(folded);
    }

    private static string CollapseWhitespace(string value)
    {
        var sb = new StringBuilder(value.Length);
        var lastWasSpace = false;
        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace && sb.Length > 0) sb.Append(' ');
                lastWasSpace = true;
                continue;
            }
            sb.Append(ch);
            lastWasSpace = false;
        }
        return sb.ToString().TrimEnd();
    }
}
