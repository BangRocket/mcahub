using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace McadiffHub.Tests;

/// <summary>
/// Master-token lifecycle (#11): the admin token can be supplied hashed (MCAHUB_TOKEN_SHA256, keeping the
/// plaintext out of the environment), and listing more than one hash gives a no-downtime rotation window.
/// </summary>
public class MasterTokenTests
{
    private static Auth.Config Read(params (string Key, string Value)[] settings)
    {
        (string, string)[] baseline = [("OAuthClientId", ""), ("OAuthClientSecret", "")];
        IConfiguration c = new ConfigurationBuilder()
            .AddInMemoryCollection(baseline.Concat(settings.Select(s => (s.Key, s.Value)))
                .Select(s => new KeyValuePair<string, string?>(s.Item1, s.Item2)))
            .Build();
        return Auth.Read(c);
    }

    private static string Sha(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    [Fact]
    public void Plaintext_master_token_matches()
    {
        Auth.Config cfg = Read(("PushToken", "plain"));
        Assert.True(Auth.IsMasterToken(cfg, "plain"));
        Assert.False(Auth.IsMasterToken(cfg, "nope"));
    }

    [Fact]
    public void A_hashed_token_matches_with_no_plaintext_at_rest()
    {
        Auth.Config cfg = Read(("TokenSha256", Sha("rolled")));
        Assert.Null(cfg.MasterPlaintext);
        Assert.True(cfg.HasMaster);
        Assert.True(Auth.IsMasterToken(cfg, "rolled"));
        Assert.False(Auth.IsMasterToken(cfg, "wrong"));
    }

    [Fact]
    public void Two_hashes_give_a_rotation_window()
    {
        Auth.Config cfg = Read(("TokenSha256", $"{Sha("old")}, {Sha("new")}"));
        Assert.True(Auth.IsMasterToken(cfg, "old"));
        Assert.True(Auth.IsMasterToken(cfg, "new"));
        Assert.False(Auth.IsMasterToken(cfg, "other"));
    }

    [Fact]
    public async Task A_hashed_master_token_authorizes_a_push()
    {
        using var f = new HubFactory(HubMode.Open, settings: [new("TokenSha256", Sha("secret"))]);
        using HttpResponseMessage ok = await Accounts.PushAsync(f, "secret", "world1");
        Assert.NotEqual(HttpStatusCode.Unauthorized, ok.StatusCode); // matched the hash → admin → allowed
        using HttpResponseMessage bad = await Accounts.PushAsync(f, "wrong", "world2");
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
    }
}
