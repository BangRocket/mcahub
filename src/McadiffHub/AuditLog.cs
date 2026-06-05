using System.Text.Json;

namespace McadiffHub;

/// <summary>One security-relevant change: who did what to which repo, when, and from where.</summary>
public sealed record AuditEntry(string At, string Actor, string Action, string? Repo, string? Detail, string Source, string? Ip);

/// <summary>
/// Append-only audit trail (#16) of role / visibility / ownership / ref / token changes, written as JSON
/// lines next to <c>hub.json</c>. It's the after-the-fact review surface for the identity controls (and
/// the detection mechanism for the claim-on-first-push and role-change vectors). Append never rewrites,
/// so it slots beside <see cref="HubDb"/> without touching its state; a corrupt line never breaks a read.
/// </summary>
public sealed class AuditLog(string path)
{
    private readonly string _path = Path.GetFullPath(path);
    private readonly object _lock = new();

    public void Append(string actor, string action, string? repo, string? detail, string source, string? ip)
    {
        var entry = new AuditEntry(DateTimeOffset.UtcNow.ToString("o"), actor, action, repo, detail, source, ip);
        string line = JsonSerializer.Serialize(entry);
        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.AppendAllText(_path, line + "\n");
        }
    }

    /// <summary>The most recent entries for a repo, newest first, capped at <paramref name="limit"/>.</summary>
    public IReadOnlyList<AuditEntry> Recent(string repo, int limit)
    {
        lock (_lock)
        {
            if (!File.Exists(_path)) return [];
            var hits = new List<AuditEntry>();
            foreach (string line in File.ReadLines(_path))
            {
                if (line.Length == 0) continue;
                AuditEntry? e;
                try { e = JsonSerializer.Deserialize<AuditEntry>(line); } catch { continue; } // skip a torn/corrupt line
                if (e is not null && e.Repo == repo) hits.Add(e);
            }
            hits.Reverse(); // newest first
            return hits.Count > limit ? hits.GetRange(0, limit) : hits;
        }
    }
}
