using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace MaxHub.Server.Tests;

/// <summary>Phase 4：Agent 版本元数据端点（网页下载横幅 + 自更新的来源）。</summary>
public class AgentReleaseTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    [Fact]
    public async Task Agent_latest_returns_configured_version_metadata()
    {
        var res = await fixture.CreateClient().GetAsync("/api/v1/agent/latest");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("2.0.0", body.GetProperty("version").GetString());
        Assert.Contains("MaxHubAgent-2.0.0-win-x64.zip", body.GetProperty("downloadUrl").GetString());
        Assert.Equal("abc123", body.GetProperty("sha256").GetString());
    }

    [Fact]
    public async Task Agent_latest_is_public()
    {
        // 无需登录即可访问（index.html 横幅使用）
        var res = await fixture.CreateClient().GetAsync("/api/v1/agent/latest");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }
}
