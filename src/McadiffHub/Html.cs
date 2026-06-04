using System.Net;

namespace McadiffHub;

/// <summary>Tiny server-rendered HTML helper — a shared layout + escaping, no SPA or template engine.</summary>
public static class Html
{
    /// <summary>HTML-escape untrusted text (repo names, commit messages, block ids).</summary>
    public static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    public static IResult Page(string title, string body) => Results.Content(Layout(title, body), "text/html; charset=utf-8");

    public static IResult NotFound(string what) =>
        Results.Content(Layout("Not found", $"<p class=\"empty\">No such {E(what)}.</p>"), "text/html; charset=utf-8", statusCode: 404);

    private static string Layout(string title, string body) => $$"""
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>{{E(title)}} · mcadiff-hub</title>
          <link rel="stylesheet" href="/style.css">
        </head>
        <body>
          <header><a class="brand" href="/">🟩 mcadiff-hub</a><span class="tag">worlds, version-controlled</span></header>
          <main>
        {{body}}
          </main>
          <footer>Self-hosted Minecraft worlds, powered by <code>mcadiff</code>.</footer>
        </body>
        </html>
        """;
}
