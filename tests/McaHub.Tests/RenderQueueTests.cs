using System.Collections.Concurrent;
using System.Text.Json;

namespace McaHub.Tests;

/// <summary>
/// Background render queue (#41 enabler 2). Cold map renders run off the request thread on a hosted worker
/// pool: a client giving up doesn't abort an in-flight render (it finishes and fills the cache), same-map
/// requests coalesce onto one render, and a job interrupted by a crash/deploy is resumed from its durable
/// marker on startup. The cache probe + render are injected, so the queue is exercised without a real world.
/// </summary>
public class RenderQueueTests
{
    private static RenderQueue Queue(string jobsDir, RenderQueue.CacheProbe cached, RenderQueue.RenderFn render, int workers = 2) =>
        new(cached, render, jobsDir, workers);

    [Fact]
    public async Task A_cold_map_is_rendered_in_the_background_and_returned()
    {
        using var tmp = new TempDir();
        var rendered = new ConcurrentBag<string>();
        var q = Queue(tmp.Path,
            cached: (_, _, _, _) => Task.FromResult<byte[]?>(null),               // always cold
            render: (repo, commit, _, _) => { rendered.Add($"{repo}:{commit}"); return Task.FromResult(new byte[] { 1, 2, 3 }); });
        await q.StartAsync(default);

        byte[] png = await q.RequestAsync("w", "abc", MapDimension.Overworld, default);

        Assert.Equal(new byte[] { 1, 2, 3 }, png);
        Assert.Contains("w:abc", rendered);
        await q.StopAsync(default);
    }

    [Fact]
    public async Task A_warm_map_is_served_without_touching_the_worker_pool()
    {
        using var tmp = new TempDir();
        bool renderCalled = false;
        var q = Queue(tmp.Path,
            cached: (_, _, _, _) => Task.FromResult<byte[]?>(new byte[] { 9 }),   // warm
            render: (_, _, _, _) => { renderCalled = true; return Task.FromResult(new byte[] { 0 }); });
        await q.StartAsync(default);

        byte[] png = await q.RequestAsync("w", "abc", MapDimension.Overworld, default);

        Assert.Equal(new byte[] { 9 }, png);
        Assert.False(renderCalled); // a warm map never enters the queue
        await q.StopAsync(default);
    }

    [Fact]
    public async Task Concurrent_requests_for_the_same_map_coalesce_onto_one_render()
    {
        using var tmp = new TempDir();
        int renderCount = 0;
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var q = Queue(tmp.Path,
            cached: (_, _, _, _) => Task.FromResult<byte[]?>(null),
            render: async (_, _, _, _) => { Interlocked.Increment(ref renderCount); started.TrySetResult(); await release.Task; return new byte[] { 7 }; },
            workers: 4);
        await q.StartAsync(default);

        // Three requests for the same map; the render is held in-flight (release not yet set), so the assertion
        // that exactly one render started runs while all three are coalesced behind it — no completion-timing race.
        Task<byte[]> a = q.RequestAsync("w", "abc", MapDimension.Overworld, default);
        Task<byte[]> b = q.RequestAsync("w", "abc", MapDimension.Overworld, default);
        Task<byte[]> c = q.RequestAsync("w", "abc", MapDimension.Overworld, default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, renderCount);                                  // one render serves all three requests

        release.SetResult();
        byte[][] all = await Task.WhenAll(a, b, c);
        Assert.All(all, x => Assert.Equal(new byte[] { 7 }, x));       // all three get that one render's bytes
        await q.StopAsync(default);
    }

    [Fact]
    public async Task A_client_giving_up_does_not_abort_the_in_flight_render()
    {
        using var tmp = new TempDir();
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var finished = new TaskCompletionSource();
        var q = Queue(tmp.Path,
            cached: (_, _, _, _) => Task.FromResult<byte[]?>(null),
            render: async (_, _, _, _) =>
            {
                started.SetResult();
                await release.Task;     // a slow render, deliberately not tied to the request's token
                finished.SetResult();
                return new byte[] { 5 };
            },
            workers: 1);
        await q.StartAsync(default);

        using var clientGaveUp = new CancellationTokenSource();
        Task<byte[]> req = q.RequestAsync("w", "abc", MapDimension.Overworld, clientGaveUp.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); // render is underway
        clientGaveUp.Cancel();                                 // the client disconnects / times out

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => req); // the request gives up…
        release.SetResult();                                                // …but the render keeps running…
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));             // …and completes regardless (cache filled)

        await q.StopAsync(default);
    }

    [Fact]
    public async Task A_leftover_job_marker_is_resumed_on_startup()
    {
        using var tmp = new TempDir();
        // A marker a crashed/redeployed instance left behind (filename is arbitrary; the queue reads any *.json).
        File.WriteAllText(Path.Combine(tmp.Path, "leftover.json"),
            JsonSerializer.Serialize(new { Repo = "w", Commit = "abc", Dim = MapDimension.Overworld }));

        var rendered = new TaskCompletionSource<string>();
        var q = Queue(tmp.Path,
            cached: (_, _, _, _) => Task.FromResult<byte[]?>(null),
            render: (repo, commit, _, _) => { rendered.TrySetResult($"{repo}:{commit}"); return Task.FromResult(new byte[] { 1 }); },
            workers: 1);
        await q.StartAsync(default);

        string key = await rendered.Task.WaitAsync(TimeSpan.FromSeconds(5)); // resumed with no live request
        Assert.Equal("w:abc", key);

        await WaitUntil(() => !Directory.EnumerateFiles(tmp.Path, "*.json").Any(), TimeSpan.FromSeconds(2)); // marker cleaned up
        await q.StopAsync(default);
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < until) await Task.Delay(20);
        Assert.True(condition(), "condition not met within timeout");
    }
}
