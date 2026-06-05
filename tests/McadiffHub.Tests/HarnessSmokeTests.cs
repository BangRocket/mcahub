using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace McadiffHub.Tests;

public class HarnessSmokeTests
{
    [Fact]
    public async Task Open_mode_home_page_returns_200()
    {
        using var hub = new HubFactory(HubMode.Open);
        using HttpClient client = hub.CreateClient();
        HttpResponseMessage resp = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Open_mode_has_no_account_page()
    {
        using var hub = new HubFactory(HubMode.Open);
        using HttpClient client = hub.CreateClient();
        HttpResponseMessage resp = await client.GetAsync("/account");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Accounts_mode_account_page_redirects_to_login()
    {
        using var hub = new HubFactory(HubMode.Accounts);
        using HttpClient client = hub.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        HttpResponseMessage resp = await client.GetAsync("/account");
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.StartsWith("/auth/login", resp.Headers.Location?.OriginalString);
    }
}
