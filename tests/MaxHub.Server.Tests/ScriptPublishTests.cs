using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace MaxHub.Server.Tests;

/// <summary>Phase 3：脚本直传——自动识别预填 + 打包提交。</summary>
public class ScriptPublishTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    [Fact]
    public async Task Analyze_returns_suggested_name_and_description()
    {
        var api = new ApiTests(fixture);
        var publisher = await api.LoginPublicAsync("emp-pub", "张三");

        var res = await publisher.PostAsJsonAsync("/api/v1/scripts/analyze", new
        {
            fileName = "batch_renamer.ms",
            content = "-- 批量重命名场景对象\nrollout BatchRenamer \"批量重命名\" ( button go \"开始\" )",
        });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("批量重命名", body.GetProperty("name").GetString());
        Assert.Contains("批量重命名场景对象", body.GetProperty("description").GetString());
        Assert.Equal("com.company.batch-renamer", body.GetProperty("suggestedId").GetString());
    }

    [Fact]
    public async Task Analyze_requires_login()
    {
        var res = await fixture.CreateClient().PostAsJsonAsync("/api/v1/scripts/analyze",
            new { fileName = "a.ms", content = "x" });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Publish_script_packs_and_submits_for_review()
    {
        var api = new ApiTests(fixture);
        var publisher = await api.LoginPublicAsync("emp-pub", "张三");

        var res = await publisher.PostAsJsonAsync("/api/v1/scripts/publish", new
        {
            fileName = "batch_renamer.ms",
            content = "-- 批量重命名\nfn go() = ()",
            name = "批量重命名",
            description = "批量重命名场景对象",
            version = "1.0.0",
            minMaxYear = 2019,
            maxMaxYear = 2026,
        });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("pendingReview", body.GetProperty("status").GetString());

        // 提交后出现在我的提交中
        var mine = await publisher.GetFromJsonAsync<JsonElement[]>("/api/v1/my-tools");
        Assert.Contains(mine!, r => r.GetProperty("name").GetString() == "批量重命名");
    }

    [Fact]
    public async Task Publish_script_rejects_missing_fields()
    {
        var api = new ApiTests(fixture);
        var publisher = await api.LoginPublicAsync("emp-pub", "张三");
        var res = await publisher.PostAsJsonAsync("/api/v1/scripts/publish", new
        {
            fileName = "a.ms", content = "x", name = "", version = "1.0.0",
            minMaxYear = 2019, maxMaxYear = 2026,
        });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }
}
