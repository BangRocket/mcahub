using System.Diagnostics;

namespace McaHub.Sidecar;

/// <summary>
/// The snapshot-and-commit step the sidecar runs each tick, via the Rust <c>mcagit</c> binary:
/// <c>mcagit -C &lt;repo&gt; commit -m &lt;msg&gt; &lt;world&gt;</c>. mcagit content-addresses the tree, so an
/// unchanged world is a no-op (empty output) and the push is skipped.
/// </summary>
public static class Backup
{
    /// <summary>Returns the new commit hash, or null when the world is unchanged since the last backup.</summary>
    public static string? Snapshot(string mcagit, string repoDir, string worldDir, string message)
    {
        var (code, outp, err) = Mcagit.Run(mcagit, ["-C", repoDir, "commit", "-m", message, worldDir]);
        if (code != 0) throw new InvalidOperationException($"mcagit commit exited {code}: {err.Trim()}");
        string hash = outp.Trim();
        return hash.Length > 0 ? hash : null; // empty stdout ⇒ "nothing to commit"
    }
}

/// <summary>Minimal <c>mcagit</c> process runner for the sidecar (the binary is the Rust engine).</summary>
internal static class Mcagit
{
    public static (int Code, string Out, string Err) Run(string binary, string[] args, string? token = null)
    {
        var psi = new ProcessStartInfo(binary)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);
        if (token is { Length: > 0 }) psi.Environment["MCAGIT_TOKEN"] = token;
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"failed to launch {binary}");
        string o = p.StandardOutput.ReadToEnd();
        string e = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, o, e);
    }
}
