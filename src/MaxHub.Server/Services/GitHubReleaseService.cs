using System.Text.Json;

namespace MaxHub.Server.Services;

public sealed record GitHubAgentRelease(string Version, string DownloadUrl, string Sha256);

/// <summary>
/// 从 GitHub Releases 自动获取最新 Agent 版本（网页下载横幅与 Agent 自更新共用）。
/// 结果缓存一段时间；GitHub 不可达时沿用旧缓存，避免发布入口抖动。
/// </summary>
public sealed class GitHubReleaseService(HttpClient http, string repo, TimeSpan? cacheTtl = null)
{
    private readonly TimeSpan _ttl = cacheTtl ?? TimeSpan.FromMinutes(10);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private GitHubAgentRelease? _cached;
    private DateTimeOffset _fetchedAtUtc;

    public async Task<GitHubAgentRelease?> GetLatestAsync()
    {
        if (_cached is not null && DateTimeOffset.UtcNow - _fetchedAtUtc < _ttl)
            return _cached;

        await _gate.WaitAsync();
        try
        {
            if (_cached is not null && DateTimeOffset.UtcNow - _fetchedAtUtc < _ttl)
                return _cached;
            var release = await FetchAsync();
            if (release is not null)
            {
                _cached = release;
                _fetchedAtUtc = DateTimeOffset.UtcNow;
            }
            return release ?? _cached;
        }
        catch
        {
            return _cached; // GitHub 故障时退回旧缓存（可能为 null，由调用方走 DB 兜底）
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<GitHubAgentRelease?> FetchAsync()
    {
        try
        {
            if (await FetchFromApiAsync() is { } fromApi)
                return fromApi;
        }
        catch
        {
            // api.github.com 在部分内网不可达，退到 github.com 302 探测
        }
        return await FetchFromRedirectAsync();
    }

    private async Task<GitHubAgentRelease?> FetchFromApiAsync()
    {
        var json = await http.GetFromJsonAsync<JsonElement>($"https://api.github.com/repos/{repo}/releases/latest");
        var version = json.GetProperty("tag_name").GetString()?.TrimStart('v', 'V');
        if (string.IsNullOrWhiteSpace(version) || !json.TryGetProperty("assets", out var assets))
            return null;

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                continue;
            var url = asset.GetProperty("browser_download_url").GetString() ?? "";
            var sha256 = "";
            if (asset.TryGetProperty("digest", out var digest) && digest.ValueKind == JsonValueKind.String)
            {
                var value = digest.GetString()!;
                sha256 = value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? value[7..] : value;
            }
            return new GitHubAgentRelease(version, url, sha256);
        }
        return null;
    }

    /// <summary>releases/latest 会 302 到 tag 页；从 Location 解析版本并按发布命名规则拼下载地址（无 sha256）。</summary>
    private async Task<GitHubAgentRelease?> FetchFromRedirectAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://github.com/{repo}/releases/latest");
        using var response = await http.SendAsync(request);
        if ((int)response.StatusCode is < 300 or >= 400 || response.Headers.Location is not { } location)
            return null;
        var tag = location.Segments[^1].Trim('/');
        var version = tag.TrimStart('v', 'V');
        if (!Version.TryParse(version, out _))
            return null;
        return new GitHubAgentRelease(version,
            $"https://github.com/{repo}/releases/download/{tag}/MaxHubAgent-{version}-win-x64.exe", "");
    }
}
