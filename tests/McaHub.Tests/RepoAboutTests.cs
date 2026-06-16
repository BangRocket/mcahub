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
}
