using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace MaxHub.Server.Tests;

/// <summary>Phase 1：MaxHub 自管角色体系——登录返回角色、admin 管理成员角色、越权拒绝。</summary>
public class RoleApiTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    [Fact]
    public async Task Login_returns_roles_with_bootstrap_admin_and_default_publisher()
    {
        // 引导配置里的 emp-admin 应解析为 admin
        var (_, adminRoles) = await LoginWithRolesAsync("emp-admin", "管理员");
        Assert.Contains("admin", adminRoles);

        // 未配置的普通用户默认 publisher
        var (_, nobodyRoles) = await LoginWithRolesAsync("emp-nobody", "普通用户");
        Assert.Equal(new[] { "publisher" }, nobodyRoles);
    }

    [Fact]
    public async Task Admin_can_grant_and_revoke_roles()
    {
        var admin = await LoginWithRolesAsync("emp-admin", "管理员");
        var target = await LoginWithRolesAsync("emp-target", "目标用户");

        // 初始为 publisher
        Assert.Equal(new[] { "publisher" }, target.Roles);

        // admin 授予 reviewer
        var grant = await admin.Client.PutAsJsonAsync("/api/v1/admin/users/emp-target/roles", new { roles = new[] { "reviewer" } });
        Assert.Equal(HttpStatusCode.OK, grant.StatusCode);

        // 重新登录后角色已更新为 reviewer，可访问审核接口
        var promoted = await LoginWithRolesAsync("emp-target", "目标用户");
        Assert.Equal(new[] { "reviewer" }, promoted.Roles);
        Assert.Equal(HttpStatusCode.OK, (await promoted.Client.GetAsync("/api/v1/admin/releases")).StatusCode);

        // admin 收回角色 → 回到 publisher，审核接口被拒
        var revoke = await admin.Client.PutAsJsonAsync("/api/v1/admin/users/emp-target/roles", new { roles = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        var demoted = await LoginWithRolesAsync("emp-target", "目标用户");
        Assert.Equal(new[] { "publisher" }, demoted.Roles);
        Assert.Equal(HttpStatusCode.Forbidden, (await demoted.Client.GetAsync("/api/v1/admin/releases")).StatusCode);
    }

    [Fact]
    public async Task Non_admin_cannot_manage_roles()
    {
        var reviewer = await LoginWithRolesAsync("emp-rev", "李四");

        Assert.Equal(HttpStatusCode.Forbidden, (await reviewer.Client.GetAsync("/api/v1/admin/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await reviewer.Client.PutAsJsonAsync("/api/v1/admin/users/emp-rev/roles", new { roles = new[] { "admin" } })).StatusCode);
    }

    [Fact]
    public async Task Invalid_role_is_rejected()
    {
        var admin = await LoginWithRolesAsync("emp-admin", "管理员");
        var bad = await admin.Client.PutAsJsonAsync("/api/v1/admin/users/emp-admin/roles", new { roles = new[] { "superuser" } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    /// <summary>走完整登录流程，返回带 token 的 client 与解析出的角色。</summary>
    private async Task<(HttpClient Client, string[] Roles)> LoginWithRolesAsync(string employeeId, string username)
    {
        var client = fixture.CreateClient();
        var created = await client.PostAsync("/api/v1/auth/feishu/qr-sessions", null);
        var session = await created.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = session.GetProperty("sessionId").GetString()!;

        var authorized = await client.PostAsJsonAsync(
            $"/api/v1/auth/feishu/qr-sessions/{sessionId}/mock-authorize",
            new { employeeId, username });
        Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);

        var polled = await client.GetFromJsonAsync<JsonElement>($"/api/v1/auth/feishu/qr-sessions/{sessionId}");
        var token = polled.GetProperty("session").GetProperty("accessToken").GetString()!;
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var roles = polled.GetProperty("session").GetProperty("roles")
            .EnumerateArray().Select(r => r.GetString()!).ToArray();
        return (client, roles);
    }
}
