using ClaudeLauncher.Core.Contracts;
using ClaudeLauncher.Infrastructure.Backends;
using ClaudeLauncher.Infrastructure.Launch;

namespace ClaudeLauncher.UnitTests;

/// <summary>
/// Gemini's answers come out of Google Takeout as HTML; the reader renders Markdown. What each
/// element becomes, and that nothing but a web link survives as a link.
/// </summary>
public sealed class HtmlMarkdownTests
{
    [Fact]
    public void ParagraphsHeadingsAndEmphasis()
    {
        var md = HtmlMarkdown.ToMarkdown(
            "<h3>标题</h3><p>第一段 <strong>加粗</strong> 和 <em>斜体</em>，还有 <code>a &lt; b</code>。</p><p>第二段<br>换行</p><hr><h2>再一个</h2>");
        Assert.Equal("### 标题\n\n第一段 **加粗** 和 *斜体*，还有 `a < b`。\n\n第二段\n换行\n\n---\n\n## 再一个", md);
    }

    [Fact]
    public void ListsNestAndCount()
    {
        var md = HtmlMarkdown.ToMarkdown(
            "<p>步骤：</p><ol><li>先做这个</li><li>再做那个<ul><li>细节一</li><li> 细节二 </li></ul></li><li>最后</li></ol><p>完</p>");
        Assert.Equal("步骤：\n\n1. 先做这个\n2. 再做那个\n   - 细节一\n   - 细节二\n3. 最后\n\n完", md);
        Assert.Equal("p:步骤： || ol1[p:先做这个 ; p:再做那个 | ul[p:细节一 ; p:细节二] ; p:最后] || p:完", MarkdownTests.Show(md));
    }

    [Fact]
    public void ANumberedListInANumberedItemStaysInIt()
    {
        var md = HtmlMarkdown.ToMarkdown("<ol><li>A<ol><li>a</li><li>b</li></ol></li><li>B</li></ol>");
        Assert.Equal("1. A\n   1. a\n   2. b\n2. B", md);
        Assert.Equal("ol1[p:A | ol1[p:a ; p:b] ; p:B]", MarkdownTests.Show(md));
    }

    [Fact]
    public void FromTenOnANestedListGoesUnderTheWiderNumber()
    {
        var first = string.Concat(Enumerable.Range(1, 8).Select(n => $"<li>{n}</li>"));
        var md = HtmlMarkdown.ToMarkdown($"<ol>{first}<li>九<ul><li>n</li></ul></li><li>十<ul><li>t</li></ul></li></ol>");
        Assert.EndsWith("8. 8\n9. 九\n   - n\n10. 十\n    - t", md);
        Assert.Equal("ol1[p:1 ; p:2 ; p:3 ; p:4 ; p:5 ; p:6 ; p:7 ; p:8 ; p:九 | ul[p:n] ; p:十 | ul[p:t]]", MarkdownTests.Show(md));
    }

    [Fact]
    public void ParagraphsAndHeadingsInAnItemStayInIt()
    {
        var md = HtmlMarkdown.ToMarkdown(
            "<ol><li><p>A</p><p>A2</p><ul><li>x</li></ul></li><li><h3>B</h3><div>B2</div></li></ol><p>后面</p>");
        Assert.Equal("1. A\n\n   A2\n\n   - x\n2. ### B\n\n   B2\n\n后面", md);
        Assert.Equal("ol1[p:A | p:A2 | ul[p:x] ; h3:B | p:B2] || p:后面", MarkdownTests.Show(md));
    }

    [Fact]
    public void AQuoteInAnItemStaysAQuoteInIt()
    {
        var md = HtmlMarkdown.ToMarkdown(
            "<ol><li><p>Intro</p><blockquote><p>Quote</p></blockquote></li><li>B<blockquote><p>引用</p></blockquote>后面</li></ol>");
        Assert.Equal("1. Intro\n\n   > Quote\n\n2. B\n\n   > 引用\n\n   后面", md);
        Assert.Equal("ol1[p:Intro | quote[p:Quote] ; p:B | quote[p:引用] | p:后面]", MarkdownTests.Show(md));

        // Right after the marker, and a quote of more than one line.
        md = HtmlMarkdown.ToMarkdown("<ul><li><blockquote>第一行<br>第二行</blockquote></li><li>x</li></ul>");
        Assert.Equal("- > 第一行\n  > 第二行\n\n- x", md);
        Assert.Equal("ul[quote[p:第一行<br>第二行] ; p:x]", MarkdownTests.Show(md));
    }

    [Fact]
    public void CodeBlocksKeepTheirLinesAndLanguage()
    {
        var md = HtmlMarkdown.ToMarkdown(
            "<p>代码：</p><pre><code class=\"language-python\">def f(x):\n    return x &amp;&amp; 1\n</code></pre><p>后面</p>");
        Assert.Equal("代码：\n\n```python\ndef f(x):\n    return x && 1\n```\n\n后面", md);
    }

    [Fact]
    public void TablesGetAHeaderRuleAndEscapedPipes()
    {
        var md = HtmlMarkdown.ToMarkdown(
            "<table><thead><tr><th>名称</th><th>说明</th></tr></thead><tbody>"
            + "<tr><td>a|b</td><td><p>两段</p><p>文字</p></td></tr><tr><td><strong>c</strong></td><td>d</td></tr></tbody></table>");
        Assert.Equal("| 名称 | 说明 |\n| --- | --- |\n| a\\|b | 两段 文字 |\n| **c** | d |", md);
    }

    [Fact]
    public void OnlyWebLinksStayLinksAndNothingIsFetched()
    {
        var md = HtmlMarkdown.ToMarkdown(
            "<p><a href=\"https://example.com/a?b=1&amp;c=2\">网页</a> <a href=\"javascript:alert(1)\">脚本</a> "
            + "<a href=\"file:///C:/x\">文件</a> <img src=\"https://example.com/p.png\" alt=\"图\"> <img src=\"data:image/png;base64,AA\" alt=\"内嵌\"></p>"
            + "<script>alert(1)</script><style>p{}</style>");
        Assert.Equal("[网页](https://example.com/a?b=1&c=2) 脚本 文件 ![图](https://example.com/p.png) （图片：内嵌）", md);
    }

    [Fact]
    public void QuotesAndUnknownTagsKeepTheirText()
    {
        Assert.Equal("> 引用的话\n\n正文 带 span", HtmlMarkdown.ToMarkdown(
            "<blockquote><p>引用的话</p></blockquote><div>正文 <span class=\"x\">带 span</span></div><!-- 注释 -->"));
        Assert.Equal("", HtmlMarkdown.ToMarkdown(null));
        Assert.Equal("纯文字", HtmlMarkdown.ToMarkdown("纯文字"));
    }

    [Theory]
    [InlineData("611f669baabc4573", "https://gemini.google.com/app/611f669baabc4573")]
    [InlineData("t611f669baabc4573", null)]          // put together by time: no conversation on the web
    [InlineData("611F669BAABC4573", null)]          // Gemini's own ids are lower case
    [InlineData("../app/611f669b", null)]
    [InlineData("1234567", null)]
    [InlineData(null, null)]
    public void OnlyAConversationIdFromALinkGoesIntoOne(string? id, string? link) =>
        Assert.Equal(link, WebLinks.Build(ConversationKey.GeminiWeb, id));
}
