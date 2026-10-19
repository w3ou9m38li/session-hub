using System.Diagnostics;
using ClaudeLauncher.Core.Display;

namespace ClaudeLauncher.UnitTests;

/// <summary>The Markdown the assistants write, read into the blocks the transcript window lays out.</summary>
public sealed class MarkdownTests
{
    [Fact]
    public void HtmlEscapesTextAndLinksOnlyToTheWeb()
    {
        var html = MarkdownHtml.Render(Markdown.Parse(
            "# 标题 <b>\n\n- [x] 完成\n- [ ] 未完成\n\n| a | b |\n|:-:|--:|\n| `x<y` | [点我](javascript:alert(1)) |\n\n```js\nif (a < b) {}\n```"));

        Assert.Contains("<h1>标题 &lt;b&gt;</h1>", html, StringComparison.Ordinal);
        Assert.Contains("<ul class=\"tasks\">", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"box done\">", html, StringComparison.Ordinal);
        Assert.Contains("<th style=\"text-align:center\">a</th>", html, StringComparison.Ordinal);
        Assert.Contains("<code>x&lt;y</code>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"javascript", html, StringComparison.Ordinal);
        Assert.Contains("<pre><code data-lang=\"js\">if (a &lt; b) {}</code></pre>", html, StringComparison.Ordinal);
    }

    /// <summary>The parsed blocks in one line; also how other tests check what Markdown they wrote reads as.</summary>
    internal static string Show(string markdown) => string.Join(" || ", Markdown.Parse(markdown).Select(Block));

    private static string Block(MdBlock block) => block switch
    {
        MdHeading h => $"h{h.Level}:{Inlines(h.Inlines)}",
        MdParagraph p => $"p:{Inlines(p.Inlines)}",
        MdCodeBlock c => $"code({c.Language}):{c.Code.Replace("\n", "⏎")}",
        MdQuote q => $"quote[{string.Join(" | ", q.Blocks.Select(Block))}]",
        MdList l => (l.Ordered ? $"ol{l.Start}" : "ul") + "[" + string.Join(" ; ", l.Items.Select(i =>
            (i.Checked is null ? "" : i.Checked.Value ? "[x]" : "[ ]") + string.Join(" | ", i.Blocks.Select(Block)))) + "]",
        MdTable t => $"table({string.Join(",", t.Aligns)}):{string.Join(",", t.Header.Select(Inlines))}"
                     + string.Concat(t.Rows.Select(r => " / " + string.Join(",", r.Select(Inlines)))),
        MdRule => "hr",
        _ => "?",
    };

    private static string Inlines(IEnumerable<MdInline> inlines) => string.Concat(inlines.Select(i => i switch
    {
        MdText t => t.Text,
        MdStrong s => $"<b>{Inlines(s.Inlines)}</b>",
        MdEmphasis e => $"<i>{Inlines(e.Inlines)}</i>",
        MdStrike s => $"<s>{Inlines(s.Inlines)}</s>",
        MdCode c => $"<c>{c.Code}</c>",
        MdLink l => $"<a {l.Url}>{Inlines(l.Inlines)}</a>",
        MdBreak => "<br>",
        _ => "?",
    }));

    [Fact]
    public void HeadingsAndParagraphsKeepTheirLineBreaks() =>
        Assert.Equal("h2:查到的实情 || p:第一行<br>第二行", Show("## 查到的实情\n第一行\n第二行"));

    [Fact]
    public void EmphasisCodeAndStrike() =>
        Assert.Equal("p:<b>粗</b> 和 <i>斜</i> 和 <s>删</s> 以及 <c>code</c>", Show("**粗** 和 *斜* 和 ~~删~~ 以及 `code`"));

    [Fact]
    public void NestedEmphasisClosesInnerPairsFirst()
    {
        Assert.Equal("p:<b><i>x</i></b>", Show("***x***"));
        Assert.Equal("p:<i>a <b>b</b></i>", Show("*a **b***"));
        Assert.Equal("p:<b>A 组 <i>FDR</i> 下降</b>", Show("**A 组 *FDR* 下降**"));
    }

    [Fact]
    public void WhatIsNotEmphasisStaysText()
    {
        Assert.Equal("p:my_var_name 和 a * b * c", Show("my_var_name 和 a * b * c"));
        Assert.Equal("p:*不是斜体*", Show("\\*不是斜体\\*"));
        Assert.Equal("p:**没有结尾", Show("**没有结尾"));
    }

    [Fact]
    public void ListsNestByIndentation() =>
        Assert.Equal("ul[p:a ; p:b | ul[p:c] ; p:d]", Show("- a\n- b\n  - c\n- d"));

    [Fact]
    public void OrderedListsKeepTheirStart()
    {
        Assert.Equal("ol1[p:一 ; p:二]", Show("1. 一\n2. 二"));
        Assert.Equal("ol3[p:三]", Show("3. 三"));
        Assert.Equal("ul[p:a ; p:b]", Show("- a\n\n- b"));
    }

    [Fact]
    public void AYearAtTheStartOfALineIsNotAList() =>
        Assert.Equal("p:去年<br>2024. 我们", Show("去年\n2024. 我们"));

    [Fact]
    public void TaskListsAreTicked() =>
        Assert.Equal("ul[[x]p:完成 ; [ ]p:待办]", Show("- [x] 完成\n- [ ] 待办"));

    [Fact]
    public void FencedCodeKeepsItsIndentation()
    {
        Assert.Equal("code(python):if x:⏎    pass", Show("```python\nif x:\n    pass\n```"));
        Assert.Equal("p:前 || code():未闭合⏎到结尾", Show("前\n```\n未闭合\n到结尾"));
    }

    [Fact]
    public void CodeInsideAListItem() =>
        Assert.Equal("ol1[p:运行： | code(bash):dotnet test ; p:完成]",
            Show("1. 运行：\n   ```bash\n   dotnet test\n   ```\n2. 完成"));

    [Fact]
    public void TablesWithAlignmentAndPipesInCode() =>
        Assert.Equal("table(None,Center):a,b / 1,<c>x|y</c> / 2,",
            Show("| a | b |\n|---|:-:|\n| 1 | `x|y` |\n| 2 |"));

    [Fact]
    public void QuotesAndRules()
    {
        Assert.Equal("quote[p:引用<br>第二行]", Show("> 引用\n> 第二行"));
        Assert.Equal("p:上 || hr || p:下", Show("上\n\n---\n\n下"));
    }

    [Fact]
    public void LinksExplicitAngledAndBare() =>
        Assert.Equal(
            "p:<a https://a.b/c>文档</a> 和 <a https://x.y/z>https://x.y/z</a>。以及 <a https://q.r>https://q.r</a>",
            Show("[文档](https://a.b/c) 和 https://x.y/z。以及 <https://q.r>"));

    [Fact]
    public void ALinkToAFileKeepsItsTarget() =>
        Assert.Equal("p:见 <a src/App/MainWindow.xaml:42>MainWindow.xaml</a>", Show("见 [MainWindow.xaml](src/App/MainWindow.xaml:42)"));

    [Fact]
    public void NoLinkStartsInsideALinksText()
    {
        Assert.Equal("p:<a https://a.b/c>https://a.b/c</a>", Show("[https://a.b/c](https://a.b/c)"));
        Assert.Equal("p:<a https://x.com>见 https://x.com</a>", Show("[见 https://x.com](https://x.com)"));
        Assert.Equal("p:<a https://y.com><b>https://x.com</b></a>", Show("[**https://x.com**](https://y.com)"));
        Assert.Equal("p:<a https://y.com><https://x.com></a>", Show("[<https://x.com>](https://y.com)"));
        Assert.Equal("p:<a https://c.d>[a](https://a.b)</a>", Show("[[a](https://a.b)](https://c.d)"));

        // The exported page gets one anchor, not an anchor inside an anchor.
        var html = MarkdownHtml.Render(Markdown.Parse("[https://a.b/c](https://a.b/c)"));
        Assert.Single(html.Split("<a ", StringSplitOptions.None)[1..]);
    }

    [Fact]
    public void PathologicalInputStaysFast()
    {
        var noise = string.Concat(Enumerable.Repeat("*a _b `c [d ~~e ", 4000));
        var watch = Stopwatch.StartNew();
        var blocks = Markdown.Parse(noise);
        Assert.Single(blocks);
        Assert.True(watch.ElapsedMilliseconds < 2000, $"took {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void DeepNestingIsCapped()
    {
        var deep = string.Concat(Enumerable.Repeat("> ", 200)) + "底";
        Assert.NotEmpty(Markdown.Parse(deep));
    }
}
