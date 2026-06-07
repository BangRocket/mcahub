namespace McaHub.Tests;

/// <summary>
/// Usability fixes (#31): exposing a private world is guarded by a confirm, and the role dropdown carries
/// plain-language capability hints (the safety + vocabulary fixes that are assertable on the rendered page).
/// </summary>
public class UsabilityTests
{
    [Fact]
    public async Task Make_public_is_confirm_guarded_and_roles_have_capability_hints()
    {
        using var f = new HubFactory(HubMode.Accounts);
        HttpClient alice = await Accounts.SignInAsync(f, "alice");
        await Accounts.CreateRepoAsync(f, await Accounts.MintTokenAsync(alice), "secret"); // private by default

        string html = await (await alice.GetAsync("/r/secret")).Content.ReadAsStringAsync();

        Assert.Contains("Make public", html);
        Assert.Contains("data-confirm", html);                 // exposing a private world asks first
        Assert.Contains("write — push new backups", html);     // role dropdown hint (add-collaborator form)
        Assert.Contains("admin — + add/remove people", html);
    }
}
