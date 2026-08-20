using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MaxHub.Core.Manifests;

namespace MaxHub.Server.Tests;

/// <summary>后台规范化编辑：修改已上传版本的名称/描述/频道。</summary>
public class ReleaseMetadataTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    private async Task<string> PublishToolAsync(HttpClient publisher, HttpClient reviewer, string version)
    {
        var res = await publisher.PostAsJsonAsync("/api/v1/scripts/publish", new
        {
            fileName = "meta_tool.ms",
            content = "-- 原始描述\nfn go() = ()",
            name = "Meta Tool",
            description = "原始描述",
            version,
            minMaxYear = 2019,
            maxMaxYear = 2026,
        });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var releaseId = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("releaseId").GetString()!;

        var review = await reviewer.PostAsJsonAsync($"/api/v1/releases/{releaseId}/review",
            new { approve = true, channel = "internal" });
        Assert.Equal(HttpStatusCode.OK, review.StatusCode);
        return releaseId;
    }

    [Fact]
    public async Task Admin_edits_name_description_channel_and_index_reflects_it()
    {
        var api = new ApiTests(fixture);
        var publisher = await api.LoginPublicAsync("emp-pub", "张三");
        var reviewer = await api.LoginPublicAsync("emp-rev", "李四");
        var admin = await api.LoginPublicAsync("emp-admin", "王五");
        var releaseId = await PublishToolAsync(publisher, reviewer, "1.0.0");

        var res = await admin.PatchAsJsonAsync($"/api/v1/admin/releases/{releaseId}/metadata",
            new { name = "规范名称", description = "规范描述：材质贴图整理", channel = "stable" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        // 后台列表反映修改
        var all = await admin.GetFromJsonAsync<JsonElement[]>("/api/v1/admin/releases");
        var row = all!.Single(r => r.GetProperty("releaseId").GetString() == releaseId);
        Assert.Equal("规范名称", row.GetProperty("name").GetString());
        Assert.Equal("规范描述：材质贴图整理", row.GetProperty("description").GetString());
        Assert.Equal("stable", row.GetProperty("channel").GetString());
        Assert.True(row.GetProperty("signed").GetBoolean()); // 签名不受影响

        // 公开索引反映修改
        var tools = await fixture.CreateClient().GetFromJsonAsync<JsonElement[]>("/api/v1/tools?maxVersion=2025");
        var tool = tools!.Single(t => t.GetProperty("toolId").GetString() == ToolId.Generate("Meta Tool"));
        Assert.Equal("规范名称", tool.GetProperty("name").GetString());
        Assert.Equal("规范描述：材质贴图整理", tool.GetProperty("description").GetString());
    }

    [Fact]
    public async Task Publisher_cannot_edit_metadata()
    {
        var api = new ApiTests(fixture);
        var publisher = await api.LoginPublicAsync("emp-pub", "张三");
        var res = await publisher.PatchAsJsonAsync("/api/v1/admin/releases/any/metadata",
            new { name = "x" });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Rejects_invalid_channel()
    {
        var api = new ApiTests(fixture);
        var admin = await api.LoginPublicAsync("emp-admin", "王五");
        var res = await admin.PatchAsJsonAsync("/api/v1/admin/releases/any/metadata",
            new { channel = "hacked" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Unknown_release_returns_404()
    {
        var api = new ApiTests(fixture);
        var admin = await api.LoginPublicAsync("emp-admin", "王五");
        var res = await admin.PatchAsJsonAsync("/api/v1/admin/releases/nonexistent/metadata",
            new { name = "x" });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }
}
