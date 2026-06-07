namespace McaHub.Tests;

/// <summary>
/// RenderGate (#4) bounds expensive render work: same-key requests coalesce (no two run at once),
/// total concurrency is capped, and a cancelled wait never runs the work. These are the guarantees the
/// map endpoint relies on to stop a global lock from serializing every render.
/// </summary>
public class RenderGateTests
{
    [Fact]
    public async Task Same_key_runs_do_not_overlap()
    {
        var gate = new RenderGate(maxConcurrency: 4);
        int concurrent = 0, peak = 0;
        var firstRunning = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        Task Run(bool hold) => gate.RunAsync("samekey", async () =>
        {
            int n = Interlocked.Increment(ref concurrent);
            InterlockedMax(ref peak, n);
            if (hold) { firstRunning.SetResult(); await release.Task; }
            Interlocked.Decrement(ref concurrent);
            return 0;
        }, CancellationToken.None);

        Task first = Run(hold: true);
        await firstRunning.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task second = Run(hold: false);       // same key — must wait for first
        await Task.Delay(50);                  // give 'second' a chance to (wrongly) start
        Assert.Equal(1, Volatile.Read(ref concurrent)); // second is blocked on the per-key gate
        release.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(1, peak);                 // never two at once for the same key
    }

    [Fact]
    public async Task Different_keys_run_concurrently_but_capped_at_the_limit()
    {
        var gate = new RenderGate(maxConcurrency: 3);
        int concurrent = 0, peak = 0;
        var capReached = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        Task Run(int i) => gate.RunAsync($"key{i}", async () =>
        {
            int n = Interlocked.Increment(ref concurrent);
            InterlockedMax(ref peak, n);
            if (n == 3) capReached.TrySetResult();
            await release.Task;
            Interlocked.Decrement(ref concurrent);
            return 0;
        }, CancellationToken.None);

        Task[] all = Enumerable.Range(0, 6).Select(Run).ToArray(); // 6 distinct keys, cap 3
        await capReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, Volatile.Read(ref concurrent));            // exactly the cap; the rest wait
        release.SetResult();
        await Task.WhenAll(all);
        Assert.Equal(3, peak);
    }

    [Fact]
    public async Task Cancelled_wait_throws_and_never_runs_the_work()
    {
        var gate = new RenderGate(maxConcurrency: 1);
        var firstRunning = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        Task first = gate.RunAsync("k", async () => { firstRunning.SetResult(); await release.Task; return 0; }, CancellationToken.None);
        await firstRunning.Task.WaitAsync(TimeSpan.FromSeconds(5));

        bool ran = false;
        using var cts = new CancellationTokenSource();
        Task second = gate.RunAsync("k", () => { ran = true; return Task.FromResult(0); }, cts.Token); // queued behind first
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.False(ran);

        release.SetResult();
        await first;
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while (value > (seen = Volatile.Read(ref target)))
            if (Interlocked.CompareExchange(ref target, value, seen) == seen) return;
    }
}
