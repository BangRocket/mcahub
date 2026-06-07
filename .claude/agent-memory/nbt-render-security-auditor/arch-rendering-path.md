---
name: arch-rendering-path
description: Key file locations, constants, allocation order, and call graph for the untrusted-NBT-to-image path in mcahub
metadata:
  type: project
---

## Rendering Path Architecture

### File Locations (hub)
- `src/McaHub/MapRenderer.cs` — surface scan, PNG encoder, color mapping
- `src/McaHub/MapCache.cs` — per-commit PNG cache (lock + tmp+rename pattern)
- `src/McaHub/WorldCache.cs` — per-commit world materialization cache
- `src/McaHub/Pages.cs:386` — `Map()` handler for `/r/{repo}/map/{reff}.png`
- `src/McaHub/Transport.cs` — write endpoint; no size/rate limiting on push

### File Locations (core, submodule at ./mca-git per ADR-0006)
- `src/McaDiff/Diff/BlockStateDecoder.cs` — palette decode, packed long-array decode
- `src/McaDiff/Anvil/RegionFile.cs` — Anvil region parser (header + chunk payloads)
- `src/McaDiff/Anvil/ChunkCodec.cs` — decompression via InflateBounded (128 MiB cap per chunk)
- `src/McaDiff/Nbt/NbtDepthGuard.cs` — pre-parse depth scan, MaxDepth=512
- `src/McaDiff/Repo/SafeInflate.cs` — 512 MiB cap for object-store inflate
- `src/McaDiff/Repo/ObjectStore.cs` — content-addressed store; Read() has NO inflate cap (unbounded ZLib decompress)
- `src/McaDiff/Repo/Checkout.cs` — materializes manifest to disk via PathGuard.Confine
- `src/McaDiff/PathGuard.cs` — path confinement + NTFS ADS guard on Windows

### Key Constants
- `MapRenderer.MaxSideChunks = 160` — span cap (both X and Z); applied AFTER the dictionary fills
- `MapRenderer.MaxChunks = 30_000` — hard ceiling on chunks decoded per render; fills BEFORE the span cap
- `ChunkCodec.MaxNbtBytes = 128 MiB` — per-chunk decompression cap
- `SafeInflate.DefaultMax = 512 MiB` — object-store inflate cap (but ObjectStore.Read() bypasses this)
- `NbtDepthGuard.MaxDepth = 512` — NBT nesting cap
- `ObjectStore.MaxObjectBytes = 512 MiB` — cap for ImportRaw (untrusted peer objects)

### Allocation Order in MapRenderer.Render
1. `Dictionary<ChunkPos, Surface> surfaces` fills up to MaxChunks (30k) entries — BEFORE span cap
2. Each `Surface` = 3x byte[256] + int[256] = ~1.25 KB → 30k surfaces ≈ 37.5 MB (bounded)
3. minCX/maxCX/minCZ/maxCZ tracked while filling dictionary
4. Span cap applied: `if (maxCX - minCX + 1 > 160) { ... clamp ... }` — AFTER dictionary fills
5. `w = (maxCX - minCX + 1) * 16`, `h = (maxCZ - minCZ + 1) * 16` — computed from clamped bounds
6. `byte[] rgb = new byte[w * h * 3]` — max 160*16=2560, so max w*h=6,553,600; rgb = ~19.7 MB
7. `int[] height = new int[w * h]` — max ~26.2 MB
8. PNG encode: `byte[] raw = new byte[h * (1 + w * 3)]` — max ~26.2 MB; then ZLibStream + MemoryStream for comp

### Section Y Handling
- Read as `NbtByte` then cast to `sbyte`: `(sbyte)yTag.Value` — range -128..127
- Y used only to compute `secY * 16 + ly` for height shading (stored in Surface.Y, int[256])
- No clamp applied to section Y before storing in Surface.Y
- Extreme Y (e.g. -128 → block Y -2048, or 127 → block Y 2047+15=2032) goes into height[] without bound
- NorthShade uses height[] differences only; extreme values don't index any array

### Cache Key Structure
- WorldCache: `cache/<repoName>/<commitHash>` — commit hash is SHA-256 hex validated by IsValidHash
- MapCache: `maps/<repoName>/<commitHash>.png` — same validation path
- RepoName validated by `RepoStore.IsValidName` regex `^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$`
- Immutability: commit is content-addressed; once a dir/PNG exists and is non-empty, it's returned as-is

### Concurrency Model
- MapCache._lock is a single process-wide lock — ALL renders serialized through one lock
- No per-repo locks; two repos rendering simultaneously block each other
- No timeout on render; a slow/hostile render holds the lock indefinitely

### ObjectStore.Read() Gap
- ObjectStore.Read() decompresses loose objects with `ZLibStream` and `CopyTo` with NO size cap
- Objects stored by WriteText/Write are already SHA-256 verified, but on READ there is no re-inflate bound
- An object written with a valid hash but crafted content could expand without bound on Read (theoretical; content is hash-locked after import)

**Why:** First full audit pass, 2026-06-05.
**How to apply:** Use these paths and constants as ground truth in future audits without re-reading all files.
