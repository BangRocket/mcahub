namespace McaHub.Tests;

/// <summary>The README renderer is the one place the hub emits HTML not escaped by Html.E, so these
/// tests pin its sanitization: raw HTML is neutralized and dangerous URL schemes are dropped.</summary>
public class MarkdownRenderTests
{
    [Fact]
    public void Renders_basic_markdown_to_html()
    {
        string html = Markdown.Render("# Title\n\nSome **bold** text.");
        Assert.Contains("<h1", html);
        Assert.Contains("Title", html);
        Assert.Contains("<strong>bold</strong>", html);
    }

    [Fact]
    public void Neutralizes_raw_html_tags()
    {
        // DisableHtml turns raw tags into escaped text, so no live <script>/<img> element appears.
        string html = Markdown.Render("Hi <script>alert(1)</script> <img src=x onerror=alert(1)>");
        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("<img", html);
    }

    [Fact]
    public void Drops_javascript_scheme_links()
    {
        string html = Markdown.Render("[click](javascript:alert(1))");
        Assert.DoesNotContain("javascript:", html);
    }

    [Fact]
    public void Drops_data_uri_images()
    {
        string html = Markdown.Render("![x](data:text/html;base64,PHN2Zz4=)");
        Assert.DoesNotContain("data:text/html", html);
    }

    [Fact]
    public void Drops_percent_encoded_javascript_scheme()
    {
        // %3A is an encoded colon; a browser decodes it to `javascript:` and would execute it. The link
        // text is "x", so "javascript" surviving in the output means the dangerous href leaked through.
        Assert.DoesNotContain("javascript", Markdown.Render("[x](javascript%3Aalert(1))"));
        Assert.DoesNotContain("javascript", Markdown.Render("![x](javascript%3Aalert(1))"));
    }

    [Fact]
    public void Keeps_http_links_and_marks_them_nofollow()
    {
        string html = Markdown.Render("[site](https://example.com/page)");
        Assert.Contains("https://example.com/page", html);
        Assert.Contains("nofollow", html);
    }

    [Fact]
    public void Renders_pipe_tables()
    {
        string html = Markdown.Render("| a | b |\n|---|---|\n| 1 | 2 |");
        Assert.Contains("<table", html);
    }

    [Fact]
    public void Empty_input_renders_empty_string()
    {
        Assert.Equal("", Markdown.Render(null));
        Assert.Equal("", Markdown.Render("   "));
    }

    [Fact]
    public void Drops_javascript_autolink()
    {
        // CommonMark angle-bracket autolink <url> produces AutolinkInline, not LinkInline — must also be filtered.
        string html = Markdown.Render("<javascript:alert(1)>");
        Assert.DoesNotContain("javascript:", html);
    }
}
