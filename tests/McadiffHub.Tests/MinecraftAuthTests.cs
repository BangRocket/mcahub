using System.Net;
using System.Text;

namespace McadiffHub.Tests;

/// <summary>
/// The Minecraft sign-in chain (#37): the request bodies, the XErr→message mapping, and the full
/// Microsoft→Xbox→XSTS→Minecraft flow, exercised against canned responses — never live Xbox/MC services.
/// </summary>
public class MinecraftAuthTests
{
    private sealed class StubHandler(Func<string, (HttpStatusCode, string)> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            (HttpStatusCode code, string body) = route(req.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private static HttpClient Client(Func<string, (HttpStatusCode, string)> route) => new(new StubHandler(route));

    private const string XblOk = """{"Token":"xbl-token","DisplayClaims":{"xui":[{"uhs":"user-hash"}]}}""";

    [Fact]
    public async Task The_chain_resolves_the_java_uuid_and_username()
    {
        HttpClient http = Client(url => url switch
        {
            "https://user.auth.xboxlive.com/user/authenticate" => (HttpStatusCode.OK, XblOk),
            "https://xsts.auth.xboxlive.com/xsts/authorize" => (HttpStatusCode.OK, """{"Token":"xsts-token","DisplayClaims":{"xui":[{"uhs":"user-hash"}]}}"""),
            "https://api.minecraftservices.com/authentication/login_with_xbox" => (HttpStatusCode.OK, """{"access_token":"mc-token","expires_in":86400}"""),
            "https://api.minecraftservices.com/minecraft/profile" => (HttpStatusCode.OK, """{"id":"069a79f4-uuid","name":"Notch"}"""),
            _ => (HttpStatusCode.NotFound, "{}"),
        });

        (string uuid, string username) = await MinecraftAuth.ResolveAsync("ms-token", http, default);
        Assert.Equal("069a79f4-uuid", uuid);
        Assert.Equal("Notch", username);
    }

    [Fact]
    public async Task A_no_java_ownership_404_is_a_friendly_error()
    {
        HttpClient http = Client(url => url switch
        {
            "https://user.auth.xboxlive.com/user/authenticate" => (HttpStatusCode.OK, XblOk),
            "https://xsts.auth.xboxlive.com/xsts/authorize" => (HttpStatusCode.OK, XblOk),
            "https://api.minecraftservices.com/authentication/login_with_xbox" => (HttpStatusCode.OK, """{"access_token":"mc"}"""),
            "https://api.minecraftservices.com/minecraft/profile" => (HttpStatusCode.NotFound, """{"error":"NOT_FOUND"}"""),
            _ => (HttpStatusCode.OK, "{}"),
        });

        MinecraftAuthException ex = await Assert.ThrowsAsync<MinecraftAuthException>(() => MinecraftAuth.ResolveAsync("t", http, default));
        Assert.Contains("doesn't own Minecraft", ex.Message);
    }

    [Fact]
    public async Task A_child_account_xsts_401_explains_microsoft_family()
    {
        HttpClient http = Client(url => url switch
        {
            "https://user.auth.xboxlive.com/user/authenticate" => (HttpStatusCode.OK, XblOk),
            "https://xsts.auth.xboxlive.com/xsts/authorize" => (HttpStatusCode.Unauthorized, """{"XErr":2148916238}"""),
            _ => (HttpStatusCode.OK, "{}"),
        });

        MinecraftAuthException ex = await Assert.ThrowsAsync<MinecraftAuthException>(() => MinecraftAuth.ResolveAsync("t", http, default));
        Assert.Contains("Microsoft Family", ex.Message);
    }

    [Theory]
    [InlineData(2148916233L, "Xbox profile")]
    [InlineData(2148916238L, "Microsoft Family")]
    [InlineData(2148916227L, "banned")]
    [InlineData(999L, "rejected")] // unknown → generic
    public void Xsts_errors_map_to_friendly_messages(long xerr, string expected) =>
        Assert.Contains(expected, MinecraftAuth.XstsErrorMessage($$"""{"XErr":{{xerr}}}"""));

    [Fact]
    public void Request_bodies_are_well_formed()
    {
        Assert.Contains("\"RpsTicket\":\"d=ms-token\"", MinecraftAuth.XboxBody("ms-token"));
        Assert.Contains("rp://api.minecraftservices.com/", MinecraftAuth.XstsBody("xbl"));
    }
}
