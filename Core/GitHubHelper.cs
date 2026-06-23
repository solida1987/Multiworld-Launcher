using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LauncherV2.Core;

// ═══════════════════════════════════════════════════════════════════════════════
// GitHubHelper — shared release-resolution logic for all game plugins.
//
// Two access tiers:
//   • FetchLatestTagAsync    — HEAD redirect trick. CDN-served, never counted
//     against the 60 req/hr unauthenticated REST API quota. Use this for
//     CheckForUpdateAsync so startup update checks never hit the rate limit.
//
//   • FetchLatestReleaseAsync — REST API GET (needs the full assets list to
//     find an asset whose filename cannot be predicted from the tag alone).
//     Use only in InstallOrUpdateAsync (rare user-triggered action). Cached
//     per-session by the caller to avoid repeated calls.
//
// RATE LIMIT STRATEGY
// ──────────────────
// Startup update check (all library plugins in parallel) must use ONLY the
// CDN HEAD redirect. The REST API fallback is acceptable for individual installs
// (user-triggered, one game at a time) since the 60 req/hr limit resets hourly.
// ═══════════════════════════════════════════════════════════════════════════════

public static class GitHubHelper
{
    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(20),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── CDN HEAD redirect trick ───────────────────────────────────────────────

    /// Resolve the latest release tag for a GitHub repo using the CDN redirect.
    /// GitHub's /releases/latest page returns HTTP 302 → /releases/tag/<tag>.
    /// This is served by the CDN and is NOT counted against the 60 req/hr quota.
    /// Returns null on any network failure (non-throwing by contract).
    public static async Task<string?> FetchLatestTagAsync(
        string owner, string repo, CancellationToken ct = default)
    {
        try
        {
            string latestUrl = $"https://github.com/{owner}/{repo}/releases/latest";
            using var req = new HttpRequestMessage(HttpMethod.Head, latestUrl);
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var client  = new HttpClient(handler, disposeHandler: true)
            {
                Timeout = TimeSpan.FromSeconds(15),
                DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
            };

            using var resp = await client.SendAsync(req, ct);
            string? location = resp.Headers.Location?.ToString();
            if (string.IsNullOrEmpty(location)) return null;

            const string marker = "/releases/tag/";
            int idx = location.IndexOf(marker, StringComparison.Ordinal);
            return idx < 0 ? null : location[(idx + marker.Length)..].TrimEnd('/');
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    /// Build a direct CDN download URL for a known asset filename + tag.
    /// No API quota consumed — the file is served directly from GitHub releases CDN.
    public static string AssetUrl(string owner, string repo, string tag, string filename)
        => $"https://github.com/{owner}/{repo}/releases/download/{tag}/{filename}";

    // ── REST API (rate-limited, 60 req/hr unauthenticated) ───────────────────

    /// Fetch the latest release from the GitHub REST API.
    /// Returns a lightweight summary of tag + asset list.
    /// Throws on network error (caller decides whether to fall back to CDN).
    public static async Task<GitHubRelease?> FetchLatestReleaseAsync(
        string owner, string repo, CancellationToken ct = default)
    {
        string url = $"https://api.github.com/repos/{owner}/{repo}/releases/latest";
        string json = await _http.GetStringAsync(url, ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string? tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        if (tag == null) return null;

        var assets = new System.Collections.Generic.List<GitHubAsset>();
        if (root.TryGetProperty("assets", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in arr.EnumerateArray())
            {
                string? name = a.TryGetProperty("name",                  out var n) ? n.GetString() : null;
                string? durl = a.TryGetProperty("browser_download_url",  out var u) ? u.GetString() : null;
                long    size = a.TryGetProperty("size",                  out var s) ? s.GetInt64()  : 0;
                if (name != null && durl != null)
                    assets.Add(new GitHubAsset(name, durl, size));
            }
        }

        return new GitHubRelease(tag, assets);
    }

    // ── Tag normalization ─────────────────────────────────────────────────────

    /// "v2.0.1" → "2.0.1". Leading 'v' before a digit is stripped.
    public static string NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return "";
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1]) ? tag[1..] : tag;
    }
}

public sealed record GitHubRelease(string Tag, System.Collections.Generic.List<GitHubAsset> Assets);
public sealed record GitHubAsset(string Name, string DownloadUrl, long Size);
