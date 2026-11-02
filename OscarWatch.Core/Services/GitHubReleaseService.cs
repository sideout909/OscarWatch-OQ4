using System.Net.Http.Headers;
using System.Text.Json;
using OscarWatch.Core.Net;
using System.Text.Json.Serialization;
using OscarWatch.Core.Models;

namespace OscarWatch.Core.Services;

public sealed class GitHubReleaseService : IGitHubReleaseService
{
    public const string LatestReleaseApiUrl =
        "https://api.github.com/repos/sideout909/OscarWatch-OQ4/releases?per_page=100";

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private readonly HttpClient _httpClient;
    private readonly object _cacheLock = new();
    private GitHubLatestRelease? _cachedRelease;
    private DateTime _cachedAtUtc;

    public GitHubReleaseService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? CreateDefaultClient();
    }

    public async Task<GitHubLatestRelease> FetchLatestAsync(CancellationToken cancellationToken = default)
    {
        lock (_cacheLock)
        {
            if (_cachedRelease is not null && DateTime.UtcNow - _cachedAtUtc < CacheTtl)
                return _cachedRelease;
        }

        var release = await FetchFromApiAsync(cancellationToken).ConfigureAwait(false);

        lock (_cacheLock)
        {
            _cachedRelease = release;
            _cachedAtUtc = DateTime.UtcNow;
        }

        return release;
    }

    public async Task<AppUpdateCheckResult> CheckForUpdateAsync(
        Version currentVersion,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var release = await FetchLatestAsync(cancellationToken).ConfigureAwait(false);
            var latestVersion = ReleaseVersion.TryParseTag(release.TagName);
            if (latestVersion is null)
                return AppUpdateCheckResult.Failed(new InvalidOperationException($"Unparseable release tag: {release.TagName}"));

            if (ReleaseVersion.IsNewer(latestVersion, currentVersion))
                return AppUpdateCheckResult.Available(release);

            return AppUpdateCheckResult.UpToDate();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return AppUpdateCheckResult.Failed(ex);
        }
    }

    private async Task<GitHubLatestRelease> FetchFromApiAsync(CancellationToken cancellationToken)
    {
        using var response = await _httpClient
            .GetAsync(LatestReleaseApiUrl, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);

        var releases = await JsonSerializer
            .DeserializeAsync<GitHubReleaseDto[]>(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        // Include this fork's prereleases; /latest excludes them.
        var dto = releases?
            .Where(r => !r.Draft && r.TagName is not null
                && r.TagName.EndsWith("-OQ4", StringComparison.OrdinalIgnoreCase)
                && ReleaseVersion.TryParseTag(r.TagName) is not null)
            .OrderByDescending(r => ReleaseVersion.TryParseTag(r.TagName))
            .FirstOrDefault();

        if (dto is null || string.IsNullOrWhiteSpace(dto.TagName) || string.IsNullOrWhiteSpace(dto.HtmlUrl))
            throw new InvalidOperationException("GitHub release response was missing required fields.");

        return new GitHubLatestRelease
        {
            TagName = dto.TagName,
            HtmlUrl = dto.HtmlUrl,
            Name = dto.Name ?? "",
            PublishedAt = dto.PublishedAt,
            Body = dto.Body ?? ""
        };
    }

    private static HttpClient CreateDefaultClient()
    {
        var client = OscarWatchHttpClients.Create(TimeSpan.FromSeconds(15));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private sealed class GitHubReleaseDto
    {
        [JsonPropertyName("draft")]
        public bool Draft { get; set; }

        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("published_at")]
        public DateTimeOffset? PublishedAt { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }
    }

}
