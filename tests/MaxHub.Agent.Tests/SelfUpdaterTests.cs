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

    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json"),
    };

    [Fact]
    public async Task No_update_when_versions_equal()
    {
        var hub = HubFor(_ => Json(new { version = "1.0.1", downloadUrl = "https://x/agent.exe", sha256 = "abc" }));
        var updater = new SelfUpdater(hub) { CurrentVersion = "1.0.1" };
        Assert.Null(await updater.CheckForUpdateAsync());
    }

    [Fact]
    public async Task Update_available_when_server_newer()
    {
        var hub = HubFor(_ => Json(new { version = "1.1.0", downloadUrl = "https://x/agent.exe", sha256 = "abc" }));
        var updater = new SelfUpdater(hub) { CurrentVersion = "1.0.1" };
        var release = await updater.CheckForUpdateAsync();
        Assert.NotNull(release);
        Assert.Equal("1.1.0", release!.Version);
    }

    [Fact]
    public async Task No_update_when_server_older_or_missing()
    {
        var hub = HubFor(_ => Json(new { version = "1.0.0", downloadUrl = "https://x/agent.exe", sha256 = "abc" }));
        var updater = new SelfUpdater(hub) { CurrentVersion = "1.0.1" };
        Assert.Null(await updater.CheckForUpdateAsync());
    }

    [Fact]
    public async Task Null_when_server_returns_404()
    {
        var hub = HubFor(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var updater = new SelfUpdater(hub) { CurrentVersion = "1.0.1" };
        Assert.Null(await updater.CheckForUpdateAsync());
    }

    [Fact]
    public async Task Null_when_download_url_empty()
    {
        var hub = HubFor(_ => Json(new { version = "9.9.9", downloadUrl = "", sha256 = "" }));
        var updater = new SelfUpdater(hub) { CurrentVersion = "1.0.1" };
        Assert.Null(await updater.CheckForUpdateAsync());
    }

    [Fact]
    public async Task Null_when_server_unreachable()
    {
        var hub = HubFor(_ => throw new HttpRequestException("unreachable"));
        var updater = new SelfUpdater(hub) { CurrentVersion = "1.0.1" };
        Assert.Null(await updater.CheckForUpdateAsync());
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
        var updater = new SelfUpdater(hub) { CurrentVersion = "1.0.1" };
        var release = new AgentReleaseInfo("2.0.0", "https://x/agent.exe", new string('0', 64));

        var tmp = Path.Combine(Path.GetTempPath(), $"maxhub-selfupdate-{Guid.NewGuid():N}.exe");
        await Assert.ThrowsAsync<InvalidOperationException>(() => updater.DownloadAndInstallAsync(release));
        // 校验失败后临时文件被清理
        Assert.False(File.Exists(tmp));
    }
}
