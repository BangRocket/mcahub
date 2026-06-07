namespace McaHub.Tests;

/// <summary>
/// Multi-instance store safety (#41): two HubDb instances sharing one hub.json (a rolling deploy) must not
/// clobber each other's writes (reload-before-mutate under a cross-process lock) and must see each other's
/// committed changes (reload-if-changed on read). The cross-process lock (FileShare.None) is enforced
/// intra-process too, so two instances in one test process exercise the real contention path.
/// </summary>
public class MultiInstanceStoreTests
{
    private static (HubDb A, HubDb B, string Path) Pair(TempDir t)
    {
        string path = Path.Combine(t.Path, "hub.json");
        return (new HubDb(path), new HubDb(path), path);
    }

    [Fact]
    public void A_second_instance_sees_the_first_instances_write()
    {
        using var t = new TempDir();
        var (a, b, _) = Pair(t);
        a.UpsertUser("alice", "alice", "Alice", "");
        Assert.NotNull(b.GetUser("alice")); // b reloads (file stamp changed) and sees alice
    }

    [Fact]
    public void Concurrent_instances_do_not_clobber_each_others_writes()
    {
        using var t = new TempDir();
        var (a, b, _) = Pair(t);
        a.UpsertUser("alice", "alice", "Alice", "");
        b.UpsertUser("bob", "bob", "Bob", ""); // b reloads-under-lock → sees alice → adds bob → saves both

        Assert.NotNull(a.GetUser("alice"));
        Assert.NotNull(a.GetUser("bob"));
        Assert.NotNull(b.GetUser("alice")); // alice was NOT clobbered by b's write
        Assert.NotNull(b.GetUser("bob"));
    }

    [Fact]
    public void A_revoked_token_stops_resolving_on_the_other_instance()
    {
        using var t = new TempDir();
        var (a, b, _) = Pair(t);
        a.UpsertUser("alice", "alice", "Alice", "");
        string tok = a.CreateToken("alice", "laptop");

        Assert.NotNull(b.ResolveToken(tok)); // b sees the freshly-minted token
        a.RevokeAllTokens("alice");
        Assert.Null(b.ResolveToken(tok));    // b sees the revocation (reload-if-changed)
    }

    [Fact]
    public void Parallel_writers_on_one_file_lose_nothing()
    {
        using var t = new TempDir();
        var (a, b, path) = Pair(t);

        Parallel.Invoke(
            () => { for (int i = 0; i < 25; i++) a.UpsertUser($"a{i}", $"a{i}", "A", ""); },
            () => { for (int i = 0; i < 25; i++) b.UpsertUser($"b{i}", $"b{i}", "B", ""); });

        var fresh = new HubDb(path); // re-read from disk
        for (int i = 0; i < 25; i++)
        {
            Assert.NotNull(fresh.GetUser($"a{i}"));
            Assert.NotNull(fresh.GetUser($"b{i}")); // all 50 writes from both instances survived
        }
    }
}
