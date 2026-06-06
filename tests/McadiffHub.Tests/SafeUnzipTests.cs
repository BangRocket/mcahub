using System.IO.Compression;

namespace McadiffHub.Tests;

/// <summary>
/// The upload extractor's guards (#26): a normal archive extracts, but zip-slip paths, too many entries,
/// and oversized/lying archives are refused — the new attack surface a browser upload opens.
/// </summary>
public class SafeUnzipTests
{
    private static MemoryStream Zip(params (string Name, byte[] Data)[] entries)
    {
        var ms = new MemoryStream();
        using (var a = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach ((string name, byte[] data) in entries)
            {
                using Stream s = a.CreateEntry(name).Open();
                s.Write(data);
            }
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public void Extracts_a_normal_world_archive()
    {
        using var tmp = new TempDir();
        using MemoryStream z = Zip(("region/r.0.0.mca", [1, 2, 3]), ("level.dat", [4]));

        SafeUnzip.Extract(z, tmp.Path, maxBytes: 1 << 20, maxEntries: 100);

        Assert.True(File.Exists(Path.Combine(tmp.Path, "region", "r.0.0.mca")));
        Assert.True(File.Exists(Path.Combine(tmp.Path, "level.dat")));
    }

    [Fact]
    public void Refuses_a_zip_slip_path_and_writes_nothing_outside()
    {
        using var tmp = new TempDir();
        string dest = Path.Combine(tmp.Path, "world");
        using MemoryStream z = Zip(("../escape.txt", [1]));

        Assert.Throws<UnsafeUploadException>(() => SafeUnzip.Extract(z, dest, 1 << 20, 100));
        Assert.False(File.Exists(Path.Combine(tmp.Path, "escape.txt"))); // nothing escaped the destination
    }

    [Fact]
    public void Refuses_too_many_entries()
    {
        using var tmp = new TempDir();
        using MemoryStream z = Zip(Enumerable.Range(0, 11).Select(i => ($"f{i}.dat", new byte[] { 1 })).ToArray());

        Assert.Throws<UnsafeUploadException>(() => SafeUnzip.Extract(z, tmp.Path, 1 << 20, maxEntries: 10));
    }

    [Fact]
    public void Refuses_an_oversized_archive()
    {
        using var tmp = new TempDir();
        using MemoryStream z = Zip(("big.bin", new byte[2000]));

        Assert.Throws<UnsafeUploadException>(() => SafeUnzip.Extract(z, tmp.Path, maxBytes: 1000, maxEntries: 100));
    }
}
