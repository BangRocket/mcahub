namespace McadiffHub;

/// <summary>
/// Bounds a disk cache (materialized worlds or rendered maps) so it can't fill the disk. Enforces both
/// a per-repo count cap and a global byte ceiling, evicting least-recently-used entries (by an access
/// counter, touched on every cache hit). An entry larger than the whole ceiling is refused outright
/// rather than admitted. Eviction removes the entry's files via the supplied <paramref name="delete"/>
/// callback. Thread-safe.
/// </summary>
public sealed class DiskCacheQuota(long maxBytes, int maxPerRepo, Action<string> delete)
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _byKey = new(StringComparer.Ordinal);
    private long _clock;
    private long _total;

    private sealed class Entry(string key, string repo, string path, long size, long access)
    {
        public string Key { get; } = key;
        public string Repo { get; } = repo;
        public string Path { get; } = path;
        public long Size { get; } = size;
        public long Access { get; set; } = access;
    }

    /// <summary>Register an entry already on disk (startup seeding). Does not evict — call
    /// <see cref="Enforce"/> afterwards to trim a cache that's already over the ceiling.</summary>
    public void Seed(string key, string repo, string path, long size)
    {
        lock (_lock)
        {
            if (_byKey.ContainsKey(key)) return;
            _byKey[key] = new Entry(key, repo, path, size, ++_clock);
            _total += size;
        }
    }

    /// <summary>Mark an entry most-recently-used (a cache hit).</summary>
    public void Touch(string key)
    {
        lock (_lock)
            if (_byKey.TryGetValue(key, out Entry? e)) e.Access = ++_clock;
    }

    /// <summary>Register a freshly-written entry and evict to satisfy the caps. Returns false (and
    /// deletes the entry) if it alone exceeds the byte ceiling — the caller should treat that as
    /// "too large to cache".</summary>
    public bool Admit(string key, string repo, string path, long size)
    {
        lock (_lock)
        {
            if (size > maxBytes) { delete(path); return false; }
            _byKey[key] = new Entry(key, repo, path, size, ++_clock);
            _total += size;
            EvictPerRepo(repo);
            EvictGlobal();
            return true;
        }
    }

    /// <summary>Evict least-recently-used entries until the total is back under the byte ceiling.</summary>
    public void Enforce()
    {
        lock (_lock) EvictGlobal();
    }

    private void EvictPerRepo(string repo)
    {
        List<Entry> repoEntries = _byKey.Values.Where(e => e.Repo == repo).OrderByDescending(e => e.Access).ToList();
        for (int i = maxPerRepo; i < repoEntries.Count; i++) Remove(repoEntries[i]);
    }

    private void EvictGlobal()
    {
        if (_total <= maxBytes) return;
        foreach (Entry e in _byKey.Values.OrderBy(e => e.Access).ToList())
        {
            if (_total <= maxBytes) break;
            Remove(e);
        }
    }

    private void Remove(Entry e)
    {
        if (!_byKey.Remove(e.Key)) return;
        _total -= e.Size;
        delete(e.Path);
    }
}
