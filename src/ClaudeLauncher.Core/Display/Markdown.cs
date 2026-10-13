using System.Text;
using System.Text.RegularExpressions;

namespace ClaudeLauncher.Core.Display;

/// <summary>A block of a Markdown message: what the renderer lays out one below another.</summary>
public abstract record MdBlock;

public sealed record MdHeading(int Level, IReadOnlyList<MdInline> Inlines) : MdBlock;

public sealed record MdParagraph(IReadOnlyList<MdInline> Inlines) : MdBlock;

public sealed record MdCodeBlock(string? Language, string Code) : MdBlock;

public sealed record MdQuote(IReadOnlyList<MdBlock> Blocks) : MdBlock;

public sealed record MdList(bool Ordered, int Start, IReadOnlyList<MdListItem> Items) : MdBlock;

/// <param name="Checked">For a task list item, whether it is ticked; null for an ordinary item.</param>
public sealed record MdListItem(IReadOnlyList<MdBlock> Blocks, bool? Checked);

public enum MdAlign { None, Left, Center, Right }

public sealed record MdTable(
    IReadOnlyList<MdAlign> Aligns,
    IReadOnlyList<IReadOnlyList<MdInline>> Header,
    IReadOnlyList<IReadOnlyList<IReadOnlyList<MdInline>>> Rows) : MdBlock;

public sealed record MdRule : MdBlock;

/// <summary>A run of a Markdown line: text, emphasis, code, a link.</summary>
public abstract record MdInline;

public sealed record MdText(string Text) : MdInline;

public sealed record MdStrong(IReadOnlyList<MdInline> Inlines) : MdInline;

public sealed record MdEmphasis(IReadOnlyList<MdInline> Inlines) : MdInline;

public sealed record MdStrike(IReadOnlyList<MdInline> Inlines) : MdInline;

public sealed record MdCode(string Code) : MdInline;

public sealed record MdLink(IReadOnlyList<MdInline> Inlines, string Url) : MdInline;

/// <summary>A line break inside a paragraph. Chat text means its line breaks, so every one is kept.</summary>
public sealed record MdBreak : MdInline;

/// <summary>
/// The Markdown the assistants write — headings, lists, tables, code, emphasis, links — parsed
/// into blocks for display. A practical subset of CommonMark with GitHub's tables, task lists,
/// strikethrough and bare links; HTML stays text and nothing is fetched.
///
/// <para>Never throws and never goes quadratic: nesting is capped, and a delimiter looks for
/// its partner within a bounded distance, so a pasted log full of asterisks costs no more than
/// plain text.</para>
/// </summary>
public static partial class Markdown
{
    /// <summary>Deepest nesting of quotes and lists that is still laid out as nesting.</summary>
    private const int MaxDepth = 8;

    /// <summary>How far an opening delimiter or bracket looks for its partner.</summary>
    private const int SearchWindow = 1000;

    private const int MaxInlineDepth = 6;

    public static IReadOnlyList<MdBlock> Parse(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        return ParseBlocks(lines, 0);
    }

    // ------------------------------------------------------------------ blocks

    private static List<MdBlock> ParseBlocks(IReadOnlyList<string> lines, int depth)
    {
        var blocks = new List<MdBlock>();
        var paragraph = new List<string>();

        void Flush()
        {
            if (paragraph.Count == 0) return;
            blocks.Add(new MdParagraph(Inline(string.Join('\n', paragraph))));
            paragraph.Clear();
        }

        var i = 0;
        while (i < lines.Count)
        {
            var line = lines[i];
            if (IsBlank(line))
            {
                Flush();
                i++;
                continue;
            }

            if (OpeningFence(line) is { } fence)
            {
                Flush();
                var code = new List<string>();
                i++;
                while (i < lines.Count && !ClosesFence(lines[i], fence))
                {
                    code.Add(Dedent(lines[i], fence.Indent));
                    i++;
                }
                if (i < lines.Count) i++;       // the closing fence; an unclosed one runs to the end
                blocks.Add(new MdCodeBlock(fence.Info, string.Join('\n', code)));
                continue;
            }

            if (HeadingPattern().Match(line) is { Success: true } heading)
            {
                Flush();
                blocks.Add(new MdHeading(heading.Groups[1].Length, Inline(heading.Groups[2].Value.Trim())));
                i++;
                continue;
            }

            if (RulePattern().IsMatch(line))
            {
                Flush();
                blocks.Add(new MdRule());
                i++;
                continue;
            }

            if (depth < MaxDepth && QuoteContent(line) is not null)
            {
                Flush();
                var quoted = new List<string>();
                while (i < lines.Count && QuoteContent(lines[i]) is { } content)
                {
                    quoted.Add(content);
                    i++;
                }
                blocks.Add(new MdQuote(ParseBlocks(quoted, depth + 1)));
                continue;
            }

            // An ordered list interrupts a paragraph only from 1, or "in 2024. we" would be a list.
            if (depth < MaxDepth && ListMarker(line) is { } marker
                && (paragraph.Count == 0 || !marker.Ordered || marker.Number == 1))
            {
                Flush();
                blocks.Add(ParseList(lines, ref i, marker, depth));
                continue;
            }

            if (i + 1 < lines.Count && line.Contains('|') && TableAligns(lines[i + 1]) is { } aligns
                && Cells(line) is { } header && header.Count == aligns.Count)
            {
                Flush();
                i += 2;
                var rows = new List<IReadOnlyList<IReadOnlyList<MdInline>>>();
                while (i < lines.Count && !IsBlank(lines[i]) && lines[i].Contains('|'))
                {
                    var cells = Cells(lines[i]);
                    rows.Add(Enumerable.Range(0, aligns.Count)
                        .Select(c => (IReadOnlyList<MdInline>)Inline(c < cells.Count ? cells[c] : string.Empty))
                        .ToList());
                    i++;
                }
                blocks.Add(new MdTable(aligns, header.Select(h => (IReadOnlyList<MdInline>)Inline(h)).ToList(), rows));
                continue;
            }

            paragraph.Add(line.Trim());
            i++;
        }

        Flush();
        return blocks;
    }

    private static bool IsBlank(string line) => string.IsNullOrWhiteSpace(line);

    /// <summary>Columns of leading space, a tab counting to the next multiple of four.</summary>
    private static int Indent(string line)
    {
        var width = 0;
        foreach (var c in line)
        {
            if (c == ' ') width++;
            else if (c == '\t') width += 4 - width % 4;
            else break;
        }
        return width;
    }

    /// <summary>The line without up to <paramref name="columns"/> columns of leading space.</summary>
    private static string Dedent(string line, int columns)
    {
        var width = 0;
        var at = 0;
        while (at < line.Length && width < columns)
        {
            if (line[at] == ' ') width++;
            else if (line[at] == '\t') width += 4 - width % 4;
            else break;
            at++;
        }
        return line[at..];
    }

    private sealed record Fence(int Indent, char Char, int Length, string? Info);

    private static Fence? OpeningFence(string line)
    {
        var indent = Indent(line);
        if (indent > 3) return null;
        var rest = line.TrimStart();
        if (rest.Length < 3 || (rest[0] != '`' && rest[0] != '~')) return null;
        var c = rest[0];
        var length = 0;
        while (length < rest.Length && rest[length] == c) length++;
        if (length < 3) return null;
        var info = rest[length..].Trim();
        if (c == '`' && info.Contains('`')) return null;
        var language = info.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return new Fence(indent, c, length, language);
    }

    private static bool ClosesFence(string line, Fence fence)
    {
        if (Indent(line) > 3) return false;
        var rest = line.Trim();
        if (rest.Length < fence.Length) return false;
        foreach (var c in rest)
            if (c != fence.Char) return false;
        return true;
    }

    private static string? QuoteContent(string line)
    {
        if (Indent(line) > 3) return null;
        var rest = line.TrimStart();
        if (rest.Length == 0 || rest[0] != '>') return null;
        return rest.Length > 1 && rest[1] == ' ' ? rest[2..] : rest[1..];
    }

    private sealed record Marker(int Indent, bool Ordered, int Number, int ContentIndent, string Rest);

    private static Marker? ListMarker(string line)
    {
        var m = ListPattern().Match(line);
        if (!m.Success) return null;
        var indent = Indent(m.Groups[1].Value);
        var ordered = m.Groups[3].Success;
        var number = ordered && int.TryParse(m.Groups[3].Value, out var n) ? n : 1;
        var width = m.Groups[2].Length;
        var spaces = m.Groups[5].Length;
        var rest = m.Groups[6].Value;
        // Five or more spaces after the marker: the content starts after one of them.
        if (spaces > 4) { rest = new string(' ', spaces - 1) + rest; spaces = 1; }
        if (rest.Length == 0) spaces = 1;
        return new Marker(indent, ordered, number, indent + width + spaces, rest);
    }

    private static MdList ParseList(IReadOnlyList<string> lines, ref int i, Marker first, int depth)
    {
        var items = new List<MdListItem>();
        while (i < lines.Count && ListMarker(lines[i]) is { } marker
               && marker.Ordered == first.Ordered && marker.Indent < first.ContentIndent)
        {
            var body = new List<string> { marker.Rest };
            i++;
            while (i < lines.Count)
            {
                var next = lines[i];
                if (IsBlank(next))
                {
                    // A blank line keeps the item open only when what follows is indented into it.
                    var j = i + 1;
                    while (j < lines.Count && IsBlank(lines[j])) j++;
                    if (j < lines.Count && Indent(lines[j]) >= marker.ContentIndent)
                    {
                        for (; i < j; i++) body.Add(string.Empty);
                        continue;
                    }
                    break;
                }
                if (Indent(next) >= marker.ContentIndent)
                {
                    body.Add(Dedent(next, marker.ContentIndent));
                    i++;
                    continue;
                }
                if (ListMarker(next) is not null || StartsBlock(next)) break;
                body.Add(next.Trim());          // a lazy continuation of the item's text
                i++;
            }

            bool? ticked = null;
            if (TaskPattern().Match(body[0]) is { Success: true } task)
            {
                ticked = task.Groups[1].Value is "x" or "X";
                body[0] = body[0][task.Length..];
            }
            items.Add(new MdListItem(ParseBlocks(body, depth + 1), ticked));

            // Blank lines between items keep the list going when another item follows.
            var k = i;
            while (k < lines.Count && IsBlank(lines[k])) k++;
            if (k > i && k < lines.Count && ListMarker(lines[k]) is { } after
                && after.Ordered == first.Ordered && after.Indent < first.ContentIndent)
                i = k;
        }
        return new MdList(first.Ordered, first.Number, items);
    }

    private static bool StartsBlock(string line) =>
        OpeningFence(line) is not null || HeadingPattern().IsMatch(line) || RulePattern().IsMatch(line)
        || QuoteContent(line) is not null;

    private static IReadOnlyList<MdAlign>? TableAligns(string line)
    {
        if (!line.Contains('|') || !DelimiterRowPattern().IsMatch(line)) return null;
        return SplitCells(line).Select(cell =>
        {
            var c = cell.Trim();
            var left = c.StartsWith(':');
            var right = c.EndsWith(':');
            return left && right ? MdAlign.Center : right ? MdAlign.Right : left ? MdAlign.Left : MdAlign.None;
        }).ToList();
    }

    private static List<string> Cells(string line) => SplitCells(line).Select(c => c.Trim()).ToList();

    /// <summary>
    /// A table row's cells. A pipe inside inline code stays in its cell — GitHub would split
    /// there, but the assistants rarely escape it, and splitting mangles the row.
    /// </summary>
    private static List<string> SplitCells(string line)
    {
        var text = line.Trim();
        if (text.StartsWith('|')) text = text[1..];
        if (text.EndsWith('|') && !text.EndsWith("\\|", StringComparison.Ordinal)) text = text[..^1];
        var cells = new List<string>();
        var cell = new StringBuilder();
        var inCode = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length && text[i + 1] == '|')
            {
                cell.Append('|');
                i++;
                continue;
            }
            if (c == '`') inCode = !inCode;
            if (c == '|' && !inCode)
            {
                cells.Add(cell.ToString());
                cell.Clear();
                continue;
            }
            cell.Append(c);
        }
        cells.Add(cell.ToString());
        return cells;
    }

    [GeneratedRegex(@"^ {0,3}(#{1,6})(?:[ \t]+(.*?))?(?:[ \t]+#+)?[ \t]*$")]
    private static partial Regex HeadingPattern();

    [GeneratedRegex(@"^ {0,3}([-*_])(?:[ \t]*\1){2,}[ \t]*$")]
    private static partial Regex RulePattern();


    /// <summary>A list marker: a bullet or a number, then a space (or the end of the line).</summary>
    [GeneratedRegex(@"^([ \t]*)([-*+]|(\d{1,9})[.)])(?:(?=[ \t])|$)()([ \t]*)(.*)$")]
    private static partial Regex ListPattern();

    [GeneratedRegex(@"^\[([ xX])\][ \t]+")]
    private static partial Regex TaskPattern();

    [GeneratedRegex(@"^[ \t]*\|?[ \t]*:?-+:?[ \t]*(\|[ \t]*:?-+:?[ \t]*)*\|?[ \t]*$")]
    private static partial Regex DelimiterRowPattern();

    // ------------------------------------------------------------------ inlines

    /// <summary>The inline runs of one paragraph, heading or cell.</summary>
    public static IReadOnlyList<MdInline> Inline(string text) => ParseInline(text, 0);

    private static List<MdInline> ParseInline(string text, int depth)
    {
        var result = new List<MdInline>();
        var plain = new StringBuilder();

        void Emit(MdInline inline)
        {
            if (plain.Length > 0)
            {
                result.Add(new MdText(plain.ToString()));
                plain.Clear();
            }
            result.Add(inline);
        }

        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            switch (c)
            {
                case '\\' when i + 1 < text.Length && char.IsAsciiLetterOrDigit(text[i + 1]) is false
                               && text[i + 1] is > ' ' and <= '~':
                    plain.Append(text[i + 1]);
                    i += 2;
                    continue;

                case '\n':
                    Emit(new MdBreak());
                    i++;
                    continue;

                case '`':
                {
                    var run = RunLength(text, i, '`');
                    var close = FindBacktickRun(text, i + run, run);
                    if (close >= 0)
                    {
                        var code = text[(i + run)..close].Replace('\n', ' ');
                        if (code.Length >= 2 && code[0] == ' ' && code[^1] == ' ' && code.Trim().Length > 0) code = code[1..^1];
                        Emit(new MdCode(code));
                        i = close + run;
                    }
                    else
                    {
                        plain.Append(text, i, run);
                        i += run;
                    }
                    continue;
                }

                case '[' when depth < MaxInlineDepth && TryLink(text, i, depth) is { } link:
                    Emit(link.Link);
                    i = link.End;
                    continue;

                case '<' when TryAutolink(text, i) is { } auto:
                    Emit(new MdLink([new MdText(auto.Url)], auto.Url));
                    i = auto.End;
                    continue;

                case 'h' when (i == 0 || !char.IsAsciiLetterOrDigit(text[i - 1])) && TryBareUrl(text, i) is { } bare:
                    Emit(new MdLink([new MdText(bare.Url)], bare.Url));
                    i = bare.End;
                    continue;

                case '*' or '_' or '~' when depth < MaxInlineDepth && TryEmphasis(text, i, depth) is { } emphasis:
                    Emit(emphasis.Inline);
                    i = emphasis.End;
                    continue;
            }

            plain.Append(c);
            i++;
        }

        if (plain.Length > 0) result.Add(new MdText(plain.ToString()));
        return result;
    }

    private static int RunLength(string text, int at, char c)
    {
        var n = 0;
        while (at + n < text.Length && text[at + n] == c) n++;
        return n;
    }

    /// <summary>The start of the next run of exactly <paramref name="length"/> backticks, or -1.</summary>
    private static int FindBacktickRun(string text, int from, int length)
    {
        var limit = Math.Min(text.Length, from + SearchWindow * 4);
        var i = from;
        while (i < limit)
        {
            if (text[i] != '`')
            {
                i++;
                continue;
            }
            var run = RunLength(text, i, '`');
            if (run == length) return i;
            i += run;
        }
        return -1;
    }

    private static (MdInline Link, int End)? TryLink(string text, int open, int depth)
    {
        // The label: up to the matching bracket, code spans skipped.
        var level = 0;
        var close = -1;
        var limit = Math.Min(text.Length, open + SearchWindow);
        for (var i = open; i < limit; i++)
        {
            var c = text[i];
            if (c == '\\') { i++; continue; }
            if (c == '`')
            {
                var run = RunLength(text, i, '`');
                var end = FindBacktickRun(text, i + run, run);
                if (end >= 0) i = end + run - 1;
                continue;
            }
            if (c == '[') level++;
            else if (c == ']' && --level == 0)
            {
                close = i;
                break;
            }
        }
        if (close < 0 || close + 1 >= text.Length || text[close + 1] != '(') return null;

        // The destination: no spaces, parentheses balanced; an optional "title" is dropped.
        var at = close + 2;
        var url = new StringBuilder();
        var parens = 0;
        while (at < text.Length && at < close + 2 + SearchWindow)
        {
            var c = text[at];
            if (c == '(') parens++;
            else if (c == ')')
            {
                if (parens == 0) break;
                parens--;
            }
            else if (char.IsWhiteSpace(c)) break;
            url.Append(c);
            at++;
        }
        if (at < text.Length && char.IsWhiteSpace(text[at]))
        {
            var end = text.IndexOf(')', at);
            if (end < 0 || end > at + 300) return null;
            at = end;
        }
        if (at >= text.Length || text[at] != ')' || url.Length == 0) return null;
        var target = url.ToString().Trim('<', '>');
        var label = ParseInline(text[(open + 1)..close], depth + 1);
        return (new MdLink(label.Count > 0 ? label : [new MdText(target)], target), at + 1);
    }

    private static (string Url, int End)? TryAutolink(string text, int open)
    {
        var close = text.IndexOf('>', open + 1, Math.Min(2000, text.Length - open - 1));
        if (close < 0) return null;
        var candidate = text[(open + 1)..close];
        return IsWebUrl(candidate) && !candidate.Any(char.IsWhiteSpace) ? (candidate, close + 1) : null;
    }

    /// <summary>A bare http(s) link in running text, without the punctuation that ends a sentence around it.</summary>
    private static (string Url, int End)? TryBareUrl(string text, int at)
    {
        if (!text.AsSpan(at).StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !text.AsSpan(at).StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return null;
        var end = at;
        while (end < text.Length && !char.IsWhiteSpace(text[end]) && text[end] is not ('<' or '>' or '"' or '`')
               && !IsCjkPunctuation(text[end]) && text[end] < 0x2E80)
            end++;
        // Trailing punctuation belongs to the sentence; a closing parenthesis only when unbalanced.
        while (end > at)
        {
            var last = text[end - 1];
            if (last is '.' or ',' or ';' or ':' or '!' or '?' or '\'' or '*' or '_' or '~') { end--; continue; }
            if (last == ')' && text[at..end].Count(ch => ch == '(') < text[at..end].Count(ch => ch == ')')) { end--; continue; }
            break;
        }
        var url = text[at..end];
        return url.Length > "https://".Length ? (url, end) : null;
    }

    private static bool IsCjkPunctuation(char c) => c is '，' or '。' or '；' or '：' or '！' or '？' or '）' or '（'
        or '」' or '「' or '』' or '『' or '】' or '【' or '、' or '“' or '”' or '‘' or '’' or '《' or '》';

    public static bool IsWebUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static (MdInline Inline, int End)? TryEmphasis(string text, int open, int depth)
    {
        var c = text[open];
        var run = RunLength(text, open, c);
        if (c == '~' && run != 2) return null;
        var width = c == '~' ? 2 : run >= 2 ? 2 : 1;
        var start = open + width;
        if (start >= text.Length || char.IsWhiteSpace(text[start])) return null;
        // Underscores emphasise whole words only: snake_case_names stay as they are.
        if (c == '_' && open > 0 && char.IsLetterOrDigit(text[open - 1])) return null;

        var limit = Math.Min(text.Length, start + SearchWindow);
        for (var i = start; i < limit; i++)
        {
            var ch = text[i];
            if (ch == '\\') { i++; continue; }
            if (ch == '`')
            {
                var codeRun = RunLength(text, i, '`');
                var end = FindBacktickRun(text, i + codeRun, codeRun);
                if (end >= 0) i = end + codeRun - 1;
                else i += codeRun - 1;
                continue;
            }
            if (ch != c) continue;

            // Which delimiters of this run close the pair: a longer run ends inner pairs first
            // ("***x***", "*a **b***"), and a double run inside single emphasis is a pair of its own.
            var closeRun = RunLength(text, i, c);
            int closer;
            if (closeRun == width) closer = i;
            else if (c != '~' && width == 2 && closeRun > 2) closer = i + closeRun - 2;
            else if (c != '~' && width == 1 && closeRun % 2 == 1) closer = i + closeRun - 1;
            else
            {
                i += closeRun - 1;
                continue;
            }
            if (closer == start || char.IsWhiteSpace(text[closer - 1])
                || (c == '_' && closer + width < text.Length && char.IsLetterOrDigit(text[closer + width])))
            {
                i += closeRun - 1;
                continue;
            }

            var inner = ParseInline(text[start..closer], depth + 1);
            MdInline inline = c == '~' ? new MdStrike(inner) : width == 2 ? new MdStrong(inner) : new MdEmphasis(inner);
            return (inline, closer + width);
        }
        return null;
    }

    /// <summary>The words of a message without its markup, for a one-line excerpt.</summary>
    public static string PlainText(IReadOnlyList<MdInline> inlines)
    {
        var sb = new StringBuilder();
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case MdText t: sb.Append(t.Text); break;
                case MdCode code: sb.Append(code.Code); break;
                case MdBreak: sb.Append(' '); break;
                case MdStrong s: sb.Append(PlainText(s.Inlines)); break;
                case MdEmphasis e: sb.Append(PlainText(e.Inlines)); break;
                case MdStrike s: sb.Append(PlainText(s.Inlines)); break;
                case MdLink l: sb.Append(PlainText(l.Inlines)); break;
            }
        }
        return sb.ToString();
    }
}
