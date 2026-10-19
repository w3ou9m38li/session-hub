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

    /// <summary>
    /// An open list: the column its markers go at, the column the text of its latest item
    /// starts at (where a list or paragraph inside that item has to go to stay in it), and how
    /// many quotes were open when that item began (a deeper one was opened inside the item).
    /// </summary>
    private readonly record struct ListLevel(bool Ordered, int Count, int Column, int Content, bool InItem, int Quote);

    private sealed class Writer
    {
        private readonly StringBuilder _out = new();
        private readonly Stack<ListLevel> _lists = new();
        private readonly Stack<string?> _links = new();
        private int _markerEnd = -1;
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
            if (OpenItem is { } item && _quote > item.Quote) QuoteInItem(item.Content);
            else if (_quote > 0 && AtLineStart) _out.Append("> ");
            _out.Append(flat);
        }

        public void Tag(string name, bool closing, string attributes)
        {
            switch (name)
            {
                case "p" or "div" or "section" or "article" or "header" or "footer" or "figure" or "main":
                    if (OpenItem is { } item) ItemBlock(item); else Block();
                    break;
                case "br":
                    if (_pre > 0) _out.Append('\n');
                    else if (_inCell) Space();
                    else Line();
                    break;
                case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                    if (OpenItem is { } inItem) ItemBlock(inItem); else Block();
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
                        // Inside an item, at the column its text starts: any less and the reader
                        // takes the items for siblings of that one ("1. " needs three spaces).
                        var column = _lists.Count > 0 ? _lists.Peek().Content : 0;
                        _lists.Push(new ListLevel(name == "ol", 0, column, column, false, _quote));
                    }
                    break;
                case "li":
                    if (closing)
                    {
                        if (_lists.Count > 0) _lists.Push(_lists.Pop() with { InItem = false });
                        break;
                    }
                    Line();
                    if (_lists.Count == 0) { _out.Append("- "); break; }
                    var list = _lists.Pop();
                    var marker = list.Ordered ? $"{list.Count + 1}. " : "- ";
                    _out.Append(' ', list.Column).Append(marker);
                    _markerEnd = _out.Length;
                    _lists.Push(list with { Count = list.Count + 1, Content = list.Column + marker.Length, InItem = true, Quote = _quote });
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
                    if (OpenItem is { } quoteItem) ItemBlock(quoteItem); else Block();
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

        /// <summary>The list item being written, when a paragraph, heading or quote belongs in it.</summary>
        private ListLevel? OpenItem => _pre == 0 && !_inCell && _lists.TryPeek(out var list) && list.InItem ? list : null;

        private bool AtMarker => _out.Length == _markerEnd && _out[^1] == ' ';

        /// <summary>Nothing but indentation on the line being written.</summary>
        private bool OnlyIndented
        {
            get
            {
                for (var i = _out.Length - 1; i >= 0 && _out[i] != '\n'; i--)
                    if (_out[i] != ' ') return false;
                return true;
            }
        }

        /// <summary>
        /// A paragraph, heading or quote inside a list item (a loose list's &lt;li&gt;&lt;p&gt;): right
        /// after the marker it goes on the marker's line; later, after a blank line at the item's
        /// text column, so it stays in the item instead of ending the list with the item empty.
        /// </summary>
        private void ItemBlock(ListLevel item)
        {
            if (AtMarker) return;
            Block();
            _out.Append(' ', item.Content);
        }

        /// <summary>
        /// The marker of a quote opened inside a list item, written with the text that follows it
        /// (a dangling one would read as an empty quote): after the item's marker or at its text
        /// column, since at the start of a line it would end the item.
        /// </summary>
        private void QuoteInItem(int column)
        {
            if (AtLineStart) _out.Append(' ', column);
            if (AtMarker || OnlyIndented) _out.Append("> ");
        }
    }
}
