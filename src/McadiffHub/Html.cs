using System.Net;

namespace McadiffHub;

/// <summary>Tiny server-rendered HTML helper — a shared layout + escaping, no SPA or template engine.</summary>
public static class Html
{
    /// <summary>HTML-escape untrusted text (repo names, commit messages, block ids).</summary>
    public static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    public static IResult Page(string title, string body, string headerRight = "") =>
        Results.Content(Layout(title, body, headerRight), "text/html; charset=utf-8");

    public static IResult NotFound(string what, string headerRight = "") =>
        Results.Content(Layout("Not found", $"<p class=\"empty\">No such {E(what)}.</p>", headerRight), "text/html; charset=utf-8", statusCode: 404);

    private static string Layout(string title, string body, string headerRight) => $$"""
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>{{E(title)}} · mcadiff-hub</title>
          <link rel="stylesheet" href="/style.css">
        </head>
        <body>
          <header>
            <a class="brand" href="/">🟩 mcadiff-hub</a><span class="tag">worlds, version-controlled</span>
            <nav class="user">{{headerRight}}</nav>
          </header>
          <main>
        {{body}}
          </main>
          <footer>Self-hosted Minecraft worlds, powered by <code>mcadiff</code>.</footer>
          <script>
          // Show a "Generating map…" spinner while a map PNG renders (cold renders take a few seconds),
          // then reveal the image. No-JS degrades to just showing the image once it loads.
          (function(){
            function wire(img){
              var box = img.closest('.map-box'); if(!box) return;
              img.addEventListener('load', function(){ box.classList.remove('loading','error'); });
              img.addEventListener('error', function(){
                box.classList.remove('loading'); box.classList.add('error');
                var s = box.querySelector('.map-status'); if(s) s.textContent = 'Map unavailable';
              });
              if(img.getAttribute('src') && (!img.complete || img.naturalWidth === 0)) box.classList.add('loading');
            }
            document.addEventListener('DOMContentLoaded', function(){ document.querySelectorAll('.map-box img').forEach(wire); });
          })();
          </script>
        </body>
        </html>
        """;
}
