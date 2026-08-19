using System.Net;
using System.Text.Json;
using MaxHub.Server.Services;

namespace MaxHub.Server.Tests;

/// <summary>GitHub Releases 自动同步 Agent 版本：解析、缓存与容错。</summary>
public class GitHubReleaseServiceTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json"),
    };

    private static object ReleaseJson(string tag, params object[] assets) => new { tag_name = tag, assets };

    private static object ExeAsset(string version, string? digest = null) => new
    {
        name = $"MaxHubAgent-{version}-win-x64.exe",
        browser_download_url = $"https://github.com/o/r/releases/download/v{version}/MaxHubAgent-{version}-win-x64.exe",
        digest,
    };

    private static (GitHubReleaseService Service, StubHandler Handler) ServiceFor(
        Func<HttpRequestMessage, HttpResponseMessage> respond, TimeSpan? ttl = null)
    {
        var handler = new StubHandler(respond);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://github-stub") };
        return (new GitHubReleaseService(http, "owner/repo", ttl), handler);
    }

    [Fact]
    public async Task Parses_version_url_and_sha256_digest()
    {
        var (service, _) = ServiceFor(req =>
        {
            Assert.Equal("/repos/owner/repo/releases/latest", req.RequestUri!.PathAndQuery);
            return Json(ReleaseJson("v1.0.3", ExeAsset("1.0.3", "sha256:abc123def")));
        });

        var release = await service.GetLatestAsync();

        Assert.NotNull(release);
        Assert.Equal("1.0.3", release!.Version);
        Assert.EndsWith("MaxHubAgent-1.0.3-win-x64.exe", release.DownloadUrl);
        Assert.Equal("abc123def", release.Sha256);
    }

    [Fact]
    public async Task Sha256_empty_when_digest_missing()
    {
        var (service, _) = ServiceFor(_ => Json(ReleaseJson("v2.0.0", ExeAsset("2.0.0"))));
        var release = await service.GetLatestAsync();
        Assert.NotNull(release);
        Assert.Equal("", release!.Sha256);
    }

    [Fact]
    public async Task Null_when_release_has_no_exe_asset()
    {
        var (service, _) = ServiceFor(_ => Json(ReleaseJson("v1.0.0",
            new { name = "readme.txt", browser_download_url = "https://x/readme.txt", digest = (string?)null })));
        Assert.Null(await service.GetLatestAsync());
    }

    [Fact]
    public async Task Caches_result_within_ttl()
    {
        var (service, handler) = ServiceFor(_ => Json(ReleaseJson("v1.0.3", ExeAsset("1.0.3", "sha256:x"))),
            ttl: TimeSpan.FromMinutes(10));

        await service.GetLatestAsync();
        await service.GetLatestAsync();
        await service.GetLatestAsync();

        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Returns_stale_cache_when_github_fails()
    {
        var fail = false;
        var (service, _) = ServiceFor(_ => fail
            ? throw new HttpRequestException("down")
            : Json(ReleaseJson("v1.0.3", ExeAsset("1.0.3", "sha256:x"))),
            ttl: TimeSpan.Zero); // TTL 0：每次都重新拉取

        var first = await service.GetLatestAsync();
        fail = true;
        var second = await service.GetLatestAsync();

        Assert.NotNull(first);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task Null_when_github_unreachable_and_no_cache()
    {
        var (service, _) = ServiceFor(_ => throw new HttpRequestException("down"));
        Assert.Null(await service.GetLatestAsync());
    }

    [Fact]
    public async Task Falls_back_to_redirect_probe_when_api_unreachable()
    {
        var (service, _) = ServiceFor(req =>
        {
            if (req.RequestUri!.Host == "api.github.com")
                throw new HttpRequestException("api blocked");
            var redirect = new HttpResponseMessage(HttpStatusCode.Found);
            redirect.Headers.Location = new Uri("https://github.com/owner/repo/releases/tag/v1.0.3");
            return redirect;
        });

        var release = await service.GetLatestAsync();

        Assert.NotNull(release);
        Assert.Equal("1.0.3", release!.Version);
        Assert.Equal("https://github.com/owner/repo/releases/download/v1.0.3/MaxHubAgent-1.0.3-win-x64.exe", release.DownloadUrl);
        Assert.Equal("", release.Sha256);
    }

    [Fact]
    public async Task Redirect_probe_rejects_non_version_location()
    {
        var (service, _) = ServiceFor(req =>
        {
            if (req.RequestUri!.Host == "api.github.com")
                throw new HttpRequestException("api blocked");
            var redirect = new HttpResponseMessage(HttpStatusCode.Found);
            redirect.Headers.Location = new Uri("https://github.com/login");
            return redirect;
        });

        Assert.Null(await service.GetLatestAsync());
    }
}
