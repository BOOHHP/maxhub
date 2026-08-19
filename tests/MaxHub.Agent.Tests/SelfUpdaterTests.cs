using System.Net;
using System.Text.Json;
using MaxHub.Agent.Core.Remote;

namespace MaxHub.Agent.Tests;

/// <summary>SelfUpdater：版本比较与下载校验逻辑。</summary>
public class SelfUpdaterTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private static HubClient HubFor(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var http = new HttpClient(new StubHandler(respond)) { BaseAddress = new Uri("http://server") };
        return new HubClient(http);
    }

    /// <summary>GitHub 不可达的 stub：强制走服务器回退路径，保持旧用例语义。</summary>
    private static HttpClient GitHubDown() =>
        new(new StubHandler(_ => throw new HttpRequestException("github down")));

    private static HttpClient GitHubFor(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new StubHandler(respond));

    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json"),
    };

    [Fact]
    public async Task No_update_when_versions_equal()
    {
        var hub = HubFor(_ => Json(new { version = "1.0.1", downloadUrl = "https://x/agent.exe", sha256 = "abc" }));
        var updater = new SelfUpdater(hub, GitHubDown()) { CurrentVersion = "1.0.1" };
        Assert.Null(await updater.CheckForUpdateAsync());
    }

    [Fact]
    public async Task Update_available_when_server_newer()
    {
        var hub = HubFor(_ => Json(new { version = "1.1.0", downloadUrl = "https://x/agent.exe", sha256 = "abc" }));
        var updater = new SelfUpdater(hub, GitHubDown()) { CurrentVersion = "1.0.1" };
        var release = await updater.CheckForUpdateAsync();
        Assert.NotNull(release);
        Assert.Equal("1.1.0", release!.Version);
    }

    [Fact]
    public async Task No_update_when_server_older_or_missing()
    {
        var hub = HubFor(_ => Json(new { version = "1.0.0", downloadUrl = "https://x/agent.exe", sha256 = "abc" }));
        var updater = new SelfUpdater(hub, GitHubDown()) { CurrentVersion = "1.0.1" };
        Assert.Null(await updater.CheckForUpdateAsync());
    }

    [Fact]
    public async Task Null_when_server_returns_404()
    {
        var hub = HubFor(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var updater = new SelfUpdater(hub, GitHubDown()) { CurrentVersion = "1.0.1" };
        Assert.Null(await updater.CheckForUpdateAsync());
    }

    [Fact]
    public async Task Null_when_download_url_empty()
    {
        var hub = HubFor(_ => Json(new { version = "9.9.9", downloadUrl = "", sha256 = "" }));
        var updater = new SelfUpdater(hub, GitHubDown()) { CurrentVersion = "1.0.1" };
        Assert.Null(await updater.CheckForUpdateAsync());
    }

    [Fact]
    public async Task Null_when_server_unreachable()
    {
        var hub = HubFor(_ => throw new HttpRequestException("unreachable"));
        var updater = new SelfUpdater(hub, GitHubDown()) { CurrentVersion = "1.0.1" };
        Assert.Null(await updater.CheckForUpdateAsync());
    }

    [Fact]
    public async Task GitHub_release_takes_priority_over_server()
    {
        var hub = HubFor(_ => Json(new { version = "1.0.2", downloadUrl = "https://server/old.exe", sha256 = "old" }));
        var github = GitHubFor(_ => Json(new
        {
            tag_name = "v2.0.0",
            assets = new[] { new {
                name = "MaxHubAgent-2.0.0-win-x64.exe",
                browser_download_url = "https://github.com/o/r/releases/download/v2.0.0/MaxHubAgent-2.0.0-win-x64.exe",
                digest = "sha256:feedbeef",
            } },
        }));
        var updater = new SelfUpdater(hub, github) { CurrentVersion = "1.0.1" };

        var release = await updater.CheckForUpdateAsync();

        Assert.NotNull(release);
        Assert.Equal("2.0.0", release!.Version);
        Assert.Equal("feedbeef", release.Sha256);
        Assert.EndsWith("MaxHubAgent-2.0.0-win-x64.exe", release.DownloadUrl);
    }

    [Fact]
    public async Task Falls_back_to_server_when_github_has_no_exe_asset()
    {
        var hub = HubFor(_ => Json(new { version = "1.5.0", downloadUrl = "https://server/agent.exe", sha256 = "srv" }));
        var github = GitHubFor(_ => Json(new { tag_name = "v9.9.9", assets = Array.Empty<object>() }));
        var updater = new SelfUpdater(hub, github) { CurrentVersion = "1.0.1" };

        var release = await updater.CheckForUpdateAsync();

        Assert.NotNull(release);
        Assert.Equal("1.5.0", release!.Version);
    }

    [Fact]
    public async Task Download_rejects_sha256_mismatch()
    {
        // 返回内容与声明的 sha256 不符
        var bytes = "payload".Select(c => (byte)c).ToArray();
        var hub = HubFor(_ =>
        {
            var msg = new HttpResponseMessage(HttpStatusCode.OK);
            msg.Content = new ByteArrayContent(bytes);
            return msg;
        });
        var updater = new SelfUpdater(hub, GitHubDown()) { CurrentVersion = "1.0.1" };
        var release = new AgentReleaseInfo("2.0.0", "https://x/agent.exe", new string('0', 64));

        var tmp = Path.Combine(Path.GetTempPath(), $"maxhub-selfupdate-{Guid.NewGuid():N}.exe");
        await Assert.ThrowsAsync<InvalidOperationException>(() => updater.DownloadAndInstallAsync(release));
        // 校验失败后临时文件被清理
        Assert.False(File.Exists(tmp));
    }
}
