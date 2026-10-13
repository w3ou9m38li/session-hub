using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace ClaudeLauncher.Infrastructure.Backends;

/// <summary>
/// HTML as the Markdown the reader renders: paragraphs, headings, lists, code, emphasis, links,
/// tables. Google Takeout gives Gemini's answers as HTML; anything not listed here keeps its
/// text and loses its tag. Scripts and styles are dropped, and nothing is fetched — a picture
/// on the web becomes a link to it, never a download.
/// </summary>
public static partial class HtmlMarkdown
{
    [GeneratedRegex(@"<(script|style)\b.*?</\1\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Hidden();

    [GeneratedRegex(@"<!--.*?-->|<(/?)([a-zA-Z][a-zA-Z0-9]*)((?:[^>""']|""[^""]*""|'[^']*')*)>", RegexOptions.Singleline)]
    private static partial Regex Tag();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex Gaps();

    public static string ToMarkdown(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;
        html = Hidden().Replace(html, string.Empty);
        var writer = new Writer();
        var last = 0;
        foreach (Match tag in Tag().Matches(html))
        {
            if (tag.Index > last) writer.Text(html[last..tag.Index]);
            last = tag.Index + tag.Length;
            if (tag.Groups[2].Success)
                writer.Tag(tag.Groups[2].Value.ToLowerInvariant(), tag.Groups[1].Length > 0, tag.Groups[3].Value);
        }
        if (last < html.Length) writer.Text(html[last..]);
        return Gaps().Replace(writer.ToString(), "\n\n").Trim();
    }

    /// <summary>An attribute's value, decoded; null when the tag does not have it.</summary>
    internal static string? Attribute(string attributes, string name)
    {
        var match = Regex.Match(attributes, $@"(?:^|\s){name}\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success) return null;
        var value = match.Groups[1].Success ? match.Groups[1].Value
            : match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value;
        return WebUtility.HtmlDecode(value);
    }

    /// <summary>Only links to the web are kept as links: anything else would be a scheme the reader should never follow.</summary>
    private static bool IsWebLink(string? href) =>
        href is not null && (href.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                             || href.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        && !href.Any(c => c is ' ' or ')' or '\n' or '\r');

    private sealed class Writer
    {
        private readonly StringBuilder _out = new();
        private readonly Stack<(bool Ordered, int Count)> _lists = new();
        private readonly Stack<string?> _links = new();
        private int _pre;
        private int _quote;
        private bool _inCell;
        private int _row;
        private int _cells;

        public override string ToString() => _out.ToString();

        public void Text(string raw)
        {
            var text = WebUtility.HtmlDecode(raw);
            if (_pre > 0)
            {
                _out.Append(text.Replace("\r\n", "\n"));
                return;
            }
            var flat = Regex.Replace(text, @"\s+", " ");
            if (_inCell) flat = flat.Replace("|", "\\|");
            if (AtLineStart || _out[^1] == ' ') flat = flat.TrimStart();
            if (flat.Length == 0) return;
            if (_quote > 0 && AtLineStart) _out.Append("> ");
            _out.Append(flat);
        }

        public void Tag(string name, bool closing, string attributes)
        {
            switch (name)
            {
                case "p" or "div" or "section" or "article" or "header" or "footer" or "figure" or "main":
                    Block();
                    break;
                case "br":
                    if (_pre > 0) _out.Append('\n');
                    else if (_inCell) Space();
                    else Line();
                    break;
                case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                    Block();
                    if (!closing) _out.Append('#', name[1] - '0').Append(' ');
                    break;
                case "ul" or "ol":
                    if (closing)
                    {
                        if (_lists.Count > 0) _lists.Pop();
                        if (_lists.Count == 0) Block(); else Line();
                    }
                    else
                    {
                        if (_lists.Count == 0) Block(); else Line();
                        _lists.Push((name == "ol", 0));
                    }
                    break;
                case "li":
                    if (closing) break;
                    Line();
                    if (_lists.Count == 0) { _out.Append("- "); break; }
                    var (ordered, count) = _lists.Pop();
                    _lists.Push((ordered, count + 1));
                    _out.Append(' ', 2 * (_lists.Count - 1)).Append(ordered ? $"{count + 1}. " : "- ");
                    break;
                case "pre":
                    if (closing)
                    {
                        if (_pre == 0) break;
                        _pre--;
                        if (_out.Length > 0 && _out[^1] != '\n') _out.Append('\n');
                        _out.Append("```");
                        Block();
                    }
                    else
                    {
                        Block();
                        _out.Append("```").Append(Language(attributes)).Append('\n');
                        _pre++;
                    }
                    break;
                case "code":
                    if (_pre == 0) _out.Append('`');
                    else if (!closing && Language(attributes) is { Length: > 0 } language) FenceLanguage(language);
                    break;
                case "strong" or "b":
                    if (_pre == 0) _out.Append("**");
                    break;
                case "em" or "i":
                    if (_pre == 0) _out.Append('*');
                    break;
                case "a":
                    if (closing)
                    {
                        if (_links.Count > 0 && _links.Pop() is { } link) _out.Append("](").Append(link).Append(')');
                    }
                    else
                    {
                        var href = Attribute(attributes, "href");
                        _links.Push(IsWebLink(href) ? href : null);
                        if (IsWebLink(href)) _out.Append('[');
                    }
                    break;
                case "img":
                    var src = Attribute(attributes, "src");
                    var alt = Attribute(attributes, "alt") ?? "";
                    if (IsWebLink(src)) _out.Append("![").Append(alt.Replace("]", "")).Append("](").Append(src).Append(')');
                    else if (alt.Length > 0) _out.Append("（图片：").Append(alt).Append('）');
                    break;
                case "hr":
                    Block();
                    _out.Append("---");
                    Block();
                    break;
                case "blockquote":
                    Block();
                    _quote = closing ? Math.Max(0, _quote - 1) : _quote + 1;
                    break;
                case "table":
                    _inCell = false;
                    Block();
                    _row = 0;
                    break;
                case "tr":
                    if (closing)
                    {
                        if (_cells == 0) break;
                        if (_row++ == 0)
                        {
                            _out.Append('\n').Append('|');
                            for (var i = 0; i < _cells; i++) _out.Append(" --- |");
                        }
                    }
                    else
                    {
                        Line();
                        _out.Append('|');
                        _cells = 0;
                    }
                    break;
                case "td" or "th":
                    if (closing)
                    {
                        if (!_inCell) break;
                        TrimEndSpaces();
                        _out.Append(" |");
                        _inCell = false;
                        _cells++;
                    }
                    else
                    {
                        Space();
                        _inCell = true;
                    }
                    break;
            }
        }

        private static string Language(string attributes) =>
            Attribute(attributes, "class") is { } css
            && Regex.Match(css, @"(?:language|lang)-([A-Za-z0-9_+#-]{1,20})") is { Success: true } m
                ? m.Groups[1].Value
                : "";

        /// <summary>A language named on the code inside a pre, written onto the fence just opened.</summary>
        private void FenceLanguage(string language)
        {
            var text = _out.ToString();
            if (text.EndsWith("```\n", StringComparison.Ordinal)) _out.Insert(_out.Length - 1, language);
        }

        private bool AtLineStart => _out.Length == 0 || _out[^1] == '\n';

        private void TrimEndSpaces()
        {
            while (_out.Length > 0 && _out[^1] == ' ') _out.Length--;
        }

        private void Space()
        {
            if (_out.Length > 0 && _out[^1] != ' ') _out.Append(' ');
        }

        /// <summary>Ends the line being written, unless one has just ended (inside a table cell, a space instead).</summary>
        private void Line()
        {
            if (_inCell) { Space(); return; }
            TrimEndSpaces();
            if (_out.Length > 0 && _out[^1] != '\n') _out.Append('\n');
        }

        /// <summary>A blank line between blocks — once, however many tags open or close there.</summary>
        private void Block()
        {
            if (_pre > 0) return;
            if (_inCell) { Space(); return; }
            TrimEndSpaces();
            if (_out.Length == 0) return;
            if (_out[^1] != '\n') _out.Append('\n');
            if (_out.Length < 2 || _out[^2] != '\n') _out.Append('\n');
        }
    }
}
