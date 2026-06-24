using McaHub.Rust;

namespace McaHub.Tests;

/// <summary>
/// Commit metadata is content-addressed and immutable, and every caller resolves a hash via
/// rev-parse before calling <see cref="RustEngine.ReadCommit"/>, so the engine memoizes it to
/// avoid re-spawning <c>mcagit cat-file</c> on every page view (the "every page is slow" cost:
/// a repo page fans out one subprocess per commit in the timeline). These tests drive that cache
/// through a fake <c>mcagit</c> that records each invocation, so we can assert spawns, not timings.
/// </summary>
public sealed class CommitCacheTests
{
    [Fact]
    public void ReadCommit_shells_out_once_for_a_repeated_repo_and_hash()
    {
        using var tmp = new TempDir();
        string calls = Path.Combine(tmp.Path, "calls.log");
        var rust = new RustEngine(FakeMcagit(tmp.Path, calls));
        const string hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        CommitMeta a = rust.ReadCommit(tmp.Path, hash);
        CommitMeta b = rust.ReadCommit(tmp.Path, hash);

        Assert.Equal(hash, a.Tree); // fake echoes the requested commit into Tree
        Assert.Equal(a.Tree, b.Tree);
        Assert.Equal(1, CallCount(calls)); // second read served from cache — no subprocess
    }

    [Fact]
    public void ReadCommit_keys_on_the_hash_so_distinct_commits_never_collide()
    {
        using var tmp = new TempDir();
        string calls = Path.Combine(tmp.Path, "calls.log");
        var rust = new RustEngine(FakeMcagit(tmp.Path, calls));
        const string h1 = "1111111111111111111111111111111111111111111111111111111111111111";
        const string h2 = "2222222222222222222222222222222222222222222222222222222222222222";

        Assert.Equal(h1, rust.ReadCommit(tmp.Path, h1).Tree);
        Assert.Equal(h2, rust.ReadCommit(tmp.Path, h2).Tree);
        Assert.Equal(2, CallCount(calls)); // different keys ⇒ two real reads
    }

    [Fact]
    public void ReadCommit_cache_is_bounded_so_old_entries_are_evicted()
    {
        using var tmp = new TempDir();
        string calls = Path.Combine(tmp.Path, "calls.log");
        var rust = new RustEngine(FakeMcagit(tmp.Path, calls), commitCacheMax: 2);
        const string h1 = "1111111111111111111111111111111111111111111111111111111111111111";
        const string h2 = "2222222222222222222222222222222222222222222222222222222222222222";
        const string h3 = "3333333333333333333333333333333333333333333333333333333333333333";

        rust.ReadCommit(tmp.Path, h1); // miss → 1
        rust.ReadCommit(tmp.Path, h2); // miss → 2
        rust.ReadCommit(tmp.Path, h3); // overflows cap of 2, evicts → 3
        rust.ReadCommit(tmp.Path, h1); // h1 was evicted → miss again → 4

        Assert.Equal(4, CallCount(calls));
    }

    // A stand-in `mcagit` that appends a line per invocation and prints a CommitMeta whose `tree`
    // is the requested commit (the 4th arg of `-C <repo> cat-file <commit>`), so a test can both
    // count spawns and confirm the right value came back for the right key.
    private static string FakeMcagit(string dir, string callsLog)
    {
        string path = Path.Combine(dir, "fake-mcagit");
        File.WriteAllText(path,
            "#!/bin/sh\n" +
            $"echo x >> \"{callsLog}\"\n" +
            "printf '{\"tree\":\"%s\",\"parents\":[],\"message\":\"m\",\"author\":\"a\",\"time\":\"t\"}\\n' \"$4\"\n");
        // The hub's test + runtime targets are Unix (the fake is a /bin/sh script); the chmod is too.
#pragma warning disable CA1416 // SetUnixFileMode is unsupported on Windows
        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
#pragma warning restore CA1416
        return path;
    }

    private static int CallCount(string callsLog) =>
        File.Exists(callsLog) ? File.ReadAllLines(callsLog).Length : 0;
}
