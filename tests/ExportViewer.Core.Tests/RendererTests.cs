using ExportViewer.Core.Rendering;

namespace ExportViewer.Core.Tests;

public class RendererTests
{
    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("<img src=x onerror=alert(1)>")]
    [InlineData("<iframe src=\"https://example.org\"></iframe>")]
    [InlineData("<a href=\"javascript:alert(1)\">x</a>")]
    [InlineData("<style>body{display:none}</style>")]
    [InlineData("<svg onload=alert(1)>")]
    public void Raw_html_in_chat_text_is_shown_as_text(string input)
    {
        var html = SafeHtmlRenderer.Markdown(input);

        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<iframe", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<svg", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<style", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;", html);
    }

    [Fact]
    public void Links_lose_their_href_and_keep_the_url_for_confirmation()
    {
        var html = SafeHtmlRenderer.Markdown("See [the docs](https://example.org/page?a=1&b=2) and https://example.net");

        Assert.DoesNotContain(" href=", html);
        Assert.Contains("data-href=\"https://example.org/page?a=1&amp;b=2\"", html);
        Assert.Contains("data-href=\"https://example.net\"", html);
        Assert.Contains("class=\"ext-link\"", html);
    }

    [Theory]
    [InlineData("[x](javascript:alert(1))")]
    [InlineData("[x](data:text/html,<script>alert(1)</script>)")]
    [InlineData("[x](vbscript:msgbox)")]
    [InlineData("[x](file:///C:/Windows)")]
    public void Dangerous_link_schemes_are_dropped(string md)
    {
        var html = SafeHtmlRenderer.Markdown(md);

        Assert.DoesNotContain("href=", html);
        Assert.DoesNotContain("javascript:", html.Replace("&quot;", ""), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("![chart](https://example.org/tracker.png)", "example.org")]
    [InlineData("![](http://example.org/a.gif)", "example.org")]
    [InlineData("![inline](data:image/png;base64,iVBORw0KGgo=)", "inline")]
    public void Images_are_never_loaded(string md, string expectedText)
    {
        var html = SafeHtmlRenderer.Markdown(md);

        Assert.DoesNotContain("<img", html);
        Assert.DoesNotContain("src=", html);
        Assert.Contains("blocked-image", html);
        Assert.Contains(expectedText, html);
    }

    [Fact]
    public void Ordinary_markdown_renders()
    {
        var html = SafeHtmlRenderer.Markdown("# Title\n\n**bold** and `code`\n\n| a | b |\n|---|---|\n| 1 | 2 |\n\n- [x] done\n\n```csharp\nvar x = 1;\n```");

        Assert.Contains("<h1>Title</h1>", html);
        Assert.Contains("<strong>bold</strong>", html);
        Assert.Contains("<table>", html);
        Assert.Contains("class=\"language-csharp\"", html);
        Assert.Contains("disabled", html);
    }

    [Fact]
    public void Plain_text_is_encoded_and_long_text_is_truncated()
    {
        Assert.Equal("<pre class=\"plain\">&lt;b&gt;hi&lt;/b&gt;</pre>", SafeHtmlRenderer.PlainText("<b>hi</b>"));
        Assert.Contains("Showing the first 10", SafeHtmlRenderer.PlainText(new string('x', 50), maxChars: 10));
    }
}
