using System.Text.RegularExpressions;
using McaDiff.Repo;

namespace McaHub;

/// <summary>Hosts a set of bare mcadiff repositories under a data directory — the hub's "organization
/// of repos". Repo names are validated so a name can never escape the data dir.</summary>
public sealed partial class RepoStore(string dataDir)
{
    private readonly string _root = Path.GetFullPath(dataDir);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex ValidName();

    public static bool IsValidName(string name) => ValidName().IsMatch(name);

    public string PathOf(string name)
    {
        if (!IsValidName(name)) throw new ArgumentException($"invalid repo name: '{name}'");
        return Path.Combine(_root, name + ".mcagit");
    }

    public bool Exists(string name) => IsValidName(name) && Repository.IsRepository(PathOf(name));

    public Repository Open(string name) => Repository.Open(PathOf(name));

    public Repository Create(string name)
    {
        if (Exists(name)) throw new InvalidOperationException($"repo already exists: {name}");
        Directory.CreateDirectory(_root);
        return Repository.Init(PathOf(name));
    }

    /// <summary>Delete a hosted repo from disk. Returns false if it didn't exist.</summary>
    public bool Delete(string name)
    {
        if (!Exists(name)) return false;
        Directory.Delete(PathOf(name), recursive: true);
        return true;
    }

    /// <summary>Every hosted repo, with a little summary for the listing page.</summary>
    public IEnumerable<RepoSummary> List()
    {
        if (!Directory.Exists(_root)) yield break;
        foreach (string dir in Directory.EnumerateDirectories(_root, "*.mcagit").OrderBy(d => d, StringComparer.Ordinal))
        {
            if (!Repository.IsRepository(dir)) continue;
            string name = Path.GetFileNameWithoutExtension(dir);
            Repository repo = Repository.Open(dir);
            string? tip = repo.HeadCommit();
            yield return new RepoSummary(
                name,
                repo.Branches().Count(),
                tip is null ? null : repo.ReadCommit(tip).Message,
                tip is null ? null : repo.ReadCommit(tip).CommitTime ?? repo.ReadCommit(tip).Time);
        }
    }
}

public sealed record RepoSummary(string Name, int Branches, string? LastMessage, string? LastWhen);
