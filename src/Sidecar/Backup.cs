using McaDiff.Repo;

namespace McadiffHub.Sidecar;

/// <summary>
/// The snapshot-and-commit step the sidecar runs on each tick. Snapshots a world directory into a local
/// mcadiff repo and commits it on <paramref name="branch"/> — but only if the world actually changed
/// (mcadiff content-addresses the tree, so an unchanged world produces the same tree and we skip the
/// commit). Pure and deterministic, so it's unit-tested without a server; the network push lives in the
/// loop (<see cref="RemoteOps.Push"/>).
/// </summary>
public static class Backup
{
    /// <summary>Returns the new commit hash, or null when the world is unchanged since the last backup.</summary>
    public static string? Snapshot(Repository repo, string worldDir, string branch, string author, string message)
    {
        string tree = repo.WriteManifest(Snapshotter.Snapshot(repo, worldDir));
        string? head = repo.ReadBranch(branch);
        if (head is not null && repo.ReadCommit(head).Tree == tree) return null; // nothing changed
        string commit = repo.CreateCommit(tree, head is null ? [] : [head], message, author);
        repo.WriteBranch(branch, commit);
        return commit;
    }
}
