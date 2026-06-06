using System.Net;

namespace McadiffHub.Tests;

/// <summary>
/// Governance (#35d): a per-user world cap as fair-use governance (distinct from the per-IP/size DoS
/// bounds), and an acceptable-use policy page linked from every page so there's a basis for takedown.
/// </summary>
public class GovernanceTests
{
    [Fact]
    public async Task A_per_user_world_quota_is_enforced()
    {
        using var f = new HubFactory(HubMode.Accounts, settings: [new("MaxWorldsPerUser", "2")]);
        HttpClient alice = await Accounts.SignInAsync(f, "alice");
        string token = await Accounts.MintTokenAsync(alice);
        await Accounts.CreateRepoAsync(f, token, "w1");
        await Accounts.CreateRepoAsync(f, token, "w2");

        using HttpResponseMessage third = await Accounts.PushAsync(f, token, "w3"); // over the cap
        Assert.Equal(HttpStatusCode.Forbidden, third.StatusCode);

        using var anon = f.CreateClient();
        using HttpResponseMessage missing = await anon.GetAsync("/r/w3"); // and w3 was never created
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task No_quota_by_default()
    {
        using var f = new HubFactory(HubMode.Accounts); // MaxWorldsPerUser unset → unlimited
        HttpClient alice = await Accounts.SignInAsync(f, "alice");
        string token = await Accounts.MintTokenAsync(alice);
        await Accounts.CreateRepoAsync(f, token, "w1");
        await Accounts.CreateRepoAsync(f, token, "w2");

        using HttpResponseMessage third = await Accounts.PushAsync(f, token, "w3"); // allowed
        Assert.NotEqual(HttpStatusCode.Forbidden, third.StatusCode);
    }

    [Fact]
    public async Task The_aup_page_is_served_and_linked_in_the_footer()
    {
        using var f = new HubFactory(HubMode.Accounts);
        using var c = f.CreateClient();

        string aup = await (await c.GetAsync("/aup")).Content.ReadAsStringAsync();
        Assert.Contains("Acceptable Use Policy", aup);

        string login = await (await c.GetAsync("/auth/dev")).Content.ReadAsStringAsync(); // the sign-in page
        Assert.Contains("/aup", login); // linked in the footer
    }
}
