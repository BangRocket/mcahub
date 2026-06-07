using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace McaHub.Tests;

/// <summary>
/// Multi-provider sign-in (#37): each provider is enabled by its own id+secret, and a provider's userinfo
/// JSON maps to (id, login, name, avatar) — the part that's pure and testable without a live OAuth round-trip.
/// </summary>
public class ProviderMappingTests
{
    private static Auth.Config Read(params (string Key, string Value)[] s)
    {
        var dict = new Dictionary<string, string?> { ["OAuthClientId"] = "", ["OAuthClientSecret"] = "" }; // no ambient legacy provider
        foreach ((string k, string v) in s) dict[k] = v; // test values win over the baseline
        return Auth.Read(new ConfigurationBuilder().AddInMemoryCollection(dict).Build());
    }

    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    [Fact]
    public void GitHub_userinfo_maps_id_login_name_avatar()
    {
        var (sub, login, name, avatar) = Auth.MapUser(Auth.ProviderKind.GitHub,
            Json("""{"id":123,"login":"octocat","name":"The Octocat","avatar_url":"https://x/y.png"}"""));
        Assert.Equal("123", sub);
        Assert.Equal("octocat", login);
        Assert.Equal("The Octocat", name);
        Assert.Equal("https://x/y.png", avatar);
    }

    [Fact]
    public void Microsoft_userinfo_maps_sub_email_and_picture()
    {
        var (sub, login, name, avatar) = Auth.MapUser(Auth.ProviderKind.Microsoft,
            Json("""{"sub":"oid-1","name":"Alice","email":"a@b.com","picture":"https://x/p.png"}"""));
        Assert.Equal("oid-1", sub);
        Assert.Equal("a@b.com", login); // no login/preferred_username → email
        Assert.Equal("Alice", name);
        Assert.Equal("https://x/p.png", avatar);
    }

    [Fact]
    public void Discord_userinfo_builds_the_avatar_cdn_url()
    {
        var (sub, login, name, avatar) = Auth.MapUser(Auth.ProviderKind.Discord,
            Json("""{"id":"42","username":"coolkid","global_name":"Cool Kid","avatar":"abc123"}"""));
        Assert.Equal("42", sub);
        Assert.Equal("coolkid", login);
        Assert.Equal("Cool Kid", name);
        Assert.Equal("https://cdn.discordapp.com/avatars/42/abc123.png", avatar);
    }

    [Fact]
    public void Providers_are_enabled_per_id_secret_pair()
    {
        Auth.Config cfg = Read(("OAuthGitHubClientId", "g"), ("OAuthGitHubClientSecret", "gs"),
                               ("OAuthDiscordClientId", "d"), ("OAuthDiscordClientSecret", "ds"));
        Assert.True(cfg.Accounts && cfg.Oauth);
        Assert.Contains(cfg.Providers, p => p.Name == "github" && p.Kind == Auth.ProviderKind.GitHub);
        Assert.Contains(cfg.Providers, p => p.Name == "discord");
        Assert.DoesNotContain(cfg.Providers, p => p.Name == "microsoft"); // not configured
    }

    [Fact]
    public void Microsoft_tenant_is_configurable_and_minecraft_uses_consumers()
    {
        Auth.Config cfg = Read(("OAuthMicrosoftClientId", "m"), ("OAuthMicrosoftClientSecret", "ms"), ("OAuthMicrosoftTenant", "consumers"),
                               ("OAuthMinecraftClientId", "mc"), ("OAuthMinecraftClientSecret", "mcs"));
        Assert.Contains("consumers", cfg.Providers.Single(p => p.Name == "microsoft").AuthUrl);
        Auth.Provider mcp = cfg.Providers.Single(p => p.Name == "minecraft");
        Assert.Equal(Auth.ProviderKind.Minecraft, mcp.Kind);
        Assert.Contains("consumers", mcp.AuthUrl); // Minecraft is always the consumers tenant
    }

    [Fact]
    public void Legacy_generic_oauth_still_works_on_the_old_callback()
    {
        Auth.Provider p = Read(("OAuthClientId", "cid"), ("OAuthClientSecret", "cs")).Providers.Single();
        Assert.Equal("github", p.Name);            // default provider name
        Assert.Equal("/auth/callback", p.CallbackPath); // back-compat path preserved
    }
}
