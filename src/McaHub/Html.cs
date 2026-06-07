using System.Net;

namespace McaHub;

/// <summary>Tiny server-rendered HTML helper — a shared layout + escaping, no SPA or template engine.</summary>
public static class Html
{
    /// <summary>HTML-escape untrusted text (repo names, commit messages, block ids).</summary>
    public static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    public static IResult Page(string title, string body, string headerRight = "", string headExtra = "") =>
        Results.Content(Layout(title, body, headerRight, headExtra), "text/html; charset=utf-8");

    /// <summary>OpenGraph + Twitter-card meta so a pasted world link unfurls into the map (#25). All values
    /// are HTML-escaped; <paramref name="image"/> should be an absolute URL.</summary>
    public static string OgTags(string title, string description, string image) => $"""
        <meta property="og:title" content="{E(title)}">
        <meta property="og:description" content="{E(description)}">
        <meta property="og:image" content="{E(image)}">
        <meta property="og:type" content="website">
        <meta name="twitter:card" content="summary_large_image">
        <meta name="twitter:image" content="{E(image)}">
        """;

    public static IResult NotFound(string what, string headerRight = "")
    {
        // A plain-language message; for a world it stays deliberately ambiguous (doesn't exist OR no access)
        // so it never reveals a private world's existence (the 404-not-403 oracle).
        string msg = what == "world"
            ? "That world doesn't exist, or you don't have access to it."
            : $"No such {E(what)}.";
        string body = $"""<h1>Not found</h1><p class="empty">{msg}</p><p class="back"><a href="/">← all worlds</a></p>""";
        return Results.Content(Layout("Not found", body, headerRight), "text/html; charset=utf-8", statusCode: 404);
    }

    private static string Layout(string title, string body, string headerRight, string headExtra = "") => $$"""
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>{{E(title)}} · mcahub</title>
          <link rel="stylesheet" href="/style.css">
        {{headExtra}}
        </head>
        <body>
          <a class="skip" href="#main">Skip to content</a>
          <header>
            <a class="brand" href="/">🟩 mcahub</a><span class="tag">worlds, version-controlled</span>
            <nav class="user">{{headerRight}}</nav>
          </header>
          <main id="main">
        {{body}}
          </main>
          <footer>Self-hosted Minecraft worlds, powered by <code>mcadiff</code>. · <a href="/aup">Acceptable use</a></footer>
          <script src="/app.js"></script>
        </body>
        </html>
        """;
}
