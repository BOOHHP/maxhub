using System.Net.Http.Json;
using System.Text.Json;

namespace MaxHub.Agent.Core.Remote;

public sealed record QrSessionInfo(string SessionId, string AuthorizeUrl);
public sealed record HubSession(string AccessToken, string RefreshToken, string EmployeeId, string Username);
public sealed record ToolIndexItem(string ToolId, string Name, string? Description, string LatestVersion, string Channel);
public sealed record RemoteInstallPlan(string ToolId, string Version, string Sha256, long SizeBytes, bool RestartRequired, string RiskLevel);
public sealed record ConnectorInfo(string Version, int MinMaxYear, int MaxMaxYear, string Sha256, long SizeBytes);

/// <summary>Agent 侧的 Hub API 客户端。所有请求携带 MaxHub 会话令牌。</summary>
public sealed class HubClient(HttpClient http)
{
    public async Task<QrSessionInfo> CreateQrSessionAsync()
    {
        var response = await http.PostAsync("/api/v1/auth/feishu/qr-sessions", null);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return new QrSessionInfo(json.GetProperty("sessionId").GetString()!, json.GetProperty("authorizeUrl").GetString()!);
    }

    /// <summary>轮询扫码状态；授权完成时返回会话并自动携带令牌。</summary>
    public async Task<HubSession?> PollQrAsync(string sessionId)
    {
        var json = await http.GetFromJsonAsync<JsonElement>($"/api/v1/auth/feishu/qr-sessions/{sessionId}");
        if (json.GetProperty("status").GetString() != "authorized")
            return null;
        var session = json.GetProperty("session");
        var hubSession = new HubSession(
            session.GetProperty("accessToken").GetString()!,
            session.GetProperty("refreshToken").GetString()!,
            session.GetProperty("user").GetProperty("employeeId").GetString()!,
            session.GetProperty("user").GetProperty("username").GetString()!);
        UseToken(hubSession.AccessToken);
        return hubSession;
    }

    public void UseToken(string accessToken) =>
        http.DefaultRequestHeaders.Authorization = new("Bearer", accessToken);

    /// <summary>本机回调模式：把飞书重定向拿到的授权码交给服务端换身份。</summary>
    public async Task CompleteQrAsync(string sessionId, string code, string state)
    {
        var response = await http.PostAsJsonAsync($"/api/v1/auth/feishu/qr-sessions/{sessionId}/complete", new { code, state });
        response.EnsureSuccessStatusCode();
    }

    public Task<ToolIndexItem[]> GetToolsAsync(int maxYear) =>
        http.GetFromJsonAsync<ToolIndexItem[]>($"/api/v1/tools?maxVersion={maxYear}")!;

    public Task<RemoteInstallPlan> GetInstallPlanAsync(string toolId, string version) =>
        http.GetFromJsonAsync<RemoteInstallPlan>($"/api/v1/tools/{toolId}/releases/{version}/install-plan")!;

    public Task<ConnectorInfo[]> GetConnectorsAsync(int maxYear) =>
        http.GetFromJsonAsync<ConnectorInfo[]>($"/api/v1/connectors?maxVersion={maxYear}")!;

    public async Task DownloadToolAsync(string toolId, string version, string targetPath)
    {
        await DownloadAsync($"/downloads/{toolId}/{version}/package.zip", targetPath);
    }

    public async Task DownloadConnectorAsync(int maxYear, string version, string targetPath)
    {
        await DownloadAsync($"/downloads/connectors/{maxYear}/{version}/package.zip", targetPath);
    }

    public async Task PostInstallEventAsync(string eventId, string type, string subject, string? clientVersion = null)
    {
        var response = await http.PostAsJsonAsync("/api/v1/installations/events", new { eventId, type, subject, clientVersion });
        response.EnsureSuccessStatusCode();
    }

    private async Task DownloadAsync(string url, string targetPath)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(targetPath))!);
        await using var file = File.Create(targetPath);
        await response.Content.CopyToAsync(file);
    }
}
