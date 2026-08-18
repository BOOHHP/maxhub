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

        var reviewer = await api.LoginPublicAsync("emp-rev", "审核者");
        var releases = await reviewer.GetFromJsonAsync<JsonElement[]>("/api/v1/admin/releases");
        var release = Assert.Single(releases!, r => r.GetProperty("toolId").GetString() == "com.company.scene-batch-renamer");
        Assert.Equal("Published", release.GetProperty("status").GetString());
        Assert.True(release.GetProperty("signed").GetBoolean());
        Assert.Equal("emp-rev", release.GetProperty("reviewedBy").GetString());
    }

    [Fact]
    public async Task Admin_page_is_served()
    {
        var response = await fixture.CreateClient().GetAsync("/admin.html");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("MaxHub 管理后台", await response.Content.ReadAsStringAsync());
    }
}
