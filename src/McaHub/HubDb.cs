using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace McaHub;

/// <summary>
/// The hub's tiny account database — users, personal access tokens (stored hashed), and per-repo
/// ownership/visibility — a single JSON file, in keeping with the hub's no-external-DB design.
///
/// Multi-instance safe (#41): so two instances can share one <c>hub.json</c> behind a proxy (zero-downtime
/// rolling deploys), every write takes a <b>cross-process advisory file lock</b> and <b>reloads from disk
/// before mutating</b> — so a second instance never clobbers the first's committed change (the read-modify-
/// write is atomic across processes). Reads reload only when the file's stamp (length + mtime) changed, so
/// a revoked token / changed grant on one instance is seen by the other on its next read. Writes publish
/// atomically (temp + rename), so a reader never sees a torn file.
/// </summary>
public sealed class HubDb
{
    private readonly string _path;
    private readonly object _lock = new();                 // in-process serialization
    private Db _db = new();                                 // reloaded under the lock before each mutation
    private readonly Dictionary<string, TokenRecord> _byHash = new(StringComparer.Ordinal);
    private (long Len, long Ticks) _stamp;                 // last-loaded file fingerprint (change detector)
    private FileStream? _flock;                            // held while a cross-process write is in flight
    private int _flockDepth;                               // reentrancy (guarded by _lock)

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private const int CurrentSchema = 1; // bump when hub.json's shape changes incompatibly

    public HubDb(string path)
    {
        _path = Path.GetFullPath(path);
        Reload(); // throws HubDbSchemaException on a too-new file (#32)
    }

    // ---- users ----

    /// <summary>Insert or refresh an identity (login/name/avatar may change between logins).</summary>
    public HubUser UpsertUser(string id, string login, string name, string avatar) => Mutate(() =>
    {
        int i = _db.Users.FindIndex(u => u.Id == id);
        HubUser? prev = i >= 0 ? _db.Users[i] : null;
        // preserve the session epoch + Minecraft identity across logins (don't reset "sign out everywhere")
        var user = new HubUser(id, login, name, avatar, prev?.CreatedAt ?? Now(), prev?.Epoch ?? 0, prev?.McUuid, prev?.McUsername, prev?.Suspended ?? false, prev?.AgeAck ?? false);
        if (i >= 0) _db.Users[i] = user; else _db.Users.Add(user);
        return user;
    });

    /// <summary>Record a user's verified Minecraft Java identity (#37) — the attribution primitive.</summary>
    public void SetMinecraftIdentity(string userId, string uuid, string username) => Mutate(() =>
    {
        int i = _db.Users.FindIndex(u => u.Id == userId);
        if (i >= 0) _db.Users[i] = _db.Users[i] with { McUuid = uuid, McUsername = username };
    });

    /// <summary>Suspend or un-suspend a user — a non-destructive penalty enforced across the capability checks.
    /// Suspending also revokes the user's PATs and bumps their epoch, so the lockout takes effect immediately
    /// on existing tokens and live web sessions, not just on new requests. (#35, audit)</summary>
    public void SetSuspended(string userId, bool suspended)
    {
        Mutate(() =>
        {
            int i = _db.Users.FindIndex(u => u.Id == userId);
            if (i >= 0) _db.Users[i] = _db.Users[i] with { Suspended = suspended };
        });
        if (suspended) { RevokeAllTokens(userId); BumpEpoch(userId); } // CLI tokens dead + every session invalidated now
    }

    /// <summary>Record that a user confirmed the age gate (#35).</summary>
    public void SetAgeAck(string userId) => Mutate(() =>
    {
        int i = _db.Users.FindIndex(u => u.Id == userId);
        if (i >= 0) _db.Users[i] = _db.Users[i] with { AgeAck = true };
    });

    /// <summary>Erase a user (GDPR/CCPA): their identity, tokens, collaborator grants, team memberships,
    /// and owned teams + the metadata of repos they own. Returns the names of the repos they owned so the
    /// caller can delete them from disk (and their caches).</summary>
    public IReadOnlyList<string> DeleteUser(string userId) => Mutate(() =>
    {
        List<string> owned = _db.Repos.Where(r => r.OwnerId == userId).Select(r => r.Name).ToList();
        foreach (string name in owned)
        {
            _db.Repos.RemoveAll(r => r.Name == name);
            _db.Collabs.RemoveAll(c => c.Repo == name);
            _db.TeamGrants.RemoveAll(g => g.Repo == name);
        }
        _db.Collabs.RemoveAll(c => c.UserId == userId);                 // their grants on others' repos
        foreach (string team in _db.Teams.Where(t => t.OwnerId == userId).Select(t => t.Name).ToList())
        {
            _db.Teams.RemoveAll(t => t.Name == team);                   // teams they own
            _db.TeamGrants.RemoveAll(g => g.TeamName == team);
        }
        foreach (Team t in _db.Teams) t.Members.Remove(userId);          // memberships in others' teams
        foreach (TokenRecord tok in _db.Tokens.Where(t => t.UserId == userId).ToList()) _byHash.Remove(tok.Hash);
        _db.Tokens.RemoveAll(t => t.UserId == userId);
        _db.Users.RemoveAll(u => u.Id == userId);
        return (IReadOnlyList<string>)owned;
    });

    /// <summary>How many worlds a user owns — for the per-user governance quota (#35).</summary>
    public int OwnedRepoCount(string userId) => Query(() => _db.Repos.Count(r => r.OwnerId == userId));

    /// <summary>Hand a repo to another existing user (#17). The previous owner is kept on as an admin
    /// collaborator (so they don't lose access); all other collaborator/team grants are untouched. Returns
    /// false if the repo or the new owner doesn't exist, or it's a no-op (already the owner).</summary>
    public bool TransferOwnership(string repo, string newOwnerId) => Mutate(() =>
    {
        int i = _db.Repos.FindIndex(r => r.Name == repo);
        if (i < 0 || _db.Users.All(u => u.Id != newOwnerId)) return false;
        string oldOwner = _db.Repos[i].OwnerId;
        if (oldOwner == newOwnerId) return false;
        _db.Repos[i] = _db.Repos[i] with { OwnerId = newOwnerId };
        _db.Collabs.RemoveAll(c => c.Repo == repo && c.UserId == newOwnerId); // the owner needs no grant
        if (oldOwner != "__system__") // a master-token/ops-owned repo has no human to demote
        {
            _db.Collabs.RemoveAll(c => c.Repo == repo && c.UserId == oldOwner);
            _db.Collabs.Add(new Collab(repo, oldOwner, "admin")); // keep the ex-owner's access
        }
        return true;
    });

    /// <summary>Forget a repo's metadata + all its grants (the on-disk repo is deleted separately).</summary>
    public void DeleteRepo(string name) => Mutate(() =>
    {
        _db.Repos.RemoveAll(r => r.Name == name);
        _db.Collabs.RemoveAll(c => c.Repo == name);
        _db.TeamGrants.RemoveAll(g => g.Repo == name);
    });

    public HubUser? GetUser(string? id) => id is null ? null : Query(() => _db.Users.FirstOrDefault(u => u.Id == id));

    /// <summary>Resolve a typed login (what an owner enters to add a collaborator) to a known user.
    /// Returns null if nobody by that login has signed in yet.</summary>
    public HubUser? UserByLogin(string login) =>
        Query(() => _db.Users.FirstOrDefault(u => string.Equals(u.Login, login, StringComparison.OrdinalIgnoreCase)));

    // ---- tokens ----

    /// <summary>Mint a new personal access token. The plaintext is returned <em>once</em>; only its
    /// SHA-256 is stored, so a leak of the DB can't reconstruct usable tokens.</summary>
    public string CreateToken(string userId, string label, string scope = "write", string? expiresAt = null)
    {
        string secret = "mcahub_" + Base64Url(RandomNumberGenerator.GetBytes(30));
        var rec = new TokenRecord(Sha(secret), secret[..14], userId, Trim(label), Now(), null, Scope(scope), expiresAt);
        Mutate(() => { _db.Tokens.Add(rec); _byHash[rec.Hash] = rec; });
        return secret;
    }

    /// <summary>Mint a replacement for a token (same label/scope/expiry), revoking the old one. Returns the
    /// new plaintext, or null if no such token.</summary>
    public string? RegenerateToken(string userId, string prefix) => Mutate(() =>
    {
        int i = _db.Tokens.FindIndex(t => t.UserId == userId && t.Prefix == prefix);
        if (i < 0) return null;
        TokenRecord old = _db.Tokens[i];
        string secret = "mcahub_" + Base64Url(RandomNumberGenerator.GetBytes(30));
        var fresh = new TokenRecord(Sha(secret), secret[..14], userId, old.Label, Now(), null, Scope(old.Scope), old.ExpiresAt);
        _byHash.Remove(old.Hash);
        _db.Tokens[i] = fresh;
        _byHash[fresh.Hash] = fresh;
        return secret;
    });

    public IReadOnlyList<TokenInfo> ListTokens(string userId) => Query(() =>
        (IReadOnlyList<TokenInfo>)_db.Tokens.Where(t => t.UserId == userId)
            .Select(t => new TokenInfo(t.Prefix, t.Label, t.CreatedAt, t.LastUsedAt, Scope(t.Scope), t.ExpiresAt)).ToList());

    public bool RevokeToken(string userId, string prefix) => Mutate(() =>
    {
        int i = _db.Tokens.FindIndex(t => t.UserId == userId && t.Prefix == prefix);
        if (i < 0) return false;
        _byHash.Remove(_db.Tokens[i].Hash);
        _db.Tokens.RemoveAt(i);
        return true;
    });

    /// <summary>Revoke every token a user holds (part of "sign out everywhere"). Returns the count removed.</summary>
    public int RevokeAllTokens(string userId) => Mutate(() => _db.Tokens.RemoveAll(t => t.UserId == userId && _byHash.Remove(t.Hash)));

    /// <summary>Advance a user's session epoch so existing web sessions (carrying the old epoch) are
    /// rejected on their next request. Returns the new epoch.</summary>
    public int BumpEpoch(string userId) => Mutate(() =>
    {
        int i = _db.Users.FindIndex(u => u.Id == userId);
        if (i < 0) return 0;
        _db.Users[i] = _db.Users[i] with { Epoch = _db.Users[i].Epoch + 1 };
        return _db.Users[i].Epoch;
    });

    /// <summary>Resolve a presented token to its owner + scope (or null if unknown or expired). A read
    /// (reload-if-changed), so a token revoked on another instance is seen on the next call — and so an
    /// object-fetch-heavy clone doesn't write the DB on every request. <c>LastUsedAt</c> is stamped
    /// in-memory only (best-effort) rather than persisted per request.</summary>
    public TokenAuth? ResolveToken(string secret)
    {
        string hash = Sha(secret);
        return Query(() =>
        {
            if (!_byHash.TryGetValue(hash, out TokenRecord? t)) return null;
            if (t.ExpiresAt is { } exp && DateTimeOffset.TryParse(exp, out DateTimeOffset when) && when <= DateTimeOffset.UtcNow)
                return null; // expired
            int i = _db.Tokens.IndexOf(t);
            if (i >= 0) { _db.Tokens[i] = t with { LastUsedAt = Now() }; _byHash[hash] = _db.Tokens[i]; } // best-effort, not persisted
            return new TokenAuth(t.UserId, Scope(t.Scope));
        });
    }

    private static string Scope(string? s) => s == "read" ? "read" : "write"; // anything but explicit read ⇒ write (back-compat)

    // ---- repo ownership / visibility ----

    public HubRepoMeta? GetRepo(string name) => Query(() => _db.Repos.FirstOrDefault(r => r.Name == name));

    /// <summary>Record ownership the first time a repo is seen (push auto-create); a no-op if it
    /// already has an owner. Returns the effective meta.</summary>
    public HubRepoMeta EnsureRepo(string name, string ownerId, bool isPrivate) => Mutate(() =>
    {
        if (_db.Repos.FirstOrDefault(r => r.Name == name) is { } existing) return existing;
        var meta = new HubRepoMeta(name, ownerId, isPrivate, Now());
        _db.Repos.Add(meta);
        return meta;
    });

    public void SetPrivate(string name, bool isPrivate) => Mutate(() =>
    {
        int i = _db.Repos.FindIndex(r => r.Name == name);
        if (i >= 0) _db.Repos[i] = _db.Repos[i] with { Private = isPrivate };
    });

    /// <summary>Set a repo's web-edited About description + README (either may be null to clear). Caller
    /// validates/caps the values; the store trusts them.</summary>
    public void SetRepoAbout(string name, string? description, string? readme) => Mutate(() =>
    {
        int i = _db.Repos.FindIndex(r => r.Name == name);
        if (i >= 0) _db.Repos[i] = _db.Repos[i] with { Description = description, Readme = readme };
    });

    // ---- collaborators ----

    /// <summary>A user's effective role on a repo: <c>owner</c>, <c>admin</c>, <c>maintain</c>, <c>write</c>,
    /// <c>read</c>, or null (no access). The owner outranks everything; otherwise the strongest of any direct
    /// collaborator grant and any team grant the user inherits through membership.</summary>
    public string? RoleOf(string repo, string? userId)
    {
        if (userId is null) return null;
        return Query(() =>
        {
            if (_db.Repos.FirstOrDefault(r => r.Name == repo) is { } m && m.OwnerId == userId) return "owner";

            int best = Rank(_db.Collabs.FirstOrDefault(c => c.Repo == repo && c.UserId == userId)?.Role);
            foreach (TeamGrant g in _db.TeamGrants.Where(g => g.Repo == repo))
                if (_db.Teams.FirstOrDefault(t => t.Name == g.TeamName) is { } t && t.Members.Contains(userId))
                    best = Math.Max(best, Rank(g.Role));

            return best switch { 4 => "admin", 3 => "maintain", 2 => "write", 1 => "read", _ => (string?)null };
        });
    }

    public static bool IsRole(string role) => role is "read" or "write" or "maintain" or "admin";
    private static int Rank(string? role) => role switch { "admin" => 4, "maintain" => 3, "write" => 2, "read" => 1, _ => 0 };

    public IReadOnlyList<Collab> CollabsOf(string repo) => Query(() => (IReadOnlyList<Collab>)_db.Collabs.Where(c => c.Repo == repo).ToList());

    /// <summary>Repos where the user is a collaborator (not those they own).</summary>
    public IReadOnlyList<Collab> CollabsForUser(string userId) => Query(() => (IReadOnlyList<Collab>)_db.Collabs.Where(c => c.UserId == userId).ToList());

    public void SetCollab(string repo, string userId, string role)
    {
        if (!IsRole(role)) return;
        Mutate(() =>
        {
            int i = _db.Collabs.FindIndex(c => c.Repo == repo && c.UserId == userId);
            if (i >= 0) _db.Collabs[i] = _db.Collabs[i] with { Role = role };
            else _db.Collabs.Add(new Collab(repo, userId, role));
        });
    }

    public void RemoveCollab(string repo, string userId) => Mutate(() => _db.Collabs.RemoveAll(c => c.Repo == repo && c.UserId == userId));

    // ---- teams ----

    public Team? GetTeam(string name) => Query(() => _db.Teams.FirstOrDefault(t => t.Name == name));

    /// <summary>Create a team owned by <paramref name="ownerId"/> (who is also its first member).
    /// No-op if the name is taken.</summary>
    public Team? CreateTeam(string name, string ownerId) => Mutate(() =>
    {
        if (_db.Teams.Any(t => t.Name == name)) return null;
        var team = new Team(name, ownerId, [ownerId], Now());
        _db.Teams.Add(team);
        return team;
    });

    /// <summary>Teams the user owns or belongs to.</summary>
    public IReadOnlyList<Team> TeamsForUser(string userId) =>
        Query(() => (IReadOnlyList<Team>)_db.Teams.Where(t => t.OwnerId == userId || t.Members.Contains(userId)).ToList());

    public void AddTeamMember(string name, string userId) => Mutate(() =>
    {
        if (_db.Teams.FirstOrDefault(t => t.Name == name) is { } t && !t.Members.Contains(userId)) t.Members.Add(userId);
    });

    public void RemoveTeamMember(string name, string userId) => Mutate(() =>
    {
        // the owner is always a member; don't strand a team with no owner-member
        if (_db.Teams.FirstOrDefault(t => t.Name == name) is { } t && userId != t.OwnerId) t.Members.Remove(userId);
    });

    public void DeleteTeam(string name) => Mutate(() =>
    {
        _db.Teams.RemoveAll(t => t.Name == name);
        _db.TeamGrants.RemoveAll(g => g.TeamName == name); // a deleted team grants nothing
    });

    // ---- team → repo grants ----

    public IReadOnlyList<TeamGrant> TeamGrantsOf(string repo) => Query(() => (IReadOnlyList<TeamGrant>)_db.TeamGrants.Where(g => g.Repo == repo).ToList());

    public void SetTeamGrant(string repo, string team, string role)
    {
        if (!IsRole(role)) return;
        Mutate(() =>
        {
            int i = _db.TeamGrants.FindIndex(g => g.Repo == repo && g.TeamName == team);
            if (i >= 0) _db.TeamGrants[i] = _db.TeamGrants[i] with { Role = role };
            else _db.TeamGrants.Add(new TeamGrant(repo, team, role));
        });
    }

    public void RemoveTeamGrant(string repo, string team) => Mutate(() => _db.TeamGrants.RemoveAll(g => g.Repo == repo && g.TeamName == team));

    // ---- store internals: reload / cross-process lock / save ----

    private T Query<T>(Func<T> body) { lock (_lock) { ReloadIfChanged(); return body(); } }

    private T Mutate<T>(Func<T> body)
    {
        lock (_lock)
        {
            using IDisposable _ = AcquireFileLock(); // serialize writes across processes
            Reload();                                 // always — see every other instance's committed write first
            T r = body();
            Save();
            return r;
        }
    }

    private void Mutate(Action body) => Mutate<object?>(() => { body(); return null; });

    /// <summary>Load (or reload) hub.json into memory, rebuilding the token index and the change stamp.</summary>
    private void Reload()
    {
        Db loaded = File.Exists(_path)
            ? JsonSerializer.Deserialize<Db>(File.ReadAllBytes(_path)) ?? new Db()
            : new Db();
        if (loaded.SchemaVersion > CurrentSchema)
            throw new HubDbSchemaException(
                $"hub.json is schema v{loaded.SchemaVersion} but this hub understands up to v{CurrentSchema}. " +
                "Back up hub.json and upgrade the hub (see the release notes for any migration).");
        _db = loaded with { SchemaVersion = CurrentSchema }; // normalize so the next Save() stamps the version
        _byHash.Clear();
        foreach (TokenRecord t in _db.Tokens) _byHash[t.Hash] = t;
        _stamp = Stamp();
    }

    private void ReloadIfChanged() { if (Stamp() != _stamp) Reload(); }

    private (long, long) Stamp()
    {
        try { var fi = new FileInfo(_path); return fi.Exists ? (fi.Length, fi.LastWriteTimeUtc.Ticks) : (0L, 0L); }
        catch { return (0L, 0L); }
    }

    /// <summary>A cross-process exclusive lock on <c>hub.json.lock</c> (FileShare.None — the OS enforces it
    /// across processes on every platform). Reentrant within this process; guarded by <see cref="_lock"/>.</summary>
    private IDisposable AcquireFileLock()
    {
        if (_flockDepth++ > 0) return new Releaser(this); // already held by this thread's outer Mutate
        string lockPath = _path + ".lock";
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        for (int attempt = 0; ; attempt++)
        {
            try { _flock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); return new Releaser(this); }
            catch (IOException) when (attempt < 400) { Thread.Sleep(25); } // wait up to ~10s for another instance
            catch (IOException e) { _flockDepth--; throw new HubDbSaveException($"timed out acquiring the account-store lock at {lockPath}", e); }
        }
    }

    private void ReleaseFileLock() { if (--_flockDepth == 0) { _flock?.Dispose(); _flock = null; } }

    private sealed class Releaser(HubDb db) : IDisposable
    {
        private bool _done;
        public void Dispose() { if (_done) return; _done = true; db.ReleaseFileLock(); }
    }

    private void Save()
    {
        string tmp = _path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        try
        {
            File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(_db, Json));
            File.Move(tmp, _path, overwrite: true); // atomic publish; a torn write never becomes the live db
            _stamp = Stamp();                         // our own write isn't a "change" to re-read next time
        }
        catch (Exception e)
        {
            // Don't leave a stray temp file (e.g. on a full disk); the live db is untouched (never torn),
            // and we surface the failure clearly instead of silently dropping the mutation.
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best-effort */ }
            throw new HubDbSaveException($"failed to persist the account database to {_path} (disk full?): {e.Message}", e);
        }
    }

    private static string Sha(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    private static string Now() => DateTimeOffset.UtcNow.ToString("o");
    private static string Trim(string s) => string.IsNullOrWhiteSpace(s) ? "token" : s.Trim()[..Math.Min(s.Trim().Length, 40)];

    private sealed record Db
    {
        public int SchemaVersion { get; init; } = CurrentSchema; // missing in a pre-versioned file → current
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

public sealed record HubUser(string Id, string Login, string Name, string Avatar, string CreatedAt, int Epoch = 0,
    string? McUuid = null, string? McUsername = null, // verified Minecraft Java identity (#37), null for other providers
    bool Suspended = false,                           // operator lockout — a non-destructive penalty (#35)
    bool AgeAck = false);                             // confirmed 13+/parental consent at the age gate (#35)
public sealed record HubRepoMeta(string Name, string OwnerId, bool Private, string CreatedAt,
    string? Description = null, string? Readme = null); // about/README, web-edited (#repo-page-parity)
public sealed record TokenInfo(string Prefix, string Label, string CreatedAt, string? LastUsedAt, string Scope = "write", string? ExpiresAt = null);
public sealed record TokenAuth(string UserId, string Scope); // resolved Bearer token: who + what it can do
public sealed record Collab(string Repo, string UserId, string Role); // Role: "read" | "write"
public sealed record Team(string Name, string OwnerId, List<string> Members, string CreatedAt);
public sealed record TeamGrant(string Repo, string TeamName, string Role); // Role: "read" | "write"

/// <summary>hub.json couldn't be persisted (e.g. disk full) — the mutation is in memory but not on disk (#32).</summary>
public sealed class HubDbSaveException(string message, Exception inner) : Exception(message, inner);

/// <summary>hub.json is a schema version this hub can't safely read — refuse to start (#32).</summary>
public sealed class HubDbSchemaException(string message) : Exception(message);
