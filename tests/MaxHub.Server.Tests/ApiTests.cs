using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MaxHub.Core.Packaging;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace MaxHub.Server.Tests;

public sealed class ServerFixture : WebApplicationFactory<Program>
{
    public string DataDir { get; } = Directory.CreateTempSubdirectory("maxhub-server-test").FullName;

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.UseEnvironment("Testing"); // 阻止加载含真实飞书凭据的 appsettings.Local.json
        builder.ConfigureHostConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:DataDir"] = DataDir,
            ["Auth:EnableMockProvider"] = "true",
            ["Roles:Publishers:0"] = "emp-pub",
            ["Roles:Reviewers:0"] = "emp-rev",
            ["Roles:Admins:0"] = "emp-admin",
        }));
        return base.CreateHost(builder);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); // 释放 maxhub.db 句柄，避免临时目录删除失败
        if (Directory.Exists(DataDir))
            Directory.Delete(DataDir, recursive: true);
    }
}

public class ApiTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    private static string RepoRoot { get; } = FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MaxHub.sln")))
            dir = dir.Parent!;
        return dir!.FullName;
    }

    private async Task<HttpClient> LoginAsync(string employeeId, string username)
    {
        var client = fixture.CreateClient();
        var created = await client.PostAsync("/api/v1/auth/feishu/qr-sessions", null);
        var session = await created.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = session.GetProperty("sessionId").GetString()!;

        // 模拟员工在飞书移动端扫码授权
        var authorized = await client.PostAsJsonAsync(
            $"/api/v1/auth/feishu/qr-sessions/{sessionId}/mock-authorize",
            new { employeeId, username });
        Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);

        var polled = await client.GetFromJsonAsync<JsonElement>($"/api/v1/auth/feishu/qr-sessions/{sessionId}");
        Assert.Equal("authorized", polled.GetProperty("status").GetString());
        Assert.Equal(username, polled.GetProperty("session").GetProperty("user").GetProperty("username").GetString());

        var token = polled.GetProperty("session").GetProperty("accessToken").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static MultipartFormDataContent PackageContent(string sampleName)
    {
        var sampleDir = Path.Combine(RepoRoot, "samples", "tools", sampleName);
        var zipPath = Path.Combine(Path.GetTempPath(), $"maxhub-api-{sampleName}-{Guid.NewGuid():N}.zip");
        ToolPackage.Pack(sampleDir, zipPath);
        var bytes = File.ReadAllBytes(zipPath);
        File.Delete(zipPath);
        var content = new MultipartFormDataContent { { new ByteArrayContent(bytes), "package", "package.zip" } };
        return content;
    }

    private async Task<string> PublishAndApproveAsync(string sampleName, string channel = "stable")
    {
        var publisher = await LoginAsync("emp-pub", "张三");
        var upload = await publisher.PostAsync("/api/v1/publish/releases", PackageContent(sampleName));
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        var releaseId = (await upload.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("releaseId").GetString()!;

        var reviewer = await LoginAsync("emp-rev", "李四");
        var review = await reviewer.PostAsJsonAsync($"/api/v1/releases/{releaseId}/review", new { approve = true, channel });
        Assert.Equal(HttpStatusCode.OK, review.StatusCode);
        return releaseId;
    }

    [Fact]
    public async Task Anonymous_requests_are_rejected()
    {
        var client = fixture.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/tools?maxVersion=2026")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/downloads/com.x.y/1.0.0/package.zip")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/activity/events", new { eventId = "e", type = "t", subject = "s" })).StatusCode);
    }

    [Fact]
    public async Task Publish_review_index_download_full_flow()
    {
        await PublishAndApproveAsync("scene-batch-renamer");

        var viewer = await LoginAsync("emp-viewer", "王五");
        var index = await viewer.GetFromJsonAsync<JsonElement[]>("/api/v1/tools?maxVersion=2026");
        var tool = Assert.Single(index!, t => t.GetProperty("toolId").GetString() == "com.company.scene-batch-renamer");
        Assert.Equal("1.4.0", tool.GetProperty("latestVersion").GetString());

        var plan = await viewer.GetFromJsonAsync<JsonElement>("/api/v1/tools/com.company.scene-batch-renamer/releases/1.4.0/install-plan");
        Assert.Equal("low", plan.GetProperty("riskLevel").GetString());
        var expectedSha = plan.GetProperty("sha256").GetString()!;

        var download = await viewer.GetAsync("/downloads/com.company.scene-batch-renamer/1.4.0/package.zip");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(tempFile, await download.Content.ReadAsByteArrayAsync());
            Assert.Equal(expectedSha, ToolPackage.ComputeSha256(tempFile));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task Index_filters_by_max_year_compatibility()
    {
        await PublishAndApproveAsync("startup-env-check"); // 2019-2024
        var viewer = await LoginAsync("emp-viewer2", "赵六");

        var for2026 = await viewer.GetFromJsonAsync<JsonElement[]>("/api/v1/tools?maxVersion=2026");
        Assert.DoesNotContain(for2026!, t => t.GetProperty("toolId").GetString() == "com.company.startup-env-check");

        var for2022 = await viewer.GetFromJsonAsync<JsonElement[]>("/api/v1/tools?maxVersion=2022");
        var tool = Assert.Single(for2022!, t => t.GetProperty("toolId").GetString() == "com.company.startup-env-check");
        // 启动脚本必须标记为中风险
        var plan = await viewer.GetFromJsonAsync<JsonElement>(
            $"/api/v1/tools/com.company.startup-env-check/releases/{tool.GetProperty("latestVersion").GetString()}/install-plan");
        Assert.Equal("medium", plan.GetProperty("riskLevel").GetString());
    }

    [Fact]
    public async Task Non_publisher_cannot_upload_and_non_reviewer_cannot_review()
    {
        var viewer = await LoginAsync("emp-viewer3", "钱七");
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync("/api/v1/publish/releases", PackageContent("quick-exporter"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/v1/releases/any/review", new { approve = true, channel = "stable" })).StatusCode);
    }

    [Fact]
    public async Task Invalid_package_is_rejected_with_manifest_errors()
    {
        var publisher = await LoginAsync("emp-pub", "张三");

        // 构造一个使用第二阶段 destination 的非法包
        var dir = Directory.CreateTempSubdirectory("maxhub-badpkg").FullName;
        try
        {
            var manifest = File.ReadAllText(Path.Combine(RepoRoot, "samples", "tools", "quick-exporter", "manifest.json"))
                .Replace("\"userScripts\"", "\"userPlugins\"");
            File.WriteAllText(Path.Combine(dir, "manifest.json"), manifest);
            Directory.CreateDirectory(Path.Combine(dir, "payload", "3dsmax", "scripts"));
            File.WriteAllText(Path.Combine(dir, "payload", "3dsmax", "scripts", "quick_exporter.py"), "# x");

            var zipPath = Path.Combine(dir, "bad.zip");
            ToolPackage.Pack(dir, zipPath);
            var content = new MultipartFormDataContent { { new ByteArrayContent(File.ReadAllBytes(zipPath)), "package", "bad.zip" } };

            var response = await publisher.PostAsync("/api/v1/publish/releases", content);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Contains("userPlugins", body.GetProperty("errors")[0].GetString());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Activity_events_are_idempotent_by_event_id()
    {
        var viewer = await LoginAsync("emp-viewer4", "孙八");
        var body = new { eventId = "evt-001", type = "browse", subject = "com.company.scene-batch-renamer", clientVersion = "agent/0.1" };

        var first = await (await viewer.PostAsJsonAsync("/api/v1/activity/events", body)).Content.ReadFromJsonAsync<JsonElement>();
        var second = await (await viewer.PostAsJsonAsync("/api/v1/activity/events", body)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(first.GetProperty("accepted").GetBoolean());
        Assert.True(second.GetProperty("duplicate").GetBoolean());
    }

    [Fact]
    public async Task Connector_register_query_download_by_max_year()
    {
        var admin = await LoginAsync("emp-admin", "管理员");
        var connectorZip = new byte[] { 0x50, 0x4B, 0x05, 0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }; // 空 zip
        var content = new MultipartFormDataContent
        {
            { new ByteArrayContent(connectorZip), "package", "connector.zip" },
            { new StringContent("1.0.0"), "version" },
            { new StringContent("2019"), "minMaxYear" },
            { new StringContent("2021"), "maxMaxYear" },
        };
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync("/api/v1/admin/connectors", content)).StatusCode);

        var viewer = await LoginAsync("emp-viewer5", "周九");
        var for2020 = await viewer.GetFromJsonAsync<JsonElement[]>("/api/v1/connectors?maxVersion=2020");
        Assert.Single(for2020!);
        var for2024 = await viewer.GetFromJsonAsync<JsonElement[]>("/api/v1/connectors?maxVersion=2024");
        Assert.Empty(for2024!);

        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/downloads/connectors/2020/1.0.0/package.zip")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync("/downloads/connectors/2024/1.0.0/package.zip")).StatusCode);
    }

    [Fact]
    public async Task Session_refresh_and_logout_lifecycle()
    {
        var client = await LoginAsync("emp-viewer6", "吴十");

        // 拿 refreshToken 需要重新走一次登录取回完整会话
        var created = await client.PostAsync("/api/v1/auth/feishu/qr-sessions", null);
        var sessionId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sessionId").GetString()!;
        await client.PostAsJsonAsync($"/api/v1/auth/feishu/qr-sessions/{sessionId}/mock-authorize", new { employeeId = "emp-viewer6", username = "吴十" });
        var polled = await client.GetFromJsonAsync<JsonElement>($"/api/v1/auth/feishu/qr-sessions/{sessionId}");
        var refreshToken = polled.GetProperty("session").GetProperty("refreshToken").GetString()!;

        var refreshed = await client.PostAsJsonAsync("/api/v1/auth/sessions/refresh", new { refreshToken });
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var newToken = (await refreshed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;

        var authed = fixture.CreateClient();
        authed.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", newToken);
        Assert.Equal(HttpStatusCode.OK, (await authed.GetAsync("/api/v1/tools?maxVersion=2026")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await authed.DeleteAsync("/api/v1/auth/sessions/current")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await authed.GetAsync("/api/v1/tools?maxVersion=2026")).StatusCode);
    }
}
