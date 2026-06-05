using System.Collections.Concurrent;

namespace McadiffHub;

/// <summary>
/// Bounds expensive render work so a burst of map requests can't stall the whole app. Requests for the
/// <em>same</em> key (repo + commit) coalesce behind one per-key gate — instead of every render blocking
/// a single process-wide lock — and total concurrent renders are capped, so N distinct hostile requests
/// can't saturate CPU/RAM. The wait honors cancellation (client disconnect or request timeout).
/// </summary>
public sealed class RenderGate(int maxConcurrency)
{
    private readonly SemaphoreSlim _global = new(maxConcurrency, maxConcurrency);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _perKey = new(StringComparer.Ordinal);

    public async Task<T> RunAsync<T>(string key, Func<Task<T>> work, CancellationToken ct)
    {
        SemaphoreSlim k = _perKey.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await k.WaitAsync(ct);
        try
        {
            await _global.WaitAsync(ct);
            try { return await work(); }
            finally { _global.Release(); }
        }
        finally
        {
            k.Release();
            // Drop idle keys so the map can't grow without bound. The semaphore is not disposed, so a
            // lost race here only risks a redundant render (the cache write is atomic and double-checked
            // by the caller), never a use-after-dispose.
            if (k.CurrentCount == 1) _perKey.TryRemove(new KeyValuePair<string, SemaphoreSlim>(key, k));
        }
    }
}
