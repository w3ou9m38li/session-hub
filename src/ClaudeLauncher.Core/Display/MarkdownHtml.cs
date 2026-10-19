using System.Net;
using System.Text;

namespace ClaudeLauncher.Core.Display;

/// <summary>
/// Parsed Markdown as HTML, for a page that keeps a conversation looking the way the reader
/// shows it. Every piece of text is escaped; only web and mail addresses become links; no
/// script, style or markup from the text ever reaches the page.
/// </summary>
public static class MarkdownHtml
{
    public static string Render(IReadOnlyList<MdBlock> blocks)
    {
        var html = new StringBuilder();
        Blocks(html, blocks);
        return html.ToString();
    }

    /// <summary>Text as the user typed it: escaped, line breaks kept by the page's styling.</summary>
    public static string Escape(string text) => WebUtility.HtmlEncode(text);

    private static void Blocks(StringBuilder html, IReadOnlyList<MdBlock> blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case MdHeading heading:
                    var level = Math.Clamp(heading.Level, 1, 6);
                    html.Append("<h").Append(level).Append('>');
                    Inlines(html, heading.Inlines);
                    html.Append("</h").Append(level).Append(">\n");
                    break;
                case MdParagraph paragraph:
                    html.Append("<p>");
                    Inlines(html, paragraph.Inlines);
                    html.Append("</p>\n");
                    break;
                case MdCodeBlock code:
                    html.Append("<pre><code");
                    if (!string.IsNullOrWhiteSpace(code.Language))
                        html.Append(" data-lang=\"").Append(Escape(code.Language.Trim())).Append('"');
                    html.Append('>').Append(Escape(code.Code)).Append("</code></pre>\n");
                    break;
                case MdQuote quote:
                    html.Append("<blockquote>\n");
                    Blocks(html, quote.Blocks);
                    html.Append("</blockquote>\n");
                    break;
                case MdList list:
                    List(html, list);
                    break;
                case MdTable table:
                    Table(html, table);
                    break;
                case MdRule:
                    html.Append("<hr>\n");
                    break;
            }
        }
    }

    private static void List(StringBuilder html, MdList list)
    {
        var tag = list.Ordered ? "ol" : "ul";
        html.Append('<').Append(tag);
        if (list.Ordered && list.Start != 1) html.Append(" start=\"").Append(list.Start).Append('"');
        if (list.Items.Any(i => i.Checked is not null)) html.Append(" class=\"tasks\"");
        html.Append(">\n");
        foreach (var item in list.Items)
        {
            html.Append("<li>");
            if (item.Checked is { } done) html.Append(done ? "<span class=\"box done\">✓</span>" : "<span class=\"box\"></span>");
            // A single paragraph sits in the item itself, as a tight list is written.
            if (item.Blocks is [MdParagraph only]) Inlines(html, only.Inlines);
            else Blocks(html, item.Blocks);
            html.Append("</li>\n");
        }
        html.Append("</").Append(tag).Append(">\n");
    }

    private static void Table(StringBuilder html, MdTable table)
    {
        html.Append("<div class=\"table\"><table>\n<thead><tr>");
        for (var i = 0; i < table.Header.Count; i++)
        {
            html.Append("<th").Append(Align(table, i)).Append('>');
            Inlines(html, table.Header[i]);
            html.Append("</th>");
        }
        html.Append("</tr></thead>\n<tbody>\n");
        foreach (var row in table.Rows)
        {
            html.Append("<tr>");
            for (var i = 0; i < row.Count; i++)
            {
                html.Append("<td").Append(Align(table, i)).Append('>');
                Inlines(html, row[i]);
                html.Append("</td>");
            }
            html.Append("</tr>\n");
        }
        html.Append("</tbody>\n</table></div>\n");
    }

    private static string Align(MdTable table, int column) =>
        column < table.Aligns.Count ? table.Aligns[column] switch
        {
            MdAlign.Center => " style=\"text-align:center\"",
            MdAlign.Right => " style=\"text-align:right\"",
            _ => "",
        } : "";

    private static void Inlines(StringBuilder html, IReadOnlyList<MdInline> inlines)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case MdText text:
                    html.Append(Escape(text.Text));
                    break;
                case MdStrong strong:
                    html.Append("<strong>");
                    Inlines(html, strong.Inlines);
                    html.Append("</strong>");
                    break;
                case MdEmphasis emphasis:
                    html.Append("<em>");
                    Inlines(html, emphasis.Inlines);
                    html.Append("</em>");
                    break;
                case MdStrike strike:
                    html.Append("<del>");
                    Inlines(html, strike.Inlines);
                    html.Append("</del>");
                    break;
                case MdCode code:
                    html.Append("<code>").Append(Escape(code.Code)).Append("</code>");
                    break;
                case MdLink link:
                    // Only somewhere a browser can go; a file path or a reference stays text, its target on hover.
                    if (IsSafeUrl(link.Url))
                        html.Append("<a href=\"").Append(Escape(link.Url)).Append("\" target=\"_blank\" rel=\"noopener noreferrer\">");
                    else
                        html.Append("<span class=\"ref\" title=\"").Append(Escape(link.Url)).Append("\">");
                    Inlines(html, link.Inlines);
                    html.Append(IsSafeUrl(link.Url) ? "</a>" : "</span>");
                    break;
                case MdBreak:
                    html.Append("<br>");
                    break;
            }
        }
    }

    /// <summary>http, https and mailto — nothing that could run in the page or reach the disk.</summary>
    public static bool IsSafeUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeMailto);
}
