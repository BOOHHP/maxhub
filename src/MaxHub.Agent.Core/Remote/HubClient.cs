using System.Net.Http.Json;
using System.Text.Json;

namespace MaxHub.Agent.Core.Remote;

public sealed record QrSessionInfo(string SessionId, string AuthorizeUrl);
public sealed record HubSession(string AccessToken, string RefreshToken, string EmployeeId, string Username, DateTimeOffset ExpiresAtUtc);
public sealed record ToolIndexItem(
    string ToolId, string Name, string? Description, string LatestVersion, string Channel, string Category = "其他");
public sealed record RemoteInstallPlan(string ToolId, string Version, string Sha256, long SizeBytes, bool RestartRequired, string RiskLevel, string? Signature = null);
public sealed record ConnectorInfo(string Version, int MinMaxYear, int MaxMaxYear, string Sha256, long SizeBytes, string? Signature = null);
public sealed record AgentReleaseInfo(string Version, string DownloadUrl, string Sha256);

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
            session.GetProperty("user").GetProperty("username").GetString()!,
            session.GetProperty("expiresAtUtc").GetDateTimeOffset());
        UseToken(hubSession.AccessToken);
        return hubSession;
    }

    public void UseToken(string accessToken) =>
        http.DefaultRequestHeaders.Authorization = new("Bearer", accessToken);

    /// <summary>服务器标识（host:port），用于按服务器隔离本地固定的签名公钥。</summary>
    public string ServerAuthority => http.BaseAddress?.Authority ?? "default";

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

    public async Task<string> GetSigningPublicKeyAsync()
    {
        var json = await http.GetFromJsonAsync<JsonElement>("/api/v1/signing/public-key");
        return json.GetProperty("publicKey").GetString()!;
    }

    /// <summary>最新 Agent 版本元数据（公开端点，用于自更新）。未配置时服务端返回 404。</summary>
    public async Task<AgentReleaseInfo?> GetLatestAgentAsync()
    {
        var response = await http.GetAsync("/api/v1/agent/latest");
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return new AgentReleaseInfo(
            json.GetProperty("version").GetString()!,
            json.GetProperty("downloadUrl").GetString() ?? "",
            json.GetProperty("sha256").GetString() ?? "");
    }

    public async Task DownloadToolAsync(string toolId, string version, string targetPath, IProgress<double>? progress = null)
    {
        await DownloadAsync($"/downloads/{toolId}/{version}/package.zip", targetPath, progress);
    }

    public async Task DownloadConnectorAsync(int maxYear, string version, string targetPath, IProgress<double>? progress = null)
    {
        await DownloadAsync($"/downloads/connectors/{maxYear}/{version}/package.zip", targetPath, progress);
    }

    /// <summary>从任意 URL 下载文件（用于 Agent 自更新，走 GitHub Release）。</summary>
    public async Task DownloadAgentAsync(string url, string targetPath, IProgress<double>? progress = null)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(targetPath))!);
        var totalBytes = response.Content.Headers.ContentLength;
        await using var file = File.Create(targetPath);
        await using var stream = await response.Content.ReadAsStreamAsync();
        var buffer = new byte[81920];
        long readTotal = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read));
            readTotal += read;
            if (totalBytes > 0)
                progress?.Report(readTotal * 100.0 / totalBytes.Value);
        }
    }

    public async Task PostInstallEventAsync(string eventId, string type, string subject, string? clientVersion = null)
    {
        var response = await http.PostAsJsonAsync("/api/v1/installations/events", new { eventId, type, subject, clientVersion });
        response.EnsureSuccessStatusCode();
    }

    public sealed record PublishOutcome(bool Success, string? ReleaseId, string[] Errors);

    public async Task<PublishOutcome> PublishAsync(string zipPath)
    {
        using var content = new MultipartFormDataContent
        {
            { new ByteArrayContent(await File.ReadAllBytesAsync(zipPath)), "package", Path.GetFileName(zipPath) },
        };
        var response = await http.PostAsync("/api/v1/publish/releases", content);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        if (!response.IsSuccessStatusCode)
        {
            var errors = json.TryGetProperty("errors", out var e)
                ? e.EnumerateArray().Select(x => x.GetString() ?? "").ToArray()
                : [response.StatusCode.ToString()];
            return new PublishOutcome(false, null, errors);
        }
        return new PublishOutcome(true, json.GetProperty("releaseId").GetString(), []);
    }

    /// <summary>脚本直传：后端自动打包并提交审核（复用自动识别预填的名称/描述）。</summary>
    public async Task<PublishOutcome> PublishScriptAsync(string fileName, string content, string name, string description, string version, int minMaxYear, int maxMaxYear)
    {
        var response = await http.PostAsJsonAsync("/api/v1/scripts/publish", new
        {
            fileName, content, name, description, version, minMaxYear, maxMaxYear,
        });
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        if (!response.IsSuccessStatusCode)
        {
            var errors = json.TryGetProperty("errors", out var e)
                ? e.EnumerateArray().Select(x => x.GetString() ?? "").ToArray()
                : [response.StatusCode.ToString()];
            return new PublishOutcome(false, null, errors);
        }
        return new PublishOutcome(true, json.GetProperty("releaseId").GetString(), []);
    }

    /// <summary>脚本自动识别：返回建议的名称/描述/ID。</summary>
    public async Task<(string Name, string Description, string SuggestedId)?> AnalyzeScriptAsync(string fileName, string content)
    {
        var response = await http.PostAsJsonAsync("/api/v1/scripts/analyze", new { fileName, content });
        if (!response.IsSuccessStatusCode) return null;
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (json.GetProperty("name").GetString()!, json.GetProperty("description").GetString()!, json.GetProperty("suggestedId").GetString()!);
    }

    public async Task ReviewAsync(string releaseId, bool approve, string channel)
    {
        var response = await http.PostAsJsonAsync($"/api/v1/releases/{releaseId}/review", new { approve, channel });
        response.EnsureSuccessStatusCode();
    }

    public async Task RegisterConnectorAsync(string zipPath, string version, int minMaxYear, int maxMaxYear)
    {
        using var content = new MultipartFormDataContent
        {
            { new ByteArrayContent(await File.ReadAllBytesAsync(zipPath)), "package", Path.GetFileName(zipPath) },
            { new StringContent(version), "version" },
            { new StringContent(minMaxYear.ToString()), "minMaxYear" },
            { new StringContent(maxMaxYear.ToString()), "maxMaxYear" },
        };
        var response = await http.PostAsync("/api/v1/admin/connectors", content);
        response.EnsureSuccessStatusCode();
    }

    private async Task DownloadAsync(string url, string targetPath, IProgress<double>? progress = null)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(targetPath))!);
        var totalBytes = response.Content.Headers.ContentLength;
        await using var file = File.Create(targetPath);
        await using var stream = await response.Content.ReadAsStreamAsync();
        var buffer = new byte[81920];
        long readTotal = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read));
            readTotal += read;
            if (totalBytes > 0)
                progress?.Report(readTotal * 100.0 / totalBytes.Value);
        }
    }
}
