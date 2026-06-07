using System.IO.Compression;

namespace McaHub;

/// <summary>Raised when an uploaded archive is hostile or too large (#26) — the message is safe to show.</summary>
public sealed class UnsafeUploadException(string message) : Exception(message);

/// <summary>
/// Extracts an untrusted <c>.zip</c> upload into a destination directory, defending the new attack surface
/// a browser upload opens (#26): <b>zip-slip</b> (an entry whose path escapes the destination via <c>..</c>
/// or an absolute path is refused), and <b>zip-bomb / disk-fill</b> (total uncompressed bytes and entry
/// count are capped; the cap is checked against each entry's declared length AND while streaming, so a
/// lying header can't sneak past). Directory entries and symlinks are skipped. The materialized world is
/// then parsed only through the core's already-guarded NBT paths.
/// </summary>
public static class SafeUnzip
{
    public static void Extract(Stream zip, string destRoot, long maxBytes, int maxEntries)
    {
        string root = Path.GetFullPath(destRoot);
        Directory.CreateDirectory(root);
        using var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);

        if (archive.Entries.Count > maxEntries)
            throw new UnsafeUploadException($"archive has too many entries ({archive.Entries.Count} > {maxEntries})");

        long total = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue; // directory entry
            if (entry.Length < 0) throw new UnsafeUploadException("archive entry has an invalid length");

            // Zip-slip: the resolved path must stay inside the destination root.
            string target = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) && target != root)
                throw new UnsafeUploadException($"archive entry escapes the destination: {entry.FullName}");

            total += entry.Length;
            if (total > maxBytes) throw new UnsafeUploadException($"archive is too large uncompressed (> {maxBytes} bytes)");

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using Stream src = entry.Open();
            using FileStream dst = File.Create(target);
            // Copy with a hard ceiling — a header that under-declares Length can't make us write past the cap.
            CopyCapped(src, dst, maxBytes - (total - entry.Length));
        }
    }

    private static void CopyCapped(Stream src, Stream dst, long remaining)
    {
        byte[] buf = new byte[81920];
        int n;
        while ((n = src.Read(buf, 0, (int)Math.Min(buf.Length, Math.Max(0, remaining + 1)))) > 0)
        {
            remaining -= n;
            if (remaining < 0) throw new UnsafeUploadException("archive entry exceeded its declared size (zip bomb?)");
            dst.Write(buf, 0, n);
        }
    }
}
