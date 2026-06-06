using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;

namespace McadiffHub;

/// <summary>
/// Runs cold map renders (which also materialize the backup's world) as background jobs instead of on the
/// request thread (#41 enabler 2). A request enqueues a job and awaits it, but the render runs on a hosted
/// worker pool under the <em>app</em> lifetime — so a client disconnect or request timeout no longer aborts
/// an in-flight render: it completes and fills the immutable, commit-keyed cache for the next viewer.
/// Concurrent requests for the same map coalesce onto one job. Each pending job has a durable marker on
/// disk, re-enqueued on startup, so a render interrupted by a crash or rolling deploy resumes rather than
/// being lost — renders are idempotent, so a resumed job that already completed just hits the warm cache.
///
/// The cache probe and the render step are injected as delegates, so the queue carries no dependency on the
/// concrete <see cref="MapCache"/>/<c>RepoStore</c> and can be exercised in isolation.
/// </summary>
public sealed class RenderQueue : BackgroundService
{
    /// <summary>Return the cached PNG for a map, or null if it is cold (not yet rendered).</summary>
    public delegate Task<byte[]?> CacheProbe(string repo, string commit, MapDimension dim, CancellationToken ct);

    /// <summary>Render (and cache) a map, returning its PNG bytes. Runs under the app-lifetime token.</summary>
    public delegate Task<byte[]> RenderFn(string repo, string commit, MapDimension dim, CancellationToken ct);

    private readonly CacheProbe _cached;
    private readonly RenderFn _render;
    private readonly string _jobsDir;
    private readonly int _workers;
    private readonly Channel<RenderJob> _channel = Channel.CreateUnbounded<RenderJob>();
    private readonly ConcurrentDictionary<string, Job> _pending = new(StringComparer.Ordinal);

    public RenderQueue(CacheProbe cached, RenderFn render, string jobsDir, int workers)
    {
        _cached = cached;
        _render = render;
        _jobsDir = jobsDir;
        _workers = Math.Max(1, workers);
        Directory.CreateDirectory(_jobsDir);
    }

    private sealed record RenderJob(string Repo, string Commit, MapDimension Dim, string Key, string MarkerPath);
    private sealed class Job
    {
        public TaskCompletionSource<byte[]> Tcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed record MarkerData(string Repo, string Commit, MapDimension Dim);

    /// <summary>Serve a map: a warm cache hit returns immediately; a cold one enqueues a background render
    /// and awaits it. The await honors <paramref name="requestCt"/> (the client may give up), but the render
    /// keeps running under the app lifetime and still fills the cache for the next viewer.</summary>
    public async Task<byte[]> RequestAsync(string repo, string commit, MapDimension dim, CancellationToken requestCt)
    {
        if (await _cached(repo, commit, dim, requestCt) is { } warm) return warm; // warm: no queue, no worker
        Job job = Enqueue(repo, commit, dim);
        return await job.Tcs.Task.WaitAsync(requestCt);
    }

    private Job Enqueue(string repo, string commit, MapDimension dim)
    {
        string key = MapCache.RenderKey(repo, commit, dim);
        var fresh = new Job();
        Job job = _pending.GetOrAdd(key, fresh);
        if (ReferenceEquals(job, fresh)) // we won the race → we own the (exactly-once) enqueue side effects
        {
            var rj = new RenderJob(repo, commit, dim, key, Path.Combine(_jobsDir, Sha(key) + ".json"));
            WriteMarker(rj);
            if (!_channel.Writer.TryWrite(rj))
            {
                _pending.TryRemove(new KeyValuePair<string, Job>(key, job));
                DeleteMarker(rj);
                job.Tcs.TrySetException(new InvalidOperationException("the render queue is shutting down"));
            }
        }
        return job;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ResumeFromDisk(); // re-enqueue jobs left by a previous crash/deploy so their renders finish
        var workers = new Task[_workers];
        for (int i = 0; i < _workers; i++) workers[i] = WorkerLoop(stoppingToken);
        await Task.WhenAll(workers);
    }

    private async Task WorkerLoop(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (RenderJob rj in _channel.Reader.ReadAllAsync(stoppingToken))
                await Process(rj, stoppingToken);
        }
        catch (OperationCanceledException) { /* draining: any unstarted jobs keep their markers and resume next boot */ }
    }

    private async Task Process(RenderJob rj, CancellationToken stoppingToken)
    {
        _pending.TryGetValue(rj.Key, out Job? job); // may be null for a resumed job with no live awaiter
        try
        {
            byte[] png = await _render(rj.Repo, rj.Commit, rj.Dim, stoppingToken);
            DeleteMarker(rj);
            _pending.TryRemove(new KeyValuePair<string, Job>(rj.Key, job!));
            job?.Tcs.TrySetResult(png);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown interrupted the render: keep the marker so the next boot resumes it; tell any awaiter
            // to retry (its request is draining anyway).
            _pending.TryRemove(new KeyValuePair<string, Job>(rj.Key, job!));
            job?.Tcs.TrySetCanceled(stoppingToken);
        }
        catch (Exception e)
        {
            // A genuine render failure (e.g. an unreadable world): drop the marker so we don't retry it forever.
            // The next request re-enqueues and re-fails, surfacing the error to that client just as before.
            DeleteMarker(rj);
            _pending.TryRemove(new KeyValuePair<string, Job>(rj.Key, job!));
            job?.Tcs.TrySetException(e);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _channel.Writer.TryComplete(); // stop taking new work; in-flight renders finish within the drain window
        await base.StopAsync(cancellationToken);
    }

    // ---- durable markers ----

    private void WriteMarker(RenderJob rj)
    {
        try
        {
            Directory.CreateDirectory(_jobsDir);
            string tmp = rj.MarkerPath + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
            File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(new MarkerData(rj.Repo, rj.Commit, rj.Dim)));
            File.Move(tmp, rj.MarkerPath, overwrite: true);
        }
        catch { /* durability is best-effort — a lost marker just means a lazy re-render on the next request */ }
    }

    private static void DeleteMarker(RenderJob rj)
    {
        try { if (File.Exists(rj.MarkerPath)) File.Delete(rj.MarkerPath); } catch { /* best-effort */ }
    }

    private void ResumeFromDisk()
    {
        if (!Directory.Exists(_jobsDir)) return;
        foreach (string f in Directory.EnumerateFiles(_jobsDir, "*.json"))
        {
            try
            {
                MarkerData? d = JsonSerializer.Deserialize<MarkerData>(File.ReadAllBytes(f));
                if (d is null) { File.Delete(f); continue; }
                // Resume with no live awaiter — the render just fills the cache for the next viewer.
                _channel.Writer.TryWrite(new RenderJob(d.Repo, d.Commit, d.Dim, MapCache.RenderKey(d.Repo, d.Commit, d.Dim), f));
            }
            catch { try { File.Delete(f); } catch { /* unreadable marker — drop it */ } }
        }
    }

    private static string Sha(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
}
