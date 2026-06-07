namespace McaHub;

/// <summary>
/// Failure-specific brute-force defense for Bearer tokens, on top of the per-IP rate limiter: an IP
/// that presents <paramref name="maxFailures"/> bad tokens is locked out for a cooldown that doubles
/// with each further failure (capped), so guessing the master token or a PAT becomes infeasible. A
/// valid token resets the IP's count. In-memory, per-IP, thread-safe; the clock is injectable for tests.
/// </summary>
public sealed class AuthThrottle(int maxFailures, TimeSpan baseCooldown, Func<DateTimeOffset>? clock = null)
{
    private static readonly TimeSpan MaxCooldown = TimeSpan.FromHours(1);
    private readonly Func<DateTimeOffset> _now = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly object _lock = new();
    private readonly Dictionary<string, State> _byIp = new(StringComparer.Ordinal);

    private struct State { public int Failures; public DateTimeOffset LockedUntil; }

    public bool IsLockedOut(string ip, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        lock (_lock)
        {
            if (_byIp.TryGetValue(ip, out State s) && s.LockedUntil > _now())
            {
                retryAfter = s.LockedUntil - _now();
                return true;
            }
            return false;
        }
    }

    /// <summary>Record the outcome of a token check: a bad token counts toward lockout; a valid one
    /// (or any non-token request the caller passes <c>false</c> for) clears the IP.</summary>
    public void OnResult(string ip, bool badToken)
    {
        lock (_lock)
        {
            if (!badToken) { _byIp.Remove(ip); return; }
            int failures = (_byIp.TryGetValue(ip, out State s) ? s.Failures : 0) + 1;
            DateTimeOffset lockedUntil = failures >= maxFailures ? _now() + Backoff(failures) : default;
            _byIp[ip] = new State { Failures = failures, LockedUntil = lockedUntil };
        }
    }

    private TimeSpan Backoff(int failures)
    {
        int over = Math.Min(failures - maxFailures, 20); // how far past the threshold (cap the shift)
        long ticks = baseCooldown.Ticks * (1L << over);
        return TimeSpan.FromTicks(Math.Min(ticks, MaxCooldown.Ticks));
    }
}
