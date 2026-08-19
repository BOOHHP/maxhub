using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

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
        Assert.Contains("MaxHubAgent-", body.GetProperty("downloadUrl").GetString());
    }

    [Fact]
    public async Task Agent_latest_is_public()
    {
        // 无需登录即可访问（index.html 横幅使用）
        var res = await fixture.CreateClient().GetAsync("/api/v1/agent/latest");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
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
