using System.Net;

namespace McaHub.Tests;

/// <summary>
/// The #13 hardening cluster: the open-redirect guard blocks both `//host` and `/\host`, and logout is a
/// CSRF-protected POST rather than a forgeable GET.
/// </summary>
public class MinorHardeningTests
{
    [Theory]
    [InlineData("/account", "/account")]
    [InlineData("/r/world", "/r/world")]
    [InlineData("/", "/")]
    [InlineData("//evil.example", "/account")]   // protocol-relative
    [InlineData("/\\evil.example", "/account")]  // backslash — legacy browsers read /\ as //
    [InlineData("https://evil.example", "/account")]
    [InlineData(null, "/account")]
    [InlineData("", "/account")]
    public void Open_redirect_guard_only_allows_same_site_paths(string? input, string expected)
        => Assert.Equal(expected, Auth.Local(input));

    [Fact]
    public async Task Logout_is_a_csrf_protected_post_not_a_get_link()
    {
        using var f = new HubFactory(HubMode.Accounts);
        HttpClient alice = await Accounts.SignInAsync(f, "alice");

        // the header offers a logout form (POST), not a GET link
        string page = await (await alice.GetAsync("/account")).Content.ReadAsStringAsync();
        Assert.Contains("action=\"/auth/logout\"", page);

        // a POST without the antiforgery token is rejected
        using var noCsrf = new FormUrlEncodedContent(Array.Empty<KeyValuePair<string, string>>());
        HttpResponseMessage resp = await alice.PostAsync("/auth/logout", noCsrf);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
