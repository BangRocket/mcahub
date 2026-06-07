using System.Text;
using System.Text.Json;
using McaDiff.Diff;
using McaDiff.Query;
using McaDiff.Repo;

namespace McaHub;

/// <summary>
/// Posts a grief summary to a Discord webhook when a backup is pushed (#25): "📦 myworld — backup abcd —
/// 847 destroyed · 12 placed". Operator-configured (<c>MCAHUB_DISCORD_WEBHOOK</c>), and the URL is
/// validated to a real <c>discord.com</c> webhook before any request — so a misconfiguration can't turn
/// the push path into an SSRF gadget.
/// </summary>
public static class DiscordWebhook
{
    public static bool IsValidWebhookUrl(string? url) =>
        url is { Length: > 0 } &&
        (url.StartsWith("https://discord.com/api/webhooks/", StringComparison.OrdinalIgnoreCase) ||
         url.StartsWith("https://discordapp.com/api/webhooks/", StringComparison.OrdinalIgnoreCase));

    /// <summary>The Discord embed payload for a push — pure, so it's unit-tested without a network call.</summary>
    public static string BuildPayload(string repoName, string repoUrl, GriefSummary? grief, string newShort, string actor)
    {
        bool griefed = grief is { } g0 && g0.Destroyed + g0.Built + g0.Replaced > 0;
        string desc = griefed
            ? $"**{grief!.Destroyed:N0}** destroyed · {grief.Built:N0} placed · {grief.Replaced:N0} replaced"
            : "New backup pushed.";
        var payload = new
        {
            embeds = new[]
            {
                new
                {
                    title = $"📦 {repoName} — backup {newShort}",
                    description = desc,
                    url = repoUrl,
                    color = grief is { Destroyed: > 0 } ? 0xE0413A : 0x2ECC71, // red if anything was destroyed, else green
                    fields = new[] { new { name = "by", value = actor, inline = true } },
                },
            },
        };
        return JsonSerializer.Serialize(payload);
    }

    /// <summary>Compute the grief for old→new and post it. Never throws — a Discord blip or a slow endpoint
    /// must not fail or hang the push beyond the client's timeout.</summary>
    public static async Task NotifyPushAsync(string? url, Repository repo, string repoName, string repoUrl,
        string? oldHash, string newHash, string actor, HttpClient http, CancellationToken ct)
    {
        if (!IsValidWebhookUrl(url)) return;
        GriefSummary? grief = null;
        try
        {
            if (oldHash is { Length: > 0 }) // a first push has nothing to diff against
            {
                Manifest mA = repo.ReadManifest(repo.ReadCommit(oldHash).Tree);
                Manifest mB = repo.ReadManifest(repo.ReadCommit(newHash).Tree);
                grief = GriefReport.Analyze(RepoDiffer.Diff(
                    oldHash[..10], mA, new RepoDiffer.CommitSource(repo, mA),
                    newHash[..10], mB, new RepoDiffer.CommitSource(repo, mB), new DiffRunOptions(ExpandArrays: false)));
            }
        }
        catch { /* couldn't diff → still send a plain "new backup" notification */ }

        string shortNew = newHash.Length >= 10 ? newHash[..10] : newHash;
        try
        {
            using var content = new StringContent(BuildPayload(repoName, repoUrl, grief, shortNew, actor), Encoding.UTF8, "application/json");
            using HttpResponseMessage _ = await http.PostAsync(url, content, ct);
        }
        catch { /* swallow — the backup is already saved; the alert is best-effort */ }
    }
}
