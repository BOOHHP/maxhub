using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace MaxHub.Server.Tests;

/// <summary>我的提交查询：独立 fixture 避免与其它测试共用 sample 版本冲突。</summary>
public class MyToolsTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    [Fact]
    public async Task My_tools_lists_own_submissions_with_status()
    {
        var api = new ApiTests(fixture);
        var releaseId = await api.PublishAndApprovePublicAsync("quick-exporter");

        var publisher = await api.LoginPublicAsync("emp-pub", "张三");
        var mine = await publisher.GetFromJsonAsync<JsonElement[]>("/api/v1/my-tools");
        var release = Assert.Single(mine!, r => r.GetProperty("releaseId").GetString() == releaseId);
        Assert.Equal("Published", release.GetProperty("status").GetString());
        Assert.Equal("李四", release.GetProperty("reviewedBy").GetString());

        // 其他人的提交不可见
        var other = await api.LoginPublicAsync("emp-other", "赵六");
        var others = await other.GetFromJsonAsync<JsonElement[]>("/api/v1/my-tools");
        Assert.DoesNotContain(others!, r => r.GetProperty("releaseId").GetString() == releaseId);
    }
}
