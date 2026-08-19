using System.Net;
using System.Text.Json;
using MaxHub.Agent.Core.Remote;

namespace MaxHub.Agent.Tests;

/// <summary>Phase 6：HubClient 脚本分析/直传 API 的请求构造与响应解析。</summary>
public class HubClientScriptTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private static HubClient HubFor(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var http = new HttpClient(new StubHandler(respond)) { BaseAddress = new Uri("http://server") };
        return new HubClient(http);
    }

    private static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json"),
    };

    [Fact]
    public async Task AnalyzeScript_returns_parsed_name_and_description()
    {
        string? requestPath = null;
        string? capturedBody = null;
        var hub = HubFor(req =>
        {
            requestPath = req.RequestUri?.PathAndQuery;
            capturedBody = req.Content?.ReadAsStringAsync().Result;
            return Json(new { name = "批量重置变换", description = "重置选中物体的变换", suggestedId = "reset-transform" });
        });

        var result = await hub.AnalyzeScriptAsync("reset.ms", "rollout resetRollout \"批量重置\"");

        Assert.NotNull(result);
        Assert.Equal("批量重置变换", result!.Value.Name);
        Assert.Equal("重置选中物体的变换", result.Value.Description);
        Assert.Equal("reset-transform", result.Value.SuggestedId);
        Assert.Equal("/api/v1/scripts/analyze", requestPath);
        using (var doc = JsonDocument.Parse(capturedBody!))
        {
            Assert.Equal("reset.ms", doc.RootElement.GetProperty("fileName").GetString());
            Assert.Contains("批量重置", doc.RootElement.GetProperty("content").GetString() ?? "");
        }
    }

    [Fact]
    public async Task AnalyzeScript_returns_null_on_server_error()
    {
        var hub = HubFor(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        Assert.Null(await hub.AnalyzeScriptAsync("a.ms", "x"));
    }

    [Fact]
    public async Task PublishScript_posts_expected_fields_and_parses_release_id()
    {
        string? capturedBody = null;
        var hub = HubFor(req =>
        {
            capturedBody = req.Content?.ReadAsStringAsync().Result;
            return Json(new { releaseId = "rel-42", status = "pendingReview" });
        });

        var outcome = await hub.PublishScriptAsync("tool.ms", "rollout toolRollout", "示例工具",
            "示例描述", "1.2.0", 2019, 2026);

        Assert.True(outcome.Success);
        Assert.Equal("rel-42", outcome.ReleaseId);
        Assert.NotNull(capturedBody);
        using var doc = JsonDocument.Parse(capturedBody!);
        var root = doc.RootElement;
        Assert.Equal("tool.ms", root.GetProperty("fileName").GetString());
        Assert.Equal("rollout toolRollout", root.GetProperty("content").GetString());
        Assert.Equal("示例工具", root.GetProperty("name").GetString());
        Assert.Equal("示例描述", root.GetProperty("description").GetString());
        Assert.Equal("1.2.0", root.GetProperty("version").GetString());
        Assert.Equal(2019, root.GetProperty("minMaxYear").GetInt32());
        Assert.Equal(2026, root.GetProperty("maxMaxYear").GetInt32());
    }

    [Fact]
    public async Task PublishScript_surfaces_server_errors()
    {
        var hub = HubFor(_ => Json(new { errors = new[] { "名称已存在" } }, HttpStatusCode.BadRequest));
        var outcome = await hub.PublishScriptAsync("t.ms", "x", "重名工具", "", "1.0.0", 2019, 2026);
        Assert.False(outcome.Success);
        Assert.Contains("名称已存在", outcome.Errors);
    }
}
