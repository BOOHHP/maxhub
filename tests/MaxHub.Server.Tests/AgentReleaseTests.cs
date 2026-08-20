using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace MaxHub.Server.Tests;

/// <summary>Phase 4：Agent 版本元数据端点（网页下载横幅 + 自更新的来源）。</summary>
public class AgentReleaseTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    [Fact]
    public async Task Agent_latest_returns_version_metadata()
    {
        var res = await fixture.CreateClient().GetAsync("/api/v1/agent/latest");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        // 配置兜底或 DB 更新后都应有非空版本
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("version").GetString()));
        Assert.StartsWith("/downloads/agent/", body.GetProperty("downloadUrl").GetString());
        Assert.Contains("MaxHubAgent-", body.GetProperty("downloadUrl").GetString());
        Assert.StartsWith("https://", body.GetProperty("fallbackDownloadUrl").GetString());
    }

    [Fact]
    public async Task Agent_latest_is_public()
    {
        // 无需登录即可访问（index.html 横幅使用）
        var res = await fixture.CreateClient().GetAsync("/api/v1/agent/latest");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Market_page_keeps_download_handler_inside_script_block()
    {
        var html = await fixture.CreateClient().GetStringAsync("/index.html");
        var scriptStart = html.IndexOf("<script>", StringComparison.Ordinal);
        var handler = html.IndexOf("document.getElementById('agent-download').onclick", StringComparison.Ordinal);

        Assert.True(scriptStart > 0);
        Assert.True(handler > scriptStart);
        Assert.DoesNotContain(
            "<h1 class=\"title\">工具市场</h1>\n    document.getElementById",
            html);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "id=\"tools\"").Cast<System.Text.RegularExpressions.Match>());
    }

    [Fact]
    public async Task Agent_mirror_serves_local_file_when_present()
    {
        const string version = "8.8.8";
        var fileName = $"MaxHubAgent-{version}-win-x64.exe";
        var mirrorDir = Directory.CreateDirectory(Path.Combine(fixture.DataDir, "agent"));
        var mirrorPath = Path.Combine(mirrorDir.FullName, fileName);
        await File.WriteAllBytesAsync(mirrorPath, [1, 2, 3, 4]);
        try
        {
            var response = await fixture.CreateClient().GetAsync($"/downloads/agent/{version}/{fileName}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal([1, 2, 3, 4], await response.Content.ReadAsByteArrayAsync());

            using var headRequest = new HttpRequestMessage(HttpMethod.Head, $"/downloads/agent/{version}/{fileName}");
            var head = await fixture.CreateClient().SendAsync(headRequest);
            Assert.Equal(HttpStatusCode.OK, head.StatusCode);
            Assert.Equal(4, head.Content.Headers.ContentLength);
            Assert.Empty(await head.Content.ReadAsByteArrayAsync());
        }
        finally
        {
            File.Delete(mirrorPath);
        }
    }

    [Fact]
    public async Task Agent_mirror_redirects_to_github_when_file_missing()
    {
        var client = fixture.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await client.GetAsync("/downloads/agent/2.0.0/MaxHubAgent-2.0.0-win-x64.exe");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(
            "https://github.com/example/maxhub/releases/download/v2.0.0/MaxHubAgent-2.0.0-win-x64.zip",
            response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Admin_can_update_agent_release_in_db()
    {
        var api = new ApiTests(fixture);
        var admin = await api.LoginPublicAsync("emp-admin", "管理员");

        // admin 更新 DB 中的 Agent 版本
        var update = await admin.PutAsJsonAsync("/api/v1/admin/agent-release", new
        {
            version = "3.0.0",
            downloadUrl = "https://github.com/example/maxhub/releases/download/v3.0.0/MaxHubAgent-3.0.0-win-x64.exe",
            sha256 = "def456",
        });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        // 立即生效（DB 优先于配置）
        var res = await fixture.CreateClient().GetAsync("/api/v1/agent/latest");
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("3.0.0", body.GetProperty("version").GetString());
        Assert.Equal("def456", body.GetProperty("sha256").GetString());
    }

    [Fact]
    public async Task Non_admin_cannot_update_agent_release()
    {
        var api = new ApiTests(fixture);
        var reviewer = await api.LoginPublicAsync("emp-rev", "李四");
        var res = await reviewer.PutAsJsonAsync("/api/v1/admin/agent-release", new
        {
            version = "9.9.9",
            downloadUrl = "https://x/agent.exe",
        });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }
}
