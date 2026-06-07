namespace McaHub.Tests;

/// <summary>
/// PAT lifecycle at the data layer (#18): tokens carry a scope and an optional expiry, resolve to both,
/// reject after expiry, can be regenerated (old revoked, scope carried), and a user's epoch + bulk
/// revoke back "sign out everywhere".
/// </summary>
public class HubDbTokenTests
{
    private static HubDb New(TempDir tmp) => new(Path.Combine(tmp.Path, "hub.json"));

    [Fact]
    public void Resolve_returns_the_user_and_scope()
    {
        using var tmp = new TempDir();
        HubDb db = New(tmp);
        string t = db.CreateToken("u1", "laptop", "read", expiresAt: null);
        TokenAuth? auth = db.ResolveToken(t);
        Assert.NotNull(auth);
        Assert.Equal("u1", auth!.UserId);
        Assert.Equal("read", auth.Scope);
    }

    [Fact]
    public void An_expired_token_does_not_resolve()
    {
        using var tmp = new TempDir();
        HubDb db = New(tmp);
        string t = db.CreateToken("u1", "old", "write", DateTimeOffset.UtcNow.AddMinutes(-1).ToString("o"));
        Assert.Null(db.ResolveToken(t));
    }

    [Fact]
    public void A_future_expiry_still_resolves()
    {
        using var tmp = new TempDir();
        HubDb db = New(tmp);
        string t = db.CreateToken("u1", "ok", "write", DateTimeOffset.UtcNow.AddDays(1).ToString("o"));
        Assert.NotNull(db.ResolveToken(t));
    }

    [Fact]
    public void Regenerate_revokes_the_old_and_keeps_the_scope()
    {
        using var tmp = new TempDir();
        HubDb db = New(tmp);
        string old = db.CreateToken("u1", "ci", "read", null);
        string prefix = db.ListTokens("u1").Single().Prefix;
        string? fresh = db.RegenerateToken("u1", prefix);
        Assert.NotNull(fresh);
        Assert.Null(db.ResolveToken(old));                  // old is gone
        Assert.Equal("read", db.ResolveToken(fresh!)!.Scope); // scope carried over
    }

    [Fact]
    public void Sign_out_everywhere_revokes_tokens_and_advances_the_epoch()
    {
        using var tmp = new TempDir();
        HubDb db = New(tmp);
        db.UpsertUser("u1", "alice", "Alice", "");
        string t = db.CreateToken("u1", "x", "write", null);
        int before = db.GetUser("u1")!.Epoch;
        Assert.Equal(1, db.RevokeAllTokens("u1"));
        Assert.True(db.BumpEpoch("u1") > before);
        Assert.Null(db.ResolveToken(t)); // the token is gone
    }
}
