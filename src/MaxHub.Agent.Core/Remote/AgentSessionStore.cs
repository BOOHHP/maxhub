using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MaxHub.Agent.Core.Remote;

public sealed record AgentUser(string EmployeeId, string Username);

/// <summary>
/// Agent 本地会话存储：访问/刷新令牌 DPAPI 加密落盘，用户信息明文。
/// 访问令牌临近到期时用刷新令牌自动续期，服务端重启后无需重新扫码。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AgentSessionStore(string settingsPath)
{
    public bool HasSession => File.Exists(settingsPath);

    public AgentUser? ReadUser()
    {
        if (!File.Exists(settingsPath))
            return null;
        using var json = JsonDocument.Parse(File.ReadAllText(settingsPath));
        if (!json.RootElement.TryGetProperty("employeeId", out var id) || id.ValueKind != JsonValueKind.String)
            return null;
        return new AgentUser(id.GetString()!, json.RootElement.GetProperty("username").GetString() ?? "");
    }

    public void Save(string accessToken, string refreshToken, DateTimeOffset expiresAtUtc, AgentUser? user = null)
    {
        user ??= ReadUser(); // 刷新续期时保留已存用户信息
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(new
        {
            accessToken = Protect(accessToken),
            refreshToken = Protect(refreshToken),
            expiresAtUtc,
            employeeId = user?.EmployeeId,
            username = user?.Username,
        }));
    }

    public void Clear()
    {
        if (File.Exists(settingsPath))
            File.Delete(settingsPath);
    }

    public string LoadAccessToken(string server)
    {
        if (!File.Exists(settingsPath))
            throw new InvalidOperationException("尚未登录，请先执行 login。");
        using var json = JsonDocument.Parse(File.ReadAllText(settingsPath));
        var expiresAtUtc = json.RootElement.TryGetProperty("expiresAtUtc", out var exp)
            ? exp.GetDateTimeOffset()
            : DateTimeOffset.MinValue;
        if (expiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(1))
            return Unprotect(json.RootElement.GetProperty("accessToken").GetString()!);
        return ForceRefresh(server);
    }

    /// <summary>用持久化刷新令牌换新会话；失败抛 InvalidOperationException 要求重新登录。</summary>
    public string ForceRefresh(string server)
    {
        if (!File.Exists(settingsPath))
            throw new InvalidOperationException("尚未登录，请先执行 login。");
        string refreshToken;
        using (var json = JsonDocument.Parse(File.ReadAllText(settingsPath)))
        {
            if (!json.RootElement.TryGetProperty("refreshToken", out var rt) || rt.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException("会话已过期，请重新登录。");
            refreshToken = Unprotect(rt.GetString()!);
        }

        using var http = new HttpClient { BaseAddress = new Uri(server) };
        var response = http.PostAsJsonAsync("/api/v1/auth/sessions/refresh", new { refreshToken })
            .GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("会话已过期，请重新登录。");
        var session = response.Content.ReadFromJsonAsync<JsonElement>().GetAwaiter().GetResult();
        var accessToken = session.GetProperty("accessToken").GetString()!;
        Save(accessToken, session.GetProperty("refreshToken").GetString()!, session.GetProperty("expiresAtUtc").GetDateTimeOffset());
        return accessToken;
    }

    private static string Protect(string value) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));

    private static string Unprotect(string value) =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser));
}

/// <summary>401 时用刷新令牌强制续期并重试一次（覆盖服务端重启使未到期访问令牌失效的场景）。</summary>
public sealed class SessionRefreshHandler : DelegatingHandler
{
    private readonly Func<string> _forceRefresh;

    public SessionRefreshHandler(Func<string> forceRefresh) : base(new HttpClientHandler()) => _forceRefresh = forceRefresh;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode != System.Net.HttpStatusCode.Unauthorized)
            return response;

        string newToken;
        try { newToken = _forceRefresh(); }
        catch { return response; }

        response.Dispose();
        var retry = new HttpRequestMessage(request.Method, request.RequestUri) { Content = request.Content };
        retry.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", newToken);
        return await base.SendAsync(retry, cancellationToken);
    }
}
