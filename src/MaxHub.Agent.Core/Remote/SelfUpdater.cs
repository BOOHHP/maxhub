using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace MaxHub.Agent.Core.Remote;

/// <summary>
/// Agent 自更新：优先直连 GitHub Releases（用户机器可出网，服务器不能），
/// 失败回退服务器端点。下载 exe、校验 SHA256、替换并重启。
/// </summary>
public sealed class SelfUpdater(HubClient hub, HttpClient? githubHttp = null)
{
    public string? CurrentVersion { get; init; }
    public string GitHubRepo { get; init; } = "BOOHHP/maxhub";

    private readonly HttpClient _github = githubHttp ?? CreateGitHubClient();

    /// <summary>兼容旧更新器：内容已更新但文件名仍是旧版本时，退出后改成新版本文件名。</summary>
    public static bool TryNormalizeVersionedExecutableName(string currentVersion)
    {
        if (Environment.ProcessPath is not { } currentExe)
            return false;
        var targetExe = GetTargetExePath(currentExe, currentVersion);
        if (string.Equals(currentExe, targetExe, StringComparison.OrdinalIgnoreCase))
            return false;

        var scriptPath = Path.Combine(Path.GetDirectoryName(currentExe)!, "maxhub-normalize.cmd");
        File.WriteAllText(
            scriptPath,
            BuildRestartScript(currentExe, currentExe, targetExe, currentVersion));
        Process.Start(new ProcessStartInfo
        {
            FileName = scriptPath,
            WindowStyle = ProcessWindowStyle.Hidden,
            UseShellExecute = true,
        });
        return true;
    }

    private static HttpClient CreateGitHubClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MaxHub-Agent");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    /// <summary>检查是否有更新：服务器优先，回退 GitHub。无更新或都不可达返回 null。</summary>
    public async Task<AgentReleaseInfo?> CheckForUpdateAsync()
    {
        var release = await GetLatestFromServerAsync() ?? await GetLatestFromGitHubAsync();
        if (release is null || string.IsNullOrWhiteSpace(release.DownloadUrl))
            return null;
        return IsNewer(release.Version) ? release : null;
    }

    private async Task<AgentReleaseInfo?> GetLatestFromGitHubAsync()
    {
        try
        {
            var json = await _github.GetFromJsonAsync<JsonElement>(
                $"https://api.github.com/repos/{GitHubRepo}/releases/latest");
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
                return new AgentReleaseInfo(version, url, sha256);
            }
            return null;
        }
        catch
        {
            return null; // GitHub 不可达时走服务器回退
        }
    }

    private async Task<AgentReleaseInfo?> GetLatestFromServerAsync()
    {
        try
        {
            return await hub.GetLatestAgentAsync();
        }
        catch
        {
            return null; // 服务器不可达时不打扰用户
        }
    }

    /// <summary>下载新版本 exe 到应用目录旁的临时文件，校验后替换并重启。</summary>
    public async Task DownloadAndInstallAsync(AgentReleaseInfo release, IProgress<double>? progress = null)
    {
        if (Environment.ProcessPath is not { } currentExe)
            throw new InvalidOperationException("无法定位当前可执行文件路径。");

        var dir = Path.GetDirectoryName(currentExe)!;
        var tempPath = Path.Combine(dir, $"MaxHubAgent.new-{release.Version}.exe");
        var targetExe = GetTargetExePath(currentExe, release.Version);
        try
        {
            try
            {
                TimeSpan? serverTimeout = string.IsNullOrWhiteSpace(release.FallbackDownloadUrl)
                    ? null
                    : TimeSpan.FromSeconds(15);
                await hub.DownloadAgentAsync(release.DownloadUrl, tempPath, progress, serverTimeout);
            }
            catch when (!string.IsNullOrWhiteSpace(release.FallbackDownloadUrl) &&
                        !string.Equals(release.DownloadUrl, release.FallbackDownloadUrl, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(tempPath);
                progress?.Report(0);
                await hub.DownloadAgentAsync(release.FallbackDownloadUrl!, tempPath, progress);
            }

            // 校验 SHA256（服务器配置了 hash 时）
            if (!string.IsNullOrWhiteSpace(release.Sha256))
            {
                var actual = await ComputeSha256Async(tempPath);
                if (!string.Equals(actual, release.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("下载文件校验失败（SHA256 不匹配），已取消更新。");
            }

            // 准备重启脚本：等待当前进程退出后替换 exe 再启动
            var scriptPath = Path.Combine(dir, "maxhub-update.cmd");
            await File.WriteAllTextAsync(
                scriptPath,
                BuildRestartScript(currentExe, tempPath, targetExe, release.Version));
            Process.Start(new ProcessStartInfo
            {
                FileName = scriptPath,
                WindowStyle = ProcessWindowStyle.Hidden,
                UseShellExecute = true,
            });
        }
        catch
        {
            try { File.Delete(tempPath); } catch { /* ignore */ }
            throw;
        }
    }

    /// <summary>与当前版本比较：服务器版本更新（语义化）返回 true。</summary>
    private bool IsNewer(string serverVersion)
    {
        if (string.IsNullOrWhiteSpace(CurrentVersion))
            return true;
        var cur = Parse(CurrentVersion);
        var next = Parse(serverVersion);
        return cur is not null && next is not null && next.CompareTo(cur) > 0;
    }

    private static Version? Parse(string v)
    {
        // 去预发布后缀（如 1.0.1-beta）后解析
        var core = v.Split('-')[0];
        return Version.TryParse(core, out var parsed) ? parsed : null;
    }

    private static string GetTargetExePath(string currentExe, string version)
    {
        var fileName = Path.GetFileName(currentExe);
        var usesVersionedName = fileName.StartsWith("MaxHubAgent-", StringComparison.OrdinalIgnoreCase) &&
            fileName.EndsWith("-win-x64.exe", StringComparison.OrdinalIgnoreCase);
        return usesVersionedName
            ? Path.Combine(Path.GetDirectoryName(currentExe)!, $"MaxHubAgent-{version}-win-x64.exe")
            : currentExe;
    }

    private static string BuildRestartScript(
        string currentExe,
        string newExe,
        string targetExe,
        string version)
    {
        var lines = new List<string>
        {
            "@echo off",
            "set /a tries=0",
            ":retry",
            "timeout /t 1 /nobreak > nul",
            $"move /y \"{newExe}\" \"{targetExe}\" > nul 2>&1",
            "if errorlevel 1 (",
            "  set /a tries+=1",
            "  if %tries% lss 30 goto retry",
            "  del \"%~f0\"",
            "  exit /b 1",
            ")",
        };

        if (!string.Equals(currentExe, targetExe, StringComparison.OrdinalIgnoreCase))
        {
            lines.AddRange([
                "set /a tries=0",
                ":delete_old",
                $"del /f /q \"{currentExe}\" > nul 2>&1",
                $"if exist \"{currentExe}\" (",
                "  set /a tries+=1",
                "  if %tries% lss 30 (timeout /t 1 /nobreak > nul & goto delete_old)",
                ")",
            ]);
        }

        lines.Add($"start \"\" \"{targetExe}\" --after-update \"{version}\"");
        lines.Add("del \"%~f0\"");
        return string.Join("\r\n", lines);
    }

    private static async Task<string> ComputeSha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
