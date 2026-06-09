using System.Net;

namespace McaHub.Tests;

/// <summary>
/// Claim-on-first-push (#6): in accounts mode a genuinely new name may auto-create and claim itself, but
/// a repo that already exists on disk with no owner (a world that predates accounts) must NOT be
/// claimable by a non-admin — unless adoption is explicitly enabled. Master-token pushes leave an owner
/// behind so they aren't orphan-claimable either.
/// </summary>
public class ClaimTakeoverTests
{
    private static void SeedLegacyRepo(HubFactory f, string name)
    {
        _ = f.CreateClient();                     // make sure the host (and its temp config) is built
        var store = new RepoStore(f.DataDir, McaHub.Rust.RustEngine.FromEnv());
        Directory.CreateDirectory(Path.Combine(store.PathOf(name), "objects")); // an on-disk repo with no hub.json owner
    }

    [Fact]
    public async Task Non_admin_cannot_claim_a_preexisting_unowned_repo()
    {
        using var f = new HubFactory(HubMode.Accounts);
        SeedLegacyRepo(f, "legacy");

        HttpClient alice = await Accounts.SignInAsync(f, "alice");
        string aliceToken = await Accounts.MintTokenAsync(alice);
        using HttpResponseMessage resp = await Accounts.PushAsync(f, aliceToken, "legacy");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode); // refused, not silently claimed
    }

    [Fact]
    public async Task A_genuinely_new_name_still_creates_and_claims()
    {
        using var f = new HubFactory(HubMode.Accounts);
        HttpClient alice = await Accounts.SignInAsync(f, "alice");
        string aliceToken = await Accounts.MintTokenAsync(alice);
        await Accounts.CreateRepoAsync(f, aliceToken, "fresh"); // throws if creation/claim was refused
    }

    [Fact]
    public async Task Adoption_window_lets_a_user_claim_an_unowned_repo()
    {
        using var f = new HubFactory(HubMode.Accounts, settings: [new("AdoptUnowned", "1")]);
        SeedLegacyRepo(f, "legacy");

        HttpClient alice = await Accounts.SignInAsync(f, "alice");
        string aliceToken = await Accounts.MintTokenAsync(alice);
        using HttpResponseMessage resp = await Accounts.PushAsync(f, aliceToken, "legacy");
        Assert.NotEqual(HttpStatusCode.Forbidden, resp.StatusCode); // adoption permitted the write
    }

    [Fact]
    public async Task Master_token_push_is_not_orphan_claimable()
    {
        // Even with adoption ON, a repo created by a master-token push has an owner (__system__) and so
        // is not claimable by a regular user.
        using var f = new HubFactory(HubMode.Accounts, settings: [new("PushToken", "master"), new("AdoptUnowned", "1")]);
        using (await Accounts.PushAsync(f, "master", "viamaster")) { } // admin push creates + owns it

        HttpClient bob = await Accounts.SignInAsync(f, "bob");
        string bobToken = await Accounts.MintTokenAsync(bob);
        using HttpResponseMessage resp = await Accounts.PushAsync(f, bobToken, "viamaster");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }
}
