using System.Net;

namespace McadiffHub.Tests;

/// <summary>
/// PAT lifecycle end-to-end (#18): read-scoped tokens can't push, regenerate revokes the old token, and
/// "sign out everywhere" invalidates every other web session (via the epoch) plus all tokens.
/// </summary>
public class PatLifecycleTests
{
    [Fact]
    public async Task A_read_scoped_token_cannot_push_but_a_write_one_can()
    {
        using var f = new HubFactory(HubMode.Accounts);
        HttpClient alice = await Accounts.SignInAsync(f, "alice");
        string readTok = await Accounts.MintTokenAsync(alice, "read");
        string writeTok = await Accounts.MintTokenAsync(alice, "write");

        using HttpResponseMessage readPush = await Accounts.PushAsync(f, readTok, "world1");
        Assert.Equal(HttpStatusCode.Forbidden, readPush.StatusCode); // read-only token

        using HttpResponseMessage writePush = await Accounts.PushAsync(f, writeTok, "world1");
        Assert.NotEqual(HttpStatusCode.Forbidden, writePush.StatusCode); // allowed
    }

    [Fact]
    public async Task Regenerate_revokes_the_old_token_and_the_new_one_works()
    {
        using var f = new HubFactory(HubMode.Accounts);
        HttpClient alice = await Accounts.SignInAsync(f, "alice");
        string old = await Accounts.MintTokenAsync(alice, "write");
        string fresh = await Accounts.RegenerateAsync(alice, old[..14]); // prefix = first 14 chars

        using HttpResponseMessage oldPush = await Accounts.PushAsync(f, old, "world1");
        Assert.Equal(HttpStatusCode.Unauthorized, oldPush.StatusCode);
        using HttpResponseMessage newPush = await Accounts.PushAsync(f, fresh, "world1");
        Assert.NotEqual(HttpStatusCode.Unauthorized, newPush.StatusCode);
    }

    [Fact]
    public async Task Sign_out_everywhere_invalidates_other_sessions_and_tokens()
    {
        using var f = new HubFactory(HubMode.Accounts);
        HttpClient sessionA = await Accounts.SignInAsync(f, "alice");
        HttpClient sessionB = await Accounts.SignInAsync(f, "alice"); // a second session for the same user
        string token = await Accounts.MintTokenAsync(sessionA, "write");

        await Accounts.SignOutEverywhereAsync(sessionA);

        HttpResponseMessage b = await sessionB.GetAsync("/account"); // the other session is now stale (epoch advanced)
        Assert.Equal(HttpStatusCode.Redirect, b.StatusCode);
        Assert.StartsWith("/auth/login", b.Headers.Location?.OriginalString);

        using HttpResponseMessage push = await Accounts.PushAsync(f, token, "world1"); // and the token is gone
        Assert.Equal(HttpStatusCode.Unauthorized, push.StatusCode);
    }
}
