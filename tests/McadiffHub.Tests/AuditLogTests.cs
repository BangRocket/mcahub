namespace McadiffHub.Tests;

/// <summary>
/// AuditLog (#16) is the append-only trail of security-relevant changes — written as JSON lines, read
/// back most-recent-first and filtered to a repo for the per-repo history view. A corrupt line never
/// breaks the read.
/// </summary>
public class AuditLogTests
{
    [Fact]
    public void Recent_returns_a_repos_entries_most_recent_first()
    {
        using var tmp = new TempDir();
        var log = new AuditLog(Path.Combine(tmp.Path, "audit.jsonl"));
        log.Append("alice", "visibility", "world1", "public→private", "web", "1.1.1.1");
        log.Append("bob", "collaborator.add", "world2", "carol=write", "web", "2.2.2.2");
        log.Append("alice", "ref.update", "world1", "main abcd→ef01", "cli", "3.3.3.3");

        var w1 = log.Recent("world1", 10);
        Assert.Equal(2, w1.Count);
        Assert.Equal("ref.update", w1[0].Action);     // most recent first
        Assert.Equal("visibility", w1[1].Action);
        Assert.Equal("alice", w1[0].Actor);
    }

    [Fact]
    public void Recent_caps_at_the_limit()
    {
        using var tmp = new TempDir();
        var log = new AuditLog(Path.Combine(tmp.Path, "audit.jsonl"));
        for (int i = 0; i < 5; i++) log.Append("u", "ref.update", "r", "n" + i, "cli", null);
        Assert.Equal(3, log.Recent("r", 3).Count);
    }

    [Fact]
    public void A_corrupt_line_is_skipped_not_fatal()
    {
        using var tmp = new TempDir();
        string path = Path.Combine(tmp.Path, "audit.jsonl");
        var log = new AuditLog(path);
        log.Append("alice", "visibility", "world1", "x", "web", null);
        File.AppendAllText(path, "{ not json\n");
        log.Append("alice", "visibility", "world1", "y", "web", null);
        Assert.Equal(2, log.Recent("world1", 10).Count);
    }

    [Fact]
    public void Recent_on_a_missing_file_is_empty()
    {
        using var tmp = new TempDir();
        var log = new AuditLog(Path.Combine(tmp.Path, "nope.jsonl"));
        Assert.Empty(log.Recent("any", 10));
    }

    [Fact]
    public void ForgetActor_erases_a_users_identity_but_keeps_the_actions() // audit: GDPR erasure of audit PII
    {
        using var tmp = new TempDir();
        var log = new AuditLog(Path.Combine(tmp.Path, "audit.jsonl"));
        log.Append("alice", "visibility", "w", "public→private", "web", "203.0.113.7");
        log.Append("bob", "collaborator.add", "w", "carol=read", "web", "198.51.100.2");

        log.ForgetActor("alice");

        var entries = log.Recent("w", 10);
        AuditEntry alice = entries.Single(e => e.Action == "visibility");
        Assert.Equal("deleted-user", alice.Actor); // login pseudonymized
        Assert.Null(alice.Ip);                      // IP erased
        Assert.Equal("public→private", alice.Detail); // the action itself survives
        AuditEntry bob = entries.Single(e => e.Action == "collaborator.add");
        Assert.Equal("bob", bob.Actor);             // another user's entry untouched
        Assert.Equal("198.51.100.2", bob.Ip);
    }
}
