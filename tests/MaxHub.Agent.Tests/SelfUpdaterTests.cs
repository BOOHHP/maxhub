using System.Net;
using System.Reflection;
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
    public async Task Server_release_takes_priority_over_GitHub()
    {
        var hub = HubFor(_ => Json(new
        {
            version = "2.0.0",
            downloadUrl = "/downloads/agent/2.0.0/MaxHubAgent-2.0.0-win-x64.exe",
            fallbackDownloadUrl = "https://github.com/o/r/releases/download/v2.0.0/MaxHubAgent-2.0.0-win-x64.exe",
            sha256 = "feedbeef",
        }));
        var github = GitHubFor(_ => throw new InvalidOperationException("GitHub should not be queried"));
        var updater = new SelfUpdater(hub, github) { CurrentVersion = "1.0.1" };

        var release = await updater.CheckForUpdateAsync();

        Assert.NotNull(release);
        Assert.Equal("2.0.0", release!.Version);
        Assert.Equal("feedbeef", release.Sha256);
        Assert.StartsWith("/downloads/agent/", release.DownloadUrl);
        Assert.StartsWith("https://github.com/", release.FallbackDownloadUrl);
    }

    [Fact]
    public async Task Falls_back_to_GitHub_when_server_unreachable()
    {
        var hub = HubFor(_ => throw new HttpRequestException("server down"));
        var github = GitHubFor(_ => Json(new
        {
            tag_name = "v1.5.0",
            assets = new[] { new {
                name = "MaxHubAgent-1.5.0-win-x64.exe",
                browser_download_url = "https://github.com/o/r/releases/download/v1.5.0/MaxHubAgent-1.5.0-win-x64.exe",
                digest = "sha256:srv",
            } },
        }));
        var updater = new SelfUpdater(hub, github) { CurrentVersion = "1.0.1" };

        var release = await updater.CheckForUpdateAsync();

        Assert.NotNull(release);
        Assert.Equal("1.5.0", release!.Version);
        Assert.StartsWith("https://github.com/", release.DownloadUrl);
    }

    [Fact]
    public async Task Download_falls_back_to_GitHub_when_server_mirror_fails()
    {
        var requested = new List<string>();
        var hub = HubFor(request =>
        {
            requested.Add(request.RequestUri!.ToString());
            if (request.RequestUri.AbsolutePath.StartsWith("/downloads/agent/"))
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3]),
            };
        });
        var updater = new SelfUpdater(hub, GitHubDown()) { CurrentVersion = "1.0.1" };
        var release = new AgentReleaseInfo(
            "2.0.0",
            "/downloads/agent/2.0.0/MaxHubAgent-2.0.0-win-x64.exe",
            new string('0', 64),
            "https://github.com/o/r/releases/download/v2.0.0/MaxHubAgent-2.0.0-win-x64.exe");

        await Assert.ThrowsAsync<InvalidOperationException>(() => updater.DownloadAndInstallAsync(release));

        Assert.Equal(2, requested.Count);
        Assert.StartsWith("http://server/downloads/agent/", requested[0]);
        Assert.StartsWith("https://github.com/", requested[1]);
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

    [Fact]
    public void Restart_script_renames_versioned_exe_and_deletes_old_application()
    {
        var method = typeof(SelfUpdater).GetMethod(
            "BuildRestartScript",
            BindingFlags.NonPublic | BindingFlags.Static);

        var script = Assert.IsType<string>(method!.Invoke(
            null,
            [
                @"C:\MaxHub\MaxHubAgent-1.0.14-win-x64.exe",
                @"C:\MaxHub\MaxHubAgent.new.exe",
                @"C:\MaxHub\MaxHubAgent-1.0.15-win-x64.exe",
                "1.0.15",
            ]));

        Assert.Contains("--after-update \"1.0.15\"", script);
        Assert.Contains("start \"\" \"C:\\MaxHub\\MaxHubAgent-1.0.15-win-x64.exe\"", script);
        Assert.Contains("del /f /q \"C:\\MaxHub\\MaxHubAgent-1.0.14-win-x64.exe\"", script);
    }

    [Fact]
    public void Restart_script_overwrites_fixed_name_without_deleting_target()
    {
        var method = typeof(SelfUpdater).GetMethod(
            "BuildRestartScript",
            BindingFlags.NonPublic | BindingFlags.Static);

        var script = Assert.IsType<string>(method!.Invoke(
            null,
            [@"C:\MaxHub\MaxHubAgent.exe", @"C:\MaxHub\MaxHubAgent.new.exe", @"C:\MaxHub\MaxHubAgent.exe", "1.0.15"]));

        Assert.Contains("start \"\" \"C:\\MaxHub\\MaxHubAgent.exe\" --after-update \"1.0.15\"", script);
        Assert.DoesNotContain("del /f /q \"C:\\MaxHub\\MaxHubAgent.exe\"", script);
    }

    [Theory]
    [InlineData(@"C:\MaxHub\MaxHubAgent-1.0.14-win-x64.exe", "1.0.15", @"C:\MaxHub\MaxHubAgent-1.0.15-win-x64.exe")]
    [InlineData(@"C:\MaxHub\MaxHubAgent.exe", "1.0.15", @"C:\MaxHub\MaxHubAgent.exe")]
    public void Target_exe_path_normalizes_only_versioned_file_names(
        string currentExe,
        string version,
        string expected)
    {
        var method = typeof(SelfUpdater).GetMethod(
            "GetTargetExePath",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.Equal(expected, method!.Invoke(null, [currentExe, version]));
    }
}
