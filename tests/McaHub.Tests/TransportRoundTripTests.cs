using McaDiff.Repo;

namespace McaHub.Tests;

/// <summary>
/// End-to-end transport round-trip (#20): a real world committed in a local repo, pushed to the hub with
/// the core's <c>RemoteOps</c> client, then cloned back into a fresh repo — proving the hub's server side
/// and the core's client side agree on the wire contract, and that a first push auto-creates the world.
/// </summary>
public class TransportRoundTripTests
{
    [Fact]
    public void A_world_pushed_with_RemoteOps_clones_back_to_the_same_tip()
    {
        using var f = new HubFactory(HubMode.Token); // reads anonymous, writes need the master token
        using HttpClient http = f.CreateClient();
        using var tmp = new TempDir();

        // A source repo with one committed world (two stone chunks).
        string worldDir = Worlds.Write(Path.Combine(tmp.Path, "world"), [Worlds.StoneChunk(0, 0), Worlds.StoneChunk(1, 0)]);
        var src = Repository.Init(Path.Combine(tmp.Path, "src.mcagit"));
        string tree = src.WriteManifest(Snapshotter.Snapshot(src, worldDir));
        string commit = src.CreateCommit(tree, [], "first backup", "tester");
        src.WriteBranch("main", commit);

        // Push it to a brand-new name on the hub (auto-creates "myworld").
        var push = RemoteOps.PushTo(src, new WafTransport(http, "myworld", f.MasterToken), "main", force: false);
        Assert.True(push.ObjectsCopied > 0);

        // Clone it back; anonymous reads are fine in token mode.
        string dstDir = Path.Combine(tmp.Path, "clone.mcagit");
        RemoteOps.CloneFrom(new WafTransport(http, "myworld", token: null), dstDir, originUrl: "test://myworld");

        Assert.Equal(commit, Repository.Open(dstDir).ReadBranch("main")); // same tip ⇒ same objects + history
    }

    [Fact]
    public void A_non_fast_forward_push_is_refused_without_force()
    {
        using var f = new HubFactory(HubMode.Token);
        using HttpClient http = f.CreateClient();
        using var tmp = new TempDir();
        string worldDir = Worlds.Write(Path.Combine(tmp.Path, "world"), [Worlds.StoneChunk(0, 0)]);

        var a = Repository.Init(Path.Combine(tmp.Path, "a.mcagit"));
        a.WriteBranch("main", a.CreateCommit(a.WriteManifest(Snapshotter.Snapshot(a, worldDir)), [], "a", "tester"));
        RemoteOps.PushTo(a, new WafTransport(http, "w", f.MasterToken), "main", force: false);

        // A divergent history (different commit, no shared ancestor) can't fast-forward.
        var b = Repository.Init(Path.Combine(tmp.Path, "b.mcagit"));
        b.WriteBranch("main", b.CreateCommit(b.WriteManifest(Snapshotter.Snapshot(b, worldDir)), [], "b", "other"));

        Assert.ThrowsAny<Exception>(() => RemoteOps.PushTo(b, new WafTransport(http, "w", f.MasterToken), "main", force: false));
    }
}
