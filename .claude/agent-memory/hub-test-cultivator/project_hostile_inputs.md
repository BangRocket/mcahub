---
name: project-hostile-inputs
description: Hostile input corpus for traversal, malformed chunks, CSRF edges, and renderer adversarial data
metadata:
  type: project
---

## Repo name traversal corpus (RepoStore.IsValidName)
Valid names (must accept): "world", "my-world", "w.orld", "w_orld", "a", "A0", "a" + "b"*63
Invalid names (must reject — all return false):
- ".." — double-dot traversal
- "." — single dot
- "../secret" — path separator
- "a/b" — forward slash
- "a\\b" — backslash (Windows path separator)
- "/etc/passwd" — absolute path (leading slash)
- "a\0b" — null byte
- "" — empty
- " " — space
- "a b" — space in middle
- "a:b" — colon (Windows drive separator)
- "a*b" — glob char
- "-bad" — leading hyphen (regex requires alnum start)
- ".bad" — leading dot (regex requires alnum start)
- "a" + "b"*64 — 65 chars, too long (max is 1+63=64 total)
- "ａ" (U+FF41 fullwidth a) — unicode lookalike
- "%2e%2e" — URL-encoded dot (must check the raw value, not URL-decoded)

## Malformed chunk shapes for MapRenderer
- Section with Y = -128 (sbyte min; 1.18 Bedrock bottom; negative Y after cast)
- Section with Y = 127 (sbyte max; above sky)
- Empty palette / block_states with 0-length palette list
- block_states with palette but no data array (single-entry palette → valid in vanilla, cells all index 0)
- block_states with data array longer than 4096 entries
- block_states with bits-per-entry = 0 (division by zero risk)
- Chunk NBT with 0 sections (should return null from SurfaceOf, not throw)
- 30_001 chunks in a single region (exceeds MaxChunks cap — renderer must truncate, not OOM)
- World with span > 160 chunks in X or Z (truncation path)
- Chunk at extreme coordinates (X or Z = int.MaxValue / 16)
- Surface with all-air columns (Y stays int.MinValue throughout)

## CSRF edge cases
- POST with no CSRF token at all → 400
- POST with correct cookie but wrong form field value → 400
- POST with CSRF token from a different session → 400
- POST with valid CSRF token → success (happy path)
- GET /auth/logout — no CSRF (intentional; low-impact forgery by design)
- POST /auth/dev when DevLogin is disabled → route doesn't exist (404 or redirect), never authenticated

## Open-redirect corpus (Auth.Local)
Must redirect to "/" (default) for:
- "https://evil.com"
- "//evil.com" (protocol-relative)
- "http://evil.com/path"
Must use the provided path for:
- "/account"
- "/r/world"
- "/" (root)

## Token edge cases
- Token with correct "mcahub_" prefix but wrong hash → null userId, badToken=true
- Master token exact match → admin=true, userId=null
- Master token with extra whitespace → should NOT match (FixedTimeEquals compares bytes exactly)
- Revoked token → null after revocation
- Token presented in Bearer header with no space padding (e.g. "Bearer\ttokenvalue") — see Auth.Bearer stripping

**Why:** These are the inputs that most commonly reveal boundary violations. Capturing them here means they can be turned into test cases without re-deriving the corpus.
**How to apply:** When writing traversal or renderer tests, use this list as the arrange-phase input table.
