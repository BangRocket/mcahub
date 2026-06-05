---
name: bounds-confirmed
description: Controls verified safe in the untrusted-NBT-to-image path, with the protecting bound named
metadata:
  type: project
---

## Confirmed Bounds and Protections (as of 2026-06-05 audit)

### Decompression Bomb
- ChunkCodec.InflateBounded caps each chunk at 128 MiB (MaxNbtBytes). Throws InvalidDataException, caught by SurfaceOf's outer try/catch.
- SafeInflate.ReadBounded caps object-store inflates at 512 MiB. Used by ImportRaw (untrusted peer path).
- NbtDepthGuard.Check (MaxDepth=512) runs before fNbt recursive parse; prevents stack overflow.

### BlockStateDecoder Bounds
- palette.Count drives `keys[]` allocation — bounded by actual fNbt-parsed palette list length (already post-decomp cap)
- `bits` computed from palette.Count by `while ((1 << bits) < palette.Count) bits++` — correct ceil(log2)
- `bpe = max(bits, minBits)` — floor enforced, prevents zero bpe and thus zero-division on perLong
- `perLong = 64 / bpe` — with bpe >= 1, perLong is 1..64
- Guard: `if (perLong <= 0 || data.Length < (cellCount + perLong - 1) / perLong) return null` — verifies data array is long enough for cellCount entries at the computed packing
- Out-of-range palette index produces "?out-of-range" string, not an exception or array overrun
- cellCount is caller-supplied constant 4096 (block_states) — not from stream data

### Span Cap Math (image dimensions)
- w = (maxCX - minCX + 1) * 16, max = 160 * 16 = 2560
- h = (maxCZ - minCZ + 1) * 16, max = 2560
- rgb: 2560 * 2560 * 3 = 19,660,800 bytes (~18.8 MiB) — no overflow (int can hold 2560*2560*3)
- height: 2560 * 2560 * 4 = 26,214,400 bytes (~25 MiB) — no overflow

### Path Traversal
- PathGuard.Confine: canonicalizes + prefix-checks every manifest path; throws on escape
- NTFS ADS guard: rejects ':' in path on Windows
- RepoStore.IsValidName: `^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$` — no separators, no traversal

### Cache Immutability
- WorldCache key: cacheDir/<repoName>/<commitHash> — commit hash validated by ObjectStore.IsValidHash (64 lowercase hex chars); repo name validated by IsValidName
- MapCache key: mapDir/<repoName>/<commitHash>.png — same validation
- Ready() check: dir exists AND has entries — prevents serving an empty half-written dir
- tmp+rename publish pattern prevents serving partial output

### Chunk Count Cap
- MaxChunks = 30,000; `if (surfaces.Count >= MaxChunks) break` inside the region enumeration loop
- Each Surface = 3x byte[256] + int[256] ≈ 1.28 KB; 30k * 1.28 KB ≈ 38 MB — bounded

### Integer Overflow Check on rgb/height
- w*h maximum: 2560*2560 = 6,553,600 — well within int range (~2.1B)
- w*h*3 maximum: 19,660,800 — well within int range
- No overflow possible at the 160-chunk cap

### SurfaceOf Error Isolation
- All exceptions from ChunkCodec.Decode (decompression, depth guard, parse) caught by `try { } catch { return null; }`
- Malformed/unsupported chunks produce null Surface; outer loop skips them with `continue`
- A single bad chunk cannot 500 the whole map render
