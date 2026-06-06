namespace McadiffHub;

/// <summary>
/// A COPPA-style age gate (#35): with <c>MCAHUB_MIN_AGE_GATE</c> on, a signed-in user who hasn't yet
/// confirmed they're 13+ (or have parental consent) is bounced to <c>/auth/age-gate</c> on any page until
/// they acknowledge. Full parental-consent is out of scope — this disclosure + the deletion path are the
/// practical minimum. CLI/transport requests (Bearer, no cookie) are unaffected: a PAT is minted from the
/// web, which is already gated. Off by default so a school/LAN self-host isn't bothered.
/// </summary>
public static class AgeGate
{
    public static void Map(WebApplication app, HubDb db, AuditLog audit)
    {
        app.Use(async (ctx, next) =>
        {
            string path = ctx.Request.Path.Value ?? "";
            bool exempt = path.StartsWith("/auth/") || path.StartsWith("/admin/");
            if (!exempt && Auth.Current(ctx) is { } me && db.GetUser(me.Id)?.AgeAck != true)
            {
                ctx.Response.Redirect("/auth/age-gate");
                return;
            }
            await next(ctx);
        });

        app.MapGet("/auth/age-gate", (HttpContext ctx) =>
        {
            if (Auth.Current(ctx) is null) return Results.Redirect("/auth/login");
            return Html.Page("Confirm your age", $$"""
                <h1>Before you continue</h1>
                <p>To use this hub you must confirm that you are <strong>13 or older</strong>, or that you have a
                parent or guardian's permission.</p>
                <form class="find" method="post" action="/auth/age-gate">
                  {{Auth.CsrfField(ctx)}}
                  <button>I'm 13+ or have parental consent</button>
                </form>
                <form class="settings" method="post" action="/auth/logout">{{Auth.CsrfField(ctx)}}<button>Cancel &amp; sign out</button></form>
                """);
        });

        app.MapPost("/auth/age-gate", async (HttpContext ctx) =>
        {
            if (!await Auth.CsrfOk(ctx)) return Results.Text("Invalid or expired form token — reload and retry.", statusCode: 400);
            if (Auth.Current(ctx) is { } me)
            {
                db.SetAgeAck(me.Id);
                audit.Append(me.Login, "age.ack", null, "confirmed 13+ / parental consent", "web", ctx.Connection.RemoteIpAddress?.ToString());
            }
            return Results.Redirect("/account");
        });
    }
}
