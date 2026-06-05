using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace McadiffHub;

/// <summary>
/// The hub's tiny account database — users, personal access tokens (stored hashed), and per-repo
/// ownership/visibility. A single JSON file guarded by one lock, in keeping with the rest of the hub's
/// no-external-DB design. Self-hosted scale (a handful of users and repos) makes this entirely adequate.
/// </summary>
public sealed class HubDb
{
    private readonly string _path;
    private readonly object _lock = new();
    private readonly Db _db;
    private readonly Dictionary<string, TokenRecord> _byHash = new(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public HubDb(string path)
    {
        _path = Path.GetFullPath(path);
        _db = File.Exists(_path)
            ? JsonSerializer.Deserialize<Db>(File.ReadAllBytes(_path)) ?? new Db()
            : new Db();
        foreach (TokenRecord t in _db.Tokens) _byHash[t.Hash] = t;
    }

    // ---- users ----

    /// <summary>Insert or refresh an identity (login/name/avatar may change between logins).</summary>
    public HubUser UpsertUser(string id, string login, string name, string avatar)
    {
        lock (_lock)
        {
            int i = _db.Users.FindIndex(u => u.Id == id);
            var user = new HubUser(id, login, name, avatar,
                i >= 0 ? _db.Users[i].CreatedAt : Now(),
                i >= 0 ? _db.Users[i].Epoch : 0); // preserve the session epoch across logins (don't reset "sign out everywhere")
            if (i >= 0) _db.Users[i] = user; else _db.Users.Add(user);
            Save();
            return user;
        }
    }

    public HubUser? GetUser(string? id)
    {
        if (id is null) return null;
        lock (_lock) return _db.Users.FirstOrDefault(u => u.Id == id);
    }

    /// <summary>Resolve a typed login (what an owner enters to add a collaborator) to a known user.
    /// Returns null if nobody by that login has signed in yet.</summary>
    public HubUser? UserByLogin(string login)
    {
        lock (_lock) return _db.Users.FirstOrDefault(u => string.Equals(u.Login, login, StringComparison.OrdinalIgnoreCase));
    }

    // ---- tokens ----

    /// <summary>Mint a new personal access token. The plaintext is returned <em>once</em>; only its
    /// SHA-256 is stored, so a leak of the DB can't reconstruct usable tokens.</summary>
    public string CreateToken(string userId, string label, string scope = "write", string? expiresAt = null)
    {
        string secret = "mcahub_" + Base64Url(RandomNumberGenerator.GetBytes(30));
        var rec = new TokenRecord(Sha(secret), secret[..14], userId, Trim(label), Now(), null, Scope(scope), expiresAt);
        lock (_lock)
        {
            _db.Tokens.Add(rec);
            _byHash[rec.Hash] = rec;
            Save();
        }
        return secret;
    }

    /// <summary>Mint a replacement for a token (same label/scope/expiry), revoking the old one. Returns the
    /// new plaintext, or null if no such token.</summary>
    public string? RegenerateToken(string userId, string prefix)
    {
        lock (_lock)
        {
            int i = _db.Tokens.FindIndex(t => t.UserId == userId && t.Prefix == prefix);
            if (i < 0) return null;
            TokenRecord old = _db.Tokens[i];
            string secret = "mcahub_" + Base64Url(RandomNumberGenerator.GetBytes(30));
            var fresh = new TokenRecord(Sha(secret), secret[..14], userId, old.Label, Now(), null, Scope(old.Scope), old.ExpiresAt);
            _byHash.Remove(old.Hash);
            _db.Tokens[i] = fresh;
            _byHash[fresh.Hash] = fresh;
            Save();
            return secret;
        }
    }

    public IReadOnlyList<TokenInfo> ListTokens(string userId)
    {
        lock (_lock)
            return _db.Tokens.Where(t => t.UserId == userId)
                .Select(t => new TokenInfo(t.Prefix, t.Label, t.CreatedAt, t.LastUsedAt, Scope(t.Scope), t.ExpiresAt)).ToList();
    }

    public bool RevokeToken(string userId, string prefix)
    {
        lock (_lock)
        {
            int i = _db.Tokens.FindIndex(t => t.UserId == userId && t.Prefix == prefix);
            if (i < 0) return false;
            _byHash.Remove(_db.Tokens[i].Hash);
            _db.Tokens.RemoveAt(i);
            Save();
            return true;
        }
    }

    /// <summary>Revoke every token a user holds (part of "sign out everywhere"). Returns the count removed.</summary>
    public int RevokeAllTokens(string userId)
    {
        lock (_lock)
        {
            int n = _db.Tokens.RemoveAll(t => t.UserId == userId && _byHash.Remove(t.Hash));
            if (n > 0) Save();
            return n;
        }
    }

    /// <summary>Advance a user's session epoch so existing web sessions (carrying the old epoch) are
    /// rejected on their next request. Returns the new epoch.</summary>
    public int BumpEpoch(string userId)
    {
        lock (_lock)
        {
            int i = _db.Users.FindIndex(u => u.Id == userId);
            if (i < 0) return 0;
            _db.Users[i] = _db.Users[i] with { Epoch = _db.Users[i].Epoch + 1 };
            Save();
            return _db.Users[i].Epoch;
        }
    }

    /// <summary>Resolve a presented token to its owner + scope (or null if unknown or expired), stamping
    /// last-used. Lookup is by hash, so it never compares the secret itself.</summary>
    public TokenAuth? ResolveToken(string secret)
    {
        string hash = Sha(secret);
        lock (_lock)
        {
            if (!_byHash.TryGetValue(hash, out TokenRecord? t)) return null;
            if (t.ExpiresAt is { } exp && DateTimeOffset.TryParse(exp, out DateTimeOffset when) && when <= DateTimeOffset.UtcNow)
                return null; // expired
            int i = _db.Tokens.IndexOf(t);
            _db.Tokens[i] = t with { LastUsedAt = Now() };
            _byHash[hash] = _db.Tokens[i];
            Save();
            return new TokenAuth(t.UserId, Scope(t.Scope));
        }
    }

    private static string Scope(string? s) => s == "read" ? "read" : "write"; // anything but explicit read ⇒ write (back-compat)

    // ---- repo ownership / visibility ----

    public HubRepoMeta? GetRepo(string name)
    {
        lock (_lock) return _db.Repos.FirstOrDefault(r => r.Name == name);
    }

    /// <summary>Record ownership the first time a repo is seen (push auto-create); a no-op if it
    /// already has an owner. Returns the effective meta.</summary>
    public HubRepoMeta EnsureRepo(string name, string ownerId, bool isPrivate)
    {
        lock (_lock)
        {
            HubRepoMeta? existing = _db.Repos.FirstOrDefault(r => r.Name == name);
            if (existing is not null) return existing;
            var meta = new HubRepoMeta(name, ownerId, isPrivate, Now());
            _db.Repos.Add(meta);
            Save();
            return meta;
        }
    }

    public void SetPrivate(string name, bool isPrivate)
    {
        lock (_lock)
        {
            int i = _db.Repos.FindIndex(r => r.Name == name);
            if (i < 0) return;
            _db.Repos[i] = _db.Repos[i] with { Private = isPrivate };
            Save();
        }
    }

    // ---- collaborators ----

    /// <summary>A user's effective role on a repo: <c>owner</c>, <c>write</c>, <c>read</c>, or null
    /// (no access). The owner outranks everything; otherwise the strongest of any direct collaborator
    /// grant and any team grant the user inherits through membership.</summary>
    public string? RoleOf(string repo, string? userId)
    {
        if (userId is null) return null;
        lock (_lock)
        {
            if (_db.Repos.FirstOrDefault(r => r.Name == repo) is { } m && m.OwnerId == userId) return "owner";

            int best = Rank(_db.Collabs.FirstOrDefault(c => c.Repo == repo && c.UserId == userId)?.Role);
            foreach (TeamGrant g in _db.TeamGrants.Where(g => g.Repo == repo))
                if (_db.Teams.FirstOrDefault(t => t.Name == g.TeamName) is { } t && t.Members.Contains(userId))
                    best = Math.Max(best, Rank(g.Role));

            return best switch { 4 => "admin", 3 => "maintain", 2 => "write", 1 => "read", _ => null };
        }
    }

    public static bool IsRole(string role) => role is "read" or "write" or "maintain" or "admin";
    private static int Rank(string? role) => role switch { "admin" => 4, "maintain" => 3, "write" => 2, "read" => 1, _ => 0 };

    public IReadOnlyList<Collab> CollabsOf(string repo)
    {
        lock (_lock) return _db.Collabs.Where(c => c.Repo == repo).ToList();
    }

    /// <summary>Repos where the user is a collaborator (not those they own).</summary>
    public IReadOnlyList<Collab> CollabsForUser(string userId)
    {
        lock (_lock) return _db.Collabs.Where(c => c.UserId == userId).ToList();
    }

    public void SetCollab(string repo, string userId, string role)
    {
        if (!IsRole(role)) return;
        lock (_lock)
        {
            int i = _db.Collabs.FindIndex(c => c.Repo == repo && c.UserId == userId);
            if (i >= 0) _db.Collabs[i] = _db.Collabs[i] with { Role = role };
            else _db.Collabs.Add(new Collab(repo, userId, role));
            Save();
        }
    }

    public void RemoveCollab(string repo, string userId)
    {
        lock (_lock)
        {
            if (_db.Collabs.RemoveAll(c => c.Repo == repo && c.UserId == userId) > 0) Save();
        }
    }

    // ---- teams ----

    public Team? GetTeam(string name)
    {
        lock (_lock) return _db.Teams.FirstOrDefault(t => t.Name == name);
    }

    /// <summary>Create a team owned by <paramref name="ownerId"/> (who is also its first member).
    /// No-op if the name is taken.</summary>
    public Team? CreateTeam(string name, string ownerId)
    {
        lock (_lock)
        {
            if (_db.Teams.Any(t => t.Name == name)) return null;
            var team = new Team(name, ownerId, [ownerId], Now());
            _db.Teams.Add(team);
            Save();
            return team;
        }
    }

    /// <summary>Teams the user owns or belongs to.</summary>
    public IReadOnlyList<Team> TeamsForUser(string userId)
    {
        lock (_lock) return _db.Teams.Where(t => t.OwnerId == userId || t.Members.Contains(userId)).ToList();
    }

    public void AddTeamMember(string name, string userId)
    {
        lock (_lock)
        {
            if (_db.Teams.FirstOrDefault(t => t.Name == name) is { } t && !t.Members.Contains(userId))
            {
                t.Members.Add(userId);
                Save();
            }
        }
    }

    public void RemoveTeamMember(string name, string userId)
    {
        lock (_lock)
        {
            // the owner is always a member; don't strand a team with no owner-member
            if (_db.Teams.FirstOrDefault(t => t.Name == name) is { } t && userId != t.OwnerId && t.Members.Remove(userId))
                Save();
        }
    }

    public void DeleteTeam(string name)
    {
        lock (_lock)
        {
            bool changed = _db.Teams.RemoveAll(t => t.Name == name) > 0;
            changed |= _db.TeamGrants.RemoveAll(g => g.TeamName == name) > 0; // a deleted team grants nothing
            if (changed) Save();
        }
    }

    // ---- team → repo grants ----

    public IReadOnlyList<TeamGrant> TeamGrantsOf(string repo)
    {
        lock (_lock) return _db.TeamGrants.Where(g => g.Repo == repo).ToList();
    }

    public void SetTeamGrant(string repo, string team, string role)
    {
        if (!IsRole(role)) return;
        lock (_lock)
        {
            int i = _db.TeamGrants.FindIndex(g => g.Repo == repo && g.TeamName == team);
            if (i >= 0) _db.TeamGrants[i] = _db.TeamGrants[i] with { Role = role };
            else _db.TeamGrants.Add(new TeamGrant(repo, team, role));
            Save();
        }
    }

    public void RemoveTeamGrant(string repo, string team)
    {
        lock (_lock)
        {
            if (_db.TeamGrants.RemoveAll(g => g.Repo == repo && g.TeamName == team) > 0) Save();
        }
    }

    // ---- helpers ----

    private void Save()
    {
        string tmp = _path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        try
        {
            File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(_db, Json));
            File.Move(tmp, _path, overwrite: true); // atomic publish; a torn write never becomes the live db
        }
        catch (Exception e)
        {
            // Don't leave a stray temp file (e.g. on a full disk); the live db is untouched (never torn),
            // and we surface the failure clearly instead of silently dropping the mutation.
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best-effort */ }
            throw new IOException($"failed to persist the account database to {_path} (disk full?): {e.Message}", e);
        }
    }

    private static string Sha(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    private static string Now() => DateTimeOffset.UtcNow.ToString("o");
    private static string Trim(string s) => string.IsNullOrWhiteSpace(s) ? "token" : s.Trim()[..Math.Min(s.Trim().Length, 40)];

    private sealed record Db
    {
        public List<HubUser> Users { get; init; } = [];
        public List<TokenRecord> Tokens { get; init; } = [];
        public List<HubRepoMeta> Repos { get; init; } = [];
        public List<Collab> Collabs { get; init; } = [];
        public List<Team> Teams { get; init; } = [];
        public List<TeamGrant> TeamGrants { get; init; } = [];
    }

    private sealed record TokenRecord(string Hash, string Prefix, string UserId, string Label, string CreatedAt,
        string? LastUsedAt, string Scope = "write", string? ExpiresAt = null);
}

public sealed record HubUser(string Id, string Login, string Name, string Avatar, string CreatedAt, int Epoch = 0);
public sealed record HubRepoMeta(string Name, string OwnerId, bool Private, string CreatedAt);
public sealed record TokenInfo(string Prefix, string Label, string CreatedAt, string? LastUsedAt, string Scope = "write", string? ExpiresAt = null);
public sealed record TokenAuth(string UserId, string Scope); // resolved Bearer token: who + what it can do
public sealed record Collab(string Repo, string UserId, string Role); // Role: "read" | "write"
public sealed record Team(string Name, string OwnerId, List<string> Members, string CreatedAt);
public sealed record TeamGrant(string Repo, string TeamName, string Role); // Role: "read" | "write"
