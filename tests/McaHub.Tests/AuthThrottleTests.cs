namespace McaHub.Tests;

/// <summary>
/// AuthThrottle (#10) gives bad-token brute-force a failure-specific penalty on top of the per-IP rate
/// limit: after N failed token attempts an IP is locked out for a cooldown that grows with continued
/// failures; a valid token resets the count; IPs are independent. Time is injected so it's deterministic.
/// </summary>
public class AuthThrottleTests
{
    private static (AuthThrottle t, Func<DateTimeOffset> set, Action<TimeSpan> advance) New(int maxFailures, int cooldownSeconds)
    {
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        var t = new AuthThrottle(maxFailures, TimeSpan.FromSeconds(cooldownSeconds), () => now);
        return (t, () => now, d => now += d);
    }

    [Fact]
    public void Under_the_threshold_is_not_locked_out()
    {
        var (t, _, _) = New(maxFailures: 3, cooldownSeconds: 30);
        t.OnResult("1.1.1.1", badToken: true);
        t.OnResult("1.1.1.1", badToken: true); // 2 < 3
        Assert.False(t.IsLockedOut("1.1.1.1", out _));
    }

    [Fact]
    public void Reaching_the_threshold_locks_out_with_a_retry_after()
    {
        var (t, _, _) = New(maxFailures: 3, cooldownSeconds: 30);
        for (int i = 0; i < 3; i++) t.OnResult("1.1.1.1", badToken: true);
        Assert.True(t.IsLockedOut("1.1.1.1", out TimeSpan retry));
        Assert.True(retry > TimeSpan.Zero && retry <= TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void A_valid_token_resets_the_failure_count()
    {
        var (t, _, _) = New(maxFailures: 3, cooldownSeconds: 30);
        t.OnResult("1.1.1.1", badToken: true);
        t.OnResult("1.1.1.1", badToken: true);
        t.OnResult("1.1.1.1", badToken: false); // success resets
        t.OnResult("1.1.1.1", badToken: true);
        Assert.False(t.IsLockedOut("1.1.1.1", out _)); // only 1 failure since the reset
    }

    [Fact]
    public void Lockout_expires_after_the_cooldown_and_then_escalates()
    {
        var (t, _, advance) = New(maxFailures: 2, cooldownSeconds: 30);
        t.OnResult("1.1.1.1", badToken: true);
        t.OnResult("1.1.1.1", badToken: true);
        Assert.True(t.IsLockedOut("1.1.1.1", out TimeSpan first));

        advance(first + TimeSpan.FromSeconds(1));
        Assert.False(t.IsLockedOut("1.1.1.1", out _)); // cooldown elapsed

        t.OnResult("1.1.1.1", badToken: true);         // another failure → longer lockout
        Assert.True(t.IsLockedOut("1.1.1.1", out TimeSpan second));
        Assert.True(second > first); // exponential escalation
    }

    [Fact]
    public void Different_ips_are_independent()
    {
        var (t, _, _) = New(maxFailures: 2, cooldownSeconds: 30);
        t.OnResult("1.1.1.1", badToken: true);
        t.OnResult("1.1.1.1", badToken: true);
        Assert.True(t.IsLockedOut("1.1.1.1", out _));
        Assert.False(t.IsLockedOut("2.2.2.2", out _));
    }
}
