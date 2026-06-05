namespace McadiffHub;

/// <summary>
/// Makes accidental secret logging structurally hard (#42): a config value whose key looks like a secret
/// (<c>*SECRET*</c> / <c>*TOKEN*</c> / <c>*KEY*</c>, case-insensitive) is masked rather than echoed. Use
/// <see cref="Redact"/> anywhere configuration/state is logged.
/// </summary>
public static class Secrets
{
    private static readonly string[] Markers = ["SECRET", "TOKEN", "KEY", "PASSWORD"];

    public static bool IsSecretKey(string key) =>
        Markers.Any(m => key.Contains(m, StringComparison.OrdinalIgnoreCase));

    /// <summary>The value to log for <paramref name="key"/>: masked if the key names a secret.</summary>
    public static string Redact(string key, string? value) =>
        string.IsNullOrEmpty(value) ? "" : IsSecretKey(key) ? "***" : value;
}
