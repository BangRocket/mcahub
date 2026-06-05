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
            var user = new HubUser(id, login, name, avatar, i >= 0 ? _db.Users[i].CreatedAt : Now());
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

    // ---- tokens ----

    /// <summary>Mint a new personal access token. The plaintext is returned <em>once</em>; only its
    /// SHA-256 is stored, so a leak of the DB can't reconstruct usable tokens.</summary>
    public string CreateToken(string userId, string label)
    {
        string secret = "mcahub_" + Base64Url(RandomNumberGenerator.GetBytes(30));
        var rec = new TokenRecord(Sha(secret), secret[..14], userId, Trim(label), Now(), null);
        lock (_lock)
        {
            _db.Tokens.Add(rec);
            _byHash[rec.Hash] = rec;
            Save();
        }
        return secret;
    }

    public IReadOnlyList<TokenInfo> ListTokens(string userId)
    {
        lock (_lock)
            return _db.Tokens.Where(t => t.UserId == userId)
                .Select(t => new TokenInfo(t.Prefix, t.Label, t.CreatedAt, t.LastUsedAt)).ToList();
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

    /// <summary>Resolve a presented token to its owning user id (or null), stamping last-used.
    /// Lookup is by hash, so it never compares the secret itself.</summary>
    public string? ResolveToken(string secret)
    {
        string hash = Sha(secret);
        lock (_lock)
        {
            if (!_byHash.TryGetValue(hash, out TokenRecord? t)) return null;
            int i = _db.Tokens.IndexOf(t);
            _db.Tokens[i] = t with { LastUsedAt = Now() };
            _byHash[hash] = _db.Tokens[i];
            Save();
            return t.UserId;
        }
    }

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

    // ---- helpers ----

    private void Save()
    {
        string tmp = _path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(_db, Json));
        File.Move(tmp, _path, overwrite: true); // atomic publish; a torn write never becomes the live db
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
    }

    private sealed record TokenRecord(string Hash, string Prefix, string UserId, string Label, string CreatedAt, string? LastUsedAt);
}

public sealed record HubUser(string Id, string Login, string Name, string Avatar, string CreatedAt);
public sealed record HubRepoMeta(string Name, string OwnerId, bool Private, string CreatedAt);
public sealed record TokenInfo(string Prefix, string Label, string CreatedAt, string? LastUsedAt);
