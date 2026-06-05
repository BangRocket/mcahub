namespace McadiffHub;

/// <summary>
/// Disk-cache bounds shared by the world and map caches: a global byte ceiling and a per-repo count
/// cap for each, plus the cap on how many filesystem entries a single world manifest may materialize
/// (a guard against inode/dir-count exhaustion from a hostile push).
/// </summary>
public sealed record CacheLimits(
    long WorldBytes, int WorldsPerRepo,
    long MapBytes, int MapsPerRepo,
    int ManifestEntries);
