using System.Text.Json;
using MaxHub.Server.Domain;

namespace MaxHub.Server.Services;

public sealed class FeishuAuthOptions
{
    public string AppId { get; init; } = "";
    public string AppSecret { get; init; } = "";
    /// <summary>必须与飞书开发者后台【安全设置-重定向URL】完全一致。</summary>
    public string RedirectUri { get; init; } = "http://127.0.0.1:47811/callback";
    public string PassportBaseUrl { get; init; } = "https://passport.feishu.cn";

    public bool IsConfigured => AppId.Length > 0 && AppSecret.Length > 0;
}

/// <summary>真实飞书扫码：授权页 URL，state 绑定 MaxHub 会话防 CSRF。</summary>
public sealed class RealFeishuAuthProvider(FeishuAuthOptions options) : IFeishuAuthProvider
{
    public string BuildAuthorizeUrl(string sessionId) =>
        $"{options.PassportBaseUrl}/suite/passport/oauth/authorize" +
        $"?client_id={Uri.EscapeDataString(options.AppId)}" +
        $"&redirect_uri={Uri.EscapeDataString(options.RedirectUri)}" +
        "&response_type=code" +
        $"&state={Uri.EscapeDataString(sessionId)}";
}

/// <summary>授权码换员工身份。仅服务端实现，AppSecret 不出服务端。</summary>
public interface IFeishuCodeExchanger
{
    Task<EmployeeIdentity> ExchangeAsync(string code, CancellationToken cancellationToken = default);
}

public sealed class FeishuAuthException(string message) : Exception(message);

public sealed class FeishuPassportClient(HttpClient http, FeishuAuthOptions options) : IFeishuCodeExchanger
{
    public async Task<EmployeeIdentity> ExchangeAsync(string code, CancellationToken cancellationToken = default)
    {
        // 第一步：authorization_code 换 user access_token
        using var tokenResponse = await http.PostAsync(
            $"{options.PassportBaseUrl}/suite/passport/oauth/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = options.AppId,
                ["client_secret"] = options.AppSecret,
                ["code"] = code,
                ["redirect_uri"] = options.RedirectUri,
            }),
            cancellationToken);

        var tokenJson = await ParseAsync(tokenResponse, "获取 access_token", cancellationToken);
        var accessToken = tokenJson.TryGetProperty("access_token", out var tokenProp) ? tokenProp.GetString() : null;
        if (string.IsNullOrEmpty(accessToken))
            throw new FeishuAuthException($"飞书未返回 access_token：{tokenJson.GetRawText()}");

        // 第二步：access_token 换用户身份
        using var userRequest = new HttpRequestMessage(HttpMethod.Get, $"{options.PassportBaseUrl}/suite/passport/oauth/userinfo");
        userRequest.Headers.Authorization = new("Bearer", accessToken);
        using var userResponse = await http.SendAsync(userRequest, cancellationToken);
        var userJson = await ParseAsync(userResponse, "获取用户信息", cancellationToken);

        var employeeId = FirstNonEmpty(userJson, "user_id", "open_id", "sub")
            ?? throw new FeishuAuthException($"飞书用户信息缺少可用 ID：{userJson.GetRawText()}");
        var username = FirstNonEmpty(userJson, "name", "en_name") ?? employeeId;
        return new EmployeeIdentity(employeeId, username);
    }

    private static async Task<JsonElement> ParseAsync(HttpResponseMessage response, string step, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new FeishuAuthException($"飞书{step}失败（HTTP {(int)response.StatusCode}）：{body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static string? FirstNonEmpty(JsonElement json, params string[] names)
    {
        foreach (var name in names)
            if (json.TryGetProperty(name, out var prop) && prop.GetString() is { Length: > 0 } value)
                return value;
        return null;
    }
}
