using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace McadiffHub.Tests;

/// <summary>
/// `/have` must check auth before deserializing the request body (#3), so an unauthorized caller can't
/// force JSON parsing/allocation against a private repo. A malformed body from an unauthorized caller
/// is rejected by the readable check (404) and never parsed; an authorized caller's valid body still works.
/// </summary>
public class HaveAuthOrderTests
{
    [Fact]
    public async Task Unauthorized_have_on_private_repo_is_rejected_without_parsing_the_body()
    {
        using var f = new HubFactory(HubMode.Accounts);
        HttpClient alice = await Accounts.SignInAsync(f, "alice");
        string aliceToken = await Accounts.MintTokenAsync(alice);
        await Accounts.CreateRepoAsync(f, aliceToken, "secret");
        await Accounts.SetPrivateAsync(alice, "secret", isPrivate: true);

        using HttpClient anon = f.CreateClient();
        using var body = new StringContent("{ this is not valid json", Encoding.UTF8, "application/json");
        HttpResponseMessage resp = await anon.PostAsync("/r/secret/have", body);

        // If the body were parsed before the auth check, malformed JSON would error out (500/400);
        // with auth first, the unreadable repo returns 404 and the body is never touched.
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Authorized_have_returns_the_missing_set()
    {
        using var f = new HubFactory(HubMode.Accounts);
        HttpClient alice = await Accounts.SignInAsync(f, "alice");
        string aliceToken = await Accounts.MintTokenAsync(alice);
        await Accounts.CreateRepoAsync(f, aliceToken, "world");

        using HttpClient c = f.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, "/r/world/have")
        {
            Content = new StringContent("[\"deadbeef\"]", Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", aliceToken);
        HttpResponseMessage resp = await c.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("deadbeef", await resp.Content.ReadAsStringAsync()); // unknown hash → reported missing
    }
}
