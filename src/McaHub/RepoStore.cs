using System.Text.RegularExpressions;
using McaHub.Rust;

namespace McaHub;

/// <summary>Hosts a set of bare mcagit repositories under a data directory — the hub's "organization
/// of repos". Repo names are validated so a name can never escape the data dir. Storage is the Rust
/// (blake3/zstd) object format; the sidecar <c>mcagit serve</c> serves <c>&lt;dataDir&gt;/&lt;name&gt;</c>
/// at <c>/r/&lt;name&gt;/</c> and auto-creates on first push.</summary>
public sealed partial class RepoStore(string dataDir, RustEngine rust)
{
    private readonly string _root = Path.GetFullPath(dataDir);
    private readonly RustEngine _rust = rust;

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex ValidName();

    public static bool IsValidName(string name) => ValidName().IsMatch(name);

    public string PathOf(string name)
    {
        if (!IsValidName(name)) throw new ArgumentException($"invalid repo name: '{name}'");
        return Path.Combine(_root, name);
    }

    /// <summary>True if a mcagit repo (an <c>objects/</c> store) lives at this name.</summary>
    public bool Exists(string name) =>
        IsValidName(name) && Directory.Exists(Path.Combine(PathOf(name), "objects"));

    /// <summary>Delete a hosted repo from disk. Returns false if it didn't exist.</summary>
    public bool Delete(string name)
    {
        if (!Exists(name)) return false;
        Directory.Delete(PathOf(name), recursive: true);
        return true;
    }

    /// <summary>Every hosted repo, summarized for the listing page (via the Rust engine).</summary>
    public IEnumerable<RepoSummary> List()
    {
        if (!Directory.Exists(_root)) yield break;
        foreach (string dir in Directory.EnumerateDirectories(_root).OrderBy(d => d, StringComparer.Ordinal))
        {
            if (!Directory.Exists(Path.Combine(dir, "objects"))) continue; // not a mcagit repo
            string name = Path.GetFileName(dir);
            int branches = CountBranches(dir);
            var log = _rust.Log(dir);
            if (log.Count == 0) { yield return new RepoSummary(name, branches, null, null); continue; }
            string? when = null;
            try { when = _rust.ReadCommit(dir, log[0].Hash).Time; } catch { /* tip unreadable */ }
            yield return new RepoSummary(name, branches, log[0].Message, when);
        }
    }

    private static int CountBranches(string repoDir)
    {
        string heads = Path.Combine(repoDir, "refs", "heads");
        try
        {
            return Directory.Exists(heads)
                ? Directory.EnumerateFiles(heads, "*", SearchOption.AllDirectories).Count()
                : 0;
        }
        catch { return 0; }
    }
}

public sealed record RepoSummary(string Name, int Branches, string? LastMessage, string? LastWhen);
