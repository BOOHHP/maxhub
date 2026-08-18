using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MaxHub.Core.Packaging;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace MaxHub.Server.Tests;

/// <summary>验证服务端重启后已发布工具与刷新令牌仍然可用（SQLite 持久化）。</summary>
public sealed class PersistenceTests : IDisposable
{
    private readonly string _dataDir = Directory.CreateTempSubdirectory("maxhub-persist-test").FullName;

    private sealed class RestartableFactory(string dataDir) : WebApplicationFactory<Program>
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureHostConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:DataDir"] = dataDir,
                ["Auth:EnableMockProvider"] = "true",
                ["Roles:Publishers:0"] = "emp-pub",
                ["Roles:Reviewers:0"] = "emp-rev",
            }));
            return base.CreateHost(builder);
        }
    }

    [Fact]
    public async Task Published_tools_and_refresh_tokens_survive_restart()
    {
        string refreshToken;

        // 第一次启动：登录 → 发布 → 审核通过
        await using (var first = new RestartableFactory(_dataDir))
        {
            var (publisher, publisherRefresh) = await LoginAsync(first, "emp-pub", "张三");
            refreshToken = publisherRefresh;

            var upload = await publisher.PostAsync("/api/v1/publish/releases", PackageContent("scene-batch-renamer"));
            Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
            var releaseId = (await upload.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("releaseId").GetString()!;

            var (reviewer, _) = await LoginAsync(first, "emp-rev", "李四");
            var review = await reviewer.PostAsJsonAsync($"/api/v1/releases/{releaseId}/review", new { approve = true, channel = "stable" });
            Assert.Equal(HttpStatusCode.OK, review.StatusCode);
        }

        // 第二次启动（同一 DataDir）：数据与会话应存活
        await using var second = new RestartableFactory(_dataDir);
        var client = second.CreateClient();

        // 刷新令牌跨重启换取新会话，无需重新扫码
        var refreshed = await client.PostAsJsonAsync("/api/v1/auth/sessions/refresh", new { refreshToken });
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var session = await refreshed.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", session.GetProperty("accessToken").GetString()!);

        // 已发布工具索引存活
        var index = await client.GetFromJsonAsync<JsonElement>("/api/v1/tools?maxVersion=2026");
        Assert.Contains(index.EnumerateArray(), t => t.GetProperty("toolId").GetString() == "com.company.scene-batch-renamer");

        // 刷新令牌一次性：旧令牌已被消费
        var replay = await client.PostAsJsonAsync("/api/v1/auth/sessions/refresh", new { refreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
    }

    private static async Task<(HttpClient Client, string RefreshToken)> LoginAsync(
        WebApplicationFactory<Program> factory, string employeeId, string username)
    {
        var client = factory.CreateClient();
        var created = await client.PostAsync("/api/v1/auth/feishu/qr-sessions", null);
        var sessionId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sessionId").GetString()!;

        var authorized = await client.PostAsJsonAsync(
            $"/api/v1/auth/feishu/qr-sessions/{sessionId}/mock-authorize", new { employeeId, username });
        Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);

        var polled = await client.GetFromJsonAsync<JsonElement>($"/api/v1/auth/feishu/qr-sessions/{sessionId}");
        var session = polled.GetProperty("session");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", session.GetProperty("accessToken").GetString()!);
        return (client, session.GetProperty("refreshToken").GetString()!);
    }

    private static MultipartFormDataContent PackageContent(string sampleName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MaxHub.sln")))
            dir = dir.Parent!;
        var sampleDir = Path.Combine(dir!.FullName, "samples", "tools", sampleName);
        var zipPath = Path.Combine(Path.GetTempPath(), $"maxhub-persist-{sampleName}-{Guid.NewGuid():N}.zip");
        ToolPackage.Pack(sampleDir, zipPath);
        var bytes = File.ReadAllBytes(zipPath);
        File.Delete(zipPath);
        return new MultipartFormDataContent { { new ByteArrayContent(bytes), "package", "package.zip" } };
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools(); // 释放 maxhub.db 文件句柄后再删除临时目录
        if (Directory.Exists(_dataDir))
            Directory.Delete(_dataDir, recursive: true);
    }
}
