namespace McadiffHub.Tests;

/// <summary>
/// DiskCacheQuota (#5) bounds the materialized-world and map caches: at most N entries per repo and a
/// global byte ceiling, evicting least-recently-used entries; a single entry larger than the whole
/// ceiling is refused rather than allowed to fill the disk. Eviction calls a delete callback (here a
/// spy) so the logic is testable without real files.
/// </summary>
public class DiskCacheQuotaTests
{
    private static (DiskCacheQuota quota, List<string> deleted) New(long maxBytes, int maxPerRepo)
    {
        var deleted = new List<string>();
        return (new DiskCacheQuota(maxBytes, maxPerRepo, deleted.Add), deleted);
    }

    [Fact]
    public void Per_repo_count_cap_evicts_the_oldest()
    {
        var (q, deleted) = New(maxBytes: long.MaxValue, maxPerRepo: 2);
        Assert.True(q.Admit("a/1", "a", "/a/1", 10));
        Assert.True(q.Admit("a/2", "a", "/a/2", 10));
        Assert.True(q.Admit("a/3", "a", "/a/3", 10)); // over the per-repo cap of 2
        Assert.Equal(["/a/1"], deleted);              // oldest for repo "a" evicted
    }

    [Fact]
    public void Global_byte_ceiling_evicts_lru()
    {
        var (q, deleted) = New(maxBytes: 100, maxPerRepo: 100);
        q.Admit("a/1", "a", "/a/1", 60);
        q.Admit("b/1", "b", "/b/1", 60); // total 120 > 100 → evict LRU (a/1)
        Assert.Equal(["/a/1"], deleted);
    }

    [Fact]
    public void Touch_protects_an_entry_from_lru_eviction()
    {
        var (q, deleted) = New(maxBytes: 100, maxPerRepo: 100);
        q.Admit("a/1", "a", "/a/1", 40);
        q.Admit("a/2", "a", "/a/2", 40);
        q.Touch("a/1");                  // a/1 is now most-recently-used
        q.Admit("a/3", "a", "/a/3", 40); // total 120 > 100 → evict LRU, which is now a/2
        Assert.Equal(["/a/2"], deleted);
    }

    [Fact]
    public void A_single_entry_over_the_ceiling_is_refused_and_deleted()
    {
        var (q, deleted) = New(maxBytes: 100, maxPerRepo: 100);
        bool admitted = q.Admit("a/big", "a", "/a/big", 150);
        Assert.False(admitted);
        Assert.Equal(["/a/big"], deleted); // its just-written files are removed
    }

    [Fact]
    public void Enforce_trims_seeded_entries_down_to_the_ceiling()
    {
        var (q, deleted) = New(maxBytes: 100, maxPerRepo: 100);
        q.Seed("a/1", "a", "/a/1", 60); // seeding existing on-disk entries does not evict...
        q.Seed("a/2", "a", "/a/2", 60);
        q.Seed("a/3", "a", "/a/3", 60);
        Assert.Empty(deleted);
        q.Enforce();                    // ...until enforced (e.g. at startup), oldest-first
        Assert.Equal(["/a/1", "/a/2"], deleted); // evict down to <= 100 (one 60 remains)
    }
}
