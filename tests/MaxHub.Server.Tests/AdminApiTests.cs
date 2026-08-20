using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace MaxHub.Server.Tests;

/// <summary>管理后台 API：角色控制与全量版本列表。</summary>
public class AdminApiTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    [Fact]
    public async Task Admin_releases_requires_reviewer_or_admin_and_lists_all_statuses()
    {
        var api = new ApiTests(fixture);

        // 匿名与普通用户被拒
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.CreateClient().GetAsync("/api/v1/admin/releases")).StatusCode);
        var viewer = await api.LoginPublicAsync("emp-nobody", "普通用户");
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/v1/admin/releases")).StatusCode);

        await api.PublishAndApprovePublicAsync("scene-batch-renamer");

        var reviewer = await api.LoginPublicAsync("emp-rev", "李四");
        var releases = await reviewer.GetFromJsonAsync<JsonElement[]>("/api/v1/admin/releases");
        var release = Assert.Single(releases!, r => r.GetProperty("toolId").GetString() == "com.company.scene-batch-renamer");
        Assert.Matches("^MaxTool[0-9]{8}$", release.GetProperty("publicToolId").GetString());
        Assert.Equal("Published", release.GetProperty("status").GetString());
        Assert.True(release.GetProperty("signed").GetBoolean());
        // 提交人/审核人显示姓名（登录时写入员工目录）
        Assert.Equal("张三", release.GetProperty("submittedBy").GetString());
        Assert.Equal("李四", release.GetProperty("reviewedBy").GetString());
    }

    [Fact]
    public async Task Withdraw_hides_release_from_index_and_stats_report_activity()
    {
        var api = new ApiTests(fixture);
        var releaseId = await api.PublishAndApprovePublicAsync("quick-exporter");

        var viewer = await api.LoginPublicAsync("emp-withdraw-viewer", "观察员");
        // 制造一次下载供统计验证
        var index = await viewer.GetFromJsonAsync<JsonElement[]>("/api/v1/tools?maxVersion=2026");
        var tool = Assert.Single(index!, t => t.GetProperty("toolId").GetString()!.Contains("quick-exporter"));
        Assert.Equal("导入导出", tool.GetProperty("category").GetString());
        var toolId = tool.GetProperty("toolId").GetString()!;
        var version = tool.GetProperty("latestVersion").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync($"/downloads/{toolId}/{version}/package.zip")).StatusCode);

        var reviewer = await api.LoginPublicAsync("emp-rev", "李四");
        Assert.Equal(HttpStatusCode.OK, (await reviewer.PostAsync($"/api/v1/releases/{releaseId}/withdraw", null)).StatusCode);

        // 撤回后索引不可见、下载 404
        var indexAfter = await viewer.GetFromJsonAsync<JsonElement[]>("/api/v1/tools?maxVersion=2026");
        Assert.DoesNotContain(indexAfter!, t => t.GetProperty("toolId").GetString() == toolId);
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync($"/downloads/{toolId}/{version}/package.zip")).StatusCode);

        // 统计保留下载记录
        var stats = await reviewer.GetFromJsonAsync<JsonElement>("/api/v1/admin/stats");
        Assert.True(stats.GetProperty("activeUsers").GetInt32() >= 1);
        Assert.Contains(stats.GetProperty("subjects").EnumerateArray(),
            s => s.GetProperty("subject").GetString() == $"{toolId}@{version}" &&
                 s.GetProperty("name").GetString() == "Quick Exporter" &&
                 s.GetProperty("toolId").GetString() == toolId &&
                 s.GetProperty("version").GetString() == version &&
                 s.GetProperty("downloads").GetInt32() >= 1);
    }

    [Fact]
    public async Task Admin_page_is_served()
    {
        var response = await fixture.CreateClient().GetAsync("/admin.html");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("MaxHub 后台管理", await response.Content.ReadAsStringAsync());
    }
}
