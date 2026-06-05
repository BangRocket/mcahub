namespace McadiffHub.Tests;

/// <summary>
/// Fail-closed startup guards (#9): open mode (anonymous read+write+create) and dev-login (passwordless)
/// must not be served on a non-loopback interface — open mode only with an explicit override, dev-login
/// never. The check is a pure function of the resolved mode + bind URLs.
/// </summary>
public class StartupGuardTests
{
    private static string? Check(bool accounts, string? master, bool dev, string urls, bool allowPublicOpen = false) =>
        StartupGuard.PublicExposureViolation(accounts, master, dev, urls, allowPublicOpen);

    [Fact]
    public void Open_mode_on_a_public_bind_is_refused()
        => Assert.NotNull(Check(accounts: false, master: null, dev: false, "http://0.0.0.0:8080"));

    [Fact]
    public void Open_mode_on_a_public_bind_is_allowed_with_the_override()
        => Assert.Null(Check(accounts: false, master: null, dev: false, "http://0.0.0.0:8080", allowPublicOpen: true));

    [Fact]
    public void Open_mode_on_loopback_is_fine()
        => Assert.Null(Check(accounts: false, master: null, dev: false, "http://localhost:5080"));

    [Fact]
    public void Dev_login_on_a_public_bind_is_refused_even_with_the_override()
        => Assert.NotNull(Check(accounts: true, master: null, dev: true, "http://0.0.0.0:8080", allowPublicOpen: true));

    [Fact]
    public void Dev_login_on_loopback_is_fine()
        => Assert.Null(Check(accounts: true, master: null, dev: true, "http://127.0.0.1:5080"));

    [Fact]
    public void Token_mode_and_oauth_accounts_are_fine_on_a_public_bind()
    {
        Assert.Null(Check(accounts: false, master: "secret", dev: false, "http://0.0.0.0:8080")); // token mode
        Assert.Null(Check(accounts: true, master: null, dev: false, "http://0.0.0.0:8080"));       // oauth accounts
    }

    [Theory]
    [InlineData("http://localhost:5080", true)]
    [InlineData("http://127.0.0.1:5080", true)]
    [InlineData("http://[::1]:5080", true)]
    [InlineData("http://0.0.0.0:8080", false)]
    [InlineData("http://+:80", false)]
    [InlineData("http://*:80", false)]
    [InlineData("https://hub.example.com", false)]
    [InlineData("http://localhost:5080;http://0.0.0.0:8080", false)] // any public member ⇒ public
    public void Loopback_detection(string urls, bool loopback)
        => Assert.Equal(loopback, StartupGuard.AllLoopback(urls));
}
