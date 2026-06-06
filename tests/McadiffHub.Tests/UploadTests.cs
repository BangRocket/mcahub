using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using McaDiff.Anvil;

namespace McadiffHub.Tests;

/// <summary>
/// Drag-and-drop upload (#26): a world .zip renders its map inline with no CLI/account/persistence, and a
/// non-world archive is handled with a friendly message rather than a crash.
/// </summary>
public class UploadTests
{
    private static byte[] WorldZip(params RawChunk[] chunks)
    {
        using var tmp = new TempDir();
        string world = Worlds.Write(tmp.Path, chunks);
        var ms = new MemoryStream();
        using (var a = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (string file in Directory.EnumerateFiles(world, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(world, file).Replace('\\', '/');
                using Stream e = a.CreateEntry(rel).Open();
                using FileStream fs = File.OpenRead(file);
                fs.CopyTo(e);
            }
        return ms.ToArray();
    }

    private static async Task<HttpResponseMessage> Post(HttpClient c, byte[] zip, string filename)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(zip);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        content.Add(file, "world", filename);
        return await c.PostAsync("/upload", content);
    }

    [Fact]
    public async Task Uploading_a_world_zip_renders_its_map_inline()
    {
        using var f = new HubFactory(HubMode.Open);
        using var c = f.CreateClient();

        using HttpResponseMessage resp = await Post(c, WorldZip(Worlds.StoneChunk(0, 0), Worlds.StoneChunk(1, 0)), "world.zip");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        string html = await resp.Content.ReadAsStringAsync();
        Assert.Contains("Uploaded world", html);
        Assert.Contains("data:image/png;base64,", html); // the map is inlined; nothing persisted
    }

    [Fact]
    public async Task A_non_world_archive_is_handled_gracefully()
    {
        using var f = new HubFactory(HubMode.Open);
        using var c = f.CreateClient();
        var ms = new MemoryStream();
        using (var a = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using Stream s = a.CreateEntry("readme.txt").Open();
            s.Write([1, 2, 3]);
        }

        using HttpResponseMessage resp = await Post(c, ms.ToArray(), "notaworld.zip");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode); // a friendly message, not a 500/crash
        Assert.Contains("region/", await resp.Content.ReadAsStringAsync());
    }
}
