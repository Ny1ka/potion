using Potion.Core.Documents;
using Xunit;

namespace Potion.Core.Tests;

public sealed class MarkdownRendererTests
{
    private static string Html(string md) => MarkdownRenderer.ToHtml(md);

    [Fact]
    public void Headings()
    {
        Assert.Equal("<h2>Title</h2>\n", Html("## Title ##"));
        Assert.Equal("<h1>Big</h1>\n<h2>Small</h2>\n", Html("Big\n===\n\nSmall\n---"));
        Assert.Equal("<p>#5 is not a heading</p>\n", Html("#5 is not a heading"));
    }

    [Fact]
    public void InlineFormatting()
    {
        Assert.Equal("<p><strong>bold</strong>, <em>it</em>, <em>also</em>, <del>gone</del>, <code>a*b</code></p>\n",
                     Html("**bold**, *it*, _also_, ~~gone~~, `a*b`"));
        Assert.Equal("<p>snake_case_name stays and 2 * 3 * 4 too</p>\n", Html("snake_case_name stays and 2 * 3 * 4 too"));
        Assert.Equal("<p>*not em* and [not a link]</p>\n", Html(@"\*not em\* and \[not a link\]"));
        Assert.Equal("<p>a &lt; b &amp; c</p>\n", Html("a < b & c"));
        Assert.Equal("<p>line one<br>\nline two</p>\n", Html("line one  \nline two"));
    }

    [Fact]
    public void Links()
    {
        Assert.Equal("<p><a href=\"https://x.y/a\">site</a> and <img src=\"pic.png\" alt=\"pic\"></p>\n", Html("[site](https://x.y/a) and ![pic](pic.png)"));
        Assert.Equal("<p>see <a href=\"https://ref.example\">this</a></p>\n", Html("see [this][r]\n\n[r]: https://ref.example"));
        Assert.Equal("<p><a href=\"https://auto.example/x\">https://auto.example/x</a>.</p>\n", Html("https://auto.example/x."));
        Assert.Equal("<p><a href=\"mailto:me@x.y\">me@x.y</a></p>\n", Html("<mailto:me@x.y>"));
    }

    [Fact]
    public void ListsNestAndStayTight()
    {
        var html = Html("- one\n- two\n  1. inner\n  2. more\n- three");
        Assert.Equal("<ul>\n<li>one</li>\n<li>two\n<ol>\n<li>inner</li>\n<li>more</li>\n</ol></li>\n<li>three</li>\n</ul>\n", html);
        Assert.StartsWith("<ol start=\"7\">", Html("7. seven\n8. eight"));
        Assert.Contains("<li><p>loose one</p>", Html("- loose one\n\n- loose two"));
    }

    [Fact]
    public void CodeQuotesRulesTablesAndHtml()
    {
        Assert.Equal("<pre><code class=\"language-swift\">let a = &lt;b&gt;\n\nlet c = 1</code></pre>\n", Html("```swift\nlet a = <b>\n\nlet c = 1\n```"));
        Assert.Equal("<pre><code>indented\ncode</code></pre>\n", Html("    indented\n    code"));
        Assert.Equal("<blockquote>\n<p>quoted\nlazy</p>\n</blockquote>\n", Html("> quoted\nlazy"));
        Assert.Equal("<hr>\n", Html("* * *"));
        Assert.Equal("<table>\n<tr><th>A</th><th style=\"text-align: right\">B</th></tr>\n<tr><td>1</td><td style=\"text-align: right\">2 | 3</td></tr>\n</table>\n",
                     Html("| A | B |\n|---|--:|\n| 1 | 2 \\| 3 |"));
        Assert.Equal("<div class=\"note\">\nraw\n</div>\n", Html("<div class=\"note\">\nraw\n</div>"));
    }
}
