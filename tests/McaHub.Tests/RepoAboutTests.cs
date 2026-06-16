using System.Net;

namespace McaHub.Tests;

/// <summary>The About/README feature at the data layer: the new HubRepoMeta fields round-trip,
/// survive a reload, and a pre-existing hub.json without them still loads (additive, no schema bump).</summary>
public class RepoAboutTests
{
    [Fact]
    public void SetRepoAbout_round_trips_and_survives_reload()
    {
        using var tmp = new TempDir();
        string path = Path.Combine(tmp.Path, "hub.json");
        var db = new HubDb(path);
        db.EnsureRepo("world", "u1", isPrivate: false);
        db.SetRepoAbout("world", "My base", "# Hello\nworld");

        Assert.Equal("My base", db.GetRepo("world")!.Description);
        Assert.Equal("# Hello\nworld", db.GetRepo("world")!.Readme);

        var reopened = new HubDb(path);                  // a fresh instance reads it back from disk
        Assert.Equal("My base", reopened.GetRepo("world")!.Description);
        Assert.Equal("# Hello\nworld", reopened.GetRepo("world")!.Readme);
    }

    [Fact]
    public void Old_hub_json_without_about_fields_still_loads()
    {
        using var tmp = new TempDir();
        string path = Path.Combine(tmp.Path, "hub.json");
        File.WriteAllText(path, """
            { "SchemaVersion": 1, "Repos": [
              { "Name": "w", "OwnerId": "u1", "Private": false, "CreatedAt": "2026-01-01T00:00:00Z" } ] }
            """);
        var db = new HubDb(path);
        Assert.NotNull(db.GetRepo("w"));
        Assert.Null(db.GetRepo("w")!.Description);
        Assert.Null(db.GetRepo("w")!.Readme);
    }

    [Fact]
    public async Task Manager_can_set_about_and_it_renders_on_the_landing_page()
    {
        using var f = new HubFactory(HubMode.Accounts);
        HttpClient owner = await Accounts.SignInAsync(f, "alice");
        string token = await Accounts.MintTokenAsync(owner);
        await Accounts.CreateRepoAsync(f, token, "base");

        HttpResponseMessage saved = await Accounts.SetAboutAsync(owner, "base",
            "Our survival world", "# Welcome\n\nDig **carefully**.");
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        Assert.Equal("/r/base", saved.Headers.Location!.ToString());

        string page = await (await owner.GetAsync("/r/base")).Content.ReadAsStringAsync();
        Assert.Contains("Our survival world", page);
        Assert.Contains("<h1", page);
        Assert.Contains("<strong>carefully</strong>", page);
    }

    [Fact]
    public async Task Non_manager_cannot_reach_the_edit_page()
    {
        using var f = new HubFactory(HubMode.Accounts);
        HttpClient owner = await Accounts.SignInAsync(f, "alice");
        string token = await Accounts.MintTokenAsync(owner);
        await Accounts.CreateRepoAsync(f, token, "base");
        await Accounts.SetPrivateAsync(owner, "base", false);

        HttpClient bob = await Accounts.SignInAsync(f, "bob");
        HttpResponseMessage edit = await bob.GetAsync("/r/base/edit");
        Assert.Equal(HttpStatusCode.NotFound, edit.StatusCode);
    }

    [Fact]
    public async Task Oversize_readme_is_rejected_not_saved()
    {
        using var f = new HubFactory(HubMode.Accounts);
        HttpClient owner = await Accounts.SignInAsync(f, "alice");
        string token = await Accounts.MintTokenAsync(owner);
        await Accounts.CreateRepoAsync(f, token, "base");

        string huge = new string('a', 40 * 1024);
        HttpResponseMessage resp = await Accounts.SetAboutAsync(owner, "base", "x", huge);
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Contains("/r/base/edit", resp.Headers.Location!.ToString());

        string page = await (await owner.GetAsync("/r/base")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(huge, page);
    }

    [Fact]
    public async Task Description_is_capped_at_200_chars()
    {
        using var f = new HubFactory(HubMode.Accounts);
        HttpClient owner = await Accounts.SignInAsync(f, "alice");
        string token = await Accounts.MintTokenAsync(owner);
        await Accounts.CreateRepoAsync(f, token, "base");

        await Accounts.SetAboutAsync(owner, "base", new string('d', 300), "");
        string page = await (await owner.GetAsync("/r/base")).Content.ReadAsStringAsync();
        Assert.Contains(new string('d', 200), page);
        Assert.DoesNotContain(new string('d', 201), page);
    }

    [Fact]
    public async Task Home_card_shows_description()
    {
        using var f = new HubFactory(HubMode.Accounts);
        HttpClient owner = await Accounts.SignInAsync(f, "alice");
        string token = await Accounts.MintTokenAsync(owner);
        await Accounts.CreateRepoAsync(f, token, "base");
        await Accounts.SetPrivateAsync(owner, "base", false);          // public so it lists for everyone
        await Accounts.SetAboutAsync(owner, "base", "A cosy hillside town", "");

        string home = await (await owner.GetAsync("/")).Content.ReadAsStringAsync();
        Assert.Contains("A cosy hillside town", home);
        Assert.Contains("class=\"desc\"", home);
    }
}
