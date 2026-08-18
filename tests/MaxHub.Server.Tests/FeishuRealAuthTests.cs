using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MaxHub.Server.Domain;
using MaxHub.Server.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MaxHub.Server.Tests;

/// <summary>真实飞书链路的服务端行为：授权 URL 构造、state 校验、换码成功/失败、mock 端点关闭。</summary>
public sealed class FeishuServerFixture : WebApplicationFactory<Program>
{
    public string DataDir { get; } = Directory.CreateTempSubdirectory("maxhub-feishu-test").FullName;

    private sealed class FakeExchanger : IFeishuCodeExchanger
    {
        public Task<EmployeeIdentity> ExchangeAsync(string code, string? redirectUri = null, CancellationToken cancellationToken = default) =>
            code == "good-code"
                ? Task.FromResult(new EmployeeIdentity("fs-emp-001", "测试员工"))
                : throw new FeishuAuthException("授权码无效或已过期");
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureHostConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:DataDir"] = DataDir,
            ["Auth:EnableMockProvider"] = "false",
            ["Feishu:AppId"] = "cli_testapp",
            ["Feishu:AppSecret"] = "test-secret",
            ["Feishu:RedirectUri"] = "http://127.0.0.1:47811/callback",
        }));
        // 追加注册覆盖 Program 中的真实交换器（单例取最后注册）
        builder.ConfigureServices(services => services.AddSingleton<IFeishuCodeExchanger>(new FakeExchanger()));
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

public class FeishuRealAuthTests(FeishuServerFixture fixture) : IClassFixture<FeishuServerFixture>
{
    private async Task<(HttpClient Client, string SessionId, string AuthorizeUrl)> CreateSessionAsync()
    {
        var client = fixture.CreateClient();
        var created = await client.PostAsync("/api/v1/auth/feishu/qr-sessions", null);
        var json = await created.Content.ReadFromJsonAsync<JsonElement>();
        return (client, json.GetProperty("sessionId").GetString()!, json.GetProperty("authorizeUrl").GetString()!);
    }

    [Fact]
    public async Task Authorize_url_targets_feishu_passport_with_bound_state()
    {
        var (_, sessionId, authorizeUrl) = await CreateSessionAsync();
        Assert.StartsWith("https://passport.feishu.cn/suite/passport/oauth/authorize?", authorizeUrl);
        Assert.Contains("client_id=cli_testapp", authorizeUrl);
        Assert.Contains(Uri.EscapeDataString("http://127.0.0.1:47811/callback"), authorizeUrl);
        Assert.Contains("response_type=code", authorizeUrl);
        Assert.Contains($"state={sessionId}", authorizeUrl);
    }

    [Fact]
    public async Task Complete_with_valid_code_issues_session_via_poll()
    {
        var (client, sessionId, _) = await CreateSessionAsync();

        var complete = await client.PostAsJsonAsync(
            $"/api/v1/auth/feishu/qr-sessions/{sessionId}/complete",
            new { code = "good-code", state = sessionId });
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);

        var polled = await client.GetFromJsonAsync<JsonElement>($"/api/v1/auth/feishu/qr-sessions/{sessionId}");
        Assert.Equal("authorized", polled.GetProperty("status").GetString());
        var user = polled.GetProperty("session").GetProperty("user");
        Assert.Equal("fs-emp-001", user.GetProperty("employeeId").GetString());
        Assert.Equal("测试员工", user.GetProperty("username").GetString());

        // 签发的 MaxHub 会话可正常访问业务接口
        var token = polled.GetProperty("session").GetProperty("accessToken").GetString()!;
        var authed = fixture.CreateClient();
        authed.DefaultRequestHeaders.Authorization = new("Bearer", token);
        Assert.Equal(HttpStatusCode.OK, (await authed.GetAsync("/api/v1/tools?maxVersion=2026")).StatusCode);
    }

    [Fact]
    public async Task Mismatched_state_is_rejected()
    {
        var (client, sessionId, _) = await CreateSessionAsync();
        var complete = await client.PostAsJsonAsync(
            $"/api/v1/auth/feishu/qr-sessions/{sessionId}/complete",
            new { code = "good-code", state = "forged-state" });
        Assert.Equal(HttpStatusCode.BadRequest, complete.StatusCode);
    }

    [Fact]
    public async Task Invalid_code_returns_bad_gateway_and_session_stays_pending()
    {
        var (client, sessionId, _) = await CreateSessionAsync();
        var complete = await client.PostAsJsonAsync(
            $"/api/v1/auth/feishu/qr-sessions/{sessionId}/complete",
            new { code = "bad-code", state = sessionId });
        Assert.Equal(HttpStatusCode.BadGateway, complete.StatusCode);

        var polled = await client.GetFromJsonAsync<JsonElement>($"/api/v1/auth/feishu/qr-sessions/{sessionId}");
        Assert.Equal("pending", polled.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Mock_authorize_endpoint_is_disabled_in_real_mode()
    {
        var (client, sessionId, _) = await CreateSessionAsync();
        var response = await client.PostAsJsonAsync(
            $"/api/v1/auth/feishu/qr-sessions/{sessionId}/mock-authorize",
            new { employeeId = "hacker", username = "伪造者" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
