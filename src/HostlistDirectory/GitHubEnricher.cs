using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HostlistDirectory;

/// <summary>
/// For lists hosted on GitHub (directly, via raw.githubusercontent.com, jsDelivr /gh/ or *.github.io),
/// looks up the repository to fill in license/description gaps and flag archived projects.
/// Uses GITHUB_TOKEN when present (it is in GitHub Actions), otherwise the 60 req/h anonymous limit.
/// </summary>
public sealed partial class GitHubEnricher(Downloader downloader, bool enabled)
{
    private readonly ConcurrentDictionary<string, Task<RepositoryInfo?>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
    private volatile bool _rateLimited;

    public static (string Owner, string Repo)? ParseRepo(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        foreach (var rx in new[] { GitHubRegex(), RawRegex(), JsDelivrRegex() })
        {
            var m = rx.Match(url);
            if (m.Success) return (m.Groups["o"].Value, m.Groups["r"].Value.Split('@')[0]);
        }
        var io = PagesRegex().Match(url);
        if (io.Success) return (io.Groups["o"].Value, io.Groups["r"].Success && io.Groups["r"].Value.Length > 0 ? io.Groups["r"].Value : $"{io.Groups["o"].Value}.github.io");
        return null;
    }

    public Task<RepositoryInfo?> LookupAsync(string? homepage, string? mainMirror, CancellationToken ct)
    {
        if (!enabled) return Task.FromResult<RepositoryInfo?>(null);
        var repo = ParseRepo(homepage) ?? ParseRepo(mainMirror);
        if (repo is null) return Task.FromResult<RepositoryInfo?>(null);
        var key = $"{repo.Value.Owner}/{repo.Value.Repo}";
        return _cache.GetOrAdd(key, k => FetchAsync(k, ct));
    }

    private async Task<RepositoryInfo?> FetchAsync(string ownerRepo, CancellationToken ct)
    {
        if (_rateLimited) return null;
        var headers = new List<(string, string)>
        {
            ("Accept", "application/vnd.github+json"),
            ("X-GitHub-Api-Version", "2022-11-28"),
        };
        if (!string.IsNullOrEmpty(_token)) headers.Add(("Authorization", $"Bearer {_token}"));

        try
        {
            var body = await downloader.GetStringAsync($"https://api.github.com/repos/{ownerRepo}", headers, ct);
            if (body is null) return null;
            using var doc = JsonDocument.Parse(body);
            var r = doc.RootElement;
            if (r.TryGetProperty("message", out var msg) && msg.GetString()?.Contains("rate limit", StringComparison.OrdinalIgnoreCase) == true)
            {
                _rateLimited = true;
                return null;
            }

            string? license = null;
            if (r.TryGetProperty("license", out var lic) && lic.ValueKind == JsonValueKind.Object)
            {
                var spdx = lic.TryGetProperty("spdx_id", out var s) ? s.GetString() : null;
                license = spdx is null or "NOASSERTION" ? (lic.TryGetProperty("name", out var n) ? n.GetString() : null) : spdx;
                if (license is "Other") license = null;
            }

            return new RepositoryInfo
            {
                Url = r.TryGetProperty("html_url", out var u) ? u.GetString() : $"https://github.com/{ownerRepo}",
                Description = r.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null,
                License = license,
                Stars = r.TryGetProperty("stargazers_count", out var st) && st.TryGetInt32(out var stars) ? stars : null,
                Archived = r.TryGetProperty("archived", out var a) && a.ValueKind is JsonValueKind.True or JsonValueKind.False ? a.GetBoolean() : null,
                PushedAt = r.TryGetProperty("pushed_at", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null,
            };
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    [GeneratedRegex(@"^https?://(?:www\.)?github\.com/(?<o>[\w.\-]+)/(?<r>[\w.\-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex GitHubRegex();

    [GeneratedRegex(@"^https?://raw\.githubusercontent\.com/(?<o>[\w.\-]+)/(?<r>[\w.\-]+)/", RegexOptions.IgnoreCase)]
    private static partial Regex RawRegex();

    [GeneratedRegex(@"^https?://(?:cdn|fastly|gcore|testingcf)\.jsdelivr\.net/gh/(?<o>[\w.\-]+)/(?<r>[\w.\-@]+)", RegexOptions.IgnoreCase)]
    private static partial Regex JsDelivrRegex();

    [GeneratedRegex(@"^https?://(?<o>[\w\-]+)\.github\.io/?(?<r>[\w.\-]+)?", RegexOptions.IgnoreCase)]
    private static partial Regex PagesRegex();
}
