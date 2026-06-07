using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using McaDiff.Repo;

namespace McaHub.Tests;

/// <summary>
/// Drives the hub's network protocol through the core's <see cref="IRemoteTransport"/> client seam over a
/// <see cref="HubFactory"/> <see cref="HttpClient"/> — so a test can run a real <c>RemoteOps</c>
/// push/clone round-trip against the in-memory server (no live port). Mirrors the core's HTTP transport,
/// but on the test HttpClient. Synchronous on purpose: the interface is.
/// </summary>
internal sealed class WafTransport(HttpClient http, string repo, string? token) : IRemoteTransport
{
    public RefAdvertisement ListRefs()
    {
        using HttpResponseMessage r = Send(new(HttpMethod.Get, $"/r/{repo}/info/refs"));
        r.EnsureSuccessStatusCode();
        return Read<RefAdvertisement>(r);
    }

    public IReadOnlyList<string> Missing(IReadOnlyList<string> hashes)
    {
        using HttpResponseMessage r = Send(new(HttpMethod.Post, $"/r/{repo}/have") { Content = Json(hashes) });
        r.EnsureSuccessStatusCode();
        return Read<List<string>>(r);
    }

    public byte[] GetObject(string hash)
    {
        using HttpResponseMessage r = Send(new(HttpMethod.Get, $"/r/{repo}/objects/{hash}"));
        r.EnsureSuccessStatusCode();
        return r.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
    }

    public void PutObject(string hash, byte[] compressed)
    {
        using HttpResponseMessage r = Send(new(HttpMethod.Post, $"/r/{repo}/objects/{hash}") { Content = new ByteArrayContent(compressed) });
        r.EnsureSuccessStatusCode();
    }

    public void UpdateRef(string branch, string? expectedOld, string newHash, bool force)
    {
        var u = new RefUpdate { Old = expectedOld, New = newHash, Force = force };
        using HttpResponseMessage r = Send(new(HttpMethod.Post, $"/r/{repo}/refs/heads/{branch}") { Content = Json(u) });
        r.EnsureSuccessStatusCode();
    }

    public void Dispose() { }

    private HttpResponseMessage Send(HttpRequestMessage req)
    {
        if (token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http.SendAsync(req).GetAwaiter().GetResult();
    }

    private static StringContent Json<T>(T value) =>
        new(JsonSerializer.Serialize(value, HttpProtocol.Json), Encoding.UTF8, "application/json");

    private static T Read<T>(HttpResponseMessage r) =>
        JsonSerializer.Deserialize<T>(r.Content.ReadAsStringAsync().GetAwaiter().GetResult(), HttpProtocol.Json)!;
}
