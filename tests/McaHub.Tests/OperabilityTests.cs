using System.Net;

namespace McaHub.Tests;

/// <summary>
/// Operability (#32): an incompatible hub.json refuses to start with an actionable error, a save that
/// can't reach disk surfaces a recognizable exception (→ 507, not a silent divergence), and /health
/// answers proxy/orchestrator probes.
/// </summary>
public class OperabilityTests
{
    [Fact]
    public void An_unsupported_schema_version_refuses_to_load()
    {
        using var tmp = new TempDir();
        string path = Path.Combine(tmp.Path, "hub.json");
        File.WriteAllText(path, """{"SchemaVersion":999,"Users":[]}"""); // written by a newer hub

        Assert.Throws<HubDbSchemaException>(() => new HubDb(path));
    }

    [Fact]
    public void A_pre_versioned_hub_json_still_loads()
    {
        using var tmp = new TempDir();
        string path = Path.Combine(tmp.Path, "hub.json");
        File.WriteAllText(path, """{"Users":[{"Id":"alice","Login":"alice","Name":"Alice","Avatar":"","CreatedAt":"2026-01-01T00:00:00Z"}]}""");

        var db = new HubDb(path); // no SchemaVersion field → current schema, loads fine
        Assert.NotNull(db.GetUser("alice"));
    }

    [Fact]
    public void A_save_that_cannot_reach_disk_throws_HubDbSaveException()
    {
        using var tmp = new TempDir();
        string path = Path.Combine(tmp.Path, "hub.json");
        Directory.CreateDirectory(path); // a directory sits where the file should be → the atomic move fails
        var db = new HubDb(path);

        Assert.Throws<HubDbSaveException>(() => db.UpsertUser("a", "a", "A", ""));
    }

    [Fact]
    public async Task Health_endpoint_returns_ok_without_auth()
    {
        using var f = new HubFactory(HubMode.Accounts); // even in accounts mode, /health is open
        using var c = f.CreateClient();

        using HttpResponseMessage resp = await c.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("ok", await resp.Content.ReadAsStringAsync());
    }
}
