using Markdig;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace McaHub;

/// <summary>
/// Renders a repo's user-authored README (Markdown) to <b>sanitized</b> HTML. This is the only place the
/// hub emits HTML that is not routed through <see cref="Html.E"/>, so it must stay airtight:
/// <list type="bullet">
///   <item><c>DisableHtml()</c> turns any raw inline/block HTML (e.g. <c>&lt;script&gt;</c>,
///   <c>&lt;img onerror&gt;</c>) into escaped text rather than live markup.</item>
///   <item>Every link/image URL is checked against a scheme allowlist, so <c>javascript:</c> / <c>data:</c>
///   URIs that survive DisableHtml are blanked.</item>
/// </list>
/// The strict CSP (<c>script-src 'self'</c>, no <c>unsafe-inline</c>) is the backstop.
/// </summary>
public static class Markdown
{
    // Conservative pipeline: CommonMark + autolinks + pipe tables, raw HTML disabled. Built once.
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .UseAutoLinks()
        .UsePipeTables()
        .Build();

    public static string Render(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "";
        // Markdig's static API is also named Markdown — fully-qualify to avoid resolving to this class.
        MarkdownDocument doc = Markdig.Markdown.Parse(markdown, Pipeline);
        foreach (LinkInline link in doc.Descendants<LinkInline>())
        {
            if (!IsSafeUrl(link.Url)) link.Url = "";                  // blank javascript:/data:/unknown schemes
            else if (!link.IsImage) link.GetAttributes().AddProperty("rel", "nofollow ugc noopener");
        }
        return doc.ToHtml(Pipeline);
    }

    // Allow http(s), mailto, and relative/anchor URLs; reject everything else (javascript:, data:, vbscript:, …).
    private static bool IsSafeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        string u = url.TrimStart();
        int colon = u.IndexOf(':');
        if (colon < 0) return true;                                   // no scheme ⇒ relative ⇒ safe
        int slash = u.IndexOf('/'), hash = u.IndexOf('#'), q = u.IndexOf('?');
        // A separator before the first colon means the colon is in a path segment, not a scheme.
        if ((slash >= 0 && slash < colon) || (hash >= 0 && hash < colon) || (q >= 0 && q < colon)) return true;
        return u[..colon].ToLowerInvariant() is "http" or "https" or "mailto";
    }
}
