using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using ClaudeLauncher.Core.Display;

namespace ClaudeLauncher.App;

/// <summary>
/// Lays out a message as a flow document: what the assistant wrote as the Markdown it is —
/// headings, lists, tables, code — and what the user wrote as typed. The find word is marked
/// wherever it occurs. Sizes and colours are resource references, so zoom and theme follow.
/// </summary>
internal static class MarkdownDocument
{
    /// <summary>Marks the runs that hold the find word, so the window can scroll to the first.</summary>
    public static readonly object HighlightTag = new();

    private const CompareOptions Loose = CompareOptions.IgnoreCase | CompareOptions.IgnoreWidth | CompareOptions.IgnoreKanaType;

    public static FlowDocument Markdown(IReadOnlyList<MdBlock> blocks, string? highlight)
    {
        var document = NewDocument();
        for (var i = 0; i < blocks.Count; i++)
        {
            var block = Block(blocks[i], highlight, level: 0);
            if (i == 0) block.Margin = new Thickness(0, 0, 0, block.Margin.Bottom);
            if (i == blocks.Count - 1) block.Margin = new Thickness(block.Margin.Left, block.Margin.Top, block.Margin.Right, 0);
            document.Blocks.Add(block);
        }
        return document;
    }

    /// <summary>Text shown as typed: every line break kept, nothing interpreted.</summary>
    public static FlowDocument Plain(string text, string? highlight)
    {
        var document = NewDocument();
        var paragraph = new Paragraph { Margin = new Thickness(0) };
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0) paragraph.Inlines.Add(new LineBreak());
            AddText(paragraph.Inlines, lines[i], highlight);
        }
        document.Blocks.Add(paragraph);
        return document;
    }

    private static FlowDocument NewDocument()
    {
        var document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            TextAlignment = TextAlignment.Left,
            LineStackingStrategy = LineStackingStrategy.MaxHeight,
        };
        document.SetResourceReference(System.Windows.Documents.Block.LineHeightProperty, "MessageLineHeight");
        return document;
    }

    // ------------------------------------------------------------------ blocks

    private static Block Block(MdBlock block, string? highlight, int level) => block switch
    {
        MdHeading h => Heading(h, highlight),
        MdParagraph p => Paragraph(p.Inlines, highlight, new Thickness(0, 0, 0, 8)),
        MdCodeBlock c => CodeBlock(c, highlight),
        MdQuote q => Quote(q, highlight, level),
        MdList l => List(l, highlight, level),
        MdTable t => Table(t, highlight),
        _ => Rule(),
    };

    private static Paragraph Paragraph(IReadOnlyList<MdInline> inlines, string? highlight, Thickness margin)
    {
        var paragraph = new Paragraph { Margin = margin };
        AddInlines(paragraph.Inlines, inlines, highlight);
        return paragraph;
    }

    private static Paragraph Heading(MdHeading heading, string? highlight)
    {
        var paragraph = Paragraph(heading.Inlines, highlight, new Thickness(0, 12, 0, 6));
        paragraph.FontWeight = heading.Level <= 3 ? FontWeights.SemiBold : FontWeights.Bold;
        if (heading.Level <= 3) paragraph.SetResourceReference(TextElement.FontSizeProperty, $"MessageH{heading.Level}FontSize");
        return paragraph;
    }

    private static Paragraph CodeBlock(MdCodeBlock code, string? highlight)
    {
        var paragraph = new Paragraph
        {
            Margin = new Thickness(0, 2, 0, 10),
            Padding = new Thickness(10, 7, 10, 8),
            LineHeight = double.NaN,
            LineStackingStrategy = LineStackingStrategy.MaxHeight,
        };
        paragraph.SetResourceReference(TextElement.FontFamilyProperty, "MonoFont");
        paragraph.SetResourceReference(TextElement.FontSizeProperty, "DetailFontSize");
        paragraph.SetResourceReference(TextElement.BackgroundProperty, "CodeBlockBrush");
        paragraph.SetResourceReference(TextElement.ForegroundProperty, "CodeTextBrush");
        var lines = code.Code.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0) paragraph.Inlines.Add(new LineBreak());
            AddText(paragraph.Inlines, lines[i], highlight);
        }
        return paragraph;
    }

    private static Section Quote(MdQuote quote, string? highlight, int level)
    {
        var section = new Section
        {
            Margin = new Thickness(0, 2, 0, 10),
            Padding = new Thickness(12, 0, 0, 0),
            BorderThickness = new Thickness(3, 0, 0, 0),
        };
        section.SetResourceReference(TextElement.ForegroundProperty, "MutedBrush");
        section.SetResourceReference(System.Windows.Documents.Block.BorderBrushProperty, "LineBrush");
        foreach (var inner in quote.Blocks) section.Blocks.Add(Block(inner, highlight, level + 1));
        Tighten(section.Blocks);
        return section;
    }

    private static List List(MdList list, string? highlight, int level)
    {
        var tasks = list.Items.Any(i => i.Checked is not null);
        var result = new List
        {
            MarkerStyle = tasks ? TextMarkerStyle.None
                : list.Ordered ? TextMarkerStyle.Decimal
                : (level % 3) switch { 0 => TextMarkerStyle.Disc, 1 => TextMarkerStyle.Circle, _ => TextMarkerStyle.Square },
            Margin = new Thickness(0, 0, 0, level == 0 ? 8 : 2),
            Padding = new Thickness(tasks ? 4 : list.Ordered && list.Start + list.Items.Count > 9 ? 30 : 24, 0, 0, 0),
        };
        if (list.Ordered) result.StartIndex = Math.Max(1, list.Start);

        foreach (var item in list.Items)
        {
            var listItem = new ListItem();
            foreach (var inner in item.Blocks) listItem.Blocks.Add(Block(inner, highlight, level + 1));
            if (listItem.Blocks.Count == 0) listItem.Blocks.Add(new Paragraph());
            Tighten(listItem.Blocks);
            if (item.Checked is { } ticked && listItem.Blocks.FirstBlock is Paragraph first)
                Prepend(first, new Run(ticked ? "☑ " : "☐ ") { FontFamily = new FontFamily("Segoe UI Symbol") });
            else if (tasks && listItem.Blocks.FirstBlock is Paragraph plain)
                Prepend(plain, new Run("• "));
            result.ListItems.Add(listItem);
        }
        return result;
    }

    private static void Prepend(Paragraph paragraph, Inline inline)
    {
        if (paragraph.Inlines.FirstInline is { } first) paragraph.Inlines.InsertBefore(first, inline);
        else paragraph.Inlines.Add(inline);
    }

    /// <summary>Paragraphs inside a list item or quote sit close together; the last one adds no space.</summary>
    private static void Tighten(BlockCollection blocks)
    {
        foreach (var block in blocks)
            if (block is Paragraph { Padding.Left: 0 } p && p.Margin.Bottom > 3)
                p.Margin = new Thickness(p.Margin.Left, p.Margin.Top, p.Margin.Right, 3);
        if (blocks.LastBlock is { } last && last is not System.Windows.Documents.List)
            last.Margin = new Thickness(last.Margin.Left, last.Margin.Top, last.Margin.Right, 0);
    }

    private static Table Table(MdTable table, string? highlight)
    {
        var result = new Table
        {
            CellSpacing = 0,
            Margin = new Thickness(0, 2, 0, 10),
            BorderThickness = new Thickness(1, 1, 0, 0),
        };
        result.SetResourceReference(TextElement.FontSizeProperty, "DetailFontSize");
        result.SetResourceReference(System.Windows.Documents.Block.BorderBrushProperty, "LineBrush");

        // Columns share the width by how much they hold: an index column stays narrow.
        for (var c = 0; c < table.Aligns.Count; c++)
        {
            var longest = new[] { table.Header }.Concat(table.Rows)
                .Max(row => c < row.Count ? Markdown(row[c]).Length : 0);
            result.Columns.Add(new TableColumn { Width = new GridLength(Math.Clamp(longest, 3, 40), GridUnitType.Star) });
        }

        var group = new TableRowGroup();
        group.Rows.Add(Row(table.Header, table.Aligns, highlight, header: true));
        foreach (var row in table.Rows) group.Rows.Add(Row(row, table.Aligns, highlight, header: false));
        result.RowGroups.Add(group);
        return result;

        static string Markdown(IReadOnlyList<MdInline> inlines) => Core.Display.Markdown.PlainText(inlines);
    }

    private static TableRow Row(IReadOnlyList<IReadOnlyList<MdInline>> cells, IReadOnlyList<MdAlign> aligns, string? highlight, bool header)
    {
        var row = new TableRow();
        if (header)
        {
            row.SetResourceReference(TextElement.BackgroundProperty, "RaisedBrush");
            row.FontWeight = FontWeights.SemiBold;
        }
        for (var c = 0; c < aligns.Count; c++)
        {
            var paragraph = Paragraph(c < cells.Count ? cells[c] : [], highlight, new Thickness(0));
            paragraph.TextAlignment = aligns[c] switch
            {
                MdAlign.Center => TextAlignment.Center,
                MdAlign.Right => TextAlignment.Right,
                _ => TextAlignment.Left,
            };
            var cell = new TableCell(paragraph)
            {
                Padding = new Thickness(7, 3, 7, 4),
                BorderThickness = new Thickness(0, 0, 1, 1),
            };
            cell.SetResourceReference(TableCell.BorderBrushProperty, "LineBrush");
            row.Cells.Add(cell);
        }
        return row;
    }

    private static Block Rule()
    {
        var line = new Border { Height = 1, Margin = new Thickness(0, 4, 0, 4), SnapsToDevicePixels = true };
        line.SetResourceReference(Border.BackgroundProperty, "LineBrush");
        return new BlockUIContainer(line) { Margin = new Thickness(0, 4, 0, 10) };
    }

    // ------------------------------------------------------------------ inlines

    private static void AddInlines(InlineCollection target, IReadOnlyList<MdInline> inlines, string? highlight)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case MdText text:
                    AddText(target, text.Text, highlight);
                    break;
                case MdBreak:
                    target.Add(new LineBreak());
                    break;
                case MdStrong strong:
                    var bold = new Bold();
                    AddInlines(bold.Inlines, strong.Inlines, highlight);
                    target.Add(bold);
                    break;
                case MdEmphasis emphasis:
                    var italic = new Italic();
                    AddInlines(italic.Inlines, emphasis.Inlines, highlight);
                    target.Add(italic);
                    break;
                case MdStrike strike:
                    var struck = new Span { TextDecorations = TextDecorations.Strikethrough };
                    AddInlines(struck.Inlines, strike.Inlines, highlight);
                    target.Add(struck);
                    break;
                case MdCode code:
                    var span = new Span();
                    span.SetResourceReference(TextElement.FontFamilyProperty, "MonoFont");
                    span.SetResourceReference(TextElement.BackgroundProperty, "CodeSpanBrush");
                    span.SetResourceReference(TextElement.ForegroundProperty, "CodeTextBrush");
                    AddText(span.Inlines, " " + code.Code + " ", highlight);
                    target.Add(span);
                    break;
                case MdLink link:
                    target.Add(Link(link, highlight));
                    break;
            }
        }
    }

    /// <summary>
    /// A web link opens in the browser when clicked; anything else — a file path the assistant
    /// referred to — is shown as a link would be, with its target on hover, and opens nothing.
    /// </summary>
    private static Inline Link(MdLink link, string? highlight)
    {
        if (Core.Display.Markdown.IsWebUrl(link.Url))
        {
            var hyperlink = new Hyperlink { ToolTip = link.Url, Cursor = System.Windows.Input.Cursors.Hand };
            hyperlink.SetResourceReference(TextElement.ForegroundProperty, "InfoBrush");
            AddInlines(hyperlink.Inlines, link.Inlines, highlight);
            var url = link.Url;
            hyperlink.Click += (_, _) => Shell.Open(url);
            return hyperlink;
        }
        var span = new Span { ToolTip = link.Url };
        span.SetResourceReference(TextElement.ForegroundProperty, "InfoBrush");
        AddInlines(span.Inlines, link.Inlines, highlight);
        return span;
    }

    /// <summary>Text as runs, the find word in runs of its own marked for the eye and for scrolling.</summary>
    private static void AddText(InlineCollection target, string text, string? highlight)
    {
        if (text.Length == 0) return;
        if (string.IsNullOrEmpty(highlight))
        {
            target.Add(new Run(text));
            return;
        }

        var compare = CultureInfo.InvariantCulture.CompareInfo;
        var from = 0;
        while (from < text.Length)
        {
            var at = compare.IndexOf(text.AsSpan(from), highlight, Loose, out var length);
            if (at < 0 || length == 0) break;
            at += from;
            if (at > from) target.Add(new Run(text[from..at]));
            var hit = new Run(text.Substring(at, length)) { Tag = HighlightTag };
            hit.SetResourceReference(TextElement.BackgroundProperty, "FindHighlightBrush");
            target.Add(hit);
            from = at + length;
        }
        if (from < text.Length) target.Add(new Run(text[from..]));
    }

    /// <summary>Every run holding the find word, in document order: the places F3 steps through.</summary>
    public static List<Run> Highlights(FlowDocument document)
    {
        var found = new List<Run>();
        Collect(document.Blocks, found);
        return found;
    }

    private static void Collect(IEnumerable<Block> blocks, List<Run> found)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case Paragraph p:
                    Collect(p.Inlines, found);
                    break;
                case Section s:
                    Collect(s.Blocks, found);
                    break;
                case System.Windows.Documents.List l:
                    foreach (var item in l.ListItems) Collect(item.Blocks, found);
                    break;
                case Table t:
                    foreach (var cell in t.RowGroups.SelectMany(g => g.Rows).SelectMany(r => r.Cells)) Collect(cell.Blocks, found);
                    break;
            }
        }
    }

    private static void Collect(InlineCollection inlines, List<Run> found)
    {
        foreach (var inline in inlines)
        {
            if (inline is Run { Tag: var tag } run && ReferenceEquals(tag, HighlightTag)) found.Add(run);
            else if (inline is Span span) Collect(span.Inlines, found);
        }
    }
}

/// <summary>
/// Puts a message into a read-only rich text box: its document is built from the message and
/// the find word, and rebuilt when either changes — a recycled container gets a new message,
/// or a search starts or ends.
/// </summary>
public static class MessageContent
{
    public static readonly DependencyProperty MessageProperty = DependencyProperty.RegisterAttached(
        "Message", typeof(MessageViewModel), typeof(MessageContent), new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty HighlightProperty = DependencyProperty.RegisterAttached(
        "Highlight", typeof(string), typeof(MessageContent), new PropertyMetadata(null, OnChanged));

    private static readonly DependencyProperty BuiltProperty = DependencyProperty.RegisterAttached(
        "Built", typeof(object), typeof(MessageContent), new PropertyMetadata(null));

    public static MessageViewModel? GetMessage(DependencyObject d) => (MessageViewModel?)d.GetValue(MessageProperty);
    public static void SetMessage(DependencyObject d, MessageViewModel? value) => d.SetValue(MessageProperty, value);
    public static string? GetHighlight(DependencyObject d) => (string?)d.GetValue(HighlightProperty);
    public static void SetHighlight(DependencyObject d, string? value) => d.SetValue(HighlightProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not RichTextBox box || GetMessage(box) is not { } message) return;
        var highlight = GetHighlight(box);
        if (box.GetValue(BuiltProperty) is (MessageViewModel built, string builtFor)
            && ReferenceEquals(built, message) && builtFor == (highlight ?? string.Empty))
            return;
        box.SetValue(BuiltProperty, (message, highlight ?? string.Empty));
        box.Document = message.RendersMarkdown
            ? MarkdownDocument.Markdown(message.Blocks, highlight)
            : MarkdownDocument.Plain(message.ShownText, highlight);
    }
}
