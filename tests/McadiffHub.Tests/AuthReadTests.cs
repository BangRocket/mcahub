using Microsoft.Extensions.Configuration;

namespace McadiffHub.Tests;

public class AuthReadTests
{
    private static Auth.Config Read(params (string Key, string Value)[] settings)
    {
        // Baseline forces OAuth off so ambient env can't make Oauth true under test.
        (string, string)[] baseline = [("OAuthClientId", ""), ("OAuthClientSecret", "")];
        IConfiguration c = new ConfigurationBuilder()
            .AddInMemoryCollection(baseline.Concat(settings.Select(s => (s.Key, s.Value)))
                .Select(s => new KeyValuePair<string, string?>(s.Item1, s.Item2)))
            .Build();
        return Auth.Read(c);
    }

    [Fact]
    public void DevLogin_can_be_enabled_through_configuration()
    {
        Auth.Config cfg = Read(("DevLogin", "1"));
        Assert.True(cfg.Accounts);
        Assert.True(cfg.DevLogin);
        Assert.False(cfg.Oauth);
    }

    [Fact]
    public void Empty_push_token_is_treated_as_unset()
    {
        Assert.Null(Read(("PushToken", "")).MasterToken);
    }

    [Fact]
    public void Push_token_is_read_from_configuration()
    {
        Assert.Equal("secret", Read(("PushToken", "secret")).MasterToken);
    }
}
