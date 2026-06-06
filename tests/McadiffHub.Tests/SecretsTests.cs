using Microsoft.Extensions.Configuration;

namespace McadiffHub.Tests;

/// <summary>
/// Secret redaction (#42): keys that look like secrets are recognized and masked, and a real configured
/// secret never reaches the logs.
/// </summary>
public class SecretsTests
{
    [Theory]
    [InlineData("MCAHUB_OAUTH_CLIENT_SECRET", true)]
    [InlineData("MCAHUB_TOKEN", true)]
    [InlineData("MCAHUB_TOKEN_SHA256", true)]
    [InlineData("Some_API_KEY", true)]
    [InlineData("MCAHUB_DATA", false)]
    [InlineData("MCAHUB_OAUTH_CLIENT_ID", false)]
    [InlineData("MCAHUB_BEHIND_PROXY", false)]
    public void Secret_keys_are_recognized(string key, bool secret) => Assert.Equal(secret, Secrets.IsSecretKey(key));

    [Fact]
    public void Redact_masks_only_secret_values()
    {
        Assert.Equal("***", Secrets.Redact("MCAHUB_OAUTH_CLIENT_SECRET", "hunter2"));
        Assert.Equal("data/repos", Secrets.Redact("MCAHUB_DATA", "data/repos"));
        Assert.Equal("", Secrets.Redact("MCAHUB_TOKEN", "")); // empty stays empty
    }

    [Fact]
    public async Task Startup_logs_never_contain_a_configured_secret()
    {
        const string secret = "supersecret-do-not-log-7f3a9c";
        var logs = new List<string>();
        using var f = new HubFactory(HubMode.Open,
            settings: [new("OAuthClientId", "client-id"), new("OAuthClientSecret", secret)],
            logSink: logs);

        using HttpClient c = f.CreateClient();   // boots the app → emits the startup banner
        _ = await c.GetAsync("/");               // ensure the host is fully up and has logged

        Assert.NotEmpty(logs);                    // we actually captured log output
        Assert.DoesNotContain(logs, line => line.Contains(secret));
    }

    [Fact]
    public void Config_and_provider_ToString_never_echo_a_secret() // audit: make secret-logging structurally safe
    {
        const string clientSecret = "OAUTH-SECRET-do-not-echo";
        const string master = "MASTER-TOKEN-do-not-echo";
        Auth.Config cfg = Auth.Read(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OAuthClientId"] = "client-id-public",
            ["OAuthClientSecret"] = clientSecret,
            ["PushToken"] = master,
        }).Build());

        string s = cfg.ToString();
        Assert.DoesNotContain(clientSecret, s);
        Assert.DoesNotContain(master, s);
        Assert.Contains("Master = set", s);                       // still informative, just not the value
        foreach (Auth.Provider p in cfg.Providers)
            Assert.DoesNotContain(clientSecret, p.ToString());    // a provider can't leak its secret either
    }
}
