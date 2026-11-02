using System.Net;
using System.Text;
using OscarWatch.Core.Services;

namespace OscarWatch.Tests;

public sealed class GitHubReleaseServiceTests
{
    private const string SampleJson = """
        [{
          "tag_name": "v0.8.4-OQ4",
          "html_url": "https://github.com/sideout909/OscarWatch-OQ4/releases/tag/v0.8.4-OQ4",
          "name": "OscarWatch Tracker — Version 0.8.4",
          "published_at": "2026-06-04T14:52:00Z",
          "body": "## New features\n* Example"
        }]
        """;

    [Theory]
    [InlineData("v0.8.4-OQ4", 0, 8, 4)]
    [InlineData("0.8.4", 0, 8, 4)]
    [InlineData("v1.0.0+build", 1, 0, 0)]
    public void TryParseTag_parses_release_tags(string tag, int major, int minor, int build)
    {
        var version = ReleaseVersion.TryParseTag(tag);
        Assert.NotNull(version);
        Assert.Equal(major, version!.Major);
        Assert.Equal(minor, version.Minor);
        Assert.Equal(build, version.Build);
    }

    [Fact]
    public void IsNewer_compares_semver()
    {
        Assert.True(ReleaseVersion.IsNewer(new Version(0, 8, 4), new Version(0, 8, 3)));
        Assert.False(ReleaseVersion.IsNewer(new Version(0, 8, 3), new Version(0, 8, 3)));
    }

    [Fact]
    public async Task FetchLatestAsync_deserializes_github_response()
    {
        var handler = new StubHandler(SampleJson);
        var service = new GitHubReleaseService(new HttpClient(handler));

        var release = await service.FetchLatestAsync();

        Assert.Equal("v0.8.4-OQ4", release.TagName);
        Assert.Contains("releases/tag/v0.8.4-OQ4", release.HtmlUrl);
        Assert.Contains("0.8.4", release.Name);
        Assert.Contains("New features", release.Body);
    }

    [Fact]
    public async Task CheckForUpdateAsync_returns_available_when_behind()
    {
        var handler = new StubHandler(SampleJson);
        var service = new GitHubReleaseService(new HttpClient(handler));

        var result = await service.CheckForUpdateAsync(new Version(0, 8, 3));

        Assert.Equal(AppUpdateCheckResultKind.UpdateAvailable, result.Kind);
        Assert.Equal("v0.8.4-OQ4", result.Release?.TagName);
    }

    [Fact]
    public async Task CheckForUpdateAsync_returns_up_to_date_when_current()
    {
        var handler = new StubHandler(SampleJson);
        var service = new GitHubReleaseService(new HttpClient(handler));

        var result = await service.CheckForUpdateAsync(new Version(0, 8, 4));

        Assert.Equal(AppUpdateCheckResultKind.UpToDate, result.Kind);
    }

    [Fact]
    public async Task FetchLatestAsync_uses_cache_within_ttl()
    {
        var handler = new StubHandler(SampleJson);
        var service = new GitHubReleaseService(new HttpClient(handler));

        await service.FetchLatestAsync();
        await service.FetchLatestAsync();

        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Selects_newest_oq4_prerelease_ignoring_drafts_and_upstream()
    {
        const string json = """
            [
              {"tag_name":"v99.0.0", "html_url":"https://example.test/upstream"},
              {"tag_name":"v2.0.0-OQ4", "draft":true, "html_url":"https://example.test/draft"},
              {"tag_name":"v1.1.5-OQ4", "prerelease":true, "html_url":"https://example.test/current"},
              {"tag_name":"v1.1.6-OQ4", "prerelease":true, "html_url":"https://example.test/next"}
            ]
            """;
        var service = new GitHubReleaseService(new HttpClient(new StubHandler(json)));
        var result = await service.CheckForUpdateAsync(new Version(1, 1, 5, 0));
        Assert.Equal(AppUpdateCheckResultKind.UpdateAvailable, result.Kind);
        Assert.Equal("v1.1.6-OQ4", result.Release?.TagName);
    }

    [Fact]
    public async Task Fails_without_an_oq4_release_instead_of_offering_upstream()
    {
        var service = new GitHubReleaseService(new HttpClient(new StubHandler(
            """[{"tag_name":"v99.0.0", "html_url":"https://example.test/upstream"}]""")));
        var result = await service.CheckForUpdateAsync(new Version(1, 1, 5));
        Assert.Equal(AppUpdateCheckResultKind.CheckFailed, result.Kind);
    }

    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            Assert.Equal("https://api.github.com/repos/sideout909/OscarWatch-OQ4/releases?per_page=100", request.RequestUri?.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
}
